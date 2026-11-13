using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Construct.Keys;
using Constructd.Store.Client;
using Constructd.Store.Identity;
using Constructd.Store.Records;
using Constructd.Sync.Sealing;

namespace Constructd.Sync.Participation;

/// <summary>The content of a <c>device</c> record (9.1): sealed with <c>D</c>, written by devices.</summary>
public sealed record DeviceRecordValue(string Name, string PublicKey, string Status)
{
    public const string Active = "active";
    public const string Revoked = "revoked";
}

/// <summary>Writing and reading <c>device</c> records. The record id is <c>device:&lt;device id&gt;</c>.</summary>
public static class DeviceRecords
{
    public static string IdOf(string deviceId) => "device:" + deviceId;

    /// <param name="records">The account's <c>D</c>.</param>
    public static RecordWrite Write(byte[] records, long epoch, string actor, DeviceRecordValue value, long baseRevision)
    {
        var id = IdOf(ParticipantPublicKey.Parse(value.PublicKey).Id);
        var envelope = new RecordEnvelope(id, RecordKinds.Device, epoch, actor, baseRevision, Guid.NewGuid().ToString("n"), false);
        var payload = RecordPayload.Rewrite(null, JsonSerializer.SerializeToNode(value, StoreJson.Options), [actor]);
        return new RecordWrite(envelope.Id, envelope.Kind, envelope.Epoch, envelope.Actor, envelope.BaseRevision, envelope.WriteId, false,
            RecordSealer.Seal(records, envelope, payload));
    }

    /// <summary>The value, or null when the record does not open, is not a device record, or names a key whose id is not the record's.</summary>
    public static DeviceRecordValue? Read(byte[] records, StoredRecord record)
    {
        if (record.Kind != RecordKinds.Device || record.Deleted || record.Ciphertext is null) return null;
        try
        {
            var value = RecordPayload.ValueOf(RecordSealer.Open(records, record.Envelope, record.Ciphertext)).Deserialize<DeviceRecordValue>(StoreJson.Options);
            if (value is null || !ParticipantPublicKey.TryParse(value.PublicKey, out var key) || record.Id != IdOf(key!.Id)) return null;
            return value;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Devices as an engine sees them (8.1, 8.4): an engine accepts a device when a <c>device</c>
/// record of a user it hosts lists that key and is not revoked. It issues each device a token bound
/// to that key, refuses a token presented without a proof by it, deletes a device's tokens when a
/// device revokes it here, and on every read of the <c>device</c> records drops the tokens of revoked
/// devices and acknowledges them to the store.
///
/// The records are read through <paramref name="storeFor"/> (the engine's store, as this engine) and
/// opened with the granted <c>D</c>. The engine's sync loop is later work; until then a read happens
/// when a device enrols or revokes here, when a token of an account not read yet comes in, and when
/// the host calls <see cref="RefreshAsync"/>.
/// </summary>
public sealed class EngineDevices(
    EngineKeyStore keys,
    IRecordKeyProvider recordKeys,
    Func<string, IStoreClient> storeFor,
    Func<string, string, CancellationToken, Task> acknowledge,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, DeviceRecordValue>> _devices = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string, string), bool> _acknowledged = new();

    public string EngineId => keys.EngineId;

    /// <summary>Reads the account's <c>device</c> records from the store and applies revocations.</summary>
    public async Task<IReadOnlyDictionary<string, DeviceRecordValue>> RefreshAsync(string account, CancellationToken ct)
    {
        var store = storeFor(account);
        var found = new Dictionary<string, DeviceRecordValue>(StringComparer.Ordinal);
        long since = 0;
        while (true)
        {
            var page = await store.FeedAsync(since, TimeSpan.Zero, ct).ConfigureAwait(false);
            if (page.Records.Count == 0) break;
            foreach (var record in page.Records.Where(r => r.Kind == RecordKinds.Device && !r.Deleted))
            {
                var value = DeviceRecords.Read(await recordKeys.GetKeyAsync(account, record.Epoch, ct).ConfigureAwait(false), record);
                if (value is not null) found[record.Id["device:".Length..]] = value;
            }

            since = page.Cursor;
        }

        _devices[account] = found;
        foreach (var (id, _) in found.Where(d => d.Value.Status == DeviceRecordValue.Revoked))
        {
            keys.DeleteDeviceTokens(account, id);
            if (_acknowledged.ContainsKey((account, id))) continue;
            try
            {
                await acknowledge(account, id, ct).ConfigureAwait(false);
                _acknowledged[(account, id)] = true;
            }
            catch (StoreUnavailableException)
            {
                // Acknowledged at the next read.
            }
        }

        return found;
    }

    /// <summary>
    /// Issues a device token when the account's records list the device as active and
    /// <paramref name="proof"/> is its key's proof over <see cref="ProofSubjects.EngineEnroll"/>.
    /// </summary>
    public async Task<string?> EnrollAsync(string account, string deviceId, string? proof, CancellationToken ct)
    {
        var devices = await RefreshAsync(account, ct).ConfigureAwait(false);
        if (!devices.TryGetValue(deviceId, out var device) || device.Status != DeviceRecordValue.Active) return null;
        if (!RequestProof.Verify(ParticipantPublicKey.Parse(device.PublicKey), ProofSubjects.EngineEnroll(EngineId, account, deviceId), proof, _time.GetUtcNow())) return null;
        return keys.IssueDeviceToken(account, deviceId);
    }

    /// <summary>The device a token and proof stand for, or null.</summary>
    public async Task<(string Account, string DeviceId)?> AuthenticateAsync(string token, string? proof, CancellationToken ct)
    {
        if (keys.TokenOwner(token) is not { } owner) return null;
        var devices = _devices.TryGetValue(owner.Account, out var known) ? known : await RefreshAsync(owner.Account, ct).ConfigureAwait(false);
        if (!devices.TryGetValue(owner.DeviceId, out var device) || device.Status != DeviceRecordValue.Active) return null;
        return RequestProof.Verify(ParticipantPublicKey.Parse(device.PublicKey), token, proof, _time.GetUtcNow()) ? owner : null;
    }

    /// <summary>
    /// A device revokes another here (8.4): its tokens go at once; then the records are read so the
    /// revocation is acknowledged to the store if the record says so already.
    /// </summary>
    public async Task RevokeAsync(string account, string deviceId, CancellationToken ct)
    {
        keys.DeleteDeviceTokens(account, deviceId);
        try
        {
            await RefreshAsync(account, ct).ConfigureAwait(false);
        }
        catch (StoreUnavailableException)
        {
            // The tokens are gone; the acknowledgement follows at the next read.
        }
    }
}
