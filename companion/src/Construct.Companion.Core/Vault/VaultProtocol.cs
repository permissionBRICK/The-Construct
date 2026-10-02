using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;
using static Construct.Companion.Core.Runtime.RuntimeJson;
namespace Construct.Companion.Core.Vault;

// One `construct secret` call, validated. Value is the decoded secret of an `add`.
public sealed record VaultRequest(string Id, string Op, IReadOnlyList<string> Names, int? Uses, int? Ttl, bool All, bool Replace,
    string Reason, string Description, string Username, Secret? Value, DateTimeOffset? Deadline, string Source)
{
    public override string ToString() => $"VaultRequest {{ Id = {Id}, Op = {Op} }}";
}
// PathB64 is the guest's own byte-exact spelling of the path; Path is only for display and classification.
public sealed record VaultScanHit(string Path, string PathB64, IReadOnlyList<int> Indexes, long Size, string Type);
public sealed record VaultCleanResult(string Status, string Path, string PathB64, long Count, string Detail);

// The wire contract between bin/construct-secret.sh, the guest scripts in Vault/GuestScripts and the
// Companion (docs/key-vault.md). Everything that crosses the VM boundary is validated here.
public static partial class VaultProtocol
{
    public const string SpoolDirectory = "/run/construct/vault";
    public const int MaxRequestBytes = 65536, MaxSecretBytes = 32768, MaxText = 300, MaxUses = 1000, MinTtl = 60, MaxTtl = 86400;
    // Shorter values would match all over a VM; such secrets are reported as not scannable instead.
    public const int MinPatternLength = 6;
    public static readonly string[] Operations = ["list", "status", "request", "get", "release", "add", "delete"];
    public static readonly string[] ScanRoots = ["/root", "/home", "/tmp", "/var/tmp", "/var/log", "/etc", "/opt", "/srv"];
    public const long ScanMaxFileBytes = 256L * 1024 * 1024;

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);
    // A pending approval's id as the tray pop-out and the local API name it (VaultService makes 32 hex digits).
    public static bool IsApprovalId(string? id) => id is not null && ApprovalIdPattern().IsMatch(id);

    // (null, null, _) = not answerable (no usable id); (null, id, error) = answer "invalid".
    public static (VaultRequest? Request, string? Id, string? Error) ParseRequest(string line)
    {
        if (Encoding.UTF8.GetByteCount(line) > MaxRequestBytes || Parse(line) is not { } raw) return (null, null, null);
        var id = raw.Str("id"); if (!IsValidId(id)) return (null, null, null);
        (VaultRequest?, string?, string?) Invalid(string error) => (null, id, error);
        if (raw.Str("v") != "1") return Invalid("Unsupported request version; update The Construct on this VM.");
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
        Secret? value = null;
        if (op == "add")
        {
            byte[] bytes;
            try { bytes = Convert.FromBase64String(raw.Str("secret")); }
            catch (FormatException) { return Invalid("The secret is not valid base64."); }
            if (bytes.Length == 0) return Invalid("The secret is empty.");
            if (bytes.Length > MaxSecretBytes) return Invalid($"The secret is larger than {MaxSecretBytes / 1024} KiB.");
            try { value = new(new UTF8Encoding(false, true).GetString(bytes)); }
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
    // (a private key) only ever matches whole, with LF or CRLF line ends: keys of one type share their
    // first lines, so a single line of one would flag every other key on the VM.
    public static IReadOnlyList<string> Patterns(string value, string username = "")
    {
        var text = value.Contains('\n', StringComparison.Ordinal) ? value.Replace("\r\n", "\n", StringComparison.Ordinal).Trim() : value;
        string[] parts = text.Contains('\n', StringComparison.Ordinal) ? [text, text.Replace("\n", "\r\n", StringComparison.Ordinal)] : [text];
        var patterns = new List<string>();
        foreach (var part in parts) patterns.AddRange([part, JsonEscape(part, false), JsonEscape(part, true), Uri.EscapeDataString(part)]);
        if (username.Length > 0 && parts.Length == 1) patterns.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + text)));
        return patterns.Where(p => Encoding.UTF8.GetByteCount(p) >= MinPatternLength).Distinct(StringComparer.Ordinal).ToArray();
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

    public static (IReadOnlyList<VaultScanHit> Hits, bool Complete) ParseScan(string stdout)
    {
        var hits = new List<VaultScanHit>(); var complete = false;
        foreach (var line in stdout.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f[0] == "DONE") { complete = true; continue; }
            if (f.Length != 5 || f[0] != "F" || DecodePath(f[4]) is not { } path) continue;
            var indexes = f[1].Split(',').Select(i => int.TryParse(i, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : -1).Where(n => n >= 0).Distinct().ToArray();
            if (indexes.Length == 0) continue;
            hits.Add(new(path, f[4], indexes, long.TryParse(f[2], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ? size : 0,
                f[3] is "sqlite" or "sqlite-aux" or "binary" ? f[3] : "text"));
        }
        return (hits, complete);
    }
    public static (IReadOnlyList<VaultCleanResult> Results, bool Complete) ParseClean(string stdout)
    {
        var results = new List<VaultCleanResult>(); var complete = false;
        foreach (var line in stdout.Split('\n'))
        {
            var f = line.TrimEnd('\r').Split('\t');
            if (f[0] == "DONE") { complete = true; continue; }
            if (f.Length < 4 || f[0] != "R" || DecodePath(f[2]) is not { } path) continue;
            results.Add(new(f[1], path, f[2], long.TryParse(f[3], NumberStyles.None, CultureInfo.InvariantCulture, out var count) ? count : 0, f.Length > 4 ? Sanitize(f[4], 200) : ""));
        }
        return (results, complete);
    }
    public static string EncodePath(string path) => Convert.ToBase64String(Encoding.UTF8.GetBytes(path));
    private static string? DecodePath(string b64)
    {
        try { var path = Encoding.UTF8.GetString(Convert.FromBase64String(b64)); return path.StartsWith('/') ? path : null; }
        catch (FormatException) { return null; }
    }

    private static readonly Dictionary<string, string> Templates = GuestScripts.LoadResources("VaultScripts.");
    public static IReadOnlyCollection<string> ScriptNames => Templates.Keys.ToArray();
    private static string Render(string name, Dictionary<string, string> values) => GuestScripts.Fill(Templates[name], values);
    public static string WatchScript(string dir = SpoolDirectory) => Render("vault-watch", new() { ["dir"] = ShellQuote.Single(dir), ["heartbeat"] = "60", ["fallback"] = "3" });
    public static string RespondScript(string id, string dir = SpoolDirectory) => IsValidId(id)
        ? Render("vault-respond", new() { ["dir"] = ShellQuote.Single(dir), ["id"] = ShellQuote.Single(id) }) : throw new ArgumentException("Invalid request id.", nameof(id));
    public static string ScanScript(IEnumerable<string>? roots = null, long maxBytes = ScanMaxFileBytes, string dir = SpoolDirectory) => Render("vault-scan", new()
    {
        ["dir"] = ShellQuote.Single(dir), ["roots"] = string.Join(' ', (roots ?? ScanRoots).Select(ShellQuote.Single)),
        ["maxSize"] = ShellQuote.Single(maxBytes.ToString(CultureInfo.InvariantCulture))
    });
    public static string CleanScript(string dir = SpoolDirectory) => Render("vault-clean", new() { ["dir"] = ShellQuote.Single(dir) });

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex NamePattern();
    [GeneratedRegex(@"\A[A-Za-z0-9._~-]{1,128}\z")]
    private static partial Regex ApprovalIdPattern();
    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex IdPattern();
    [GeneratedRegex("-(wal|shm|journal)$")]
    private static partial Regex SqliteSide();
    [GeneratedRegex(@"^(?:(?:/root|/home/[^/]+)/(?:\.claude/(?:projects|todos|shell-snapshots|debug|file-history|paste-cache|session-env|sessions)/|\.claude/history\.jsonl$|\.codex/(?:sessions|archived_sessions|log)/|\.codex/history\.jsonl$|\.codex/logs_[^/]*$|\.local/share/opencode/(?:storage|log)/|\.local/share/opencode/opencode\.db[^/]*$|\.t3/userdata/logs/|\.t3/userdata/state\.sqlite[^/]*$)|/tmp/claude-)")]
    private static partial Regex AgentLog();
}
