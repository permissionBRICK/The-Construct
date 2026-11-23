using System.Security.Cryptography;
using System.Text.Json;
using Construct.Keys;
using Constructd.Store.Client;
using Constructd.Store.Records;
using Constructd.Sync.Sealing;

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
        var keysJson = keysFile.ToJsonString();
        var backupKey = BackupKeyFile.Unwrap(keysJson, backupId, keys.Backup)
            ?? await LegacyBackupKeyAsync(keysJson, backupId, ct).ConfigureAwait(false)
            ?? throw new AgentFailure(409, "backup-key-not-held", "Neither this account's backup key nor an old backup key it holds opens that backup.");
        var answer = await EngineSendAsync(entry, HttpMethod.Post, $"api/v1/vms/{Uri.EscapeDataString(vm)}/jobs",
            new { kind = "restore", requestId = requestId is { Length: > 0 } ? requestId : Guid.NewGuid().ToString("n"), backupId, secrets = new { backupKey } }, ct)
            .ConfigureAwait(false);
        return new RestoreStarted(Str(answer, "jobId"), answer?["replayed"]?.GetValue<bool>() ?? false);
    }

    /// <summary>
    /// A backup made before the move to the new stack (4, "Backups stay restorable"): it is wrapped for an old
    /// per-PC key, which the converter kept under <c>U</c> as <c>setting:legacy-backup-key:&lt;fingerprint&gt;</c>
    /// (13.1). Each recipient the keys file names is looked up by that id; the private key is opened here
    /// and dropped after the attempt. Null when none of them opens it.
    /// </summary>
    private async Task<string?> LegacyBackupKeyAsync(string keysJson, string backupId, CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        var store = new HttpStoreClient(http);
        foreach (var fingerprint in RecipientFingerprints(keysJson))
        {
            var id = LegacyBackupKeyPrefix + fingerprint;
            var record = await store.GetAsync(id, ct).ConfigureAwait(false);
            if (record is not { Deleted: false, Ciphertext: not null, Kind: RecordKinds.Setting } || record.Epoch != keys.Epoch) continue;
            byte[]? pkcs8 = null;
            try
            {
                var value = RecordPayload.ValueOf(RecordSealer.Open(keys.Records, record.Envelope, record.Ciphertext));
                if (value?["wrapped"]?.GetValue<string>() is not { } wrapped) continue;
                // The context is derived from the id, not taken from the record, so a record cannot point at another wrap.
                pkcs8 = KeyWrap.Unwrap(keys.Root, Convert.FromBase64String(wrapped), "legacy-backup-key:" + fingerprint);
                using var rsa = RSA.Create();
                rsa.ImportPkcs8PrivateKey(pkcs8, out _);
                if (BackupKeyFile.Unwrap(keysJson, backupId, rsa) is { } key) return key;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException or System.Text.Json.JsonException)
            {
                // Not readable with this account's keys: the next recipient.
            }
            finally
            {
                if (pkcs8 is not null) CryptographicOperations.ZeroMemory(pkcs8);
            }
        }

        return null;
    }

    internal const string LegacyBackupKeyPrefix = "setting:legacy-backup-key:";

    /// <summary>The recipient fingerprints a keys file names (64 lowercase hex each), in its order.</summary>
    private static IReadOnlyList<string> RecipientFingerprints(string keysJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(keysJson);
            if (!doc.RootElement.TryGetProperty("recipients", out var recipients) || recipients.ValueKind != JsonValueKind.Array) return [];
            return recipients.EnumerateArray()
                .Where(r => r.ValueKind == JsonValueKind.Object && r.TryGetProperty("fingerprint", out var fp) && fp.ValueKind == JsonValueKind.String)
                .Select(r => r.GetProperty("fingerprint").GetString()!)
                .Where(fp => fp.Length == 64 && fp.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                .Distinct(StringComparer.Ordinal).ToList();
        }
        catch (JsonException)
        {
            return [];
        }
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
