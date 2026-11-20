using System.Text.Json;
using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>One line of the guest's sign-in script, as the engine reads it (design 11.8).</summary>
/// <param name="Type">
/// <c>start</c>, <c>screen</c> (the terminal's whole text now), <c>end</c>, or <c>log</c> for a line that was
/// not JSON (sudo, ssh, a missing script).
/// </param>
public sealed record AgentSignInLine(string Type, string? Text, string? Outcome = null, bool? SignedIn = null, string? Version = null);

/// <summary>
/// The guest side of an agent's sign-in (<c>bin/construct-agent-sign-in.py</c>, S34): its commands, the input
/// lines it reads and the lines it prints. Pure.
/// </summary>
public static class AgentSignInScript
{
    public const string ScriptPath = ProvisionScript.RepoDir + "/bin/construct-agent-sign-in.py";

    /// <summary>The most text a screen keeps (its end).</summary>
    public const int MaxText = 32 * 1024;

    /// <summary>The longest input line a user may send.</summary>
    public const int MaxInput = 4096;

    /// <summary>The keys an input may press besides typing.</summary>
    public static readonly IReadOnlyList<string> Keys = ["up", "down"];

    /// <summary>The agent's id in the VM's agent report (<see cref="CodingAgents.Known"/> otherwise).</summary>
    public static string Tool(string agent) => agent == "claude-code" ? "claude" : agent;

    /// <summary>
    /// Runs the script as root for <paramref name="agent"/>. Nothing the user types is on this command line:
    /// input goes on stdin.
    /// </summary>
    public static string StartCommand(string sessionId, string agent, bool connectedAsRoot)
    {
        if (!RepairScript.IsSessionId(sessionId)) throw new ArgumentException("Invalid session id.", nameof(sessionId));
        if (!CodingAgents.Known.Contains(agent)) throw new ArgumentException("Unknown agent.", nameof(agent));
        return RepairScript.AsRoot($"python3 {ScriptPath} --session {sessionId} {agent}", connectedAsRoot);
    }

    /// <summary>Ends a session's processes on the guest, whatever became of its connection.</summary>
    public static string StopCommand(string sessionId, bool connectedAsRoot)
    {
        if (!RepairScript.IsSessionId(sessionId)) throw new ArgumentException("Invalid session id.", nameof(sessionId));
        return RepairScript.AsRoot($"python3 {ScriptPath} --stop --session {sessionId}", connectedAsRoot);
    }

    /// <summary>One input line as the script reads it: a key, or text followed by Enter.</summary>
    public static string InputLine(string? text, string? key) => JsonSerializer.Serialize(key is not null
        ? new Dictionary<string, string> { ["key"] = key }
        : new Dictionary<string, string> { ["text"] = text ?? "" });

    /// <summary>Whether <paramref name="text"/> may be typed: short, and no control characters.</summary>
    public static bool ValidInput(string text) => text.Length <= MaxInput && !text.Any(c => char.IsControl(c));

    /// <summary>A guest output line.</summary>
    public static AgentSignInLine Parse(string raw)
    {
        var line = ProvisionScript.CleanLine(raw);
        if (line.StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object && Text(root, "type") is { Length: > 0 } type)
                    return new(type, Cap(Text(root, "text")), Text(root, "outcome"),
                        root.TryGetProperty("signedIn", out var s) && s.ValueKind is JsonValueKind.True or JsonValueKind.False ? s.GetBoolean() : null,
                        Text(root, "version"));
            }
            catch (JsonException) { }
        }

        return new("log", Cap(line));
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Cap(string? text) => text is { Length: > MaxText } ? text[^MaxText..] : text;
}
