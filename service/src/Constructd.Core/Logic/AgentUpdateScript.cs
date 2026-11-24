namespace Constructd.Core.Logic;

/// <summary>
/// The guest side of updating one or more coding agents without a full reprovision: strict allowlist of known tool
/// ids, the exact guest commands (narrowed <c>bin/install-ai-tools.sh</c> with <c>AI_CONSOLE_INTEGRATION=false</c>, or
/// <c>bin/construct-t3-update.sh</c> for T3 Code), and step labels. Pure.
/// </summary>
public static class AgentUpdateScript
{
    /// <summary>
    /// The known coding-agent tool ids, in update order: CLI agents first and T3 Code last (updating T3 Code restarts
    /// <c>t3code-serve</c>).
    /// </summary>
    public static readonly IReadOnlyList<string> KnownTools = ["claude", "codex", "opencode", "antigravity", "t3"];

    public const string ReportStep = "report";

    public static bool IsKnownTool(string? tool) =>
        tool is not null && KnownTools.Contains(tool, StringComparer.Ordinal);

    /// <summary>Orders <paramref name="tools"/> in <see cref="KnownTools"/>'s order (T3 Code last).</summary>
    public static IReadOnlyList<string> Order(IEnumerable<string> tools)
    {
        var set = new HashSet<string>(tools, StringComparer.Ordinal);
        return KnownTools.Where(set.Contains).ToArray();
    }

    /// <summary>
    /// The root shell command that updates only <paramref name="tool"/> on the guest. Never interpolates untrusted
    /// input: every tool maps through an explicit allowlist to a fixed script invocation.
    /// </summary>
    public static string Command(string tool, string? claudeUser = null)
    {
        var user = claudeUser is not null && ProvisionScript.IsSafeUserName(claudeUser) ? claudeUser : "root";
        var repo = ProvisionScript.RepoDir;
        if (tool == "t3")
            return $"env REPO_DIR={repo} bash {repo}/bin/construct-t3-update.sh";

        var aiTool = tool switch
        {
            "claude" => "claude-code",
            "codex" => "codex",
            "opencode" => "opencode",
            "antigravity" => "antigravity",
            _ => throw new ArgumentException("Unknown coding agent.", nameof(tool)),
        };

        return $"env TARGET_USER={ProvisionScript.Quote(user)} AI_TOOLS_OVERRIDE={aiTool} AI_CONSOLE_INTEGRATION=false REPO_DIR={repo} bash {repo}/bin/install-ai-tools.sh";
    }

    /// <summary>Short progress label for one step of an <c>update-agents</c> job.</summary>
    public static string Label(string step) => step switch
    {
        "start" => "Starting the VM",
        "claude" => "Updating Claude Code",
        "codex" => "Updating Codex",
        "opencode" => "Updating OpenCode",
        "antigravity" => "Updating Antigravity",
        "t3" => "Updating T3 Code",
        ReportStep => "Checking agents",
        _ => step,
    };
}
