using System.Text.Json.Nodes;
using Construct.Companion.Core.Backups;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
using HostIdentity = Construct.Companion.Core.Remote.RemoteHost;
namespace Construct.Companion.Host.Dispatch;

// Config backups (docs/plans/host-config-backups.md §3.7, §3.8): the panel's Backups module of one instance. A remote VM
// whose host advertises config-backups gets the host's status, list and per-VM settings; every VM gets this PC's key
// file. The model itself is BackupProtocol.PanelModel (parity with extension/src/backups.js).
public sealed partial class HostAdministration
{
    // The VM's name on its host service (pinned to the instance name for remote instances).
    public static string BackupVmName(CompanionInstance entry) => StateJson.Nonempty(entry.Definition["vmName"]) ?? entry.Name;
    // Does this client's host advertise the feature flag (HostAdminProtocol.Features key)? Detection is shared with the
    // host-administration window: at most once a minute per host.
    public async Task<bool> SupportsAsync(RemoteHostClient client, string flag, CancellationToken ct)
    {
        var model = Get(HostIdentity.HostSlug(client.BaseUrl));
        await model.Serial.WaitAsync(ct);
        try { await DetectIfStale(model, client, ct); }
        finally { model.Serial.Release(); }
        return StateJson.Boolean(model.View["features"]?[flag]) == true;
    }
    // This PC's backup key description (pc-key.json, written by lib/AgentVm.Backup.ps1), or null.
    private JsonObject? PcBackupKey() => StateJson.ReadObject(files, BackupProtocol.PcKeyPath(instances.Host.LocalAppData));
    // The module's model, or null to hide it (a remote VM on a host without the feature, or a host that cannot be asked).
    public async Task<JsonObject?> BackupsAsync(CompanionInstance entry, CancellationToken ct)
    {
        if (instances.Remote(entry) is not { } client)
            return BackupProtocol.PanelModel(new JsonObject { ["remote"] = false, ["key"] = PcBackupKey() }, clock.UtcNow);
        try { if (!await SupportsAsync(client, "configBackups", ct)) return null; }
        catch (RemoteApiException) { return null; }
        catch (IpcFailure) { return null; }
        var name = BackupVmName(entry); string? problem = null;
        async Task<JsonNode?> Read(Func<Task<JsonNode?>> call)
        {
            try { return await call(); }
            catch (RemoteApiException e) { problem ??= "Could not read the host's backups: " + e.Message; return null; }
        }
        var status = await Read(() => client.VmBackupStatusAsync(name, ct));
        var list = await Read(() => client.VmBackupsAsync(name, ct));
        var settings = await Read(() => client.VmBackupSettingsAsync(name, ct));
        return BackupProtocol.PanelModel(new JsonObject { ["remote"] = true, ["supported"] = true, ["status"] = status?.DeepClone(), ["list"] = list?.DeepClone(),
            ["settings"] = settings?.DeepClone(), ["key"] = PcBackupKey(), ["problem"] = problem }, clock.UtcNow);
    }
    public async Task PublishBackupsAsync(CompanionInstance entry, CancellationToken ct) =>
        bus.Publish(entry.Name, new { type = "backups", instance = entry.Name, backups = await BackupsAsync(entry, ct) });
}
