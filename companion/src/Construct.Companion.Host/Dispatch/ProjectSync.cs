using System.Text.Json.Nodes;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;

namespace Construct.Companion.Host.Dispatch;

public sealed partial class MessageDispatcher
{
    // Callers hold entry.Serial, so the scan, selection and transport belong to one instance.
    public async Task SyncProjectsAsync(CompanionInstance entry, CancellationToken ct, bool automatic = false)
    {
        var scanDue = !automatic || entry.ProjectsScannedAt is null || clock.UtcNow - entry.ProjectsScannedAt >= TimeSpan.FromMinutes(5);
        var root = ProjectRoot(entry);
        var before = instances.Host.ListProjectProfiles(root);
        if (entry.ConfigSync is {} area && (!automatic || area.Runtime.DueForAuto))
        {
            var result = await area.Runtime.SyncNowAsync(ct);
            if (result.Ok && !result.LockBusy) await EnableNewProjects(entry, before, ct);
            if (result.Blocked || result.Conflict || result.LockBusy) return;
        }
        if (!scanDue) return;
        entry.ProjectsScannedAt = clock.UtcNow;
        before = instances.Host.ListProjectProfiles(root);
        var failed = await ImportVmProjects(entry, ct);
        if (failed is null || failed.Count > 0)
            logs.Write(failed is null ? "Project discovery could not scan VM repositories." : "Project discovery could not save profiles: " + string.Join(", ", failed));
        if (entry.ConfigSync is {} sync && instances.Host.ListProjectProfiles(root).Except(before).Any())
            await sync.Runtime.SyncNowAsync(ct);
    }

    private async Task EnableNewProjects(CompanionInstance entry, IEnumerable<string> before, CancellationToken ct)
    {
        var available = instances.Host.ListProjectProfiles(ProjectRoot(entry)).ToHashSet(StringComparer.Ordinal);
        var added = available.Except(before).Where(EditableProject).ToArray();
        if (added.Length == 0) return;
        var current = entry.Store.HasPersistedSelection() ? entry.Store.ReadSelectedProjects() : await EffectiveProjects(entry, ct);
        entry.Store.SaveSelectedProjects(new JsonArray(current.Select(StateJson.String).Concat(added).Where(available.Contains).Distinct().Select(n => (JsonNode?)JsonValue.Create(n)).ToArray()));
    }
}
