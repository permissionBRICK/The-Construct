using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Runtime;
using static Construct.Companion.Core.Runtime.RuntimeJson;

namespace Construct.Companion.Core.Notifications;

public static class NotificationProtocol
{
    public const string SpoolDirectory = "/run/construct/notify";
    public static string NormalizeLevel(string? value) => Trim(value ?? "").ToLowerInvariant() switch
    { "warn" or "warning" => "warning", "err" or "critical" or "error" => "error", _ => "info" };
    public static JsonArray ParseEntries(string stdout)
    {
        var entries = new JsonArray();
        foreach (var line in stdout.Split('\n'))
        {
            var raw = Parse(line); if (raw is null) continue;
            var body = Sanitize(raw.Str("body"), 400, true); if (body.Length == 0) continue;
            var title = Sanitize(raw.Str("title"), 100, true);
            var ts = double.TryParse(raw.Str("ts"), System.Globalization.CultureInfo.InvariantCulture, out var t) && double.IsFinite(t) && t > 0 ? t : 0;
            entries.Add(new JsonObject { ["ts"] = ts, ["level"] = NormalizeLevel(raw.Str("level")), ["title"] = title.Length > 0 ? title : "The Construct",
                ["body"] = body, ["source"] = Sanitize(raw.Str("source"), 60, true) });
        }
        return entries;
    }
    public static JsonObject SelectDeliverable(JsonArray entries, double now, double ttlMs = 3600000, int max = 5)
    {
        var sorted = entries.OfType<JsonObject>().OrderBy(e => e["ts"]?.GetValue<double>() ?? 0).ToArray();
        var fresh = sorted.Where(e => (e["ts"]?.GetValue<double>() ?? 0) == 0 || now - e["ts"]!.GetValue<double>() <= ttlMs).ToArray();
        return new() { ["deliver"] = List(fresh.Take(max)), ["stale"] = sorted.Length - fresh.Length, ["extra"] = Math.Max(0, fresh.Length - max) };
    }
    public static string XmlEscape(string value) => value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal).Replace("'", "&apos;", StringComparison.Ordinal);
    public static ToastDocument Toast(JsonObject entry, string launchUri)
    {
        var level = NormalizeLevel(entry.Str("level")); var title = Sanitize(entry.Str("title"), 100, true);
        if (title.Length == 0) title = "The Construct";
        var body = Sanitize(entry.Str("body"), 400, true); var source = Sanitize(entry.Str("source"), 60, true);
        return new($"<toast activationType=\"protocol\" launch=\"{XmlEscape(launchUri)}\"{(level == "info" ? "" : " duration=\"long\"")}>"
            + "<visual><binding template=\"ToastGeneric\">" + $"<text>{XmlEscape(title)}</text><text>{XmlEscape(body)}</text>"
            + (source.Length > 0 ? $"<text placement=\"attribution\">{XmlEscape(source)}</text>" : "") + "</binding></visual></toast>");
    }
    public static string ClickUri(string instance) => "construct://open?instance=" + Uri.EscapeDataString(instance);
    public static string ClaimScript(string dir = SpoolDirectory) => GuestScripts.Render("notify-claim", Values(dir));
    public static string WatchScript(string dir = SpoolDirectory)
    { var values = Values(dir); values["heartbeat"] = "60"; values["fallback"] = "3"; return GuestScripts.Render("notify-watch", values); }
    private static Dictionary<string, string> Values(string dir) => new() { ["dir"] = ShellQuote.Single(dir), ["claim"] = GuestScripts.Render("notify-claim-function") };
}
