using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.HostAdmin;
using Construct.Companion.Core.State;
namespace Construct.Companion.Core.Backups;

// Config backups (docs/plans/host-config-backups.md §3.7, §3.8): the panel's Backups module model,
// the per-VM override form, the once-per-streak warning and the default file name. The port of
// extension/src/backups.js, checked against the "config-backups" parity fixture.
public static partial class BackupProtocol
{
    public const string Feature = "config-backups";
    public const string StreakKey = "backupWarnedFailingSince";
    public const int MaxIntervalHours = 720;
    public const int MaxKeep = 50;
    public const string KeyFileName = "construct-backup-key.json";
    private static readonly Dictionary<string, string> Reasons = new(StringComparer.Ordinal)
    {
        ["vm-disk-space"] = "not enough free disk space on the VM", ["no-recipients"] = "no PC backup key is enrolled on the VM",
        ["export-failed"] = "the config export failed on the VM", ["upload-failed"] = "the upload to the host failed",
        ["host-refused"] = "the host refused the backup", ["verify-failed"] = "the backup could not be verified",
    };
    private static readonly Dictionary<string, string> Triggers = new(StringComparer.Ordinal) { ["scheduled"] = "scheduled", ["manual"] = "manual", ["pre-reinstall"] = "before reinstall" };
    [GeneratedRegex("^[0-9a-f]{32}$")] private static partial Regex IdPattern();
    [GeneratedRegex("^[0-9a-f]{64}$")] private static partial Regex FingerprintPattern();
    [GeneratedRegex("[\u0000-\u001f\u007f-\u009f]+")] private static partial Regex Controls();
    [GeneratedRegex(" {2,}")] private static partial Regex Spaces();
    [GeneratedRegex("^[0-9]+$")] private static partial Regex Digits();
    [GeneratedRegex("[^A-Za-z0-9._-]+")] private static partial Regex Unsafe();
    [GeneratedRegex("^[-.]+|[-.]+$")] private static partial Regex Edges();

    private static string Text(JsonNode? node) => HostAdminProtocol.Text(node);
    private static JsonObject Obj(JsonNode? node) => node as JsonObject ?? [];
    private static bool Is(JsonNode? node, JsonValueKind kind) => node is JsonValue value && value.GetValueKind() == kind;
    private static long? Int(JsonNode? node) => StateJson.Number(node) is { } n && n == Math.Floor(n) && Math.Abs(n) <= 9007199254740991 ? (long)n : null;
    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);
    public static bool IsBackupId(JsonNode? value) => value is JsonValue v && v.TryGetValue<string>(out var s) && IdPattern().IsMatch(s);
    public static bool IsBackupId(string? value) => value is not null && IdPattern().IsMatch(value);
    private static string Clean(JsonNode? value, int max)
    {
        var s = StateJson.Trim(Spaces().Replace(Controls().Replace(value is null ? "" : StateJson.String(value), " "), " "));
        return s.Length > max ? s[..(max - 1)] + "…" : s;
    }
    public static string FailureText(JsonNode? failure)
    {
        var f = Obj(failure); var reason = Text(f["reason"]); var detail = Clean(f["detail"], 200);
        var text = Reasons.TryGetValue(reason, out var known) ? known : reason.Length > 0 ? Clean(JsonValue.Create(reason), 60) : "an unknown error";
        return detail.Length > 0 ? $"{text} ({detail})" : text;
    }
    private static JsonObject Row(JsonObject b, DateTimeOffset now)
    {
        var pinned = DateTimeOffset.TryParse(Text(b["pinnedUntil"]), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var until) && until > now;
        var trigger = Triggers.TryGetValue(Text(b["trigger"]), out var label) ? label : Clean(b["trigger"], 40) is { Length: > 0 } raw ? raw : "unknown";
        return new() { ["id"] = Text(b["id"]), ["when"] = HostAdminProtocol.FormatWhen(b["createdAt"]), ["trigger"] = trigger, ["size"] = HostAdminViews.Bytes(b["sizeBytes"]),
            ["pinned"] = pinned, ["pinnedUntil"] = pinned ? HostAdminProtocol.FormatWhen(b["pinnedUntil"]) : "" };
    }
    // This PC's key as the panel shows it, from pc-key.json (§3.2).
    public static JsonObject KeyView(JsonNode? key)
    {
        var k = Obj(key); var fingerprint = Text(k["fingerprint"]).ToLowerInvariant();
        if (!FingerprintPattern().IsMatch(fingerprint)) return new() { ["present"] = false, ["exported"] = false, ["label"] = "", ["fingerprint"] = "" };
        return new() { ["present"] = true, ["exported"] = Text(k["exportedAt"]).Length > 0, ["label"] = Clean(k["label"], 64), ["fingerprint"] = fingerprint[..16] };
    }
    public static JsonObject? Override(JsonNode? settings)
    {
        if (settings is not JsonObject s) return null;
        var host = Obj(s["host"]); var maxKeep = Int(host["maxKeep"]);
        return new() { ["intervalHours"] = Int(s["intervalHours"]) is { } interval ? N(interval) : "", ["keep"] = Int(s["keep"]) is { } keep ? N(keep) : "",
            ["hostIntervalHours"] = Int(host["intervalHours"]), ["hostKeep"] = Int(host["keep"]), ["maxKeep"] = maxKeep is >= 1 ? maxKeep : MaxKeep };
    }
    // The module's model, or null to hide it: a local instance gets the file and key buttons only, a remote one on a
    // host without the feature nothing. input: { remote, supported, status, list, settings, key, problem }.
    public static JsonObject? PanelModel(JsonObject input, DateTimeOffset now)
    {
        var key = KeyView(input["key"]);
        var keyWarning = StateJson.Boolean(key["present"]) == true && StateJson.Boolean(key["exported"]) != true
            ? "This PC's backup key is not exported yet. Without an exported copy, a lost or reinstalled PC cannot open these backups." : "";
        if (!Is(input["remote"], JsonValueKind.True))
            return new() { ["remote"] = false, ["enabled"] = false, ["statusLine"] = "", ["warning"] = "", ["warningReason"] = "", ["pending"] = false,
                ["items"] = new JsonArray(), ["override"] = null, ["key"] = key, ["keyWarning"] = keyWarning, ["problem"] = "" };
        if (!Is(input["supported"], JsonValueKind.True)) return null;
        var status = input["status"] as JsonObject; var settings = input["settings"] as JsonObject;
        var effective = Obj(settings?["effective"]); var parts = new List<string>(); var enabled = true;
        if (status is not null)
        {
            enabled = !Is(status["enabled"], JsonValueKind.False);
            if (!enabled) parts.Add("Backups are turned off on this host");
            else if (Int(effective["intervalHours"]) == 0) parts.Add("Scheduled backups are off for this VM");
            parts.Add(Text(status["lastSuccessAt"]).Length > 0 ? "Last backup " + HostAdminProtocol.FormatWhen(status["lastSuccessAt"]) : "No backup yet");
            if (Int(status["count"]) is { } count && count > 0) parts.Add($"{N(count)} stored ({HostAdminViews.Bytes(status["totalBytes"])})");
            if (enabled && Text(status["nextDueAt"]).Length > 0) parts.Add("next " + HostAdminProtocol.FormatWhen(status["nextDueAt"]));
            if (Is(status["requestPending"], JsonValueKind.True)) parts.Add("backup requested, it starts at the VM's next check-in");
        }
        var failing = status is null ? "" : Text(status["failingSince"]); var failure = Obj(status?["lastFailure"]);
        var items = new JsonArray((Obj(input["list"])["backups"] as JsonArray ?? []).OfType<JsonObject>().Where(b => IsBackupId(b["id"])).Select(b => (JsonNode)Row(b, now)).ToArray());
        return new()
        {
            ["remote"] = true, ["enabled"] = enabled, ["statusLine"] = string.Join(" · ", parts),
            ["warning"] = failing.Length > 0 ? $"Backups failing since {HostAdminProtocol.FormatWhen(JsonValue.Create(failing))}: {FailureText(failure)}." : "",
            ["warningReason"] = failing.Length > 0 ? Text(failure["reason"]) : "", ["pending"] = status is not null && Is(status["requestPending"], JsonValueKind.True),
            ["items"] = items, ["override"] = Override(settings), ["key"] = key, ["keyWarning"] = keyWarning, ["problem"] = Clean(input["problem"], 300),
        };
    }
    private static (bool Ok, long? Value) OverrideValue(JsonNode? value, long min, long max)
    {
        if (value is null) return (true, null);
        if (Is(value, JsonValueKind.Number)) return Int(value) is { } n && n >= min && n <= max && StateJson.Number(value) == n ? (true, n) : (false, null);
        if (StateJson.Text(value) is not { } text) return (false, null);
        var s = StateJson.Trim(text); if (s.Length == 0) return (true, null);
        if (!Digits().IsMatch(s)) return (false, null);
        var parsed = double.Parse(s, CultureInfo.InvariantCulture);
        return parsed >= min && parsed <= max ? (true, (long)parsed) : (false, null);
    }
    // The override form -> the PUT …/backups/settings body; maxKeep is the host's ceiling (50 when unknown).
    public static JsonObject ParseOverride(JsonNode? form, JsonNode? maxKeep = null)
    {
        var f = Obj(form); var ceiling = Int(maxKeep) is { } m && m >= 1 && Is(maxKeep, JsonValueKind.Number) ? m : MaxKeep; var problems = new JsonArray();
        var interval = OverrideValue(f["intervalHours"], 0, MaxIntervalHours);
        if (!interval.Ok) problems.Add(new JsonObject { ["field"] = "intervalHours", ["reason"] = $"hours between backups: a whole number from 0 to {MaxIntervalHours}, or empty for the host default" });
        var keep = OverrideValue(f["keep"], 1, ceiling);
        if (!keep.Ok) problems.Add(new JsonObject { ["field"] = "keep", ["reason"] = $"backups kept: a whole number from 1 to {N(ceiling)}, or empty for the host default" });
        return problems.Count > 0 ? new() { ["ok"] = false, ["body"] = null, ["problems"] = problems }
            : new() { ["ok"] = true, ["body"] = new JsonObject { ["intervalHours"] = interval.Value, ["keep"] = keep.Value }, ["problems"] = problems };
    }
    // Once per failure streak (§3.7): remember the streak's failingSince (null once it ended); notify only for a new one.
    public static JsonObject Warning(string instance, JsonNode? status, JsonNode? warned)
    {
        var s = Obj(status); var since = Text(s["failingSince"]);
        if (since.Length == 0) return new() { ["notify"] = false, ["remember"] = null, ["title"] = "", ["body"] = "" };
        if (since == Text(warned)) return new() { ["notify"] = false, ["remember"] = since, ["title"] = "", ["body"] = "" };
        return new() { ["notify"] = true, ["remember"] = since, ["title"] = "Config backups failing",
            ["body"] = $"Backups of “{Clean(JsonValue.Create(instance), 64)}” are failing since {HostAdminProtocol.FormatWhen(JsonValue.Create(since))}: {FailureText(s["lastFailure"])}. Open its control panel for details." };
    }
    // <instance>-<UTC timestamp>.cbk, safe as a Windows file name.
    public static string FileName(string? instance, DateTimeOffset now)
    {
        var name = Edges().Replace(Unsafe().Replace(StateJson.Trim(instance ?? ""), "-"), ""); if (name.Length == 0) name = "construct";
        return name + "-" + now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".cbk";
    }
    public static string? PcKeyPath(string? localAppData) => string.IsNullOrEmpty(localAppData) ? null : Path.Combine(localAppData, "The-Construct", "backup", "pc-key.json");
}
