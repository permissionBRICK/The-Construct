using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Probe;
using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Core.State;

public static class UpdatePlanner
{
    public const string DefaultRepository = "permissionBRICK/The-Construct";
    public const string DefaultRef = "main";
    public static JsonObject ReadMarkers(JsonObject? raw, JsonObject? state = null)
    {
        raw ??= []; state ??= raw;
        string Read(JsonObject o, string key, string fallback = "") => !StateJson.Truthy(o[key]) ? fallback : StateJson.Trim(StateJson.String(o[key])) is { Length: > 0 } s ? s : fallback;
        return new JsonObject { ["repo"] = Read(raw, "constructRepo", DefaultRepository), ["ref"] = Read(raw, "constructRef", DefaultRef), ["installedCommit"] = Read(raw, "installedCommit"), ["provisionedCommit"] = Read(state, "provisionedCommit") };
    }
    public static string NormalizeCommit(string? value) { var s = StateJson.Trim(value ?? "").ToLowerInvariant(); return Regex.IsMatch(s, "^[0-9a-f]{7,64}$") ? s : ""; }
    public static string EffectiveProvisionedCommit(JsonObject markers, string? guestCommit = null) => NormalizeCommit(guestCommit) is { Length: > 0 } guest ? guest : StateJson.Text(markers["provisionedCommit"]) ?? "";
    public static bool IsProvisionStale(JsonObject markers, string? guestCommit = null) => StateJson.Text(markers["installedCommit"]) is { Length: > 0 } installed && EffectiveProvisionedCommit(markers, guestCommit) is { Length: > 0 } provisioned && installed != provisioned;
    public static JsonObject? ConstructUpdateFromManifest(JsonObject? json, JsonObject markers)
    {
        var commit = StateJson.Text(json?["commit"]) ?? "";
        bool Hash(string key) => Regex.IsMatch(StateJson.Text(json?[key]) ?? "", @"\A[0-9a-f]{64}\z");
        bool Size(string key) => StateJson.Number(json?[key]) is > 0 and <= 1073741824 and var n && Math.Floor(n) == n;
        if (StateJson.Number(json?["schemaVersion"]) != 1 || StateJson.Text(json?["repository"]) != StateJson.Text(markers["repo"]) ||
            StateJson.Text(json?["ref"]) != "refs/heads/main" || !Regex.IsMatch(commit, @"\A[0-9a-f]{40}\z") ||
            StateJson.Text(json?["releaseTag"]) != "host-" + commit || StateJson.Text(json?["sourceAsset"]) != "construct-source-" + commit + ".zip" ||
            !Hash("sourceSha256") || !Size("sourceSizeBytes") || StateJson.Text(json?["payloadAsset"]) != "construct-host-" + commit[..7] + "-win-x64.zip" ||
            !Hash("payloadSha256") || !Size("payloadSizeBytes")) return null;
        return new() { ["available"] = !commit.StartsWith(StateJson.String(markers["installedCommit"]), StringComparison.Ordinal), ["count"] = null, ["commit"] = commit };
    }
    public static string? ConstructManifestUrl(JsonObject markers) => StateJson.Truthy(markers["installedCommit"]) && StateJson.Text(markers["ref"]) == "main" && Regex.IsMatch(StateJson.String(markers["repo"]), @"\A[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z")
        ? $"https://github.com/{StateJson.String(markers["repo"])}/releases/latest/download/manifest.json" : null;
    public static async Task<JsonObject?> CheckConstructAsync(IUpdateSource source, JsonObject markers, CancellationToken cancellationToken = default)
    {
        var url = ConstructManifestUrl(markers); return url is null ? null : ConstructUpdateFromManifest(await source.GetJsonAsync(new Uri(url), cancellationToken) as JsonObject, markers);
    }
    public static string[] ConstructRefreshArgs(JsonObject markers) => ["-Repo", StateJson.String(markers["repo"]), "-Ref", StateJson.String(markers["ref"])];
    public static string T3CodeUrl(string? channel) => "https://registry.npmjs.org/t3/" + (channel == "nightly" ? "nightly" : "latest");
    private static double[]? Semver(string? value)
    {
        var match = Regex.Match(value ?? "", @"(\d+)\.(\d+)\.(\d+)", RegexOptions.ECMAScript); return match.Success ? match.Groups.Cast<Group>().Skip(1).Select(g => double.Parse(g.Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray() : null;
    }
    public static bool IsNewer(string? latest, string? installed)
    {
        var l = Semver(latest); var i = Semver(installed); if (l is null || i is null) return false;
        for (var n = 0; n < 3; n++) { if (l[n] > i[n]) return true; if (l[n] < i[n]) return false; } return false;
    }
    public static string PrereleasePart(string? v) => Regex.Match(v ?? "", @"\d+\.\d+\.\d+-(.*)", RegexOptions.ECMAScript).Groups[1].Value;
    public static double ComparePrerelease(string a, string b)
    {
        if (a == b) return 0; if (a.Length == 0) return 1; if (b.Length == 0) return -1;
        var aa = a.Split('.'); var bb = b.Split('.');
        for (var n = 0; n < Math.Max(aa.Length, bb.Length); n++)
        {
            if (n >= aa.Length) return -1; if (n >= bb.Length) return 1;
            var an = Regex.IsMatch(aa[n], @"^\d+$", RegexOptions.ECMAScript); var bn = Regex.IsMatch(bb[n], @"^\d+$", RegexOptions.ECMAScript);
            if (an && bn) { var d = ProbeParser.JsNumber(aa[n]) - ProbeParser.JsNumber(bb[n]); if (d != 0) return d; }
            else if (an != bn) return an ? -1 : 1;
            else { var d = string.CompareOrdinal(aa[n], bb[n]); if (d != 0) return Math.Sign(d); }
        }
        return 0;
    }
    public static bool IsNewerNightly(string? latest, string? installed)
    {
        if (string.IsNullOrEmpty(latest) || string.IsNullOrEmpty(installed)) return false;
        latest = StateJson.Trim(latest); installed = StateJson.Trim(installed); if (latest == installed) return false;
        var lp = PrereleasePart(latest); var ip = PrereleasePart(installed);
        if ((lp.Length == 0) != (ip.Length == 0)) return false;
        bool Nightly(string s) => s == "nightly" || s.StartsWith("nightly.", StringComparison.Ordinal);
        if (lp.Length > 0 && ip.Length > 0 && (!Nightly(lp) || !Nightly(ip))) return false;
        if (IsNewer(latest, installed)) return true;
        var l = Semver(latest); var i = Semver(installed); return l is not null && i is not null && l.SequenceEqual(i) && ComparePrerelease(lp, ip) > 0;
    }
    public static string? SelectNightlyManifest(JsonArray releases)
    {
        string[] assets = ["manifest.json", "SHA256SUMS", "T3Code-Construct-Setup.exe", "t3code-server-linux-x64.tar.gz"];
        var candidate = releases.OfType<JsonObject>().Where(r => StateJson.Boolean(r["draft"]) != true && StateJson.Boolean(r["prerelease"]) == true && Regex.IsMatch(StateJson.Text(r["tag_name"]) ?? "", @"^t3-\d+\.\d+\.\d+-nightly\.\d+\.\d+-[a-f0-9]{64}$", RegexOptions.ECMAScript) && assets.All(a => (r["assets"] as JsonArray)?.OfType<JsonObject>().Any(v => StateJson.Text(v["name"]) == a) == true)).OrderByDescending(r => StateJson.Text(r["published_at"]), StringComparer.Ordinal).FirstOrDefault();
        return candidate is null ? null : "https://github.com/permissionBRICK/construct-t3-builds/releases/download/" + StateJson.Text(candidate["tag_name"]) + "/manifest.json";
    }
    public static JsonObject? ValidateManifest(JsonNode? value, string channel) => value is JsonObject o && StateJson.Text(o["channel"]) == channel && Regex.IsMatch(StateJson.Text(o["buildHash"]) ?? "", "^[0-9a-f]{64}$") && ProbeParser.ExtractVersion(StateJson.Text(o["version"])).Length > 0 ? (JsonObject)o.DeepClone() : null;
    public static async Task<JsonObject?> DiscoverT3PrebuiltAsync(IUpdateSource source, string channel, CancellationToken cancellationToken = default)
    {
        var url = "https://github.com/permissionBRICK/construct-t3-builds/releases/latest/download/manifest.json";
        if (channel == "nightly") for (var page = 1; ; page++)
        {
            if (await source.GetJsonAsync(new Uri($"https://api.github.com/repos/permissionBRICK/construct-t3-builds/releases?per_page=100&page={page}"), cancellationToken) is not JsonArray releases) return null;
            var selected = SelectNightlyManifest(releases); if (selected is not null) { url = selected; break; }
            if (releases.Count < 100) return null;
        }
        return ValidateManifest(await source.GetJsonAsync(new Uri(url), cancellationToken), channel);
    }
    public static JsonObject AugmentPrebuilt(JsonObject agent, JsonObject? manifest) => manifest is null || StateJson.Text(manifest["buildHash"]) == StateJson.Text(agent["buildHash"]) ? (JsonObject)agent.DeepClone() : StateJson.Merge(agent, new JsonObject { ["latest"] = manifest["version"]?.DeepClone(), ["updateAvailable"] = true });
    public static (string Url, string Field)? AgentLatestSource(string id, string? channel = null) => id switch
    {
        "claude-code" => ("https://registry.npmjs.org/@anthropic-ai/claude-code/latest", "version"),
        "codex" => ("https://api.github.com/repos/openai/codex/releases/latest", "tag_name"),
        "opencode" => ("https://api.github.com/repos/sst/opencode/releases/latest", "tag_name"),
        "t3code" => (T3CodeUrl(channel), "version"),
        _ => null
    };
    public static async Task<string> FetchAgentLatestAsync(IUpdateSource source, string id, string? channel = null, CancellationToken cancellationToken = default)
    {
        if (AgentLatestSource(id, channel) is not {} location) return "";
        var response = await source.GetJsonAsync(new Uri(location.Url), cancellationToken) as JsonObject;
        var picked = response?[location.Field]; return StateJson.Truthy(picked) ? ProbeParser.ExtractVersion(StateJson.String(picked)) : "";
    }
    public static async Task<JsonArray> AugmentAgentsAsync(IUpdateSource source, JsonArray agents, CancellationToken cancellationToken = default)
    {
        var result = new JsonArray();
        foreach (var node in agents)
        {
            if (node is not JsonObject agent || StateJson.Text(agent["id"]) is not { Length: > 0 } id || !StateJson.Truthy(agent["version"]) || StateJson.Text(agent["version"]) == "—" || AgentLatestSource(id) is null)
            { result.Add(node?.DeepClone()); continue; }
            var channel = StateJson.Text(agent["channel"]);
            if (id == "t3code" && StateJson.Text(agent["installationMode"]) == "prebuilt")
            { result.Add(AugmentPrebuilt(agent, await DiscoverT3PrebuiltAsync(source, channel ?? "", cancellationToken))); continue; }
            var latest = await FetchAgentLatestAsync(source, id, channel, cancellationToken);
            var version = StateJson.String(agent["version"]);
            var newer = latest.Length > 0 && (id == "t3code" && channel == "nightly" ? IsNewerNightly(latest, version) : IsNewer(latest, version));
            result.Add(newer ? StateJson.Merge(agent, new JsonObject { ["latest"] = latest, ["updateAvailable"] = true }) : agent.DeepClone());
        }
        return result;
    }
}
