using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Backups;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
namespace Construct.Companion.Host.Dispatch;

// Config backups (docs/plans/host-config-backups.md §3.8): the panel's Backups module. Host calls go through the
// instance's service; the file and key buttons launch Provision-AgentVM.ps1's backup actions like Export config
// (not elevated, not a lifecycle run). The extension's fallback does the same in extension.js runBackupCommand.
public sealed partial class MessageDispatcher
{
    private const string BackupFilter = "Construct backup (*.cbk)|*.cbk|All files (*.*)|*.*";
    private const string KeyFilter = "Backup key (*.json)|*.json|All files (*.*)|*.*";
    private static string BackupLabel(string id) => id switch
    {
        "backupNow" => "Back up now", "backupEnroll" => "Enrol this PC", "backupSave" => "Save to this PC", "backupRestore" => "Restore",
        "backupRestoreFile" => "Restore from file", "backupKeyExport" => "Export backup key", "backupKeyImport" => "Import backup key", _ => "Backups"
    };
    private static string RequireBackupId(JsonNode? value) => BackupProtocol.IsBackupId(value) ? StateJson.String(value) : throw new IpcFailure(400, "invalidBackup", "Invalid backup id.");
    // The override's "apply", re-validated here (the webview is untrusted input) against the host's own maxKeep.
    private async Task SaveBackupSettings(CompanionInstance entry, JsonObject form, CancellationToken ct)
    {
        var client = instances.Remote(entry) ?? throw new IpcFailure(409, "localInstance", "Backup settings live on a remote host service.");
        var name = HostAdministration.BackupVmName(entry);
        try
        {
            var current = await client.VmBackupSettingsAsync(name, ct);
            var parsed = BackupProtocol.ParseOverride(form, current?["host"]?["maxKeep"]);
            if (StateJson.Boolean(parsed["ok"]) != true)
            { Refuse(entry.Name, "saveBackupSettings", string.Join(" ", parsed["problems"]!.AsArray().Select(p => StateJson.String(p?["reason"]) + "."))); return; }
            await client.SetVmBackupSettingsAsync(name, parsed["body"]!.DeepClone(), ct);
        }
        catch (RemoteApiException e) { Refuse(entry.Name, "saveBackupSettings", "Couldn't save the backup settings: " + e.Message); }
        finally { await hosts.PublishBackupsAsync(entry, ct); }
    }
    private async Task BackupCommand(CompanionInstance entry, string id, JsonObject m, CancellationToken ct)
    {
        var name = entry.Name; var remote = instances.Remote(entry); var label = BackupLabel(id);
        if (id == "backupNow")
        {
            if (remote is null) { Refuse(name, id, "Host backups need a VM on a host service."); return; }
            try
            {
                await remote.RequestVmBackupAsync(HostAdministration.BackupVmName(entry), ct);
                events.Companion(new { type = "notification", level = "info", text = $"Backup of “{name}” requested. It starts at the VM's next check-in." });
            }
            catch (RemoteApiException e) { Refuse(name, id, $"{label} failed: {e.Message}"); }
            await hosts.PublishBackupsAsync(entry, ct); return;
        }
        var backupId = remote is not null && BackupProtocol.IsBackupId(m["backup"]) ? Text(m, "backup") : "";
        switch (id)
        {
            case "backupEnroll": await Lifecycle(entry, "backupEnroll", m, ct); return;
            case "backupSave":
                var target = await prompts.SaveFileAsync(new($"Save a backup of “{name}”", BackupProtocol.FileName(name, clock.UtcNow), BackupFilter), ct);
                if (target is null) return;
                var save = new JsonObject { ["backupFile"] = target }; if (backupId.Length > 0) save["backupId"] = backupId;
                await Lifecycle(entry, "backupSave", m, ct, save); return;
            case "backupRestore":
                if (remote is null) { Refuse(name, id, "Host backups need a VM on a host service."); return; }
                // The id must still be listed on the host: the panel's list may be minutes old.
                var model = await hosts.BackupsAsync(entry, ct);
                var row = (model?["items"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(b => StateJson.Text(b["id"]) == backupId);
                if (backupId.Length == 0 || row is null) { Refuse(name, id, "That backup is no longer listed on the host. Refresh and pick it again."); return; }
                if (!await ConfirmRestore(name, "the backup from " + StateJson.Text(row["when"]), ct)) return;
                await Lifecycle(entry, "backupRestore", m, ct, new() { ["backupId"] = backupId }); return;
            case "backupRestoreFile":
                var file = await prompts.OpenFileAsync(new($"Restore a backup file onto “{name}”", BackupFilter), ct);
                if (file is null || !await ConfirmRestore(name, $"“{Path.GetFileName(file)}”", ct)) return;
                await Lifecycle(entry, "backupRestore", m, ct, new() { ["backupFile"] = file }); return;
            case "backupKeyExport":
                var keyTarget = await prompts.SaveFileAsync(new("Export this PC's backup key", BackupProtocol.KeyFileName, KeyFilter), ct);
                if (keyTarget is not null) await Lifecycle(entry, "backupKeyExport", m, ct, new() { ["backupKeyFile"] = keyTarget });
                return;
            case "backupKeyImport":
                var keySource = await prompts.OpenFileAsync(new("Import a backup key", KeyFilter), ct);
                if (keySource is not null) await Lifecycle(entry, "backupKeyImport", m, ct, new() { ["backupKeyFile"] = keySource });
                return;
        }
    }
    private Task<bool> ConfirmRestore(string instance, string what, CancellationToken ct) => prompts.ConfirmAsync(new ConfirmationPrompt($"Restore {what} onto “{instance}”?",
        "This replaces the VM's agent logins, memory, chat history, secrets and git credentials with the backup's. Project repositories are not touched.", "Restore"), ct);
}
