using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Store.Records;
using Constructd.Sync.Sealing;

namespace Constructd.Sync.Vault;

/// <summary>
/// Who caused a write of a <c>secret</c> record (9.1). <see cref="Kind"/> is one of the constants;
/// the other fields are what that kind names: the VM, the approving or entering device and the
/// request id. Devices show it next to the secret and in its history.
/// </summary>
public sealed record SecretProvenance(string Kind, string? Vm = null, string? Engine = null, string? Device = null, string? RequestId = null)
{
    /// <summary>A device wrote it itself (the user in the vault screen).</summary>
    public const string ByDevice = "device";

    /// <summary>A VM added a secret under a free name; no approval (9.1 row 1).</summary>
    public const string VmAdded = "vm-added";

    /// <summary>A VM turned auto-approve off; no approval (9.1 row 2).</summary>
    public const string VmAutoApproveOff = "vm-auto-approve-off";

    /// <summary>A VM replaced a value, deleted a secret or turned auto-approve on; a device approved (9.1 row 3).</summary>
    public const string VmApproved = "vm-approved";

    /// <summary>A device entered the value a VM asked for (9.1 row 4, 10.3).</summary>
    public const string EnteredOnRequest = "entered-on-request";
}

/// <summary>
/// One vault entry as a <c>secret</c> record (9.1): today's entry shape, whose <see cref="Payload"/>
/// is sealed with <c>K</c> exactly as before (<c>Construct.Keys.VaultSeal</c>); the record around
/// it is sealed with <c>D</c> like every record, so an engine without <c>K</c> can still list names
/// and descriptions and render approvals. A deletion is a tombstone that still carries this value
/// (without payload) for its provenance.
/// </summary>
public sealed record SecretRecordValue(string Name, string Description, bool HasUsername, bool AutoApprove, string? Payload,
    long UpdatedAt, string UpdatedBy, SecretProvenance Provenance)
{
    public override string ToString() => $"SecretRecordValue {{ Name = {Name}, UpdatedAt = {UpdatedAt} }}";
}

/// <summary>A <c>secret</c> record read back: the value, its store revision and whether it is a tombstone.</summary>
public sealed record SecretRecord(SecretRecordValue Value, long Revision, bool Deleted, string Actor, DateTimeOffset StoredAt, JsonObject Payload);

/// <summary>Writing and reading <c>secret</c> records. The record id is <c>secret:&lt;name&gt;</c>.</summary>
public static class SecretRecords
{
    public const string Prefix = "secret:";

    public static string IdOf(string name) => Prefix + name;

    /// <param name="records">The account's <c>D</c>.</param>
    /// <param name="previous">The payload this write replaces, so fields a newer participant added survive (9.8).</param>
    public static RecordWrite Write(byte[] records, long epoch, string actor, SecretRecordValue value, long baseRevision, bool deleted,
        IEnumerable<string> origins, JsonObject? previous = null)
    {
        var envelope = new RecordEnvelope(IdOf(value.Name), RecordKinds.Secret, epoch, actor, baseRevision, Guid.NewGuid().ToString("n"), deleted);
        var payload = RecordPayload.Rewrite(previous, JsonSerializer.SerializeToNode(value, StoreJson.Options), origins);
        return new RecordWrite(envelope.Id, envelope.Kind, envelope.Epoch, envelope.Actor, envelope.BaseRevision, envelope.WriteId, deleted,
            RecordSealer.Seal(records, envelope, payload));
    }

    /// <summary>The record, or null when it does not open, is no secret record, or names another secret than its id.</summary>
    public static SecretRecord? Read(byte[] records, StoredRecord record)
    {
        if (record.Kind != RecordKinds.Secret || record.Ciphertext is null || !record.Id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        try
        {
            var payload = RecordSealer.Open(records, record.Envelope, record.Ciphertext);
            var value = RecordPayload.ValueOf(payload).Deserialize<SecretRecordValue>(StoreJson.Options);
            if (value?.Provenance is null || value.Name != record.Id[Prefix.Length..]) return null;
            if (!record.Deleted && value.Payload is null) return null;
            return new SecretRecord(value, record.Revision, record.Deleted, record.Actor, record.StoredAt, payload);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }
}
