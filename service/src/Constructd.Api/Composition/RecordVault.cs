using System.Security.Cryptography;
using Construct.Keys;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Store.Records;
using Constructd.Sync.Participation;
using Constructd.Sync.Vault;

namespace Constructd.Api.Composition;

/// <summary>
/// The hosted vault of accounts in the new stack (plan 9.1): <c>secret</c> records in this engine's built-in store,
/// read and written as this engine with the <c>D</c> a device granted it. An account is in the new stack here once
/// such a grant exists; <c>K</c> is held at rest only while a device has granted it (<c>available</c>, 8.2).
/// </summary>
public sealed class RecordVault(RecordStore store, EngineStoreIdentity identity, EngineKeyStore keys) : IVaultRecords
{
    private const int FeedPage = 500;

    public bool Hosts(string owner) => Account(owner) is not null;

    public IReadOnlyList<string> Owners() => keys.GrantingAccounts(EngineGrants.Records);

    public byte[]? GrantedKey(string owner) =>
        Account(owner) is { } account ? keys.LatestGrantedKey(account, EngineGrants.Vault)?.Key : null;

    public byte[] OpenKey(string owner, VaultKeyGrant grant) =>
        EngineGrants.Open(keys.HostKey, keys.EngineId, Account(owner) ?? throw new CryptographicException("Not a record-backed vault."),
            new EngineGrant(EngineGrants.Vault, grant.Epoch, grant.Sealed));

    public void SetMode(string owner, bool available, VaultKeyGrant? grant)
    {
        var account = Account(owner) ?? throw new InvalidOperationException("Not a record-backed vault.");
        if (available) keys.PutGrant(account, new EngineGrant(EngineGrants.Vault, grant!.Epoch, grant.Sealed));
        else keys.DeleteGrant(account, EngineGrants.Vault);
    }

    public async Task<IReadOnlyList<VaultEntry>> ListAsync(string owner, CancellationToken ct)
    {
        var account = Account(owner) ?? throw new InvalidOperationException("Not a record-backed vault.");
        var entries = new List<VaultEntry>();
        long since = 0;
        while (true)
        {
            var page = await store.FeedAsync(account, since, TimeSpan.Zero, FeedPage, ct).ConfigureAwait(false);
            if (page.Records.Count == 0) break;
            foreach (var record in page.Records.Where(r => r.Kind == RecordKinds.Secret && r.Id.StartsWith(SecretRecords.Prefix, StringComparison.Ordinal)))
            {
                var d = keys.GrantedKey(account, EngineGrants.Records, record.Epoch);
                var read = d is null ? null : SecretRecords.Read(d, record);
                if (d is not null) CryptographicOperations.ZeroMemory(d);
                if (read is not null)
                {
                    var v = read.Value;
                    entries.Add(new VaultEntry(v.Name, v.Description, v.HasUsername, read.Deleted ? null : v.Payload, v.UpdatedAt, v.UpdatedBy, read.Deleted,
                        v.AutoApprove, record.Revision, new VaultProvenance(v.Provenance.Kind, v.Provenance.Vm, v.Provenance.Device, v.Provenance.RequestId)));
                }
                else if (record.Deleted)
                {
                    // A tombstone without a readable payload still holds the name's revision: the base of the next write.
                    entries.Add(new VaultEntry(record.Id[SecretRecords.Prefix.Length..], "", false, null, record.StoredAt.ToUnixTimeMilliseconds(), record.Actor, true,
                        false, record.Revision));
                }
            }

            since = page.Cursor;
        }

        return entries;
    }

    public async Task<bool> WriteAsync(string owner, VaultEntry entry, long baseRevision, VaultProvenance provenance, IReadOnlyList<string> origins, CancellationToken ct)
    {
        var account = Account(owner) ?? throw new InvalidOperationException("Not a record-backed vault.");
        var client = identity.Client(store, account);
        var epoch = await client.ActiveEpochAsync(ct).ConfigureAwait(false);
        var d = keys.GrantedKey(account, EngineGrants.Records, epoch) ?? throw new CryptographicException($"No device has granted this engine the record key of epoch {epoch}.");
        try
        {
            // Fields a newer participant added to the record survive this write (9.8).
            var current = await client.GetAsync(SecretRecords.IdOf(entry.Name), ct).ConfigureAwait(false);
            var previous = current is { Revision: var revision } && revision == baseRevision && current.Epoch == epoch ? SecretRecords.Read(d, current)?.Payload : null;
            var value = new SecretRecordValue(entry.Name, entry.Description, entry.HasUsername, entry.AutoApprove, entry.Deleted ? null : entry.Payload,
                entry.UpdatedAt, entry.UpdatedBy, new SecretProvenance(provenance.Kind, provenance.Vm, keys.EngineId, provenance.Device, provenance.RequestId));
            var write = SecretRecords.Write(d, epoch, "engine:" + identity.Id, value, baseRevision, entry.Deleted, origins, previous);
            return await client.WriteAsync(write, ct).ConfigureAwait(false) switch
            {
                WriteOutcome.Accepted => true,
                WriteOutcome.Stale => false,
                WriteOutcome.Refused refused => throw new InvalidOperationException($"The store refused the secret record ({refused.Code})."),
                _ => throw new InvalidOperationException(),
            };
        }
        finally { CryptographicOperations.ZeroMemory(d); }
    }

    // The account as the grant names it; VM owners and sign-in names compare without case.
    private string? Account(string owner) =>
        Owners().FirstOrDefault(a => string.Equals(a, owner, StringComparison.OrdinalIgnoreCase));
}
