using System.Text.Json;
using System.Text.RegularExpressions;
using Construct.Companion.Core.ConfigSync;

namespace Construct.Companion.Host.ConfigSync;

public sealed class ConfigRemotes(ConfigRepository repo, string stagingRoot)
{
    public const string PendingMarker = "construct-publish-pending";
    public const string PendingSentinel = "push-to-create pending\n";
    private GitRunner Git => repo.Git;
    public string CloneDirectory(string url)
    {
        CheckUrl(url); var slug = ConfigSyncRules.RemoteSlug(url);
        if (slug.Length == 0 || slug is "." or "..") throw new ConfigSyncException("Invalid config remote URL.");
        return Path.Combine(stagingRoot, slug);
    }
    private static void CheckUrl(string url)
    {
        if (ConfigSyncRules.UrlHasCredentials(url)) throw new ConfigSyncException("Remove the credentials from the URL -- let your git credential helper supply them.");
        if (string.IsNullOrWhiteSpace(url) || url.StartsWith('-')) throw new ConfigSyncException("Invalid config remote URL.");
    }
    public IReadOnlyList<ConfigRemote> ReadRemotes()
    {
        try { return JsonSerializer.Deserialize<ConfigRemote[]>(repo.ReadText(Path.Combine(repo.Directory,"manifest","remotes.json")) ?? "[]", ConfigSyncRules.Json)?.Where(r => r != null && r.Url != null).ToArray() ?? []; }
        catch (JsonException) { return []; }
    }
    public void WriteRemotes(IEnumerable<ConfigRemote> remotes)
    {
        var list = remotes.ToArray(); foreach (var r in list) { try { CheckUrl(r.Url); } catch (ConfigSyncException e) { throw new ConfigSyncException(ConfigSyncRules.DisplayRemoteUrl(r.Url)+": "+e.Message); } }
        repo.WriteText(Path.Combine(repo.Directory,"manifest","remotes.json"),ConfigSyncRules.Serialize(list));
    }
    public IReadOnlyDictionary<string,ManifestEntry> ReadImportManifest()
    {
        var result = new Dictionary<string,ManifestEntry>();
        foreach (var path in repo.Files.EnumerateFiles(Path.Combine(repo.Directory,"manifest")))
        {
            if (!path.EndsWith(".json",StringComparison.Ordinal) || Path.GetFileName(path) == "remotes.json") continue;
            var name = Path.GetFileNameWithoutExtension(path); if (!ConfigSyncRules.IsSafeProfileName(name)) continue;
            try { var entry = JsonSerializer.Deserialize<ManifestEntry>(repo.ReadText(path) ?? "null",ConfigSyncRules.Json); if (entry != null) result[name] = entry; } catch (JsonException) { }
        }
        return result;
    }
    public void Adopt(string name, string content, ManifestEntry entry, string baseContent)
    {
        CheckUrl(entry.RemoteUrl); repo.WriteText(repo.FilePath("projects",name),content); repo.WriteText(repo.FilePath("manifest",name),ConfigSyncRules.Serialize(entry)); repo.WriteText(repo.FilePath("bases",name),baseContent);
    }
    public IReadOnlyList<ImportCandidate> ListImportCandidates(string directory)
    {
        var useProjects = repo.Files.DirectoryExists(Path.Combine(directory,"projects")); var scan = useProjects ? Path.Combine(directory,"projects") : directory;
        return repo.Files.EnumerateFiles(scan).Where(p=>p.EndsWith(".json",StringComparison.OrdinalIgnoreCase)).Select(p=>new ImportCandidate(Path.GetFileNameWithoutExtension(p),(useProjects ? "projects/" : "")+Path.GetFileName(p))).Where(c=>!ConfigSyncRules.IsReserved(c.Name) && ConfigSyncRules.IsSafeProfileName(c.Name)).ToArray();
    }
    public async Task<CloneResult> EnsureStagingCloneAsync(string url, CancellationToken ct = default)
    {
        var dir = CloneDirectory(url); repo.Files.CreateDirectory(dir);
        var check = await Git.RunAsync(dir,["rev-parse","--show-toplevel"],cancellationToken:ct);
        if (check.Code == 0 && Path.GetFullPath(check.Stdout.Trim()) == Path.GetFullPath(dir))
        {
            var fetch = await Git.RunAsync(dir,["fetch","origin"],network:true,cancellationToken:ct); if (fetch.Code != 0) return new(false,dir,Output:Safe("fetch failed: "+fetch.Stderr));
            var symref = await Git.RunAsync(dir,["symbolic-ref","refs/remotes/origin/HEAD"],cancellationToken:ct);
            var branch = symref.Code == 0 ? symref.Stdout.Trim().Replace("refs/remotes/origin/","",StringComparison.Ordinal) : "main";
            var reset = await Git.RunAsync(dir,["reset","--hard","origin/"+branch],cancellationToken:ct);
            return new(reset.Code == 0,dir,Output:reset.Code == 0 ? "" : Safe("reset failed: "+reset.Stderr));
        }
        var clone = await Git.RunAsync(Path.GetDirectoryName(dir)!,["clone",url,Path.GetFileName(dir)],network:true,cancellationToken:ct);
        return new(clone.Code == 0,dir,Output:clone.Code == 0 ? "" : Safe("clone failed: "+clone.Stderr));
    }
    public async Task<CloneResult> EnsurePublishCloneAsync(string url, CancellationToken ct = default)
    {
        var dir = CloneDirectory(url); var marker = Path.Combine(dir,".git",PendingMarker);
        if (repo.Files.DirectoryExists(Path.Combine(dir,".git")))
        {
            var set = await Git.RunAsync(dir,["remote","set-url","origin",url],cancellationToken:ct);
            if (set.Code != 0) { var add = await Git.RunAsync(dir,["remote","add","origin",url],cancellationToken:ct); if (add.Code != 0) return new(false,dir,Output:Safe(set.Stderr+"\n"+add.Stderr)); }
            var fetch = await Git.RunAsync(dir,["fetch","origin"],network:true,cancellationToken:ct);
            if (fetch.Code == 0) return new(true,dir);
            var head = await Git.RunAsync(dir,["rev-parse","--verify","--quiet","HEAD"],cancellationToken:ct);
            if (repo.Files.FileExists(marker) || head.Code != 0) { repo.WriteText(marker,PendingSentinel); return new(true,dir,true); }
            return new(false,dir,Output:Safe(fetch.Stderr));
        }
        repo.Files.CreateDirectory(Path.GetDirectoryName(dir)!);
        var clone = await Git.RunAsync(Path.GetDirectoryName(dir)!,["clone",url,Path.GetFileName(dir)],network:true,cancellationToken:ct);
        if (clone.Code == 0) return new(true,dir);
        repo.Files.DeleteDirectory(dir); repo.Files.CreateDirectory(dir);
        var init = await Git.RunAsync(dir,["init"],cancellationToken:ct); if (init.Code != 0) return new(false,dir,Output:Safe(clone.Stderr+"\n"+init.Stderr));
        var remote = await Git.RunAsync(dir,["remote","add","origin",url],cancellationToken:ct); if (remote.Code != 0) return new(false,dir,Output:Safe(remote.Stderr));
        repo.WriteText(marker,PendingSentinel); return new(true,dir,true);
    }
    public async Task<string> RemoteDefaultBranchAsync(string url, CancellationToken ct = default)
    {
        CheckUrl(url); var r = await Git.RunAsync(stagingRoot,["ls-remote","--symref",url,"HEAD"],network:true,cancellationToken:ct);
        var match = Regex.Match(r.Stdout,@"^ref:\s+refs/heads/(\S+)\s",RegexOptions.Multiline); return r.Code == 0 && match.Success ? match.Groups[1].Value : "";
    }
    public async Task<CheckoutResult> CheckoutPublishBranchAsync(string dir, string branch, CancellationToken ct = default)
    {
        if (!ConfigSyncRules.IsValidPublishBranch(branch)) return new(false,new Dictionary<string,string>(),"Invalid publish branch.");
        var hasHead = (await Git.RunAsync(dir,["rev-parse","--verify","--quiet","HEAD"],cancellationToken:ct)).Code == 0;
        var hasRemote = (await Git.RunAsync(dir,["rev-parse","--verify","--quiet","refs/remotes/origin/"+branch],cancellationToken:ct)).Code == 0;
        try
        {
            await Git.RequireAsync(dir,hasRemote ? ["checkout","-B",branch,"refs/remotes/origin/"+branch] : hasHead ? ["checkout","-B",branch] : ["symbolic-ref","HEAD","refs/heads/"+branch],cancellationToken:ct);
            if (hasHead || hasRemote) { await Git.RequireAsync(dir,["reset","--hard"],cancellationToken:ct); await Git.RequireAsync(dir,["clean","-fd"],cancellationToken:ct); }
            var profiles = new Dictionary<string,string>(); foreach (var p in repo.Files.EnumerateFiles(Path.Combine(dir,"projects"))) if (p.EndsWith(".json",StringComparison.Ordinal) && repo.ReadText(p) is { } content) profiles[Path.GetFileNameWithoutExtension(p)] = content;
            return new(true,profiles);
        }
        catch (ConfigSyncException e) { return new(false,new Dictionary<string,string>(),e.Message); }
    }
    public async Task<PublishResult> PublishToRemoteAsync(string dir, string branch, IReadOnlyList<PublishFile> publish, CancellationToken ct = default)
    {
        PublishResult Fail(string reason) => new(false,branch,"",new Dictionary<string,string>(),Safe(reason));
        if (!ConfigSyncRules.IsValidPublishBranch(branch)) return Fail("Invalid publish branch.");
        foreach (var file in publish)
        {
            if (!ConfigSyncRules.IsSafeProfileName(file.Name) || ConfigSyncRules.IsReserved(file.Name)) return Fail("refusing to publish outside projects/");
            repo.WriteText(Path.Combine(dir,"projects",file.Name+".json"),file.Content);
        }
        var add = await Git.RunAsync(dir,["-c","core.hooksPath=","add","-A"],cancellationToken:ct); if (add.Code != 0) return Fail("git add failed: "+add.Stderr);
        var staged = await Git.RunAsync(dir,["diff","--cached","--name-only"],cancellationToken:ct); if (staged.Code != 0) return Fail("git diff --cached failed: "+staged.Stderr);
        if (staged.Stdout.Trim().Length > 0)
        {
            var commit = await Git.RunAsync(dir,["-c","core.hooksPath=","commit","-m","publish "+publish.Count+" profiles"],cancellationToken:ct);
            if (commit.Code != 0)
            {
                var both = commit.Stderr+"\n"+commit.Stdout;
                return Fail(Regex.IsMatch(both,"Please tell me who you are|empty ident|unable to auto-detect email",RegexOptions.IgnoreCase) ? "git has no configured identity to commit with. Set user.name and user.email (git config --global user.name \"...\"; git config --global user.email \"...\") and publish again.\n"+both : "git commit failed: "+both);
            }
        }
        var push = await Git.RunAsync(dir,["push","origin","HEAD:refs/heads/"+branch],network:true,cancellationToken:ct); if (push.Code != 0) return Fail("git push failed: "+push.Stderr);
        var head = await Git.RunAsync(dir,["rev-parse","HEAD"],cancellationToken:ct); var sha = head.Stdout.Trim(); if (head.Code != 0 || !Regex.IsMatch(sha,"^[0-9a-f]{40}$")) return Fail("could not resolve the pushed commit");
        var blobs = new Dictionary<string,string>();
        foreach (var file in publish)
        {
            var blob = await Git.RunAsync(dir,["rev-parse","HEAD:projects/"+file.Name+".json"],cancellationToken:ct);
            if (blob.Code != 0 || !Regex.IsMatch(blob.Stdout.Trim(),"^[0-9a-f]{40}$")) return Fail("profile is not in the pushed commit"); blobs[file.Name] = blob.Stdout.Trim();
        }
        repo.Files.DeleteFile(Path.Combine(dir,".git",PendingMarker)); return new(true,branch,sha,blobs,Safe(push.Stdout));
    }
    public async Task<MergeFileResult> MergeFileAsync(string ours, string @base, string theirs, CancellationToken ct = default)
    {
        var temp = Path.Combine(repo.Files.GetRoot(Core.Abstractions.FileSystemRoot.Temp) ?? stagingRoot,"construct-merge-"+Guid.NewGuid().ToString("N")); repo.Files.CreateDirectory(temp);
        try
        {
            foreach (var pair in new[]{("ours",ours),("base",@base),("theirs",theirs)}) repo.WriteText(Path.Combine(temp,pair.Item1),pair.Item2);
            var r = await Git.RunAsync(temp,["merge-file","-p",Path.Combine(temp,"ours"),Path.Combine(temp,"base"),Path.Combine(temp,"theirs")],cancellationToken:ct);
            return new(r.Code == 0,r.Code == 0 ? r.Stdout : null,r.Code > 0);
        }
        finally { repo.Files.DeleteDirectory(temp); }
    }
    public async Task<PushResult> PushUpstreamAsync(string dir, IEnumerable<UpstreamFile> files, string branch, string? message = null, CancellationToken ct = default)
    {
        if (!ConfigSyncRules.IsValidPublishBranch(branch)) return new(false,branch,"Invalid push branch.");
        try
        {
            foreach (var file in files)
            {
                var dest = ContainedPath(dir,file.PathInRemote); var text = repo.ReadText(file.AbsSource) ?? throw new ConfigSyncException("Source profile could not be read."); repo.WriteText(dest,text);
            }
            await Git.RequireAsync(dir,["checkout","-B",branch],true,cancellationToken:ct); await Git.RequireAsync(dir,["add","-A"],true,cancellationToken:ct);
            if ((await Git.RequireAsync(dir,["diff","--cached","--name-only"],cancellationToken:ct)).Length == 0) return new(true,branch,"nothing to push");
            await Git.RequireAsync(dir,["commit","-m",message ?? "construct config update"],true,cancellationToken:ct);
            var pushed = await Git.RunAsync(dir,["push","origin",branch],network:true,cancellationToken:ct); return new(pushed.Code == 0,branch,Safe(pushed.Code == 0 ? pushed.Stdout : pushed.Stderr));
        }
        catch (ConfigSyncException e) { return new(false,branch,e.Message); }
    }
    public static string ContainedPath(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new ConfigSyncException("Invalid pathInRemote in manifest.");
        var root = Path.GetFullPath(directory); var dest = Path.GetFullPath(Path.Combine(root,relative.Replace('\\','/')));
        if (!dest.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.Ordinal) || relative.Replace('\\','/').Split('/').Any(s=>s.Equals(".git",StringComparison.OrdinalIgnoreCase))) throw new ConfigSyncException("pathInRemote escapes staging directory");
        return dest;
    }
    private static string Safe(string value) => ConfigSyncRules.RedactGitOutput(value.Trim());
}
public sealed record CloneResult(bool Ok, string Dir, bool Created = false, string Output = "");
public sealed record ImportCandidate(string Name, string RelPath);
public sealed record CheckoutResult(bool Ok, IReadOnlyDictionary<string,string> RemoteFiles, string Output = "");
public sealed record PublishResult(bool Ok, string Branch, string Commit, IReadOnlyDictionary<string,string> BlobShas, string Output = "");
public sealed record MergeFileResult(bool Ok, string? Content, bool Conflict) { public override string ToString() => $"MergeFileResult {{ Ok = {Ok}, Conflict = {Conflict} }}"; }
public sealed record PushResult(bool Ok, string Branch, string Output);
public sealed record UpstreamFile(string AbsSource, string PathInRemote);
