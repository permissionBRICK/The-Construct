using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Git;
using Construct.Agent.Http;
using Constructd.Sync.Profiles;

namespace Construct.Agent.Upstream;

/// <summary>
/// One tracked file after a refresh. <paramref name="State"/>: <c>up-to-date</c>, <c>merged</c>, <c>conflict</c> (an
/// agent in a VM that uses the profile resolves it), <c>waiting</c> (for that conflict), <c>removed-upstream</c> (the
/// local profile stays), <c>invalid</c> (the upstream file is no valid profile; the local one stays), <c>refused</c>
/// (another profile of that name, or tracked from another file), <c>dropped</c> (deleted here: no longer tracked) or
/// <c>failed</c>.
/// </summary>
public sealed record UpstreamFileStatus(string Profile, string Path, string State, string? Message = null);

/// <summary>One linked repository after a refresh: its URL as shown (no user info), the commit read, or why it was not read.</summary>
public sealed record UpstreamLinkStatus(string Url, string? Ref, string? Commit, string? Error, IReadOnlyList<UpstreamFileStatus> Files);

/// <summary><c>GET /api/upstream</c>: the last refresh (null: none yet) and whether one runs now.</summary>
public sealed record UpstreamView(bool Running, DateTimeOffset? RefreshedAt, IReadOnlyList<UpstreamLinkStatus> Links);

/// <summary>
/// Upstream config repos on this device (design 9.7, docs/config-sync.md 7): on a schedule and on demand, every linked
/// repository is fetched with plain git and this PC's own credentials into a cache (safe to delete), and each tracked
/// file's version at the fetched commit goes to the store's engine, which merges it into its profile. Push-back stays a
/// manual step outside this device. The old client's links left in <c>legacy-upstream.json</c> by an earlier converter
/// are carried over first.
/// </summary>
public sealed class UpstreamSync : IAsyncDisposable
{
    /// <summary>How often linked repositories are fetched.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan GitTimeout = TimeSpan.FromMinutes(2);

    private readonly DeviceAgent _agent;
    private readonly IDeviceEngines _engines;
    private readonly string _directory;
    private readonly TimeProvider _time;
    private readonly string? _gitPath;
    private readonly SemaphoreSlim _running = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private UpstreamView? _last;

    public UpstreamSync(DeviceAgent agent, IDeviceEngines engines, string dataDirectory, TimeProvider time, string? gitSearchPath = null)
    {
        _agent = agent;
        _engines = engines;
        _directory = dataDirectory;
        _time = time;
        _gitPath = gitSearchPath;
        Links = new UpstreamLinks(agent, engines);
    }

    public UpstreamLinks Links { get; }

    private string StatusFile => Path.Combine(_directory, "upstream-status.json");

    private string LegacyFile => Path.Combine(_directory, "legacy-upstream.json");

    /// <summary>Refreshes on the schedule from <see cref="FirstDelay"/> on.</summary>
    public void Start() => _loop ??= Task.Run(async () =>
    {
        try
        {
            await Task.Delay(FirstDelay, _time, _stop.Token).ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                try { await RefreshAsync(_stop.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is AgentFailure or HttpRequestException or IOException or JsonException or InvalidOperationException) { }
                await Task.Delay(Interval, _time, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    });

    public UpstreamView View()
    {
        var last = _last ?? Read();
        return (last ?? new UpstreamView(false, null, [])) with { Running = _running.CurrentCount == 0 };
    }

    /// <summary>One refresh of every link; a second call while one runs waits for it and answers its result.</summary>
    public async Task<UpstreamView> RefreshAsync(CancellationToken ct)
    {
        if (!await _running.WaitAsync(0, ct).ConfigureAwait(false))
        {
            await _running.WaitAsync(ct).ConfigureAwait(false);
            _running.Release();
            return View();
        }

        try
        {
            _agent.Events.Publish("upstream");
            if (!_agent.IsRegistered || Engine() is not { } engine) return View();
            IReadOnlyList<UpstreamLink> links;
            try
            {
                await CarryLegacyOverAsync(engine, ct).ConfigureAwait(false);
                links = await Links.ListAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AgentFailure or HttpRequestException or Constructd.Store.Client.StoreUnavailableException or IOException or JsonException
                || ex is TaskCanceledException && !ct.IsCancellationRequested)
            {
                // The store or the engine was not reached: the last refresh stays what is shown; the next one tries again.
                return View();
            }

            var statuses = new List<UpstreamLinkStatus>();
            foreach (var link in links.Where(l => l.Files.Count > 0).OrderBy(l => l.Url, StringComparer.Ordinal))
                statuses.Add(await RefreshLinkAsync(link, engine, ct).ConfigureAwait(false));
            _last = new UpstreamView(false, _time.GetUtcNow(), statuses);
            PrivateFiles.Write(StatusFile, JsonSerializer.SerializeToUtf8Bytes(_last, AgentJson.Options));
            return _last;
        }
        finally
        {
            _running.Release();
            _agent.Events.Publish("upstream");
        }
    }

    /// <summary>The engine that merges: the store's, as the UI's profile pages use.</summary>
    private string? Engine() => _agent.StoreEngineId is { } store && _engines.List().Any(e => e.Id == store) ? store : null;

    /// <summary>Links an earlier converter kept as data (<c>legacy-upstream.json</c>) become records; the file goes once they are.</summary>
    private async Task CarryLegacyOverAsync(string engine, CancellationToken ct)
    {
        if (!File.Exists(LegacyFile)) return;
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var (path, text) in JsonNode.Parse(File.ReadAllText(LegacyFile))?["files"] as JsonObject ?? [])
                if (text is JsonValue v && v.TryGetValue<string>(out var s)) files[path] = s;
        }
        catch (JsonException) { }

        await Links.ImportLegacyAsync(files, engine, ct).ConfigureAwait(false);
        File.Delete(LegacyFile);
    }

    private async Task<UpstreamLinkStatus> RefreshLinkAsync(UpstreamLink link, string engine, CancellationToken ct)
    {
        var shown = UpstreamUrls.Display(link.Url);
        UpstreamLinkStatus Failed(string error) =>
            new(shown, link.Ref, null, error, link.Files.Select(f => new UpstreamFileStatus(f.Profile, f.Path, "failed", error)).ToList());
        if (UpstreamUrls.Refused(link.Url)) return Failed("The URL carries a password; link it again without one (the credential helper supplies it).");
        var source = link.Ref is { Length: > 0 } r && r != "HEAD" ? r : "HEAD";
        if (!SafeRef(source)) return Failed("The ref is no branch name.");
        if (GitProcess.Find(_gitPath) is not { } git) return Failed("Git is not installed on this PC.");

        var cache = Path.Combine(_directory, "upstream", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(link.Url)))[..16]);
        if (!File.Exists(Path.Combine(cache, "HEAD")))
        {
            Directory.CreateDirectory(cache);
            if (await GitAsync(git, ["init", "--bare", "-q", cache], ct).ConfigureAwait(false) is not { Exit: 0 }) return Failed("The cache could not be created.");
        }

        var fetch = await GitAsync(git, ["-C", cache, "-c", "core.hooksPath=", "fetch", "--quiet", "--no-tags", "--depth=1", "--force", "--", link.Url,
            "+" + source + ":refs/construct/upstream"], ct).ConfigureAwait(false);
        if (fetch is not { Exit: 0 }) return Failed(FetchProblem(fetch?.Error));
        var head = await GitAsync(git, ["-C", cache, "rev-parse", "--verify", "refs/construct/upstream^{commit}"], ct).ConfigureAwait(false);
        var commit = head is { Exit: 0 } ? head.Value.Output.Trim() : "";
        if (commit.Length is < 40 or > 64 || !commit.All(Uri.IsHexDigit)) return Failed("The fetched commit could not be read.");

        var files = new List<UpstreamFileStatus>();
        foreach (var file in link.Files)
            files.Add(await RefreshFileAsync(git, cache, link, file, commit, engine, ct).ConfigureAwait(false));
        return new UpstreamLinkStatus(shown, link.Ref, commit, null, files);
    }

    private async Task<UpstreamFileStatus> RefreshFileAsync(string git, string cache, UpstreamLink link, UpstreamFile file, string commit, string engine, CancellationToken ct)
    {
        var blob = await GitAsync(git, ["-C", cache, "cat-file", "blob", commit + ":" + file.Path], ct).ConfigureAwait(false);
        // Removed upstream (or renamed, which is the same): the local profile stays (config-sync 7).
        if (blob is not { Exit: 0 }) return new UpstreamFileStatus(file.Profile, file.Path, "removed-upstream");
        JsonObject? content;
        try { content = JsonNode.Parse(blob.Value.Output) as JsonObject; }
        catch (JsonException) { content = null; }
        if (content is null) return new UpstreamFileStatus(file.Profile, file.Path, "invalid", "The upstream file is not a JSON object.");

        var body = new JsonObject
        {
            ["operationId"] = UpstreamLinks.OperationId(link.Url, file.Path, commit),
            ["url"] = link.Url, ["path"] = file.Path, ["ref"] = link.Ref, ["commit"] = commit, ["content"] = content,
        };
        JsonNode? answer;
        try
        {
            answer = await _engines.PostAsync(engine, "profiles/" + Uri.EscapeDataString(file.Profile) + "/upstream", body, ct).ConfigureAwait(false);
        }
        catch (AgentFailure failure)
        {
            return new UpstreamFileStatus(file.Profile, file.Path, failure.Code switch
            {
                "invalid-profile" => "invalid",
                "not-tracked" or "other-provenance" => "refused",
                _ => "failed",
            }, failure.Code switch
            {
                "invalid-profile" => "The upstream file is not a valid profile; the local one stays.",
                "not-tracked" => "A profile of that name exists that did not come from this repository.",
                "other-provenance" => "The profile is tracked from another file.",
                _ => "The engine did not take it (" + failure.Code + "); it is tried again at the next refresh.",
            });
        }

        var status = answer?["status"]?.GetValue<string>();
        if (status == "dropped") await Links.RemoveFileAsync(link.Url, file.Path, ct).ConfigureAwait(false);
        return new UpstreamFileStatus(file.Profile, file.Path, status switch
        {
            "unchanged" => "up-to-date",
            "merged" => "merged",
            "conflict" => "conflict",
            "waiting" => "waiting",
            "dropped" => "dropped",
            _ => "failed",
        }, status switch
        {
            "conflict" => "Changed here and upstream: " + string.Join(", ", (answer?["fields"] as JsonArray ?? []).Select(f => f?.GetValue<string>())) + ".",
            "waiting" => "Waits until the open conflict on the profile is resolved.",
            "dropped" => "Deleted here: no longer tracked.",
            _ => null,
        });
    }

    /// <summary>A branch or tag name git takes as the source of a refspec, nothing more.</summary>
    private static bool SafeRef(string name) =>
        name.Length is > 0 and <= 255 && !name.StartsWith('-') && !name.Contains("..", StringComparison.Ordinal)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/');

    /// <summary>A short reason, from git's message without anything it echoed (URLs, helper output).</summary>
    private static string FetchProblem(string? error)
    {
        var text = error ?? "";
        if (text.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) || text.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
            || text.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase) || text.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "Git has no working credential for this repository on this PC; sign in with git once (for example by cloning it).";
        if (text.Contains("not found", StringComparison.OrdinalIgnoreCase) || text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            || text.Contains("couldn't find remote ref", StringComparison.OrdinalIgnoreCase))
            return "The repository or its branch was not found.";
        return error is null ? "Git did not finish in time." : "The repository could not be fetched.";
    }

    private Task<(int Exit, string Output, string Error)?> GitAsync(string git, string[] arguments, CancellationToken ct) =>
        GitProcess.RunAsync(git, arguments, null, null, GitTimeout, ct, _directory);

    private UpstreamView? Read()
    {
        try { return File.Exists(StatusFile) ? JsonSerializer.Deserialize<UpstreamView>(File.ReadAllBytes(StatusFile), AgentJson.Options) : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        _stop.Dispose();
    }
}
