using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Construct.Keys;
using Constructd.Store.Client;
using Constructd.Store.Records;

namespace Construct.Agent;

/// <summary>What the one-time upgrade (13.1) needs of the device: the old vault key as <c>K</c>, and records under the account's keys.</summary>
public sealed partial class DeviceAgent
{
    /// <summary>
    /// The key a first-device setup takes as the account's <c>K</c> instead of a new one: the old vault's
    /// key, so hosted ciphertext stays valid (8.2, 13.1). It may throw to stop the setup (a vault that
    /// exists and cannot be opened must not get a new key).
    /// </summary>
    public Func<byte[]?>? SetupVaultKey { get; set; }

    /// <summary>The engine that holds the account's store, once set up.</summary>
    public string? StoreEngineId => _state.StoreEngine;

    public bool IsRegistered => _state.Registered;

    /// <summary>True when a record of that id exists at the store and is not deleted.</summary>
    internal async Task<bool> RecordExistsAsync(string id, CancellationToken ct)
    {
        await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        return await new HttpStoreClient(http).GetAsync(id, ct).ConfigureAwait(false) is { Deleted: false };
    }

    /// <summary>Writes a <c>setting</c> record (a device's kind, sealed with <c>D</c>).</summary>
    internal Task WriteSettingRecordAsync(string id, JsonNode value, CancellationToken ct) => WriteRecordAsync(id, RecordKinds.Setting, value, ct);

    /// <summary>True when the account's <c>K</c> is <paramref name="key"/>.</summary>
    internal async Task<bool> VaultKeyIsAsync(byte[] key, CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        return CryptographicOperations.FixedTimeEquals(keys.Vault, key);
    }

    /// <summary>Wraps <paramref name="plaintext"/> under <c>U</c> (no engine holds <c>U</c>); the epoch it was wrapped in.</summary>
    internal async Task<(long Epoch, byte[] Wrapped)> WrapUnderRootAsync(byte[] plaintext, string context, CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        return (keys.Epoch, KeyWrap.Wrap(keys.Root, plaintext, context));
    }

    /// <summary>The account's backup recipient <c>B.pub</c> as PEM (8.2), enrolled on every VM.</summary>
    internal async Task<string> BackupPublicPemAsync(CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        return keys.Backup.ExportSubjectPublicKeyInfoPem();
    }

    /// <summary>The engine's id at this device for a URL, when known.</summary>
    internal string? EngineIdFor(string url) => Find(State.EngineEntry.IdFor(url.ToLowerInvariant()))?.Id;
}
