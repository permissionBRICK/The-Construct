using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Git;
using Construct.Agent.Http;
using Constructd.Sync.Profiles;

namespace Construct.Agent.Upstream;

/// <summary>
/// One tracked file after a refresh or import. <paramref name="State"/>: <c>up-to-date</c>, <c>merged</c>, <c>conflict</c>
/// (an agent in a VM that uses the profile resolves it), <c>waiting</c> (for that conflict), <c>removed-upstream</c> (the
/// local profile stays), <c>invalid</c> (the upstream file is no valid profile; the local one stays), <c>refused</c>
/// (another profile of that name, or tracked from another file), <c>dropped</c> (deleted here: no longer tracked),
/// <c>pending</c> (linked, not refreshed yet) or <c>failed</c>.
/// </summary>
public sealed record UpstreamFileStatus(string Profile, string Path, string State, string? Message = null);

/// <summary>One linked repository after a refresh: its stable id, URL as shown (no user info), the commit read, or why it was not read.</summary>
public sealed record UpstreamLinkStatus(string Id, string Url, string? Ref, string? Commit, string? Error, IReadOnlyList<UpstreamFileStatus> Files);

/// <summary><c>GET /api/upstream</c>: the last refresh (null: none yet) and whether one runs now.</summary>
public sealed record UpstreamView(bool Running, DateTimeOffset? RefreshedAt, IReadOnlyList<UpstreamLinkStatus> Links);

/// <summary>One profile file discovered in a linked upstream config repository.</summary>
public sealed record UpstreamAvailableFile(string Path, string Name, bool Tracked, string? TrackedAs, bool ExistsLocally, bool Valid, string? Error = null);

/// <summary>The profile files discovered in one linked repository at its fetched commit.</summary>
public sealed record UpstreamRepoFiles(string Id, string Url, string? Ref, string Commit, IReadOnlyList<UpstreamAvailableFile> Files);

/// <summary>One file requested for import from a linked repository (<paramref name="As"/> overrides the profile name).</summary>
public sealed record ImportUpstreamFile(string Path, string? As = null);

/// <summary>The outcome of importing selected files from one linked repository.</summary>
public sealed record UpstreamImportResult(string Id, string Url, string Commit, IReadOnlyList<UpstreamFileStatus> Results, UpstreamView View);

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

    private string CacheDir(string url) => Path.Combine(_directory, "upstream", UpstreamLinks.Id(url));

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

    /// <summary>
    /// The last refresh, or—when no refresh has been cached yet on this device—the linked repositories currently in
    /// the account's store so another device's links appear right away.
    /// </summary>
    public async Task<UpstreamView> ViewAsync(CancellationToken ct)
    {
        var last = _last ?? Read();
        if (last is not null) return last with { Running = _running.CurrentCount == 0 };
        if (!_agent.IsRegistered) return new UpstreamView(_running.CurrentCount == 0, null, []);
        try
        {
            var links = await Links.ListAsync(ct).ConfigureAwait(false);
            var statuses = links.OrderBy(l => l.Url, StringComparer.Ordinal)
                .Select(l => new UpstreamLinkStatus(UpstreamLinks.Id(l.Url), UpstreamUrls.Display(l.Url), l.Ref, null, null,
                    l.Files.Select(f => new UpstreamFileStatus(f.Profile, f.Path, "pending")).ToList()))
                .ToList();
            return new UpstreamView(_running.CurrentCount == 0, null, statuses);
        }
        catch (Exception ex) when (ex is AgentFailure or HttpRequestException or Constructd.Store.Client.StoreUnavailableException or IOException or JsonException
            || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new UpstreamView(_running.CurrentCount == 0, null, []);
        }
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
            foreach (var link in links.OrderBy(l => l.Url, StringComparer.Ordinal))
                statuses.Add(await RefreshLinkAsync(link, engine, ct).ConfigureAwait(false));
            Save(new UpstreamView(false, _time.GetUtcNow(), statuses));
            return _last!;
        }
        finally
        {
            _running.Release();
            _agent.Events.Publish("upstream");
        }
    }

    /// <summary>
    /// Links an upstream config repository by URL (optional branch/ref): verifies the URL and fetches it first so
    /// credential or typo errors are reported before anything is saved, then returns its available profile files.
    /// </summary>
    public async Task<UpstreamRepoFiles> AddRepoAsync(string? rawUrl, string? rawRef, CancellationToken ct)
    {
        var engine = RequireEngine();
        var url = ValidateUrl(rawUrl);
        var refName = ValidateOptionalRef(rawRef);

        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var fetched = await FetchRepoAsync(url, refName, ct).ConfigureAwait(false);
            if (fetched.Error is not null) throw new AgentFailure(422, "fetch-failed", fetched.Error);

            await Links.AddAsync(new UpstreamLink(url, refName, []), ct, replaceRef: refName is not null).ConfigureAwait(false);
            var id = UpstreamLinks.Id(url);
            var saved = await Links.FindAsync(id, ct).ConfigureAwait(false) ?? new UpstreamLink(url, refName, []);
            var files = await ListFilesAsync(fetched.Git!, fetched.Cache!, fetched.Commit!, saved, engine, ct).ConfigureAwait(false);

            var current = _last ?? Read();
            var existing = current?.Links.FirstOrDefault(l => l.Id == id);
            var updated = new UpstreamLinkStatus(id, UpstreamUrls.Display(url), saved.Ref, fetched.Commit, null,
                existing?.Files ?? saved.Files.Select(f => new UpstreamFileStatus(f.Profile, f.Path, "pending")).ToList());
            await UpdateCachedLinkAsync(id, updated, ct).ConfigureAwait(false);
            _agent.Events.Publish("upstream");
            return new UpstreamRepoFiles(id, UpstreamUrls.Display(url), saved.Ref, fetched.Commit!, files);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Fetches an already-linked repository and returns its available profile files.</summary>
    public async Task<UpstreamRepoFiles> ListRepoFilesAsync(string id, CancellationToken ct)
    {
        var engine = RequireEngine();
        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var link = await Links.FindAsync(id, ct).ConfigureAwait(false)
                ?? throw new AgentFailure(404, "not-found", "No such linked repository.");
            var fetched = await FetchRepoAsync(link.Url, link.Ref, ct).ConfigureAwait(false);
            if (fetched.Error is not null) throw new AgentFailure(422, "fetch-failed", fetched.Error);
            var files = await ListFilesAsync(fetched.Git!, fetched.Cache!, fetched.Commit!, link, engine, ct).ConfigureAwait(false);
            return new UpstreamRepoFiles(id, UpstreamUrls.Display(link.Url), link.Ref, fetched.Commit!, files);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>
    /// Imports selected files from a linked repository into the account's profiles through the store's engine and
    /// records every successfully imported file in the repository's <c>upstream</c> link for ongoing refresh.
    /// </summary>
    public async Task<UpstreamImportResult> ImportAsync(string id, IReadOnlyList<ImportUpstreamFile>? requested, CancellationToken ct)
    {
        var engine = RequireEngine();
        if (requested is not { Count: > 0 })
            throw new AgentFailure(400, "invalid-request", "Select at least one profile to import.");

        var plan = new List<(string Path, string Profile)>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var seenProfiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in requested)
        {
            var path = item.Path?.Trim() ?? "";
            if (!SafeRepoPath(path))
                throw new AgentFailure(400, "invalid-path", $"'{path}' is not a valid profile file path.");
            if (!seenPaths.Add(path))
                throw new AgentFailure(400, "duplicate-path", $"'{path}' was selected more than once.");
            var profile = string.IsNullOrWhiteSpace(item.As) ? Path.GetFileNameWithoutExtension(path) : item.As.Trim();
            if (!SafeProfileName(profile))
                throw new AgentFailure(400, "invalid-name", $"'{profile}' is not a valid profile name.");
            if (!seenProfiles.Add(profile))
                throw new AgentFailure(400, "duplicate-name", $"Profile name '{profile}' was used more than once.");
            plan.Add((path, profile));
        }

        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var link = await Links.FindAsync(id, ct).ConfigureAwait(false)
                ?? throw new AgentFailure(404, "not-found", "No such linked repository.");
            var fetched = await FetchRepoAsync(link.Url, link.Ref, ct).ConfigureAwait(false);
            if (fetched.Error is not null) throw new AgentFailure(422, "fetch-failed", fetched.Error);

            var results = new List<UpstreamFileStatus>();
            var imported = new List<UpstreamFile>();
            foreach (var (path, profile) in plan)
            {
                var file = new UpstreamFile(path, profile);
                var status = await RefreshFileAsync(fetched.Git!, fetched.Cache!, link, file, fetched.Commit!, engine, import: true, ct).ConfigureAwait(false);
                results.Add(status);
                if (status.State is "merged" or "up-to-date" or "conflict" or "waiting")
                    imported.Add(file);
            }

            if (imported.Count > 0)
                await Links.AddAsync(new UpstreamLink(link.Url, link.Ref, imported), ct).ConfigureAwait(false);

            var existing = (_last ?? Read())?.Links.FirstOrDefault(l => l.Id == id);
            var byPath = new Dictionary<string, UpstreamFileStatus>(StringComparer.Ordinal);
            foreach (var f in existing?.Files ?? []) byPath[f.Path] = f;
            foreach (var r in results)
            {
                if (r.State is "merged" or "up-to-date" or "conflict" or "waiting") byPath[r.Path] = r;
            }

            var updated = new UpstreamLinkStatus(id, UpstreamUrls.Display(link.Url), link.Ref, fetched.Commit, null,
                byPath.Values.OrderBy(f => f.Path, StringComparer.Ordinal).ToList());
            await UpdateCachedLinkAsync(id, updated, ct).ConfigureAwait(false);
            _agent.Events.Publish("upstream");
            return new UpstreamImportResult(id, UpstreamUrls.Display(link.Url), fetched.Commit!, results, _last!);
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>Unlinks a repository and removes its local git cache; already-imported profiles stay in the account.</summary>
    public async Task RemoveRepoAsync(string id, CancellationToken ct)
    {
        if (!_agent.IsRegistered) throw new AgentFailure(409, "not-registered", "Set up this device first.");
        await _running.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var link = await Links.FindAsync(id, ct).ConfigureAwait(false)
                ?? throw new AgentFailure(404, "not-found", "No such linked repository.");
            await Links.RemoveAsync(link.Url, ct).ConfigureAwait(false);
            try
            {
                var cache = CacheDir(link.Url);
                if (Directory.Exists(cache)) Directory.Delete(cache, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            await UpdateCachedLinkAsync(id, null, ct).ConfigureAwait(false);
            _agent.Events.Publish("upstream");
        }
        finally
        {
            _running.Release();
        }
    }

    /// <summary>The engine that merges: the store's, as the UI's profile pages use.</summary>
    private string? Engine() => _agent.StoreEngineId is { } store && _engines.List().Any(e => e.Id == store) ? store : null;

    private string RequireEngine()
    {
        if (!_agent.IsRegistered) throw new AgentFailure(409, "not-registered", "Set up this device first.");
        return Engine() ?? throw new AgentFailure(503, "no-engine", "No store engine is connected.");
    }

    private void Save(UpstreamView view)
    {
        _last = view;
        PrivateFiles.Write(StatusFile, JsonSerializer.SerializeToUtf8Bytes(_last, AgentJson.Options));
    }

    private async Task UpdateCachedLinkAsync(string id, UpstreamLinkStatus? replacement, CancellationToken ct)
    {
        var current = _last ?? Read();
        List<UpstreamLinkStatus> list;
        if (current is not null)
        {
            list = current.Links.Where(l => l.Id != id).ToList();
        }
        else
        {
            var links = await Links.ListAsync(ct).ConfigureAwait(false);
            list = links.Where(l => UpstreamLinks.Id(l.Url) != id)
                .Select(l => new UpstreamLinkStatus(UpstreamLinks.Id(l.Url), UpstreamUrls.Display(l.Url), l.Ref, null, null,
                    l.Files.Select(f => new UpstreamFileStatus(f.Profile, f.Path, "pending")).ToList()))
                .ToList();
        }

        if (replacement is not null) list.Add(replacement);
        Save(new UpstreamView(false, current?.RefreshedAt ?? (replacement?.Commit is not null ? _time.GetUtcNow() : null),
            list.OrderBy(l => l.Url, StringComparer.Ordinal).ToList()));
    }

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

    private async Task<(string? Git, string? Cache, string? Commit, string? Error)> FetchRepoAsync(string url, string? refName, CancellationToken ct)
    {
        if (UpstreamUrls.Refused(url)) return (null, null, null, "The URL carries a password; link it again without one (the credential helper supplies it).");
        var source = refName is { Length: > 0 } r && r != "HEAD" ? r : "HEAD";
        if (!SafeRef(source)) return (null, null, null, "The ref is no branch name.");
        if (GitProcess.Find(_gitPath) is not { } git) return (null, null, null, "Git is not installed on this PC.");

        var cache = CacheDir(url);
        if (!File.Exists(Path.Combine(cache, "HEAD")))
        {
            Directory.CreateDirectory(cache);
            if (await GitAsync(git, ["init", "--bare", "-q", cache], ct).ConfigureAwait(false) is not { Exit: 0 })
                return (null, null, null, "The cache could not be created.");
        }

        var fetch = await GitAsync(git, ["-C", cache, "-c", "core.hooksPath=", "fetch", "--quiet", "--no-tags", "--depth=1", "--force", "--", url,
            "+" + source + ":refs/construct/upstream"], ct).ConfigureAwait(false);
        if (fetch is not { Exit: 0 }) return (null, null, null, FetchProblem(fetch?.Error));
        var head = await GitAsync(git, ["-C", cache, "rev-parse", "--verify", "refs/construct/upstream^{commit}"], ct).ConfigureAwait(false);
        var commit = head is { Exit: 0 } ? head.Value.Output.Trim() : "";
        if (commit.Length is < 40 or > 64 || !commit.All(Uri.IsHexDigit))
            return (null, null, null, "The fetched commit could not be read.");
        return (git, cache, commit, null);
    }

    private async Task<UpstreamLinkStatus> RefreshLinkAsync(UpstreamLink link, string engine, CancellationToken ct)
    {
        var id = UpstreamLinks.Id(link.Url);
        var shown = UpstreamUrls.Display(link.Url);
        var fetched = await FetchRepoAsync(link.Url, link.Ref, ct).ConfigureAwait(false);
        if (fetched.Error is not null)
            return new(id, shown, link.Ref, null, fetched.Error, link.Files.Select(f => new UpstreamFileStatus(f.Profile, f.Path, "failed", fetched.Error)).ToList());

        var files = new List<UpstreamFileStatus>();
        foreach (var file in link.Files)
            files.Add(await RefreshFileAsync(fetched.Git!, fetched.Cache!, link, file, fetched.Commit!, engine, import: false, ct).ConfigureAwait(false));
        return new UpstreamLinkStatus(id, shown, link.Ref, fetched.Commit, null, files);
    }

    private async Task<IReadOnlyList<UpstreamAvailableFile>> ListFilesAsync(string git, string cache, string commit, UpstreamLink link, string engine, CancellationToken ct)
    {
        // Convention from docs/config-sync.md: scan projects/*.json when a projects/ tree exists, else top-level *.json.
        var paths = await ListTreeJsonBlobsAsync(git, cache, commit, "projects/", ct).ConfigureAwait(false);
        if (paths is null)
            paths = await ListTreeJsonBlobsAsync(git, cache, commit, null, ct).ConfigureAwait(false) ?? [];

        var localNames = await ReadLocalProfileNamesAsync(engine, ct).ConfigureAwait(false);
        var trackedByPath = link.Files.ToDictionary(f => f.Path, f => f.Profile, StringComparer.Ordinal);
        var result = new List<UpstreamAvailableFile>();
        foreach (var path in paths)
        {
            var fileName = Path.GetFileName(path);
            if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".sample.json", StringComparison.OrdinalIgnoreCase)) continue;
            var stem = fileName[..^5];
            if (!SafeProfileName(stem)) continue;

            var valid = true;
            string? error = null;
            var blob = await GitAsync(git, ["-C", cache, "cat-file", "blob", commit + ":" + path], ct).ConfigureAwait(false);
            if (blob is not { Exit: 0 })
            {
                valid = false;
                error = "The file could not be read.";
            }
            else
            {
                JsonObject? parsed;
                try { parsed = JsonNode.Parse(blob.Value.Output) as JsonObject; }
                catch (JsonException) { parsed = null; }
                if (parsed is null)
                {
                    valid = false;
                    error = "Not a JSON object.";
                }
                else
                {
                    var candidate = parsed.DeepClone().AsObject();
                    candidate["name"] = stem;
                    var problems = ProfileRules.Validate(candidate);
                    if (problems.Count > 0)
                    {
                        valid = false;
                        error = string.Join("; ", problems.Select(p => p.Message));
                    }
                }
            }

            var tracked = trackedByPath.TryGetValue(path, out var trackedAs);
            var existsLocally = !tracked && localNames.Contains(stem);
            result.Add(new UpstreamAvailableFile(path, stem, tracked, tracked ? trackedAs : null, existsLocally, valid, error));
        }

        return result.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<List<string>?> ListTreeJsonBlobsAsync(string git, string cache, string commit, string? subtree, CancellationToken ct)
    {
        var args = subtree is null
            ? new[] { "-C", cache, "ls-tree", "-z", commit }
            : new[] { "-C", cache, "ls-tree", "-z", commit, subtree };
        var listed = await GitAsync(git, args, ct).ConfigureAwait(false);
        if (listed is not { Exit: 0 } || string.IsNullOrEmpty(listed.Value.Output)) return null;
        var blobs = new List<string>();
        foreach (var entry in listed.Value.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = entry.IndexOf('\t');
            if (tab <= 0 || tab + 1 >= entry.Length) continue;
            var header = entry[..tab].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (header.Length < 2 || header[1] != "blob") continue;
            var path = entry[(tab + 1)..];
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) blobs.Add(path);
        }

        return blobs;
    }

    private async Task<HashSet<string>> ReadLocalProfileNamesAsync(string engine, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (await _engines.GetAsync(engine, "profiles", ct).ConfigureAwait(false) is JsonArray list)
            {
                foreach (var item in list)
                    if (item?["name"]?.GetValue<string>() is { Length: > 0 } name) names.Add(name);
            }
        }
        catch (AgentFailure) { }

        return names;
    }

    private async Task<UpstreamFileStatus> RefreshFileAsync(string git, string cache, UpstreamLink link, UpstreamFile file, string commit, string engine, bool import, CancellationToken ct)
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
        if (import) body["import"] = true;
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

    private static string ValidateUrl(string? rawUrl)
    {
        var url = rawUrl?.Trim() ?? "";
        if (UpstreamUrls.Refused(url))
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.UserInfo.Contains(':'))
                throw new AgentFailure(400, "url-has-password", "The URL carries a password; remove it and let your git credential helper supply it.");
            throw new AgentFailure(400, "invalid-url", "Enter a valid git repository URL.");
        }

        return url;
    }

    private static string? ValidateOptionalRef(string? rawRef)
    {
        var refName = rawRef?.Trim();
        if (string.IsNullOrEmpty(refName) || refName == "HEAD") return null;
        if (!SafeRef(refName)) throw new AgentFailure(400, "invalid-ref", "The branch or ref name is not valid.");
        return refName;
    }

    /// <summary>A branch or tag name git takes as the source of a refspec, nothing more.</summary>
    private static bool SafeRef(string name) =>
        name.Length is > 0 and <= 255 && !name.StartsWith('-') && !name.Contains("..", StringComparison.Ordinal)
        && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' or '/');

    private static bool SafeRepoPath(string path) =>
        path.Length is > 0 and <= 512 && !path.StartsWith('/') && !path.StartsWith('-')
        && !path.Contains('\\') && !path.Contains("..", StringComparison.Ordinal)
        && !path.Any(char.IsControl) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    private static bool SafeProfileName(string name)
    {
        if (name.Length is 0 or > 128 || name.Any(char.IsControl)) return false;
        if (name.Contains('/') || name.Contains('\\') || name == "." || name.Contains("..", StringComparison.Ordinal)) return false;
        return name.Trim().ToLowerInvariant() is not ("default" or "project.schema");
    }

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
        try
        {
            if (!File.Exists(StatusFile)) return null;
            var view = JsonSerializer.Deserialize<UpstreamView>(File.ReadAllBytes(StatusFile), AgentJson.Options);
            return view is not null && view.Links.All(l => !string.IsNullOrEmpty(l.Id)) ? view : null;
        }
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
