using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Construct.Companion.Core.State;

public static class SettingsMapping
{
    private static readonly (string Raw, string Form, char Kind)[] Fields =
    [
        ("gitUserName", "gitName", 's'), ("gitEmail", "gitEmail", 's'), ("gitCredentialStore", "gitCred", 'b'),
        ("vmMemoryGB", "ram", 'n'), ("vmDiskGB", "disk", 'n'), ("vmCpuCount", "cpu", 'n'), ("ubuntuRelease", "ubuntu", 's'),
        ("vsCodeServeWeb", "serveWeb", 'b'), ("vsCodeTunnel", "tunnel", 'b'), ("smbShare", "smb", 'b'),
        ("claudePartialStreaming", "partialStreaming", 'b'), ("opencodeBackgroundWatcher", "opencodeBackgroundWatcher", 'b'),
        ("micPassthrough", "mic", 'b'), ("t3code", "t3code", 'b'), ("t3codeChannel", "t3codeChannel", 'c'),
        ("t3codeLimitResume", "t3codeLimitResume", 'b'), ("vmAutoCheckpoints", "autoCheckpoints", 'b')
    ];
    public static JsonObject MapToForm(JsonObject? raw)
    {
        var result = new JsonObject();
        if (raw is null) return result;
        foreach (var (key, form, kind) in Fields)
        {
            if (key == "micPassthrough") continue;
            if (key == "claudePartialStreaming" && StateJson.Boolean(raw["micPassthrough"]) is bool mic) result["mic"] = mic;
            if (!raw.ContainsKey(key)) continue;
            var value = raw[key];
            if (kind == 'b') { if (StateJson.Boolean(value) is bool b) result[form] = b; }
            else if (kind != 'c' || StateJson.String(value) is "stable" or "nightly") result[form] = StateJson.String(value);
        }
        return result;
    }
    public static JsonObject MapFromForm(JsonObject? form)
    {
        var result = new JsonObject();
        if (form is null) return result;
        foreach (var (key, field, kind) in Fields)
        {
            var value = form[field];
            if (kind == 'b') { if (StateJson.Boolean(value) is bool b) result[key] = b; continue; }
            if (kind == 'c') { if (StateJson.Text(value) is "stable" or "nightly") result[key] = value!.DeepClone(); continue; }
            if (value is null) continue;
            var s = StateJson.Trim(StateJson.String(value));
            if (s.Length == 0) continue;
            if (kind == 'n' && Regex.IsMatch(s, @"^[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?$", RegexOptions.ECMAScript))
            {
                var n = double.Parse(s, CultureInfo.InvariantCulture);
                result[key] = double.IsFinite(n) ? JsonValue.Create(n) : null;
            }
            else result[key] = s;
        }
        return result;
    }
    public static string[] PatchReprovisionChanges(JsonObject? previous, JsonObject? next)
    {
        previous ??= []; next ??= [];
        bool Is(JsonObject o, string k) => StateJson.Boolean(o[k]) == true;
        var result = new List<string>();
        if (Is(previous, "t3codeLimitResume") != Is(next, "t3codeLimitResume") ||
            (Is(next, "t3codeLimitResume") && Is(next, "t3code") && (!Is(previous, "t3code") ||
            !StateJson.StrictEquals(StateJson.Truthy(previous["t3codeChannel"]) ? previous["t3codeChannel"] : JsonValue.Create("stable"), StateJson.Truthy(next["t3codeChannel"]) ? next["t3codeChannel"] : JsonValue.Create("stable")))))
            result.Add("patched T3 Code + Desktop build");
        if (Is(previous, "opencodeBackgroundWatcher") != Is(next, "opencodeBackgroundWatcher")) result.Add("OpenCode background watcher");
        return result.ToArray();
    }
}
