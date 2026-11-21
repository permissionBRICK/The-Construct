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

    private bool? _isGit;

    /// <summary>True when the folder is a git repository with a <c>main</c> branch git can read (asked once per instance).</summary>
    public bool IsGit => _isGit ??= Exists && Directory.Exists(Path.Combine(path, ".git")) && Git("rev-parse", "--verify", "--quiet", "refs/heads/" + Main) is not null;

    /// <summary>The profiles on <c>main</c> (or in the folder): name to file text. <c>default</c> and the schema are not profiles.</summary>
    public IReadOnlyDictionary<string, string> Profiles()
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (IsGit)
        {
            var files = Files(Main, "projects").Where(f => ProfileName(f) is not null).ToArray();
            var texts = ShowMany(files.Select(f => Main + ":" + f).ToArray());
            for (var i = 0; i < files.Length; i++)
                if (texts[i] is { } text) found[ProfileName(files[i])!] = text;
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
        var files = Files(Main, "manifest").Concat(Files(Main, "bases")).ToArray();
        var texts = ShowMany(files.Select(f => Main + ":" + f).ToArray());
        for (var i = 0; i < files.Length; i++)
            if (texts[i] is { } text) found[files[i]] = text;
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
            var hasBase = mergeBase is { Length: > 0 };
            var names = Files(branch, "projects").Concat(hasBase ? Files(mergeBase!, "projects") : [])
                .Where(f => ProfileName(f) is not null).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            // One git process for every file of the branch: ours, base and theirs of each name, in that order.
            var texts = ShowMany(names.SelectMany(f => new[] { branch + ":" + f, hasBase ? mergeBase + ":" + f : null, Main + ":" + f }).ToArray());
            for (var i = 0; i < names.Length; i++)
            {
                var (ours, @base, theirs) = (texts[3 * i], texts[3 * i + 1], texts[3 * i + 2]);
                if (ours == @base || ours == theirs) continue;
                changes.Add(new BranchChange(branch, ProfileName(names[i])!, @base, ours, theirs));
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

    /// <summary>
    /// The blobs named <c>treeish:path</c> (a null entry is not asked), read by ONE <c>git cat-file --batch</c>;
    /// an entry is null when it is no blob or does not exist, all are null when git fails or is missing.
    /// </summary>
    private string?[] ShowMany(IReadOnlyList<string?> specs)
    {
        var result = new string?[specs.Count];
        var asked = Enumerable.Range(0, specs.Count).Where(i => specs[i] is not null).ToArray();
        if (asked.Length == 0) return result;
        var info = Start("cat-file", "--batch");
        info.RedirectStandardInput = true;
        // The names go in as UTF-8, as git reads them (the default would be the console's code page on Windows).
        info.StandardInputEncoding = Utf8;
        try
        {
            using var process = Process.Start(info);
            if (process is null) return result;
            _ = process.StandardError.ReadToEndAsync();
            // Written while the answers are read: git answers each line before it reads more than its pipe holds.
            var writer = Task.Run(() =>
            {
                try
                {
                    using var input = process.StandardInput;
                    input.NewLine = "\n";
                    foreach (var i in asked) input.WriteLine(specs[i]);
                }
                catch (IOException)
                {
                    // git ended early: the answers read so far stand, the rest stay null.
                }
            });
            var output = process.StandardOutput.BaseStream;
            foreach (var i in asked)
            {
                // "<object> blob <size>" then the content and a newline; "<name> missing" (or "ambiguous") alone.
                if (ReadLine(output) is not { } header) break;
                var parts = header.Split(' ');
                if (parts.Length != 3 || !long.TryParse(parts[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var size)) continue;
                var content = new byte[size];
                output.ReadExactly(content);
                output.ReadByte();
                if (parts[1] == "blob") result[i] = Utf8.GetString(content).TrimStart('\uFEFF');
            }

            writer.Wait();
            process.WaitForExit();
            return result;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or EndOfStreamException or IOException)
        {
            return new string?[specs.Count];
        }
    }

    private static readonly UTF8Encoding Utf8 = new(false);

    private static string? ReadLine(Stream stream)
    {
        var line = new List<byte>(64);
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0) return line.Count == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            if (b == '\n') return Encoding.UTF8.GetString(line.ToArray());
            line.Add((byte)b);
        }
    }

    /// <summary>A git command that reads only: no pager, no fsmonitor, no optional locks, no prompt, and no console window.</summary>
    private ProcessStartInfo Start(params string[] args)
    {
        // CreateNoWindow: the agent is a windowless app on Windows, where every console program started
        // without it opens a console window of its own (git once flashed some 60 windows per dialog refresh).
        var info = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-C", path, "--no-pager", "-c", "core.fsmonitor=false", "-c", "core.quotePath=false" }) info.ArgumentList.Add(a);
        foreach (var a in args) info.ArgumentList.Add(a);
        info.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        info.Environment["GIT_TERMINAL_PROMPT"] = "0";
        info.StandardOutputEncoding = Utf8;
        return info;
    }

    /// <summary>Runs a reading git command; null when it fails (or git is missing).</summary>
    private string? Git(params string[] args)
    {
        var info = Start(args);
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
