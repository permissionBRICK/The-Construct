using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.ConfigSync;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core;
using Construct.Companion.Core.Probe;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.ConfigSync;
namespace Construct.Companion.Host.Dispatch;

public sealed partial class MessageDispatcher
{
    private async Task<JsonArray> EffectiveProjects(CompanionInstance entry, CancellationToken ct)
    {
        var saved = entry.Store.ReadSelectedProjects();
        if (saved.Count > 0) return saved;
        if (entry.Runtime is {} runtime) await runtime.ProbeOnceAsync(ct);
        var live = state.State(entry.Name)["state"];
        if (entry.Runtime is null)
        {
            var probe = await entry.Ssh.RunRemoteScriptAsync(GuestScripts.Render("probe"), TimeSpan.FromSeconds(20), ct);
            if (probe.Code == 0) live = ProbeParser.ToState(ProbeParser.ParseProbe(probe.Stdout));
        }
        return new JsonArray((live?["projects"] as JsonArray ?? []).OfType<JsonObject>()
            .Where(p => StateJson.Boolean(p["selected"]) != false && StateJson.Text(p["name"]) is { Length: > 0 })
            .Select(p => p["name"]!.DeepClone()).ToArray());
    }
    private async Task<bool> LifecyclePreflight(CompanionInstance entry, string action, CancellationToken ct)
    {
        var label = action == "reprovision" ? "reprovisioning" : action == "reinstall" ? "reinstalling" : "redownloading";
        var import = await ImportVmProjects(entry, ct);
        if (import is null)
        {
            if (!await prompts.ConfirmAsync(new ConfirmationPrompt("Continue anyway", "Could not reach the VM to check for new project configs. Proceeding may miss repos not yet imported. Continue with " + label + "?", "Continue anyway"), ct)) return false;
        }
        else if (import.Count > 0 && !await prompts.ConfirmAsync(new ConfirmationPrompt("Continue anyway", "Some project configs could not be written: " + string.Join(", ", import) + ". Continue with " + label + "?", "Continue anyway"), ct)) return false;
        if (entry.ConfigSync is not {} area) return true;
        var before = await area.Runtime.BuildStateAsync(ct);
        if (!before.GitPresent) return true;
        SyncResult? result;
        try { result = await area.Runtime.SyncNowAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { result = null; }
        if (result is null || result.LockBusy || result.Blocked || !result.Ok && !result.Conflict || result.Ok && result.VmReadOk == false)
        {
            var reason = result?.LockBusy == true ? "Another VS Code window holds the sync lock."
                : result?.Blocked == true ? "Sync is blocked: " + (result.BlockedReason ?? "unknown")
                : result?.Ok == true && result.VmReadOk == false ? "Could not read VM config store — sync is incomplete." : "Config sync did not complete successfully.";
            if (!await prompts.ConfirmAsync(new ConfirmationPrompt("Continue anyway", reason + " Profiles may not be up to date. Continue with " + label + "?", "Continue anyway"), ct)) return false;
        }
        try
        {
            var blocked = await area.Runtime.LifecycleBlockedAsync(Text(entry.Definition, "configBranch"), ct);
            if (blocked)
            {
                Refuse(entry.Name, action, "Config sync has unresolved conflicts — resolve them before " + label + ".\n\nOpen the config repo, resolve the merge conflicts in the editor, then commit and retry. The Config sync strip in the Projects panel lists the conflicting files.");
                return false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { Refuse(entry.Name, action, "Could not verify config sync state before " + label + ". Check the config repo for issues, then try again." + (e is ConfigSyncException ? " (" + e.Message + ")" : "")); return false; }
        return true;
    }
    private async Task<List<string>?> ImportVmProjects(CompanionInstance entry, CancellationToken ct)
    {
        JsonArray? scan;
        try
        {
            var result = await entry.Ssh.RunRemoteScriptAsync(ProjectImport.ScanScript(), TimeSpan.FromSeconds(60), ct);
            scan = result.Code == 0 ? ProjectImport.ParseScan(result.Stdout) : null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
        if (scan is null) return null;
        var root = ProjectRoot(entry); var before = instances.Host.ListProjectProfiles(root);
        var profiles = new JsonObject();
        foreach (var name in before) if (instances.Host.ReadProjectProfile(root, name) is {} profile) profiles[name] = profile;
        var deleted = entry.ConfigSync is {} area ? await DeletedProfiles(area.Repository, before, ct) : (new HashSet<string>(), new HashSet<string>());
        var plan = ProjectImport.Plan(scan, profiles, deleted.Item1, deleted.Item2); var failed = new List<string>(); var added = new List<string>();
        foreach (var item in plan["toWrite"]!.AsArray().OfType<JsonObject>())
        {
            var name = Text(item, "name");
            try
            {
                var profile = item["profile"]!.AsObject();
                if (!EditableProject(name) || ProfileCodec.ValidateProfile(name, profile).Count > 0) { failed.Add(name); continue; }
                if (instances.Host.WriteProjectProfileIfAbsent(root, name, profile)) added.Add(name);
            }
            catch (Exception) { failed.Add(name); }
        }
        if (added.Count > 0)
        {
            var current = entry.Store.HasPersistedSelection() ? entry.Store.ReadSelectedProjects() : await EffectiveProjects(entry, ct);
            var available = instances.Host.ListProjectProfiles(root).ToHashSet(StringComparer.Ordinal);
            entry.Store.SaveSelectedProjects(new JsonArray(current.Select(StateJson.String).Concat(added).Where(available.Contains).Distinct().Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
        }
        return failed;
    }
    private static async Task<(HashSet<string>, HashSet<string>)> DeletedProfiles(ConfigRepository repo, IEnumerable<string> current, CancellationToken ct)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var urls = new HashSet<string>(StringComparer.Ordinal); var paths = new HashSet<string>();
        var present = current.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var args in new string[][] { ["diff", "--name-only", "--diff-filter=D", "--", "projects/"], ["log", "--all", "--name-only", "--pretty=format:", "--diff-filter=D", "--", "projects/"] })
        {
            var r = await repo.Git.RunAsync(repo.Directory, args, cancellationToken: ct);
            foreach (var line in r.Stdout.Split('\n')) { var path = line.Trim().Replace('\\', '/'); if (Regex.IsMatch(path, "^projects/[^/]+\\.json$")) paths.Add(path); }
        }
        foreach (var path in paths)
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (present.Contains(name) || ConfigSyncRules.IsReserved(name)) continue;
            names.Add(name);
            var commits = await repo.Git.RunAsync(repo.Directory, ["log", "--all", "--max-count=20", "--format=%H", "--", path], cancellationToken: ct);
            var found = false;
            foreach (var commit in commits.Stdout.Split('\n').Select(s => s.Trim()).Where(s => Regex.IsMatch(s, "^[a-fA-F0-9]{40,64}$")))
            {
                foreach (var spec in new[] { commit + ":" + path, commit + "^:" + path })
                {
                    var shown = await repo.Git.RunAsync(repo.Directory, ["show", spec], cancellationToken: ct);
                    if (shown.Code != 0 || StateJson.ParseObject(shown.Stdout) is not {} profile) continue;
                    foreach (var item in (profile["repos"] as JsonArray ?? []).OfType<JsonObject>()) if (StateJson.Text(item["url"]) is {} url && url.Trim().Length > 0) urls.Add(url.Trim());
                    found = true; break;
                }
                if (found) break;
            }
        }
        return (names, urls);
    }
}
