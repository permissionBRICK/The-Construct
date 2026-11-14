using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Runtime;
using static Construct.Companion.Core.Runtime.RuntimeJson;

namespace Construct.Companion.Core.Forwards;

public static class ForwardProtocol
{
    public const string SpoolDirectory = "/etc/construct/forwards";
    public static bool IsSafeId(string? id) => !string.IsNullOrEmpty(id) && id.Length <= 128 && !id.Contains("..", StringComparison.Ordinal)
        && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
    public static bool IsV1Document(string id, JsonObject? doc) => IsSafeId(id) && doc?["v"] is JsonValue v
        && v.TryGetValue<int>(out var version) && version == 1 && doc["id"] is JsonValue i
        && i.TryGetValue<string>(out var name) && name == id;
    public static JsonObject? ParseRequest(string id, JsonObject? doc)
    {
        if (!IsV1Document(id, doc) || Port(doc!["vmPort"]) is not int port) return null;
        var target = doc["target"] is null || doc.Str("target") == "" ? "client" : doc.Str("target").ToLowerInvariant();
        return target != "client" ? null : new() { ["id"] = id, ["vmPort"] = port, ["label"] = Sanitize(doc.Str("label"), 100), ["target"] = target };
    }
    public static JsonObject? ParseAck(string id, JsonObject? doc)
    {
        if (!IsV1Document(id, doc)) return null;
        var status = doc.Str("status").ToLowerInvariant();
        return status is not ("open" or "error") ? null : new() { ["id"] = id, ["status"] = status,
            ["localPort"] = Port(doc!["localPort"]), ["hostLabel"] = ForwardHost.Normalize(doc.Str("hostLabel")), ["message"] = Sanitize(doc.Str("message")) };
    }
    public static string? ParseClose(string id, JsonObject? doc) => IsV1Document(id, doc) ? id : null;
    public static JsonObject AckDocument(string id, JsonObject ack)
    {
        var doc = new JsonObject { ["v"] = 1, ["id"] = id, ["status"] = ack.Str("status") == "error" ? "error" : "open" };
        if (ack["localPort"] is not null) doc["localPort"] = ack["localPort"]!.DeepClone();
        var label = ForwardHost.Normalize(ack.Str("hostLabel"));
        if (label.Length != 0) doc["hostLabel"] = label;
        doc["message"] = Sanitize(ack.Str("message"));
        return doc;
    }
    public static JsonObject EmptyView(string owner = "unknown") => new() { ["owner"] = owner, ["requests"] = new JsonArray(), ["acks"] = new JsonArray(), ["closes"] = new JsonArray() };
    public static JsonObject ParseDump(string stdout)
    {
        var result = EmptyView();
        foreach (var raw in stdout.Split('\n'))
        {
            var line = Trim(raw);
            if (line.StartsWith("OWNER=", StringComparison.Ordinal))
            { var owner = Trim(line[6..]); result["owner"] = owner is "self" or "other" or "absent" ? owner : "unknown"; continue; }
            var parts = line.Split(' ');
            if (parts.Length != 3 || !IsSafeId(parts[1])) continue;
            JsonObject? doc;
            try { doc = Parse(Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]))); } catch (FormatException) { continue; }
            if (parts[0] == "R" && ParseRequest(parts[1], doc) is { } req) result.Array("requests").Add(req);
            if (parts[0] == "A" && ParseAck(parts[1], doc) is { } ack) result.Array("acks").Add(ack);
            if (parts[0] == "C" && ParseClose(parts[1], doc) is { } close) result.Array("closes").Add(close);
        }
        return result;
    }
    public static (int Base, int Count) InstancePortSlice(string? name)
    {
        var n = Trim(name ?? "");
        if (n is "" or "agent-vm") return (18800, 16);
        uint hash = 0x811c9dc5;
        foreach (var c in n) hash = unchecked((hash ^ (uint)(c & 255)) * 0x01000193);
        return (18800 + (1 + (int)(hash % 31)) * 16, 16);
    }
    public static int[] PortCandidates(int? vmPort, int? prefer = null, IEnumerable<int>? taken = null, int portBase = 18800, int count = 16)
    {
        if (portBase is < 1 or > 65535) portBase = 18800;
        if (count is < 1 or > 64) count = 16;
        var busy = (taken ?? []).ToHashSet();
        return new int?[] { prefer, vmPort }.Concat(Enumerable.Range(portBase, Math.Min(count, 65536 - portBase)).Select(p => (int?)p))
            .Where(p => p is > 0 and <= 65535 && !busy.Contains(p.Value)).Select(p => p!.Value).Distinct().ToArray();
    }
    public static int ReconnectDelayMs(double attempt) => (int)Math.Min(60000, 2000 * Math.Pow(2,
        (double.IsFinite(attempt) && attempt > 0 ? Math.Min(Math.Floor(attempt), 10) : 1) - 1));
    public static (string[] Lines, string Remainder) SplitLines(string? buffered, string? chunk, int maxRest = 8192)
    {
        var parts = ((buffered ?? "") + (chunk ?? "")).Split('\n');
        return (parts[..^1].Select(Trim).Where(l => l is not ("" or "#")).ToArray(), parts[^1].Length > maxRest ? "" : parts[^1]);
    }
    public static string CapabilityScript(string dir = SpoolDirectory) => GuestScripts.Render("forwards-capability", new Dictionary<string, string> { ["dir"] = ShellQuote.Single(dir) });
    public static bool ParseCapability(string stdout) => stdout.Split('\n').Any(l => Trim(l) == "SPOOL=1");
    public static string ReconcileScript(string claimId, string dir = SpoolDirectory) => GuestScripts.Render("forwards-reconcile", new Dictionary<string, string>
    { ["dir"] = ShellQuote.Single(dir), ["me"] = ShellQuote.Single(string.Concat(claimId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))), ["ttl"] = "90", ["lockTtl"] = "60" });
    public static string WatchScript(string dir = SpoolDirectory) => GuestScripts.Render("forwards-watch", new Dictionary<string, string>
    { ["dir"] = ShellQuote.Single(dir), ["heartbeat"] = "60", ["fallback"] = "30" });
    public static string AckScript(string id, JsonObject doc, string dir = SpoolDirectory)
    {
        if (!IsSafeId(id)) throw new ArgumentException("Unusable forward id.");
        return GuestScripts.Render("forwards-ack", new Dictionary<string, string> { ["dir"] = ShellQuote.Single(dir + "/acks"), ["id"] = id,
            ["b64"] = ShellQuote.Single(Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n"))) });
    }
    public static string RemoveScript(IEnumerable<(string Sub, string Id)> entries, string dir = SpoolDirectory)
    {
        var paths = entries.Where(e => e.Sub is "requests" or "acks" or "close" && IsSafeId(e.Id)).Select(e => ShellQuote.Single($"{dir}/{e.Sub}/{e.Id}.json")).ToArray();
        return paths.Length == 0 ? "" : GuestScripts.Render("forwards-remove", new Dictionary<string, string> { ["paths"] = string.Join(' ', paths) });
    }
    public static string ReleaseScript(string claimId, string dir = SpoolDirectory) => GuestScripts.Render("forwards-release", new Dictionary<string, string>
    { ["own"] = ShellQuote.Single(dir + "/.owner"), ["me"] = ShellQuote.Single(claimId) });
}
