using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Construct.Companion.Core.Runtime.RuntimeJson;
namespace Construct.Companion.Core.Vault;

// One entry as hosts store and exchange it: values only inside the K-encrypted payload; a tombstone has none.
public sealed record VaultWireEntry(string Name, string Description, bool HasUsername, string? Payload, long UpdatedAt, string UpdatedBy, bool Deleted)
{
    public override string ToString() => $"VaultWireEntry {{ Name = {Name}, UpdatedAt = {UpdatedAt} }}";
}
public sealed record VaultHostSnapshot(long Revision, string Mode, string? KeyCheck, IReadOnlyList<string> UnlockedVms, IReadOnlyList<VaultWireEntry> Entries);
public sealed record VaultHostApproval(string Id, string Vm, string Op, string Title, string Message, string Action, DateTimeOffset? Deadline);
public sealed record VaultHostFile(string Id, string Vm, string Path, IReadOnlyList<string> Names, string Type, long Size);
public sealed record VaultHostLease(string Id, string Vm, string Name, int? UsesLeft, DateTimeOffset ExpiresAt, string Reason, string Origin);
public sealed record VaultDevice(string Id, string Label, DateTimeOffset? CreatedAt, DateTimeOffset? LastUsedAt);
public sealed record VaultHostEvent(DateTimeOffset At, string Vm, string Text, bool Warning);

// The host service's vault routes as data (docs/plans/key-vault-hosted.md, "Shapes"). Hosts are trusted
// with names and descriptions, but every field is still bounded before it reaches a dialog or the document.
public static partial class VaultSync
{
    public const string Available = "available", Locked = "locked";
    public static readonly TimeSpan TombstoneLifetime = TimeSpan.FromDays(30);

    // Last writer wins: the larger updatedAt, on a tie the lexically larger updatedBy.
    public static bool Newer(long at, string by, long otherAt, string otherBy) => at > otherAt || at == otherAt && string.CompareOrdinal(by, otherBy) > 0;
    public static long Ms(DateTimeOffset at) => at.ToUnixTimeMilliseconds();
    public static DateTimeOffset Time(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(Math.Clamp(ms, 0, 253402300799999));

    // Null when the reply is not a vault document (a host service without the vault answers 404 or something else).
    public static VaultHostSnapshot? ParseEntries(JsonNode? body)
    {
        if (body is not JsonObject root || root["entries"] is not JsonArray entries) return null;
        var parsed = new List<VaultWireEntry>();
        foreach (var node in entries.OfType<JsonObject>())
        {
            var name = node.Str("name");
            if (!VaultProtocol.IsValidName(name) || Long(node["updatedAt"]) is not { } at || node.Str("updatedBy") is not { Length: > 0 and <= 200 } by) continue;
            var deleted = node.True("deleted");
            var payload = deleted ? null : node.Str("payload");
            if (!deleted && string.IsNullOrEmpty(payload)) continue;
            parsed.Add(new(name, Sanitize(node.Str("description"), VaultProtocol.MaxText), node.True("hasUsername"), payload, at, by, deleted));
        }
        var check = root.Str("keyCheck");
        return new(Long(root["revision"]) ?? 0, Mode(root.Str("mode")), check.Length == 0 ? null : check,
            root.Array("unlockedVms").Select(Text).Where(v => v.Length > 0).ToArray(), parsed);
    }
    public static string Mode(string? mode) => mode == Locked ? Locked : mode == Available ? Available : "";
    public static JsonObject ToJson(VaultWireEntry entry)
    {
        var json = new JsonObject { ["name"] = entry.Name, ["description"] = entry.Description, ["hasUsername"] = entry.HasUsername };
        if (!entry.Deleted) json["payload"] = entry.Payload;
        json["updatedAt"] = entry.UpdatedAt; json["updatedBy"] = entry.UpdatedBy; json["deleted"] = entry.Deleted;
        return json;
    }

    public static IReadOnlyList<VaultHostApproval> ParseApprovals(JsonNode? body) => (body as JsonObject).Array("approvals").OfType<JsonObject>()
        .Where(a => IsId(a.Str("id")))
        .Select(a => new VaultHostApproval(a.Str("id"), Sanitize(a.Str("vm"), 64), Sanitize(a.Str("op"), 20), Sanitize(a.Str("title"), 120) is { Length: > 0 } t ? t : "Key vault — access request",
            Paragraphs(a.Str("message"), 4000), Sanitize(a.Str("action"), 40) is { Length: > 0 } act ? act : "Approve", Long(a["deadline"]) is { } d ? Time(d) : null)).ToArray();
    public static IReadOnlyList<VaultHostFile> ParseFiles(JsonNode? body) => (body as JsonObject).Array("files").OfType<JsonObject>()
        .Where(f => IsId(f.Str("id")) && f.Str("path").StartsWith('/'))
        .Select(f => new VaultHostFile(f.Str("id"), Sanitize(f.Str("vm"), 64), Sanitize(f.Str("path"), 1024), f.Array("names").Select(Text).Where(VaultProtocol.IsValidName).ToArray(),
            f.Str("type") is "sqlite" or "binary" ? f.Str("type") : "text", Long(f["size"]) ?? 0)).ToArray();
    public static IReadOnlyList<VaultHostLease> ParseLeases(JsonNode? body) => (body as JsonObject).Array("leases").OfType<JsonObject>()
        .Where(l => IsId(l.Str("id")) && VaultProtocol.IsValidName(l.Str("name")))
        .Select(l => new VaultHostLease(l.Str("id"), Sanitize(l.Str("vm"), 64), l.Str("name"), Long(l["usesLeft"]) is { } u ? (int)Math.Clamp(u, 0, int.MaxValue) : null,
            Time(Long(l["expiresAt"]) ?? 0), Sanitize(l.Str("reason"), VaultProtocol.MaxText), Sanitize(l.Str("origin"), 20))).ToArray();
    public static IReadOnlyList<VaultDevice> ParseDevices(JsonNode? body) => (body as JsonObject).Array("devices").OfType<JsonObject>()
        .Where(d => IsId(d.Str("id")))
        .Select(d => new VaultDevice(d.Str("id"), Sanitize(d.Str("label"), 60), Long(d["createdAt"]) is { } c ? Time(c) : null, Long(d["lastUsedAt"]) is { } u ? Time(u) : null)).ToArray();
    public static IReadOnlyList<VaultHostEvent> ParseActivity(JsonNode? body) => (body as JsonObject).Array("events").OfType<JsonObject>()
        .Where(e => Long(e["at"]) is not null && e.Str("text").Length > 0)
        .Select(e => new VaultHostEvent(Time(Long(e["at"])!.Value), Sanitize(e.Str("vm"), 64), Sanitize(e.Str("text"), 600), e.True("warning"))).ToArray();

    // The phone page: both tokens ride in the fragment, so neither reaches a server log.
    public static string PairingUrl(string webBase, string token, string? next)
    {
        var url = webBase.TrimEnd('/') + "/vault/pair#token=" + Uri.EscapeDataString(token);
        return string.IsNullOrEmpty(next) ? url : url + "&next=" + Uri.EscapeDataString(next);
    }
    public static bool IsDeviceToken(string? token) => token is not null && DeviceToken().IsMatch(token);
    // The approval page's base for the QR code from the pairing reply, and whether it is the host service's own
    // address (self-signed, so phones warn): the host says so in webUrlSource; an older host does not, and then
    // its own address is what the Companion talks to. A non-https answer falls back to that address too.
    public static (string Web, bool ServiceDefault) PairingWebBase(JsonNode? reply, string serviceUrl)
    {
        var body = reply as JsonObject;
        var w = body.Str("webUrl");
        if (w.Length == 0 || !Uri.TryCreate(w, UriKind.Absolute, out var uri) || uri.Scheme != "https") return (serviceUrl, true);
        var source = body.Str("webUrlSource");
        return (w, source == "default" || source.Length == 0 && SameOrigin(w, serviceUrl));
    }
    // Scheme, host and port: what a browser keeps apart.
    public static bool SameOrigin(string a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var x) && Uri.TryCreate(b, UriKind.Absolute, out var y) &&
        string.Equals(x.GetLeftPart(UriPartial.Authority), y.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
    public static string Origin(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.GetLeftPart(UriPartial.Authority) : url;
    public static string SelfSignedNote(string web) =>
        $"Phones open the approval page at the host service's own address, {Origin(web)}, whose certificate is self-signed, so the phone warns about it. " +
        "A host administrator can set Host Administration → Configuration → Key vault → Approval page address (vault.webUrl) to a reverse proxy with a trusted certificate.";
    public const string SameOriginError = "T3 Code and the key vault's approval page use the same address. T3 Code is served from inside the VM, so it must not share an origin (scheme, host and port) with the page that keeps the phone's approval token: give the T3 Code proxy address or the approval page address its own host name or port.";
    // The link a phone can reach: the address the user reaches T3 Code at through their own proxy when the VM
    // records one (T3CODE_PROXY_URL), else the gateway-forwarded one; never the VM-internal address.
    public static (string? Url, string? Error) PhoneT3Link(int code, string stdout)
    {
        if (code == 7) return (null, "T3 Code's port forward is not ready. Keep the Construct client connected and retry.");
        var links = code == 0 ? State.T3Code.ExtractPairLinks(stdout) : [];
        var link = links.FirstOrDefault(l => l.Kind == "proxy") ?? links.FirstOrDefault(l => l.Kind == "forwarded");
        return link is null ? (null, "T3 Code did not return a pairing link the phone can reach (no proxy or forwarded link).") : (link.PairUrl, null);
    }

    private static bool IsId(string id) => id.Length is > 0 and <= 128 && !id.Any(char.IsControl) && !id.Contains('/', StringComparison.Ordinal);
    private static long? Long(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var d) && double.IsFinite(d) && d == Math.Truncate(d) ? (long)Math.Clamp(d, long.MinValue, long.MaxValue)
        : node is JsonValue s && s.TryGetValue<string>(out var text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    // Keeps line breaks (approval texts are paragraphs), drops every other control character.
    private static string Paragraphs(string text, int max)
    {
        var clean = string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => Sanitize(line, max))).Trim('\n');
        return clean.Length <= max ? clean : clean[..max];
    }
    [GeneratedRegex("^[A-Za-z0-9_-]{20,200}$")]
    private static partial Regex DeviceToken();
}
