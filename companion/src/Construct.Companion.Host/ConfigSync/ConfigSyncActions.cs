using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;

namespace Construct.Companion.Host.ConfigSync;

// Dialogs are outside the repo lock. After each dialog the action re-reads mutable
// state under the lock, so a long-open picker cannot overwrite another engine's work.
public sealed class ConfigSyncActions(ConfigRepository repo, ConfigRemotes remotes, SyncLock syncLock, IPrompts prompts, IClipboard clipboard, IClock clock, SemaphoreSlim? repositoryQueue = null)
{
    public async Task<ActionResult> AddRemoteAsync(bool publish = false, CancellationToken ct = default)
    {
        var url = await prompts.InputAsync(new(publish ? "Add a remote config repo and publish into it" : "Add a remote config repository",publish ? "Git URL of the config repo to publish your local profiles to" : "Git URL of the remote config repo"),ct);
        if (string.IsNullOrWhiteSpace(url)) return ActionResult.Cancelled;
        url=url.Trim(); var invalid=ConfigSyncRules.ValidateConfigRemoteUrl(url); if (invalid != null) return new(false,invalid);
        var result=await Locked(async()=>
        {
            var linked=remotes.ReadRemotes().ToList();
            if (linked.Any(r=>r.Url==url)) return new ActionResult(true,"That remote is already linked.");
            linked.Add(new(url)); remotes.WriteRemotes(linked); await repo.CommitAllAsync("link config remote",ct); return new(true,"Remote config repo added: "+ConfigSyncRules.DisplayRemoteUrl(url));
        },ct);
        return result.Ok && publish ? await PublishAsync(url,ct) : result;
    }
    public async Task<ActionResult> RemoveRemoteAsync(string fromPanel, CancellationToken ct = default)
    {
        var url=ConfigSyncRules.ResolveRemoteUrl(remotes.ReadRemotes(),fromPanel); if (url.Length==0) return ActionResult.Cancelled;
        if (!await prompts.ConfirmAsync("Remove","Remove the remote config repo?\n"+ConfigSyncRules.DisplayRemoteUrl(url),ct)) return ActionResult.Cancelled;
        return await Locked(async()=> { remotes.WriteRemotes(remotes.ReadRemotes().Where(r=>r.Url!=url)); var commit=await repo.CommitAllAsync("remove config remote",ct); return new(commit.Ok,commit.Ok ? "Remote config repo removed." : commit.Output); },ct);
    }
    public async Task<ActionResult> ImportAsync(CancellationToken ct = default)
    {
        var available=new List<ImportSelection>();
        var prepared=await Queued(async()=>
        {
            var linked=remotes.ReadRemotes(); if (linked.Count==0) return new(false,"No remote config repos linked yet. Add one first.");
            foreach (var remote in linked)
            {
                var clone=await remotes.EnsureStagingCloneAsync(remote.Url,ct); if (!clone.Ok) continue;
                foreach (var candidate in remotes.ListImportCandidates(clone.Dir))
                {
                    var text=repo.ReadText(ConfigRemotes.ContainedPath(clone.Dir,candidate.RelPath)); if (text != null) available.Add(new(remote.Url,"HEAD",candidate.RelPath,candidate.Name,text));
                }
            }
            return available.Count>0 ? new(true,"") : new(false,"No importable project profiles found in the linked remote repos.");
        },ct);
        if (!prepared.Ok) return prepared;
        var picked=await prompts.PickAsync(new("Import remote config profiles",available.Select((s,i)=>new PickItem(i.ToString(),s.Name+" -- "+ConfigSyncRules.DisplayRemoteUrl(s.RemoteUrl))).ToArray(),true) { Placeholder="Select profiles to import (none pre-selected)" },ct);
        if (picked == null || picked.Count==0) return ActionResult.Cancelled;
        var chosen=picked.Select(s=>int.TryParse(s,out var i) && i>=0 && i<available.Count ? available[i] : null).Where(s=>s!=null).Cast<ImportSelection>().ToArray();
        var rename=new Dictionary<string,string>(); var taken=repo.ReadMainProfiles().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var plan=ConfigSyncRules.PlanUpstreamImport(chosen,remotes.ReadImportManifest(),taken);
        foreach (var collision in plan.Collisions)
        {
            var suggestion=collision.Suggested;
            while (true)
            {
                var name=await prompts.InputAsync(new("Name collision: \""+collision.Name+"\" already exists","Enter a new name for the imported profile (or leave empty to skip)",suggestion),ct);
                if (string.IsNullOrWhiteSpace(name)) break;
                name=name.Trim(); if (!ConfigSyncRules.IsSafeProfileName(name) || ConfigSyncRules.IsReserved(name) || taken.Contains(name)) { suggestion=name; continue; }
                rename[collision.Name]=name; taken.Add(name); break;
            }
        }
        return await ImportSelectedAsync(chosen,rename,ct);
    }
    public Task<ActionResult> ImportSelectedAsync(IReadOnlyList<ImportSelection> selected, IReadOnlyDictionary<string,string>? rename = null, CancellationToken ct = default) => Locked(async()=>
    {
        var imported=0; var warnings=new List<string>();
        foreach (var s in selected)
        {
            if (ConfigSyncRules.UrlHasCredentials(s.RemoteUrl) || !ConfigSyncRules.IsSafeProfileName(s.Name) || ConfigSyncRules.IsReserved(s.Name)) { warnings.Add("Invalid import source."); continue; }
            var names=repo.ReadMainProfiles().Keys.ToHashSet(StringComparer.OrdinalIgnoreCase); var manifest=remotes.ReadImportManifest(); var name=s.Name; var theirs=s.Content;
            var same=manifest.TryGetValue(name,out var old) && old.RemoteUrl==s.RemoteUrl && old.PathInRemote==s.RelPath;
            if (!same && names.Contains(name))
            {
                if (rename == null || !rename.TryGetValue(name,out var newName)) { warnings.Add("Name collision: \""+name+"\" already exists"); continue; }
                if (!ConfigSyncRules.IsSafeProfileName(newName) || ConfigSyncRules.IsReserved(newName) || names.Contains(newName)) { warnings.Add("Imported profile name is no longer available."); continue; }
                name=newName;
                try { var obj=JsonNode.Parse(theirs) as JsonObject; if (obj==null) { warnings.Add("Invalid imported profile."); continue; } obj["name"]=name; theirs=obj.ToJsonString(); } catch (System.Text.Json.JsonException) { warnings.Add("Invalid imported profile."); continue; }
            }
            string content;
            if (same)
            {
                var merge=await remotes.MergeFileAsync(repo.ReadText(repo.FilePath("projects",name)) ?? "",repo.ReadText(repo.FilePath("bases",name)) ?? "",theirs,ct);
                if (!merge.Ok) { warnings.Add("Merge conflict for \""+name+"\" -- keeping local version."); continue; }
                content=merge.Content!;
            }
            else content=theirs;
            var gate=ProfileCodec.CanonicalizeProfileText(name,content); if (!gate.Ok) { warnings.Add("Merged \""+name+"\" is invalid -- keeping local version."); continue; }
            remotes.Adopt(name,gate.Content!,new(s.RemoteUrl,s.Ref,s.RelPath,name),s.Content); imported++;
        }
        var stored=imported>0 ? await repo.CommitAllAsync("import from remote config repos",ct) : new CommitResult(true,false);
        return new(stored.Ok,"Imported "+imported+" profile(s) from remote config repos.",warnings,imported,stored.Output);
    },ct);
    public async Task<ActionResult> PublishAsync(string fromPanel, CancellationToken ct = default)
    {
        var url=ConfigSyncRules.ResolveRemoteUrl(remotes.ReadRemotes(),fromPanel); if (url.Length==0) return ActionResult.Cancelled;
        PublishPlan? plan=null;
        var prepared=await Queued(async()=>
        {
            var preparation=await PreparePublish(url,ct); if (!preparation.Clone.Ok) return new(false,"Could not prepare the config repo clone: "+preparation.Clone.Output);
            if (!preparation.Checkout.Ok) return new(false,"Could not switch the config repo clone to \""+preparation.Branch+"\": "+preparation.Checkout.Output);
            plan=Plan(preparation.Checkout.RemoteFiles); return new(true,"");
        },ct);
        if (!prepared.Ok) return prepared;
        if (plan!.Publish.Count==0) return new(false,"Nothing to publish -- every profile is already tracked or cannot be published. See the Construct log.",plan.SkipTracked.Concat(plan.Refuse).Concat(plan.Invalid).Select(p=>p.Name+": "+p.Reason).ToArray());
        var model=ConfigSyncRules.BuildPublishPickerItems(plan);
        var picked=await prompts.PickAsync(new("Publish project profiles to "+ConfigSyncRules.DisplayRemoteUrl(url),model.Select(p=>new PickItem(p.Label,p.Label,p.Description,p.Picked==true) { Disabled=p.Blocked==true,Separator=p.Kind=="separator" }).ToArray(),true) { Placeholder="Untracked profiles are pre-selected; greyed rows cannot be published" },ct);
        if (picked==null || picked.Count==0) return ActionResult.Cancelled;
        var allowed=plan.Publish.Where(p=>picked.Contains(p.Name)).Select(p=>p.Name).ToArray(); if (allowed.Length==0) return new(true,"Nothing selected to publish.");
        return await PublishSelectedAsync(url,allowed,ct);
    }
    private PublishPlan Plan(IReadOnlyDictionary<string,string> remote, IEnumerable<string>? selected = null)
    {
        var profiles=repo.Files.EnumerateFiles(Path.Combine(repo.Directory,"projects")).Where(p=>p.EndsWith(".json",StringComparison.Ordinal)).Select(p=>
        {
            var name=Path.GetFileNameWithoutExtension(p);
            return new ProfileInput(name,ConfigSyncRules.IsSafeProfileName(name) ? repo.ReadText(p) ?? "" : "");
        });
        return ConfigSyncRules.PlanPublish(profiles,remotes.ReadImportManifest(),remote,selected);
    }
    private async Task<(CloneResult Clone,string Branch,CheckoutResult Checkout)> PreparePublish(string url, CancellationToken ct)
    {
        var clone=await remotes.EnsurePublishCloneAsync(url,ct); var branch=clone.Ok && !clone.Created ? await remotes.RemoteDefaultBranchAsync(url,ct) : "main"; if (!ConfigSyncRules.IsValidPublishBranch(branch)) branch="main";
        var co=clone.Ok ? await remotes.CheckoutPublishBranchAsync(clone.Dir,branch,ct) : new CheckoutResult(false,new Dictionary<string,string>());
        return (clone,branch,co);
    }
    public Task<ActionResult> PublishSelectedAsync(string url, IReadOnlyList<string> selected, CancellationToken ct = default) => Queued(async()=>
    {
        // Staging network operations do not hold the cross-process config lock:
        // PowerShell provisioning only waits 90 seconds for that lock.
        var prep=await PreparePublish(url,ct); if (!prep.Clone.Ok) return new(false,"Could not prepare the config repo clone: "+prep.Clone.Output); if (!prep.Checkout.Ok) return new(false,prep.Checkout.Output);
        PublishPlan? plan=null;
        var planned=await LockedCore(()=>
        {
            plan=Plan(prep.Checkout.RemoteFiles,selected);
            return Task.FromResult(plan.Publish.Count==0 ? new ActionResult(false,"Nothing selected to publish.",plan.Refuse.Concat(plan.Invalid).Concat(plan.SkipTracked).Select(p=>p.Name+": "+p.Reason).ToArray()) : new ActionResult(true,""));
        },ct);
        if(!planned.Ok) return planned;
        var pushed=await remotes.PublishToRemoteAsync(prep.Clone.Dir,prep.Branch,plan!.Publish,ct); if (!pushed.Ok) return new(false,"Publish failed: "+pushed.Output);
        var adopted=await LockedCore(async()=>
        {
            var manifest=remotes.ReadImportManifest();
            foreach(var file in plan.Publish)
            {
                if(manifest.ContainsKey(file.Name) || repo.ReadText(repo.FilePath("projects",file.Name))==null)
                    return new ActionResult(false,"Published, but local provenance changed before adoption; import the published profiles to reconcile.");
            }
            foreach (var file in plan.Publish)
            {
                var local=repo.ReadText(repo.FilePath("projects",file.Name))!;
                // An external engine may have synced a local edit during the push.
                // Preserve that edit while recording the bytes actually published as base.
                var gate=ProfileCodec.CanonicalizeProfileText(file.Name,local);
                remotes.Adopt(file.Name,gate.Ok && gate.Content==file.Content ? file.Content : local,ConfigSyncRules.PublishManifestEntry(url,prep.Branch,file.Name,pushed.Commit,pushed.BlobShas[file.Name]),file.Content);
            }
            var stored=await repo.CommitAllAsync("publish: "+string.Join(", ",plan.Publish.Select(p=>p.Name)),ct);
            return new ActionResult(stored.Ok && stored.Committed,stored.Ok && stored.Committed ? "Published "+plan.Publish.Count+" profile(s) to "+prep.Branch+" -- they are tracked now." : "Published, but the local config store could not be committed -- see the Construct log.",Count:plan.Publish.Count,Detail:stored.Output);
        },ct);
        return adopted.Ok || adopted.Message.StartsWith("Published",StringComparison.Ordinal) ? adopted : adopted with { Message="Published, but local adoption could not finish: "+adopted.Message };
    },ct);
    public async Task<ActionResult> PushUpstreamAsync(string fromPanel, CancellationToken ct = default)
    {
        var url=ConfigSyncRules.ResolveRemoteUrl(remotes.ReadRemotes(),fromPanel); if (url.Length==0) return ActionResult.Cancelled;
        if (!await prompts.ConfirmAsync("Push","This commits your local versions of the files imported from "+ConfigSyncRules.DisplayRemoteUrl(url)+" to a new branch and pushes.",ct)) return ActionResult.Cancelled;
        return await Queued(async()=>
        {
            var clone=await remotes.EnsureStagingCloneAsync(url,ct); if (!clone.Ok) return new(false,"Push failed: "+clone.Output);
            var branch="construct-config-update-"+clock.UtcNow.ToString("yyyyMMdd-HHmm",System.Globalization.CultureInfo.InvariantCulture);
            var files=remotes.ReadImportManifest().Where(p=>p.Value.RemoteUrl==url).Select(p=>new UpstreamFile(repo.FilePath("projects",p.Key),p.Value.PathInRemote));
            var pushed=await remotes.PushUpstreamAsync(clone.Dir,files,branch,"config update from The Construct ("+branch+")",ct);
            return new(pushed.Ok,pushed.Ok ? "Pushed to branch \""+branch+"\" -- create a PR from that branch." : "Push failed: "+pushed.Output);
        },ct);
    }
    public async Task<ActionResult> ShareAsync(string installRepo = ConfigSharing.DefaultRepo, string installRef = ConfigSharing.DefaultRef, CancellationToken ct = default)
    {
        var profiles=repo.ReadMainProfiles(); if (profiles.Count==0) return new(true,"No shareable project profiles found.");
        var picked=await prompts.PickAsync(new("Share project profiles",profiles.Keys.Select(n=>new PickItem(n,n)).ToArray(),true) { Placeholder="Select profiles to share" },ct);
        if (picked==null || picked.Count==0) return ActionResult.Cancelled;
        var names=picked.Where(profiles.ContainsKey).Distinct().ToArray(); if (names.Length==0) return ActionResult.Cancelled;
        var manifest=remotes.ReadImportManifest(); var tracked=names.Where(n=>manifest.TryGetValue(n,out var entry) && !string.IsNullOrEmpty(entry.RemoteUrl)).ToArray(); var urls=tracked.Select(n=>manifest[n].RemoteUrl).Distinct().ToArray();
        if (tracked.Length==names.Length && urls.Length==1)
        {
            if (ConfigSyncRules.UrlHasCredentials(urls[0])) return new(false,"Remove the credentials from the URL -- let your git credential helper supply them.");
            await clipboard.WriteTextAsync(ConfigSharing.BuildShareCommand(urls[0],names,installRepo,installRef),ct); return new(true,"Share command copied to clipboard.");
        }
        var target=await prompts.SaveFileAsync(new("Save shared config bundle",Path.Combine(repo.Files.GetRoot(FileSystemRoot.UserProfile) ?? "","construct-config.zip"),"Zip archive|*.zip"),ct); if (target==null) return ActionResult.Cancelled;
        var warnings=new List<string>();
        using var bytes=new MemoryStream();
        using (var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true))
        {
            void Entry(string path,string text) { using var writer=new StreamWriter(zip.CreateEntry(path,CompressionLevel.NoCompression).Open(),new UTF8Encoding(false)); writer.Write(text); }
            Entry("deploy.ps1",ConfigSharing.BuildDeployPs1(installRepo,installRef));
            foreach (var name in names) { var gate=ProfileCodec.CanonicalizeProfileText(name,profiles[name]); if (gate.Ok) Entry("projects/"+name+".json",gate.Content!); else warnings.Add("Skipped invalid profile \""+name+"\": "+gate.Reason); }
        }
        repo.Files.WriteFileAtomic(target,bytes.ToArray()); return new(true,"Config bundle saved to "+target,warnings);
    }
    private Task<ActionResult> Locked(Func<Task<ActionResult>> action,CancellationToken ct) => Queued(()=>LockedCore(action,ct),ct);
    private async Task<ActionResult> Queued(Func<Task<ActionResult>> action, CancellationToken ct)
    {
        if(repositoryQueue!=null) await repositoryQueue.WaitAsync(ct);
        try { return await action(); }
        catch(ConfigSyncException e) { return new(false,e.Message); }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException) { return new(false,"Could not access the config repository or profile."); }
        finally { repositoryQueue?.Release(); }
    }
    private async Task<ActionResult> LockedCore(Func<Task<ActionResult>> action,CancellationToken ct)
    {
        repo.EnsureConfigTree(); if (syncLock.ProvisionSyncPending()) return new(false,"provisioning sync is pending");
        var token=syncLock.Acquire(); if (token==null) return new(false,"sync lock held by another window/engine; retry when it finishes");
        try
        {
            var init=await repo.EnsureRepoAsync(ct:ct); if (!init.Repo) return new(false,"Git is not available. Install git first.");
            var state=await repo.StateAsync(ct); if (state.Conflict || state.MergeInProgress) return new(false,"unresolved merge in config repo");
            return await action();
        }
        catch (ConfigSyncException e) { return new(false,e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return new(false,"Could not access the config repository or profile."); }
        finally { syncLock.Release(token); }
    }
}
public sealed record ActionResult(bool Ok, string Message, IReadOnlyList<string>? Warnings = null, int Count = 0, string Detail = "")
{
    public static ActionResult Cancelled => new(true,"");
}
