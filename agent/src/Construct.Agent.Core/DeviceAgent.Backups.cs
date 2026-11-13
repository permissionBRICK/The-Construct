using System.Security.Cryptography;
using Construct.Keys;

namespace Construct.Agent;

/// <summary>
/// Restoring a stored backup from this device (plan 8.2, 10.2): a stored backup opens only with <c>B</c>,
/// whose private half only devices hold. The agent reads the backup's keys file from the engine, unwraps
/// the backup's key <c>E</c> with <c>B</c> here and hands <c>E</c> to the engine's <c>restore</c> job in the
/// job's secrets, which the job keeps in memory for its run. <c>B</c> never leaves; <c>E</c> goes only to that job.
/// </summary>
public sealed partial class DeviceAgent
{
    /// <summary>Starts the engine's <c>restore</c> job for <paramref name="backupId"/> of <paramref name="vm"/>.</summary>
    /// <param name="requestId">The click: the same id returns the job it started.</param>
    public async Task<RestoreStarted> RestoreBackupAsync(string engineId, string vm, string backupId, string? requestId, CancellationToken ct)
    {
        var entry = Find(engineId) ?? throw NoEngine();
        var path = $"api/v1/vms/{Uri.EscapeDataString(vm)}/backups/{Uri.EscapeDataString(backupId)}";
        var keysFile = await EngineSendAsync(entry, HttpMethod.Get, path + "/keys", null, ct).ConfigureAwait(false)
            ?? throw new AgentFailure(404, "not-found", "The backup has no keys file.");
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        var backupKey = BackupKeyFile.Unwrap(keysFile.ToJsonString(), backupId, keys.Backup)
            ?? throw new AgentFailure(409, "backup-key-not-held", "This account's backup key does not open that backup.");
        var answer = await EngineSendAsync(entry, HttpMethod.Post, $"api/v1/vms/{Uri.EscapeDataString(vm)}/jobs",
            new { kind = "restore", requestId = requestId is { Length: > 0 } ? requestId : Guid.NewGuid().ToString("n"), backupId, secrets = new { backupKey } }, ct)
            .ConfigureAwait(false);
        return new RestoreStarted(Str(answer, "jobId"), answer?["replayed"]?.GetValue<bool>() ?? false);
    }

    /// <summary><c>B</c>'s public half (SubjectPublicKeyInfo PEM): what a VM encrypts its backups to.</summary>
    public async Task<string> BackupPublicKeyAsync(CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        return keys.Backup.ExportSubjectPublicKeyInfoPem();
    }
}

/// <summary>The restore job the engine started (follow it as any job: <c>/api/engines/{id}/api/v1/jobs/{jobId}</c>).</summary>
public sealed record RestoreStarted(string JobId, bool Replayed);

public sealed record RestoreRequest(string? RequestId = null);
