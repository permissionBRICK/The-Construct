using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.ConfigSync;

namespace Construct.Companion.Host.ConfigSync;

// storeRoot is overridden only by tests that stand in for the VM with a local directory.
public sealed class ConfigSyncEngine(ConfigRepository repo, SyncLock syncLock, ISshTransport ssh, string vmBranch = "vm", string storeRoot = StoreScripts.DefaultRoot)
{
    public async Task<SyncResult> SyncTickAsync(CancellationToken ct = default)
    {
        repo.EnsureConfigTree();
        if (syncLock.ProvisionSyncPending()) return SyncResult.Busy("provision-pending");
        var token = syncLock.Acquire(); if (token == null) return SyncResult.Busy("lock-busy");
        var result = new SyncResult();
        try { await TickLocked(result, ct); }
        catch (ConfigSyncException e) { result.Blocked = true; result.BlockedReason = e.Message; result.Warnings.Add(e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { result.Blocked = true; result.BlockedReason = "config sync could not access a profile or repository file"; }
        finally { syncLock.Release(token); }
        return result;
    }
    private async Task<string?> RunStore(string script, CancellationToken ct)
    {
        try { var r = await ssh.RunRemoteScriptAsync(script, TimeSpan.FromSeconds(30), ct); return r.Code < 0 ? null : r.Stdout; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; } // any transport fault reads as "VM store unreadable"; the caller warns once
    }
    private async Task TickLocked(SyncResult result, CancellationToken ct)
    {
        var branch = ConfigSyncRules.ResolveVmBranch(vmBranch, result.Warnings.Add);
        var init = await repo.EnsureRepoAsync(branch, ct);
        if (!init.Repo)
        {
            if (init.DubiousOwnership) { result.Blocked = true; result.BlockedReason = "config folder is owned by another Windows account, so git refuses to sync it — reprovision (it repairs ownership automatically) or run from an elevated prompt: icacls \"" + repo.Directory + "\" /setowner \"%USERNAME%\" /T"; result.Warnings.Add(result.BlockedReason); }
            else result.Warnings.Add("git not available; sync skipped");
            return;
        }
        var pending = await repo.CompletePendingMergeAsync(branch, ct); result.Merged = pending.Completed;
        if (!pending.Ok) { result.Conflict = pending.Conflict; result.Blocked = pending.Blocked || !pending.Conflict; result.BlockedReason = pending.Reason; return; }
        var state = await repo.StateAsync(ct);
        if (state.Conflict || state.MergeInProgress) { result.Conflict = state.Conflict; result.Blocked = true; result.BlockedReason = "unresolved merge in config repo"; return; }
        result.Ran = true;
        await repo.CommitHostDirtyFilesAsync(result, ct);
        var parsed = StoreScripts.ParseReadStore(await RunStore(StoreScripts.BuildReadStoreScript(storeRoot), ct));
        if (parsed == null) { result.VmReadOk = false; result.Warnings.Add("could not read VM store (SSH unreachable or truncated)"); result.Ok = true; return; }
        result.VmReadOk = true;
        var raw = new Dictionary<string, string>(); var valid = new Dictionary<string, string>(); var preserve = new HashSet<string>();
        foreach (var entry in parsed.Entries)
        {
            if (ConfigSyncRules.IsReserved(entry.Name)) { preserve.Add(entry.Name); continue; }
            var gate = ProfileCodec.CanonicalizeProfileText(entry.Name, entry.Content);
            if (!gate.Ok || !ConfigSyncRules.IsSafeProfileName(entry.Name))
            {
                var reason = gate.Reason ?? "is not a safe profile file name";
                result.SkippedInvalid.Add(new(entry.Name, reason)); result.Warnings.Add(ConfigSyncRules.RedactGitOutput($"invalid VM profile \"{entry.Name}\": {reason}; skipped")); preserve.Add(entry.Name); continue;
            }
            raw[entry.Name] = entry.Content; valid[entry.Name] = gate.Content!;
        }
        var count = await repo.CountVmBranchProfilesAsync(branch, ct);
        var fresh = valid.Count == 0 && (parsed.StoreAbsent || count == 0);
        if (fresh)
        {
            result.Seeded = true;
            if (await WriteBack(repo.ReadMainProfiles().Select(p => new WriteOperation(p.Key, "write", null, p.Value)), result, true, ct)) await Advance(branch, ct);
            result.Ok = true; return;
        }
        if (valid.Count == 0 && count > 0)
        {
            result.Warnings.Add($"VM store has no valid profiles but the {branch} branch has {count}; refusing to propagate a mass deletion (delete profiles individually if intended)");
            await WriteBack(repo.ReadMainProfiles().Select(p => new WriteOperation(p.Key, "write", null, p.Value)), result, true, ct, "reseed"); result.Seeded = result.WriteBack.Done.Count > 0; result.Ok = true; return;
        }
        var changed = await repo.CommitVmBranchAsync(valid, preserve, branch, ct);
        var ancestor = await repo.Git.RunAsync(repo.Directory, ["merge-base", "--is-ancestor", branch, "main"], cancellationToken: ct);
        if (ancestor.Code == 0 && !changed)
        {
            if (await WriteBack(ConfigSyncRules.PlanWriteBack(repo.ReadMainProfiles(), raw), result, false, ct)) await Advance(branch, ct);
            result.Ok = true; return;
        }
        var mainTree = await repo.Git.RequireAsync(repo.Directory, ["rev-parse", "main^{tree}"], cancellationToken: ct);
        var vmTree = await repo.Git.RequireAsync(repo.Directory, ["rev-parse", branch + "^{tree}"], cancellationToken: ct);
        if (mainTree == vmTree) { await WriteBack(ConfigSyncRules.PlanWriteBack(repo.ReadMainProfiles(), raw), result, false, ct, "quiet"); result.Ok = true; return; }
        var merge = await repo.Git.RunAsync(repo.Directory, ["merge", "--no-ff", "--no-commit", branch], true, cancellationToken: ct);
        if (merge.Code != 0)
        {
            state = await repo.StateAsync(ct);
            if (state.Conflict) { result.Conflict = true; result.Warnings.Add("merge conflict in config repo"); return; }
            result.Blocked = true;
            if (merge.Stderr.Contains("would be overwritten", StringComparison.Ordinal) || merge.Stderr.Contains("not possible because you have unmerged files", StringComparison.Ordinal)) result.BlockedReason = "merge refused: uncommitted changes in projects/ would be overwritten — fix or remove the invalid file and retry";
            else { await repo.Git.RunAsync(repo.Directory, ["merge", "--abort"], cancellationToken: ct); result.BlockedReason = "merge failed: " + ConfigSyncRules.RedactGitOutput(merge.Stderr.Trim()); }
            result.Warnings.Add(result.BlockedReason); return;
        }
        var errors = repo.ValidateWorkingTreeProfiles();
        if (errors.Count > 0) { result.Blocked = true; result.BlockedReason = "post-merge validation failed: " + string.Join("; ", errors); result.Warnings.Add(result.BlockedReason); return; }
        var commit = await repo.Git.RunAsync(repo.Directory, ["commit", "-m", "sync merge " + branch], true, cancellationToken: ct);
        if (commit.Code != 0) { result.Blocked = true; result.BlockedReason = "merge commit failed: " + ConfigSyncRules.RedactGitOutput((string.IsNullOrEmpty(commit.Stderr) ? commit.Stdout : commit.Stderr).Trim()); result.Warnings.Add(result.BlockedReason); return; }
        result.Merged = true;
        if (await WriteBack(ConfigSyncRules.PlanWriteBack(repo.ReadMainProfiles(), raw), result, false, ct)) await Advance(branch, ct);
        result.Ok = true;
    }
    private Task<string> Advance(string branch, CancellationToken ct) => repo.Git.RequireAsync(repo.Directory, ["update-ref", "refs/heads/" + branch, "refs/heads/main"], cancellationToken: ct);
    private async Task<bool> WriteBack(IEnumerable<WriteOperation> operations, SyncResult result, bool seed, CancellationToken ct, string mode = "normal")
    {
        var ops = operations.ToArray(); if (ops.Length == 0) return true;
        var wb = StoreScripts.ParseWriteResult(await RunStore(StoreScripts.BuildWriteStoreScript(ops, storeRoot), ct));
        if (wb == null) { if (mode != "quiet") result.Warnings.Add(mode == "reseed" ? "re-seed write-back to the VM store failed" : "write-back to VM store failed; vm ref not advanced"); return false; }
        result.WriteBack = wb;
        if (wb.Skipped.Count == 0) return true;
        if (mode == "normal") result.Warnings.Add((seed ? "seed " : "") + "write-back skipped concurrently changed profile(s): " + string.Join(", ", wb.Skipped) + "; sync base not advanced"); return false;
    }
}
