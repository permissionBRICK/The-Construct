using System.Diagnostics;
using System.Text;

namespace Construct.Agent.Upgrade;

/// <summary>A profile file a VM branch changed and <c>main</c> does not have: base (merge base), ours (the branch), theirs (main); null is "no file".</summary>
public sealed record BranchChange(string Branch, string Profile, string? Base, string? Ours, string? Theirs);

/// <summary>
/// The old client's config repository (<c>%LOCALAPPDATA%\The-Construct\config</c>, docs/config-sync.md), read
/// with git commands that never write: no checkout, no status, no index refresh, optional locks off. Without
/// git, or when the folder is no repository, the profiles are read from the folder itself and no branch is seen.
/// </summary>
public sealed class LegacyConfigRepo(string path)
{
    public const string Main = "main";

    public bool Exists => Directory.Exists(path);

    /// <summary>True when the folder is a git repository with a <c>main</c> branch git can read.</summary>
    public bool IsGit => Exists && Directory.Exists(Path.Combine(path, ".git")) && Git("rev-parse", "--verify", "--quiet", "refs/heads/" + Main) is not null;

    /// <summary>The profiles on <c>main</c> (or in the folder): name to file text. <c>default</c> and the schema are not profiles.</summary>
    public IReadOnlyDictionary<string, string> Profiles()
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (IsGit)
        {
            foreach (var file in Files(Main, "projects"))
                if (ProfileName(file) is { } name && Show(Main, file) is { } text) found[name] = text;
            // With main checked out, a profile edited or added in the folder before the old client's next sync
            // tick is newer than main's commit: the folder's copy wins when it is a JSON object (a file in the
            // middle of a merge, with conflict markers, is not, and main's commit stays).
            if ((Git("symbolic-ref", "--quiet", "HEAD") ?? "").Trim() == "refs/heads/" + Main && Directory.Exists(Path.Combine(path, "projects")))
                foreach (var file in Directory.EnumerateFiles(Path.Combine(path, "projects"), "*.json"))
                    if (ProfileName("projects/" + Path.GetFileName(file)) is { } name && File.ReadAllText(file) is var text && IsObject(text)) found[name] = text;
        }
        else if (Directory.Exists(Path.Combine(path, "projects")))
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(path, "projects"), "*.json"))
                if (ProfileName("projects/" + Path.GetFileName(file)) is { } name) found[name] = File.ReadAllText(file);
        }

        return found;
    }

    /// <summary>The upstream links on <c>main</c>: <c>manifest/*.json</c> (with <c>remotes.json</c>) and the stored bases, path to text.</summary>
    public IReadOnlyDictionary<string, string> UpstreamFiles()
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!IsGit) return found;
        foreach (var file in Files(Main, "manifest").Concat(Files(Main, "bases")))
            if (Show(Main, file) is { } text) found[file] = text;
        return found;
    }

    /// <summary>VM branches (<c>vm</c>, <c>vm-*</c>) with commits <c>main</c> does not have, and the profiles each changed since the merge base.</summary>
    /// <param name="branches">Further VM branches the registry names (an instance's <c>configBranch</c>).</param>
    public IReadOnlyList<BranchChange> UnmergedChanges(IEnumerable<string>? branches = null)
    {
        var changes = new List<BranchChange>();
        if (!IsGit) return changes;
        var refs = (Git("for-each-ref", "--format=%(refname:short)", "refs/heads/vm", "refs/heads/vm-*") ?? "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var named in branches ?? [])
            if (named != Main && !refs.Contains(named) && Git("rev-parse", "--verify", "--quiet", "refs/heads/" + named) is not null) refs.Add(named);
        foreach (var branch in refs.Order(StringComparer.Ordinal))
        {
            if ((Git("rev-list", "--count", Main + ".." + branch) ?? "0").Trim() == "0") continue;
            var mergeBase = Git("merge-base", Main, branch)?.Trim();
            var names = Files(branch, "projects").Concat(mergeBase is { Length: > 0 } ? Files(mergeBase, "projects") : [])
                .Where(f => ProfileName(f) is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
            foreach (var file in names)
            {
                var ours = Show(branch, file);
                var @base = mergeBase is { Length: > 0 } ? Show(mergeBase, file) : null;
                if (ours == @base) continue;
                var theirs = Show(Main, file);
                if (ours == theirs) continue;
                changes.Add(new BranchChange(branch, ProfileName(file)!, @base, ours, theirs));
            }
        }

        return changes;
    }

    private static bool IsObject(string text)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(text) is System.Text.Json.Nodes.JsonObject; }
        catch (System.Text.Json.JsonException) { return false; }
    }

    private static string? ProfileName(string file)
    {
        if (!file.StartsWith("projects/", StringComparison.Ordinal) || !file.EndsWith(".json", StringComparison.Ordinal)) return null;
        var name = file["projects/".Length..^".json".Length];
        if (name.Contains('/') || name.Length == 0) return null;
        return name.Equals("default", StringComparison.OrdinalIgnoreCase) || name.Equals("project.schema", StringComparison.OrdinalIgnoreCase) ? null : name;
    }

    private IEnumerable<string> Files(string treeish, string folder) =>
        (Git("ls-tree", "-r", "--name-only", treeish, "--", folder + "/") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string? Show(string treeish, string file) => Git("cat-file", "blob", treeish + ":" + file);

    /// <summary>Runs a reading git command; null when it fails (or git is missing).</summary>
    private string? Git(params string[] args)
    {
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in new[] { "-C", path, "--no-pager", "-c", "core.fsmonitor=false", "-c", "core.quotePath=false" }) info.ArgumentList.Add(a);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.StandardOutputEncoding = new UTF8Encoding(false);
        try
        {
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            process.WaitForExit();
            return process.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
