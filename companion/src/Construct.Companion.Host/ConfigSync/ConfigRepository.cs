using System.Text;
using System.Text.Json;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;

namespace Construct.Companion.Host.ConfigSync;
public sealed partial class ConfigRepository(GitRunner git, IFileSystem files, IConfigSyncStorage storage, string directory)
{
    public string Directory { get; } = Path.GetFullPath(directory);
    public GitRunner Git => git;
    public IConfigSyncStorage Storage => storage;
    public IFileSystem Files => files;
    public string FilePath(string folder, string name) { if (!ConfigSyncRules.IsSafeProfileName(name)) throw new ConfigSyncException("Invalid profile file name."); return Path.Combine(Directory, folder, name + ".json"); }
    public string? ReadText(string path) { var data = files.ReadFile(path); return data == null ? null : Encoding.UTF8.GetString(data); }
    public void WriteText(string path, string text) => files.WriteFileAtomic(path, Encoding.UTF8.GetBytes(text));
    public void EnsureConfigTree() { foreach (var sub in new[] { "projects", "manifest", "bases" }) files.CreateDirectory(Path.Combine(Directory, sub)); }
    public async Task HardenAsync(CancellationToken ct = default)
    {
        foreach (var pair in new[] { ("commit.gpgsign", "false"), ("core.hooksPath", ""), ("core.autocrlf", "false") }) await git.RunAsync(Directory, ["config", pair.Item1, pair.Item2], cancellationToken: ct);
        try
        {
        var attrs = Path.Combine(Directory, ".gitattributes"); if (!files.FileExists(attrs)) WriteText(attrs, "* text=auto eol=lf\n");
        var exclude = Path.Combine(Directory, ".git", "info", "exclude"); var cur = ReadText(exclude) ?? ""; var have = cur.Split('\n').Select(s => s.Trim()).ToHashSet();
        var missing = new[] { ".gitattributes", ".migrated", SyncLock.LockFile, SyncLock.ProvisionIntent, "projects/default.json", "projects/project.schema.json" }.Where(s => !have.Contains(s)).ToArray();
        if (missing.Length > 0) WriteText(exclude, cur + (cur.Length > 0 && !cur.EndsWith('\n') ? "\n" : "") + string.Join('\n', missing) + "\n");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public async Task<RepoInit> EnsureRepoAsync(string branch = "vm", CancellationToken ct = default)
    {
        EnsureConfigTree(); branch = ConfigSyncRules.ResolveVmBranch(branch);
        var st = await git.RunAsync(Directory, ["rev-parse", "--git-dir"], cancellationToken: ct);
        if (st.Code != 0 && st.Stderr.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase)) return new(false, DubiousOwnership: true);
        if (st.Code == 0)
        {
            var top = await git.RunAsync(Directory, ["rev-parse", "--show-toplevel"], cancellationToken: ct);
            if (top.Code == 0 && Path.GetFullPath(top.Stdout.Trim()) == Directory)
            {
                await HardenAsync(ct);
                if (branch != "vm" && (await git.RunAsync(Directory, ["rev-parse", "--verify", "refs/heads/" + branch], cancellationToken: ct)).Code != 0) await git.RequireAsync(Directory, ["branch", branch, "main"], cancellationToken: ct);
                return new(true);
            }
        }
        var init = await git.RunAsync(Directory, ["init"], cancellationToken: ct); if (init.Code != 0) return new(false, DubiousOwnership: init.Stderr.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase));
        await HardenAsync(ct);
        await git.RequireAsync(Directory, ["add", "-A"], true, cancellationToken: ct);
        await git.RequireAsync(Directory, ["commit", "--allow-empty", "-m", "initial config"], true, cancellationToken: ct);
        await git.RequireAsync(Directory, ["branch", "-M", "main"], cancellationToken: ct);
        await git.RequireAsync(Directory, ["branch", branch], cancellationToken: ct); return new(true, true);
    }
    public async Task<RepoState> StateAsync(CancellationToken ct = default)
    {
        if ((await git.RunAsync(Directory, ["rev-parse", "--git-dir"], cancellationToken: ct)).Code != 0) return new(false, false, [], false);
        var ls = await git.RunAsync(Directory, ["ls-files", "-u", "--error-unmatch", "--"], cancellationToken: ct);
        var conflicts = ls.Stdout.Split('\n').Where(s => s.Contains('\t')).Select(s => s[(s.IndexOf('\t') + 1)..].Trim()).Distinct().ToArray();
        var merge = await git.RunAsync(Directory, ["rev-parse", "--verify", "MERGE_HEAD"], cancellationToken: ct); return new(true, conflicts.Length > 0, conflicts, merge.Code == 0);
    }
    public IReadOnlyDictionary<string, string> ReadMainProfiles()
    {
        var result = new Dictionary<string, string>();
        foreach (var path in files.EnumerateFiles(Path.Combine(Directory, "projects")))
        {
            if (!path.EndsWith(".json", StringComparison.Ordinal)) continue;
            var name = Path.GetFileNameWithoutExtension(path); if (ConfigSyncRules.IsReserved(name) || !ConfigSyncRules.IsSafeProfileName(name)) continue;
            if (ReadText(path) is { } text) result[name] = text;
        }
        return result;
    }
    public IReadOnlyList<string> ValidateWorkingTreeProfiles() => ReadMainProfiles().Select(p => (p.Key, Gate: ProfileCodec.CanonicalizeProfileText(p.Key, p.Value))).Where(p => !p.Gate.Ok).Select(p => p.Key + ": " + p.Gate.Reason).ToArray();
    public async Task<PendingMerge> CompletePendingMergeAsync(string branch = "vm", CancellationToken ct = default)
    {
        var state = await StateAsync(ct); if (!state.Repo || !state.MergeInProgress) return new(true);
        if (state.Conflict) return new(false, Conflict: true, Reason: "merge conflict in config repo");
        var errors = ValidateWorkingTreeProfiles(); if (errors.Count > 0) return new(false, Blocked: true, Reason: "post-merge validation failed: " + string.Join("; ", errors));
        var add = await git.RunAsync(Directory, ["add", "-A"], true, cancellationToken: ct); if (add.Code != 0) return new(false, Blocked: true, Reason: "merge staging failed");
        var commit = await git.RunAsync(Directory, ["commit", "-m", "sync merge " + ConfigSyncRules.ResolveVmBranch(branch)], true, cancellationToken: ct);
        return commit.Code == 0 ? new(true, true) : new(false, Blocked: true, Reason: "merge commit failed: " + ConfigSyncRules.RedactGitOutput((string.IsNullOrEmpty(commit.Stderr) ? commit.Stdout : commit.Stderr).Trim()));
    }
    public async Task<CommitResult> CommitAllAsync(string message, CancellationToken ct = default)
    {
        try
        {
            await git.RequireAsync(Directory, ["add", "-A"], true, cancellationToken: ct);
            var staged = await git.RequireAsync(Directory, ["diff", "--cached", "--name-only"], cancellationToken: ct);
            if (staged.Length == 0) return new(true, false);
            await git.RequireAsync(Directory, ["commit", "-m", ConfigSyncRules.RedactGitOutput(message)], true, cancellationToken: ct); return new(true, true);
        }
        catch (ConfigSyncException e) { return new(false, false, e.Message); }
    }
    public async Task CommitHostDirtyFilesAsync(SyncResult result, CancellationToken ct = default)
    {
        var diff = await git.RequireAsync(Directory, ["diff", "--name-only", "--", "projects/"], cancellationToken: ct);
        var other = await git.RequireAsync(Directory, ["ls-files", "--others", "--exclude-standard", "--", "projects/"], cancellationToken: ct);
        var stage = new List<string>();
        foreach (var path in (diff + "\n" + other).Split('\n').Where(p => Path.GetDirectoryName(p)?.Replace('\\','/') == "projects" && p.EndsWith(".json", StringComparison.Ordinal)).Distinct())
        {
            var name = Path.GetFileNameWithoutExtension(path); if (ConfigSyncRules.IsReserved(name) || !ConfigSyncRules.IsSafeProfileName(name)) continue;
            var text = ReadText(FilePath("projects", name)); if (text == null) { stage.Add(path); continue; }
            var gate = ProfileCodec.CanonicalizeProfileText(name, text);
            if (!gate.Ok) { result.Warnings.Add($"invalid host profile \"{name}\": {gate.Reason}; left uncommitted"); continue; }
            stage.Add(path);
        }
        if (stage.Count == 0) return;
        await git.RequireAsync(Directory, new[] { "add", "--" }.Concat(stage), true, cancellationToken: ct);
        if ((await git.RequireAsync(Directory, ["diff", "--cached", "--name-only"], cancellationToken: ct)).Length > 0) await git.RequireAsync(Directory, ["commit", "-m", "host config update"], true, cancellationToken: ct);
    }
    public async Task<int> CountVmBranchProfilesAsync(string branch, CancellationToken ct = default)
    {
        var ls = await git.RunAsync(Directory, ["ls-tree", "--name-only", branch, "projects/"], cancellationToken: ct); return ls.Code == 0 ? ls.Stdout.Split('\n').Count(p => p.EndsWith(".json", StringComparison.Ordinal)) : 0;
    }
    public async Task<bool> CommitVmBranchAsync(IReadOnlyDictionary<string, string> valid, ISet<string> preserve, string branch, CancellationToken ct = default)
    {
        var index = Path.Combine(Directory, ".git", "tmp-vm-index"); files.DeleteFile(index);
        try
        {
            await git.RequireAsync(Directory, ["read-tree", branch], index: index, cancellationToken: ct);
            var entries = await git.RequireAsync(Directory, ["ls-files", "--cached", "--", "projects/"], index: index, cancellationToken: ct);
            foreach (var entry in entries.Split('\n').Where(s => s.Length > 0)) if (!preserve.Contains(Path.GetFileNameWithoutExtension(entry))) await git.RequireAsync(Directory, ["update-index", "--force-remove", "--", entry], index: index, cancellationToken: ct);
            foreach (var (name, content) in valid)
            {
                if (!ConfigSyncRules.IsSafeProfileName(name)) continue;
                var temp = Path.Combine(Directory, ".git", "tmp-blob-" + Guid.NewGuid().ToString("N"));
                string sha; try { WriteText(temp, content); sha = await git.RequireAsync(Directory, ["hash-object", "-w", "--", temp], cancellationToken: ct); } finally { files.DeleteFile(temp); }
                await git.RequireAsync(Directory, ["update-index", "--add", "--cacheinfo", "100644," + sha + ",projects/" + name + ".json"], index: index, cancellationToken: ct);
            }
            var tree = await git.RequireAsync(Directory, ["write-tree"], index: index, cancellationToken: ct);
            if (tree == await git.RequireAsync(Directory, ["rev-parse", branch + "^{tree}"], cancellationToken: ct)) return false;
            var tip = await git.RequireAsync(Directory, ["rev-parse", branch], cancellationToken: ct);
            var commit = await git.RequireAsync(Directory, ["commit-tree", tree, "-p", tip, "-m", branch + " sync"], true, cancellationToken: ct);
            await git.RequireAsync(Directory, ["update-ref", "refs/heads/" + branch, commit], cancellationToken: ct); return true;
        }
        finally { files.DeleteFile(index); }
    }
}
public sealed record RepoInit(bool Repo, bool Initialized = false, bool DubiousOwnership = false);
public sealed record RepoState(bool Repo, bool Conflict, IReadOnlyList<string> ConflictFiles, bool MergeInProgress);
public sealed record PendingMerge(bool Ok, bool Completed = false, bool Conflict = false, bool Blocked = false, string Reason = "");
public sealed record CommitResult(bool Ok, bool Committed, string Output = "");
