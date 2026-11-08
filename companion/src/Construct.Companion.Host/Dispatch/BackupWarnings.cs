using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Backups;
using Construct.Companion.Core.Notifications;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
namespace Construct.Companion.Host.Dispatch;

// Config backups (docs/plans/host-config-backups.md §3.7): shortly after start and every 15 minutes, the backup status
// of each remote instance whose host advertises config-backups. One Windows notification per (instance, failingSince):
// the streak already warned about is kept in the instance's state (BackupProtocol.StreakKey, the key the extension's
// fallback uses too), so a restart does not repeat it.
public sealed class BackupWarnings(CompanionInstances instances, HostAdministration hosts, IToastRaiser toasts, IClock clock, IpcLogs logs)
{
    public static readonly TimeSpan FirstCheck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    public async Task PollAsync(CancellationToken ct)
    {
        try
        {
            // A short grace first: the instance runtimes and host detection settle before the first round.
            await clock.DelayAsync(FirstCheck, ct);
            while (!ct.IsCancellationRequested) { await CheckAllAsync(ct); await clock.DelayAsync(Interval, ct); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
    public async Task CheckAllAsync(CancellationToken ct)
    {
        foreach (var name in instances.Names)
        {
            try { await CheckAsync(instances.Get(name), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { logs.Failure("Backup status", e); }
        }
    }
    public async Task CheckAsync(CompanionInstance entry, CancellationToken ct)
    {
        if (instances.Remote(entry) is not { } client) return;
        try
        {
            if (!await hosts.SupportsAsync(client, "configBackups", ct)) return;
            await NoteAsync(entry, await client.VmBackupStatusAsync(HostAdministration.BackupVmName(entry), ct), ct);
        }
        catch (RemoteApiException) { } // an unreachable host is asked again next round
    }
    public async Task NoteAsync(CompanionInstance entry, JsonNode? status, CancellationToken ct)
    {
        var state = entry.Store.ReadState(); var warned = state[BackupProtocol.StreakKey];
        var warning = BackupProtocol.Warning(entry.Name, status, warned);
        var remember = StateJson.Text(warning["remember"]);
        if (remember != StateJson.Text(warned))
        {
            // Remembered before the toast: a notification that fails to show is not retried every round.
            if (remember is null) { state.Remove(BackupProtocol.StreakKey); entry.Store.ReplaceState(state); }
            else entry.Store.SaveState(new JsonObject { [BackupProtocol.StreakKey] = remember });
        }
        if (StateJson.Boolean(warning["notify"]) != true) return;
        try
        {
            if (await toasts.GetAvailabilityAsync(ct) != ToastAvailability.Available) return;
            await toasts.RaiseAsync(NotificationProtocol.Toast(new JsonObject { ["level"] = "warning", ["title"] = warning["title"]?.DeepClone(),
                ["body"] = warning["body"]?.DeepClone(), ["source"] = "Config backups" }, NotificationProtocol.ClickUri(entry.Name)), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) { logs.Failure("Backup warning", e); }
    }
}
