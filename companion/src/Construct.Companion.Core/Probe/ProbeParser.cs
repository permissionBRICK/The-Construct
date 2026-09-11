using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.Probe;

public static class ProbeParser
{
    public static Dictionary<string, string> ParseProbe(string stdout)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in stdout.Split('\n')) { var i = line.IndexOf('\t'); if (i > 0) result[line[..i]] = StateJson.Trim(line[(i + 1)..]); }
        return result;
    }
    public static string ConfigUnquote(string? s)
    {
        s ??= ""; return s.Length >= 2 && s.StartsWith('\'') && s.EndsWith('\'') ? s[1..^1].Replace("'\\''", "'", StringComparison.Ordinal) : s;
    }
    public static string ExtractVersion(string? s) { var m = Regex.Match(s ?? "", @"\d+\.\d+\.\d+(?:[-.][0-9A-Za-z.]+)?", RegexOptions.ECMAScript); return m.Success ? m.Value : StateJson.Trim(s ?? ""); }
    public static string FormatMarker(string? s) { s = StateJson.Trim(s ?? ""); var m = Regex.Match(s, @"^(\d{4}-\d{2}-\d{2})T"); return m.Success ? m.Groups[1].Value : s; }
    public static string ParseCommit(string? s) { s = StateJson.Trim(ConfigUnquote(StateJson.Trim(s ?? ""))).ToLowerInvariant(); return Regex.IsMatch(s, "^[0-9a-f]{7,64}$") ? s : ""; }
    public static int? ParseDiskPercent(string? s) => Regex.IsMatch(StateJson.Trim(s ?? ""), @"^\d{1,3}%?$", RegexOptions.ECMAScript) && int.TryParse(StateJson.Trim(s ?? "").TrimEnd('%'), out var n) && n <= 100 ? n : null;
    public static bool IsSafeOrigin(string? s) => Regex.IsMatch(s ?? "", @"^https?://(?:[A-Za-z0-9._-]+|\[[0-9A-Fa-f:.]+\])(?::\d{1,5})?$", RegexOptions.ECMAScript);
    public static string OriginPort(string? s) => Regex.Match(s ?? "", @":(\d{1,5})$", RegexOptions.ECMAScript).Groups[1].Value;
    public static JsonObject? ParseVmSpec(IReadOnlyDictionary<string, string> map)
    {
        var result = new JsonObject();
        foreach (var (key, field, divisor) in new[] { ("MEM_GB", "ramGb", 1d), ("DISK_DEV_BYTES", "diskGb", 1073741824d), ("VM_CPUS", "cpus", 1d) })
            if (JsNumber(map.GetValueOrDefault(key)) is > 0 and var n && double.IsFinite(n)) result[field] = Math.Floor(n / divisor + .5);
        return result.Count == 0 ? null : result;
    }
    internal static double JsNumber(string? s)
    {
        s = StateJson.Trim(s ?? ""); if (s.Length == 0) return 0;
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hex)) return hex;
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : double.NaN;
    }
    public static JsonObject? ParseVmConfig(IReadOnlyDictionary<string, string> map)
    {
        var result = new JsonObject();
        foreach (var (key, field) in new[] { ("T3CODE", "t3code"), ("T3CODE_LIMIT_RESUME", "t3codeLimitResume"), ("OPENCODE_BACKGROUND_WATCHER", "opencodeBackgroundWatcher") })
        {
            var s = StateJson.Trim(ConfigUnquote(map.GetValueOrDefault(key))).ToLowerInvariant(); if (s is "true" or "false") result[field] = s == "true";
        }
        var ch = StateJson.Trim(ConfigUnquote(map.GetValueOrDefault("T3CODE_CHANNEL"))); if (ch is "stable" or "nightly") result["t3codeChannel"] = ch;
        return result.Count == 0 ? null : result;
    }
    public static JsonObject ToState(IReadOnlyDictionary<string, string> map, string? host = null)
    {
        string Get(string key) => map.GetValueOrDefault(key) ?? "";
        string Default(string key, string fallback) => Get(key).Length > 0 ? Get(key) : fallback;
        var tools = Get("AI_TOOLS").Split(',').Select(StateJson.Trim).ToArray(); var agents = new JsonArray();
        foreach (var (id, name, detail, key) in new[] { ("claude-code", "Claude Code", "CLI + VS Code extension", "V_CLAUDE"), ("codex", "Codex", "app-server :4500", "V_CODEX"), ("opencode", "OpenCode", "serve :4096", "V_OPENCODE") })
            if (tools.Contains(id) || Get(key).Length > 0) agents.Add(Agent(id, name, detail, Get(key)));
        if (Get("T3CODE") == "true" || Get("V_T3").Length > 0)
        {
            var port = StateJson.Trim(Get("T3CODE_PORT")); if (port.Length == 0) port = "5177";
            var ch = StateJson.Trim(Get("T3CODE_CHANNEL")); var origin = ConfigUnquote(StateJson.Trim(Get("T3CODE_PUBLIC_BASE_URL"))); if (!IsSafeOrigin(origin)) origin = "";
            var https = origin.StartsWith("https://", StringComparison.Ordinal); var shownPort = OriginPort(origin);
            if (shownPort.Length == 0) shownPort = https ? StateJson.Trim(Default("T3CODE_HTTPS_PORT", "5178")) : port;
            var entry = Agent("t3code", "T3 Code", "web GUI :" + shownPort + (https ? " · https" : "") + (ch == "nightly" ? " · nightly" : ""), Get("V_T3"));
            entry["webui"] = Get("T3_ACTIVE") == "active"; entry["channel"] = ch == "nightly" ? "nightly" : "stable";
            if (Get("T3_INSTALLATION_MODE") == "prebuilt") { entry["installationMode"] = "prebuilt"; entry["buildHash"] = StateJson.Trim(Get("T3_BUILD_HASH")); }
            if (origin.Length > 0) entry["url"] = origin; else if (!string.IsNullOrEmpty(host)) entry["url"] = $"http://{(host.Contains(':') ? "[" + host + "]" : host)}:{port}";
            agents.Add(entry);
        }
        var projects = new JsonArray(Get("PROJECTS").Split(',').Select(StateJson.Trim).Where(s => s.Length > 0).Select(s => (JsonNode)new JsonObject { ["name"] = s, ["selected"] = true }).ToArray());
        var resources = new List<string>(); if (Get("MEM_GB").Length > 0) resources.Add(Get("MEM_GB") + " GB RAM"); if (Get("DISK_USED").Length > 0 && Get("DISK_SIZE").Length > 0) resources.Add(Get("DISK_USED") + " / " + Get("DISK_SIZE") + " disk");
        var result = new JsonObject { ["vmName"] = Get("AGENT_NAME"), ["ubuntu"] = Get("UBUNTU"), ["resources"] = string.Join(" · ", resources), ["agents"] = agents, ["projects"] = projects };
        foreach (var (key, field) in new[] { ("INSTALLED_AT", "installed"), ("REPROVISIONED_AT", "reprovisioned") }) { var marker = FormatMarker(Get(key)); if (marker.Length > 0) result[field] = marker; }
        if (ParseDiskPercent(Get("DISK_PCT")) is {} pct) result["diskPct"] = pct;
        var commit = ParseCommit(Get("CONSTRUCT_COMMIT")); if (commit.Length > 0) result["provisionedCommit"] = commit;
        if (ParseVmSpec(map) is {} spec) result["vmSpec"] = spec; if (ParseVmConfig(map) is {} config) result["vmConfig"] = config;
        return result;
    }
    private static JsonObject Agent(string id, string name, string detail, string version) => new() { ["id"] = id, ["name"] = name, ["detail"] = detail, ["version"] = ExtractVersion(version) is { Length: > 0 } v ? v : "—", ["updateAvailable"] = false };
}
