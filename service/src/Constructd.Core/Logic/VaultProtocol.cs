using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>One validated <c>construct secret</c> call from a guest. <see cref="Secret"/> is the decoded value of an <c>add</c>.</summary>
public sealed record VaultRequest(string Id, string Op, IReadOnlyList<string> Names, int? Uses, int? Ttl, bool All, bool Replace,
    string Reason, string Description, string Username, string? Secret, DateTimeOffset? Deadline, string Source)
{
    public override string ToString() => $"VaultRequest {{ Id = {Id}, Op = {Op} }}";
}

/// <summary>
/// The guest wire contract of the hosted vault (docs/plans/key-vault-hosted.md): the request and
/// response documents of the guest spool contract (docs/key-vault.md), the scan patterns and the
/// agent-log rules. Everything that crosses the VM boundary is validated here.
///
/// Keep in sync with companion/src/Construct.Companion.Core/Vault/VaultProtocol.cs: the limits, the
/// request validation, <see cref="Patterns"/>/<see cref="JsonEscape"/> and the agent-log expression
/// are copies of the Companion's, and test/fixtures/vault-patterns.json pins both against one record.
/// </summary>
public static partial class VaultProtocol
{
    public const int MaxRequestBytes = 65536, MaxSecretBytes = 32768, MaxText = 300, MaxUses = 1000, MinTtl = 60, MaxTtl = 86400;
    // Shorter values would match all over a VM; such secrets are reported as not scannable instead.
    public const int MinPatternLength = 6;
    public static readonly string[] Operations = ["list", "status", "request", "get", "release", "add", "delete"];
    public static readonly string[] FileActions = ["keep", "redact", "delete"];

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>
    /// Validates a request document. The guest may name its own <c>id</c>; otherwise the response
    /// documents carry <paramref name="fallbackId"/>. On failure the error is the "invalid" message.
    /// </summary>
    public static (VaultRequest? Request, string Id, string? Error) ParseRequest(JsonObject raw, string fallbackId)
    {
        var id = IsValidId(raw.Str("id")) ? raw.Str("id") : fallbackId;
        (VaultRequest?, string, string?) Invalid(string error) => (null, id, error);
        if (raw["v"] is not null && raw.Str("v") != "1") return Invalid("Unsupported request version; update The Construct on this VM.");
        var op = raw.Str("op"); if (!Operations.Contains(op)) return Invalid("Unknown operation.");
        var names = new List<string>();
        foreach (var node in raw["names"] as JsonArray ?? [])
        {
            if (node is not JsonValue v || !v.TryGetValue<string>(out var name) || !IsValidName(name)) return Invalid("Secret names use letters, digits, '.', '_' and '-' (at most 64, starting with a letter or digit).");
            if (!names.Contains(name)) names.Add(name);
        }
        var all = raw.True("all");
        var count = op switch { "request" => names.Count is >= 1 and <= 20, "get" or "add" or "delete" => names.Count == 1, "release" => all || names.Count >= 1, _ => true };
        if (!count) return Invalid(op == "request" ? "Name between 1 and 20 secrets." : op == "release" ? "Name a secret or pass --all." : "Name exactly one secret.");
        if (!Bounded(raw["uses"], 1, MaxUses, out var uses)) return Invalid($"--uses must be between 1 and {MaxUses}.");
        if (!Bounded(raw["ttl"], MinTtl, MaxTtl, out var ttl)) return Invalid("--for must be between 1 minute and 24 hours.");
        string? value = null;
        if (op == "add")
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(raw.Str("secret")); }
            catch (FormatException) { return Invalid("The secret is not valid base64."); }
            if (bytes.Length == 0) return Invalid("The secret is empty.");
            if (bytes.Length > MaxSecretBytes) return Invalid($"The secret is larger than {MaxSecretBytes / 1024} KiB.");
            try { value = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { return Invalid("The secret must be UTF-8 text."); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
            if (Sanitize(raw.Str("description"), MaxText).Length == 0) return Invalid("Describe the secret with --description.");
        }
        DateTimeOffset? deadline = raw["deadline"] is JsonValue d && d.TryGetValue<double>(out var ms) && double.IsFinite(ms) && ms > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Min(ms, 253402300799999)) : null;
        return (new(id, op, names, uses, ttl, all, raw.True("replace"), Sanitize(raw.Str("reason"), MaxText), Sanitize(raw.Str("description"), MaxText),
            Sanitize(raw.Str("username"), MaxText), value, deadline, Sanitize(raw.Str("source"), 60)), id, null);
    }
    private static bool Bounded(JsonNode? node, int min, int max, out int? value)
    {
        value = null;
        if (node is null) return true;
        if (node is not JsonValue v || !v.TryGetValue<double>(out var d) || d != Math.Truncate(d) || d < min || d > max) return false;
        value = (int)d; return true;
    }

    public static JsonObject Response(string id, string status, string message) => new() { ["v"] = 1, ["id"] = id, ["status"] = status, ["message"] = message };
    public static JsonObject Lease(VaultLease lease) => new() { ["usesLeft"] = lease.UsesLeft, ["expiresAt"] = lease.ExpiresAt.ToUnixTimeMilliseconds() };

    // Variants a secret takes in logs: raw, JSON-escaped (transcripts), ASCII-escaped JSON (Python's
    // default), URL-encoded (credentials in URLs) and the exact Basic-auth payload. A multi-line value
    // (a private key) is searched line by line, skipping PEM armor lines that every key shares.
    public static IReadOnlyList<string> Patterns(string value, string username = "")
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        var parts = normalized.Contains('\n', StringComparison.Ordinal)
            ? normalized.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Trim().Length >= 16 && !Armor().IsMatch(l.Trim())).ToArray()
            : [value];
        var patterns = new List<string>();
        foreach (var part in parts) patterns.AddRange([part, JsonEscape(part, false), JsonEscape(part, true), Uri.EscapeDataString(part)]);
        if (username.Length > 0 && parts.Length == 1 && parts[0] == value) patterns.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + value)));
        return patterns.Where(p => Encoding.UTF8.GetByteCount(p) >= MinPatternLength && !p.Contains('\n', StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
    }
    private static string JsonEscape(string text, bool ascii)
    {
        var builder = new StringBuilder(text.Length + 8);
        foreach (var c in text)
            builder.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", '\b' => "\\b", '\f' => "\\f",
                < ' ' => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                > '~' when ascii => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString()
            });
        return builder.ToString();
    }

    // Agent transcripts, session stores and harness logs are scrubbed without asking; every other
    // hit is the user's decision. Keep in sync with docs/key-vault.md.
    public static bool IsAgentLog(string path) => AgentLog().IsMatch(path);
    // SQLite keeps recent pages in side files; the scrub works on the database they belong to.
    public static string DatabaseFor(string path) => SqliteSide().Replace(path, "");

    public static string EncodePath(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
    /// <summary>The display/classification form of a guest path; null unless absolute.</summary>
    public static string? DecodePath(string? b64)
    {
        if (string.IsNullOrEmpty(b64)) return null;
        try { var path = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); return path.StartsWith('/') ? path : null; }
        catch (FormatException) { return null; }
    }
    public static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    // ---- JSON boundary helpers (the Companion's RuntimeJson rules) ---------------------------------

    public static string Text(JsonNode? node) => node switch
    {
        null => "", JsonValue v when v.TryGetValue<string>(out var s) => s,
        JsonValue v when v.TryGetValue<bool>(out var b) => b ? "true" : "false",
        JsonArray a => string.Join(',', a.Select(Text)), JsonObject => "[object Object]", _ => node.ToJsonString()
    };
    public static string Str(this JsonNode? node, string key) => Text(node is JsonObject o ? o[key] : null);
    public static bool True(this JsonNode? node, string key) => node is JsonObject o && o[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    public static string Sanitize(string? text, int max = 300, bool ellipsis = false)
    {
        var clean = Regex.Replace(Regex.Replace(text ?? "", "[\\u0000-\\u001F\\u007F\\u2028\\u2029]", " "),
            "[\\u0009-\\u000D\\u0020\\u00A0\\u1680\\u2000-\\u200A\\u2028\\u2029\\u202F\\u205F\\u3000\\uFEFF]+", " ").Trim(' ');
        return clean.Length <= max ? clean : clean[..(ellipsis ? max - 1 : max)].TrimEnd(' ') + (ellipsis ? "…" : "");
    }
    public static JsonObject? Parse(string value)
    { try { return JsonNode.Parse(value) as JsonObject; } catch (JsonException) { return null; } }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NamePattern();
    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex IdPattern();
    [GeneratedRegex("^-----[A-Z0-9 ]+-----$")]
    private static partial Regex Armor();
    [GeneratedRegex("-(wal|shm|journal)$")]
    private static partial Regex SqliteSide();
    [GeneratedRegex(@"^(?:(?:/root|/home/[^/]+)/(?:\.claude/(?:projects|todos|shell-snapshots|debug|file-history|paste-cache|session-env|sessions)/|\.claude/history\.jsonl$|\.codex/(?:sessions|archived_sessions|log)/|\.codex/history\.jsonl$|\.codex/logs_[^/]*$|\.local/share/opencode/(?:storage|log)/|\.local/share/opencode/opencode\.db[^/]*$|\.t3/userdata/logs/|\.t3/userdata/state\.sqlite[^/]*$)|/tmp/claude-)")]
    private static partial Regex AgentLog();
}
