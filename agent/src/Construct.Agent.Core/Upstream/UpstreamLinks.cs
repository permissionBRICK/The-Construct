using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Constructd.Store.Records;
using Constructd.Sync.Profiles;

namespace Construct.Agent.Upstream;

/// <summary>One file an upstream config repo tracks: its path in the repository and the profile it is imported as.</summary>
public sealed record UpstreamFile(string Path, string Profile);

/// <summary>
/// A linked upstream config repo (9.1 <c>upstream</c>, owned by devices): its URL (never with a password), the ref it
/// follows (null: the repository's default branch) and the files it tracks.
/// </summary>
public sealed record UpstreamLink(string Url, string? Ref, IReadOnlyList<UpstreamFile> Files);

/// <summary>What carrying the old client's links over did.</summary>
public sealed record UpstreamImport(int Links, int Tracked, int LeftOut);

/// <summary>
/// The account's <c>upstream</c> records (design 9.7, docs/config-sync.md 7): read and written by this device, which also
/// carries the old client's manifest over. The engine owns what was applied (<c>upstream-applied</c>); a device seeds it
/// with the old stored base through the engine.
/// </summary>
public sealed class UpstreamLinks(DeviceAgent agent, IDeviceEngines engines)
{
    public static string RecordId(string url) => "upstream:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url.Trim())))[..16];

    public async Task<IReadOnlyList<UpstreamLink>> ListAsync(CancellationToken ct) =>
        (await agent.ReadRecordsAsync(RecordKinds.Upstream, ct).ConfigureAwait(false)).Values.Select(Parse).OfType<UpstreamLink>().ToList();

    /// <summary>Adds <paramref name="link"/>'s files to the link of its URL (a file of the same path is replaced).</summary>
    public Task AddAsync(UpstreamLink link, CancellationToken ct) =>
        agent.UpdateRecordAsync(RecordId(link.Url), RecordKinds.Upstream, current =>
        {
            var known = current is null ? null : Parse(current);
            var files = (known?.Files ?? []).Where(f => link.Files.All(n => n.Path != f.Path)).Concat(link.Files).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            return Value(new UpstreamLink(link.Url.Trim(), known?.Ref ?? link.Ref, files));
        }, ct);

    /// <summary>The file is no longer tracked (its profile was deleted here); the link stays.</summary>
    public Task RemoveFileAsync(string url, string path, CancellationToken ct) =>
        agent.UpdateRecordAsync(RecordId(url), RecordKinds.Upstream, current =>
            current is null || Parse(current) is not { } known ? current : Value(known with { Files = known.Files.Where(f => f.Path != path).ToList() }), ct);

    public static JsonNode Value(UpstreamLink link) => new JsonObject
    {
        ["url"] = link.Url,
        ["ref"] = link.Ref,
        ["files"] = new JsonArray(link.Files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["profile"] = f.Profile }).ToArray()),
    };

    public static UpstreamLink? Parse(JsonNode value)
    {
        if (value["url"] is not JsonValue url || !url.TryGetValue<string>(out var text) || text.Length == 0) return null;
        var files = new List<UpstreamFile>();
        foreach (var file in value["files"] as JsonArray ?? [])
        {
            if (file?["path"]?.GetValue<string>() is { Length: > 0 } path && file["profile"]?.GetValue<string>() is { Length: > 0 } profile)
                files.Add(new UpstreamFile(path, profile));
        }

        return new UpstreamLink(text, value["ref"]?.GetValue<string>() is { Length: > 0 } r ? r : null, files);
    }

    /// <summary>The operation id of a submission: the same file at the same commit is the same operation (9.7).</summary>
    public static string OperationId(string url, string path, string commit) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(url + "\n" + path + "\n" + commit)))[..32];

    // ---- the old client's manifest (13.1) ----

    /// <summary>
    /// The old client's links and tracked files (<c>manifest/remotes.json</c>, <c>manifest/&lt;name&gt;.json</c>,
    /// <c>bases/&lt;name&gt;.json</c>, by path in the config repo) as records: one <c>upstream</c> record per repository and,
    /// through <paramref name="engine"/>, the stored base of each file as its <c>upstream-applied</c> record. A file without
    /// a readable stored base, or of a URL that carries a password, is left out. Safe to run again.
    /// </summary>
    public async Task<UpstreamImport> ImportLegacyAsync(IReadOnlyDictionary<string, string> files, string engine, CancellationToken ct)
    {
        var (remotes, tracked) = ReadLegacy(files);
        int links = 0, imported = 0, leftOut = tracked.Count(t => t.Base is null);
        foreach (var url in remotes.Concat(tracked.Select(t => t.Url)).Distinct(StringComparer.Ordinal))
        {
            var mine = tracked.Where(t => t.Url == url && t.Base is not null).ToList();
            if (UpstreamUrls.Refused(url))
            {
                leftOut += mine.Count;
                continue;
            }

            var refName = mine.Select(t => t.Ref).FirstOrDefault(r => r is not null);
            await AddAsync(new UpstreamLink(url, refName, mine.Select(t => t.File).ToList()), ct).ConfigureAwait(false);
            links++;
            foreach (var file in mine)
            {
                var commit = file.BaseCommit is { } c && c.Length <= 64 && c.All(Uri.IsHexDigit) ? c.ToLowerInvariant() : "";
                var body = new JsonObject
                {
                    ["operationId"] = OperationId(url, file.File.Path, "base:" + commit),
                    ["url"] = url, ["path"] = file.File.Path, ["ref"] = refName, ["commit"] = commit,
                    ["content"] = file.Base!.DeepClone(),
                    ["base"] = new JsonObject { ["commit"] = commit, ["content"] = file.Base!.DeepClone() },
                };
                try
                {
                    await engines.PostAsync(engine, "profiles/" + Uri.EscapeDataString(file.File.Profile) + "/upstream", body, ct).ConfigureAwait(false);
                    imported++;
                }
                catch (AgentFailure failure) when (failure.Status is 400 or 409)
                {
                    // Another profile of that name, or a base that is no valid profile: not tracked; the link keeps the file.
                    leftOut++;
                }
            }
        }

        return new UpstreamImport(links, imported, leftOut);
    }

    /// <summary>A tracked file of the old client with its stored base (<c>manifest/&lt;name&gt;.json</c>, <c>bases/&lt;name&gt;.json</c>).</summary>
    private sealed record Tracked(string Url, string? Ref, UpstreamFile File, string? BaseCommit, JsonObject? Base);

    private static (List<string> Remotes, List<Tracked> Files) ReadLegacy(IReadOnlyDictionary<string, string> files)
    {
        var remotes = new List<string>();
        if (files.TryGetValue("manifest/remotes.json", out var listed) && Parse(listed) is { } list)
        {
            foreach (var remote in list as JsonArray ?? list["remotes"] as JsonArray ?? [])
                if (Text(remote?["url"]) is { Length: > 0 } url) remotes.Add(url.Trim());
        }

        var tracked = new List<Tracked>();
        foreach (var (path, text) in files)
        {
            if (!path.StartsWith("manifest/", StringComparison.Ordinal) || !path.EndsWith(".json", StringComparison.Ordinal) || path == "manifest/remotes.json") continue;
            var name = path["manifest/".Length..^".json".Length];
            if (name.Contains('/') || Parse(text) is not JsonObject entry || Text(entry["remoteUrl"]) is not { Length: > 0 } url) continue;
            var profile = Text(entry["importedAs"]) is { Length: > 0 } imported ? imported : name;
            var inRemote = Text(entry["pathInRemote"]) is { Length: > 0 } p ? p : $"projects/{name}.json";
            var refName = Text(entry["ref"]) is { Length: > 0 } r && r != "HEAD" ? r : null;
            var stored = files.TryGetValue($"bases/{name}.json", out var b) ? Parse(b) as JsonObject : null;
            tracked.Add(new Tracked(url.Trim(), refName, new UpstreamFile(inRemote, profile), Text(entry["baseCommit"]), stored));
        }

        return (remotes, tracked);
    }

    private static JsonNode? Parse(string text)
    {
        try { return JsonNode.Parse(text); }
        catch (System.Text.Json.JsonException) { return null; }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
