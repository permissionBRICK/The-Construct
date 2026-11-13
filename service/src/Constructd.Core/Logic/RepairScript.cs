using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>One line of a repair session as the engine keeps it (design 11.5).</summary>
/// <param name="Kind">
/// What the guest script said (<c>start</c>, <c>note</c>, <c>text</c>, <c>tool</c>, <c>error</c>,
/// <c>turn</c>, <c>end</c>), <c>reply</c> for what the user sent, or <c>log</c> for a line that was
/// not JSON (sudo, ssh, a missing script).
/// </param>
public sealed record RepairLine(long Seq, DateTimeOffset At, string Kind, string? Text, string? Outcome = null);

/// <summary>What a <c>start</c> line says about the agent.</summary>
public sealed record RepairAgentInfo(string? Agent, string? Via);

/// <summary>
/// The guest side of the repair agent (<c>bin/construct-repair-agent.sh</c>): its commands, the
/// job's part of the prompt, and its output lines. Pure.
/// </summary>
public static partial class RepairScript
{
    public const string ScriptPath = ProvisionScript.RepoDir + "/bin/construct-repair-agent.sh";

    /// <summary>The most lines a session keeps; older ones go.</summary>
    public const int MaxLines = 400;

    /// <summary>The longest text a line keeps.</summary>
    public const int MaxText = 16 * 1024;

    [GeneratedRegex("^[a-z0-9]{1,64}$")]
    private static partial Regex SessionIdPattern();

    public static bool IsSessionId(string? id) => id is not null && SessionIdPattern().IsMatch(id);

    /// <summary>
    /// Runs the script as root. The prompt goes base64-encoded, the session id is checked, and
    /// nothing a user typed is on this command line: replies go on stdin.
    /// </summary>
    public static string StartCommand(string sessionId, string jobPrompt, bool connectedAsRoot)
    {
        if (!IsSessionId(sessionId)) throw new ArgumentException("Invalid session id.", nameof(sessionId));
        var prompt = Convert.ToBase64String(Encoding.UTF8.GetBytes(jobPrompt));
        return AsRoot($"bash {ScriptPath} --session {sessionId} --prompt-b64 {prompt}", connectedAsRoot);
    }

    /// <summary>Ends a session's processes on the guest, whatever became of its connection.</summary>
    public static string StopCommand(string sessionId, bool connectedAsRoot)
    {
        if (!IsSessionId(sessionId)) throw new ArgumentException("Invalid session id.", nameof(sessionId));
        return AsRoot($"bash {ScriptPath} --stop --session {sessionId}", connectedAsRoot);
    }

    /// <summary>Root through <c>sudo -n</c> (no password: stdin is the conversation) and a login shell; stderr merged.</summary>
    private static string AsRoot(string command, bool connectedAsRoot) =>
        (connectedAsRoot ? "" : "sudo -n ") + "bash -lc " + ProvisionScript.Quote(command) + " 2>&1";

    /// <summary>A reply as the script reads it: one JSON line.</summary>
    public static string ReplyLine(string text) => JsonSerializer.Serialize(new Dictionary<string, string> { ["reply"] = text });

    /// <summary>
    /// The job's part of the prompt (the rules come from <c>config/repair-prompt.md</c> on the guest),
    /// or null when the job did not fail in a way an agent on the VM can repair: a provision or a
    /// reinstall with failed guest steps, or a restore whose guest side failed.
    /// </summary>
    public static string? JobPrompt(string kind, JsonElement? result, IReadOnlyList<JobProgressLine> log)
    {
        if (result is not { ValueKind: JsonValueKind.Object } r) return null;
        var lines = new List<string>();
        switch (kind)
        {
            case JobKinds.Provision:
                lines.AddRange(FixPrompt(r));
                break;
            case JobKinds.Reinstall:
                if (r.TryGetProperty("provision", out var provision) && provision.ValueKind == JsonValueKind.Object)
                    lines.AddRange(FixPrompt(provision));
                if (lines.Count > 0)
                {
                    lines.Insert(0, "This VM was just reinstalled from a fresh image and is being provisioned again.");
                    lines.Add("Running the reinstall again builds this VM anew from the same backup: a fix inside this VM is lost then. "
                        + "Fix what the next build uses (a project profile, through construct project set); when the fix can only "
                        + "live inside this VM, make it, do not say fixed, and tell the user to run a provision instead.");
                }
                break;
            case JobKinds.Restore:
                if (Text(r, "code") == "restore-failed" && Text(r, "failedStep") == "restore")
                {
                    lines.Add($"Restoring backup {Text(r, "backupId")} into this VM failed: {Text(r, "message")}");
                    lines.Add("Read /var/log/construct/restore-config.log and diagnose and fix the underlying problem. "
                        + "Do not run the restore again yourself: it needs the backup's key, which only the user's device has. "
                        + "When the cause is fixed, say so; the user then runs the restore again.");
                }
                break;
        }

        if (lines.Count == 0) return null;
        var tail = log.TakeLast(40).Select(l => l.Text.Length > 300 ? l.Text[..300] + "..." : l.Text).ToList();
        if (tail.Count > 0)
        {
            lines.Add("");
            lines.Add($"The end of the {kind} job's log in the engine:");
            lines.AddRange(tail.Select(t => "    " + t));
        }

        return string.Join('\n', lines);
    }

    /// <summary>Whether the failed job rebooted the VM at its end (the bootstrap path always does).</summary>
    public static bool Rebooted(JsonElement? result) =>
        result is { ValueKind: JsonValueKind.Object } r && (Flag(r, "rebooted")
            || r.TryGetProperty("provision", out var p) && p.ValueKind == JsonValueKind.Object && Flag(p, "rebooted"));

    private static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IEnumerable<string> FixPrompt(JsonElement result) =>
        result.TryGetProperty("fixPrompt", out var prompt) && prompt.ValueKind == JsonValueKind.Array
            ? prompt.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)
            : [];

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A guest output line as a session line (its sequence number and time given).</summary>
    public static RepairLine Parse(string raw, long seq, DateTimeOffset at)
    {
        var line = ProvisionScript.CleanLine(raw);
        if (line.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && Text(root, "type") is { Length: > 0 } type)
                    return new(seq, at, type, Cap(Text(root, "text")), Text(root, "outcome"));
            }
            catch (JsonException) { }
        }

        return new(seq, at, "log", Cap(line));
    }

    /// <summary>The agent named in a <c>start</c> line.</summary>
    public static RepairAgentInfo? AgentInfo(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(ProvisionScript.CleanLine(raw));
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object && Text(root, "type") == "start"
                ? new(Text(root, "agent"), Text(root, "via"))
                : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Cap(string? text) => text is { Length: > MaxText } ? text[..MaxText] + "..." : text;
}
