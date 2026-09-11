using System.Text;

namespace Construct.Companion.Core.ConfigSync;

public static class StoreScripts
{
    public const string DefaultRoot = "/opt/construct/projects";
    private static string B64(string? value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));
    public static string BuildReadStoreScript(string root = DefaultRoot) => string.Join('\n', new[] {
        "set -u", "store=" + ShellQuote.Single(root), "if [ -d \"$store\" ]; then", "  for f in \"$store\"/*.json; do", "    [ -f \"$f\" ] || continue", "    name=$(basename \"$f\" .json)", "    data=$(base64 < \"$f\" | tr -d \"\\n\")", "    printf '%s\\t%s\\n' \"$name\" \"$data\"", "  done", "else", "  printf 'STORE_ABSENT\\n'", "fi", "printf 'END\\n'" });
    public static StoreRead? ParseReadStore(string? stdout)
    {
        var entries = new List<StoreEntry>(); var end = false; var absent = false;
        foreach (var line in (stdout ?? "").Split('\n'))
        {
            if (line == "END") end = true;
            if (line == "STORE_ABSENT") absent = true;
            var tab = line.IndexOf('\t'); if (tab < 0) continue;
            var name = line[..tab].Trim(); if (name.Length == 0) continue;
            var b64 = line[(tab + 1)..].Split('=')[0].Replace('-', '+').Replace('_', '/');
            b64 = System.Text.RegularExpressions.Regex.Replace(b64, "[^A-Za-z0-9+/]", "");
            if (b64.Length % 4 == 1) b64 = b64[..^1];
            b64 = b64.PadRight((b64.Length + 3) / 4 * 4, '=');
            entries.Add(new(name, Encoding.UTF8.GetString(Convert.FromBase64String(b64))));
        }
        return end ? new(entries, absent) : null;
    }
    public static string BuildWriteStoreScript(IEnumerable<WriteOperation> operations, string root = DefaultRoot)
    {
        var lines = new List<string> { "set -u", "store=" + ShellQuote.Single(root), "mkdir -p \"$store\"" };
        foreach (var op in operations)
        {
            if (!ConfigSyncRules.IsSafeProfileName(op.Name) || ConfigSyncRules.IsReserved(op.Name)) throw new ArgumentException("Invalid store profile name.");
            var name = ShellQuote.Single(op.Name); var file = "\"$store\"/" + ShellQuote.Single(op.Name + ".json");
            if (op.Action == "write" && op.Expect == null) lines.Add("if [ ! -f " + file + " ]; then");
            else
            {
                lines.Add("cur=$(base64 < " + file + " 2>/dev/null | tr -d '\\n')");
                lines.Add("if [ \"$cur\" = '" + B64(op.Expect) + "' ]; then");
            }
            lines.Add(op.Action == "write" ? "  printf '%s' '" + B64(op.Content) + "' | base64 -d > " + file : "  rm -f " + file);
            lines.Add("  printf '%s\\t%s\\n' " + name + " 'done'"); lines.Add("else");
            lines.Add("  printf '%s\\t%s\\n' " + name + " 'skipped'"); lines.Add("fi");
        }
        lines.Add("printf 'END\\n'"); return string.Join('\n', lines);
    }
    public static WriteBackResult? ParseWriteResult(string? stdout)
    {
        var result = new WriteBackResult(); var end = false;
        foreach (var line in (stdout ?? "").Split('\n'))
        {
            if (line == "END") end = true;
            var tab = line.IndexOf('\t'); if (tab < 0) continue;
            var name = line[..tab].Trim(); if (name.Length == 0) continue;
            var status = line[(tab + 1)..].Trim();
            if (status == "done") result.Done.Add(name); else if (status == "skipped") result.Skipped.Add(name);
        }
        return end ? result : null;
    }
}
public sealed record StoreEntry(string Name, string Content) { public override string ToString() => "StoreEntry"; }
public sealed record StoreRead(IReadOnlyList<StoreEntry> Entries, bool StoreAbsent);
public sealed class WriteBackResult { public List<string> Done { get; } = []; public List<string> Skipped { get; } = []; }
