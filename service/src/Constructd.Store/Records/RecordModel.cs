using System.Text.Json;
using System.Text.Json.Serialization;

namespace Constructd.Store.Records;

/// <summary>
/// The clear envelope of a record (9.2). Every field is known to the writer before it seals the
/// payload, so the whole envelope is bound into the ciphertext as associated data: the store cannot
/// move a ciphertext to another id, kind, epoch, actor or base without the reader noticing.
/// </summary>
public sealed record RecordEnvelope(
    string Id,
    string Kind,
    long Epoch,
    string Actor,
    long BaseRevision,
    string WriteId,
    bool Deleted);

/// <summary>A write as a participant sends it: the envelope plus the opaque sealed payload.</summary>
/// <param name="Ciphertext">The sealed payload; optional on a tombstone.</param>
public sealed record RecordWrite(
    string Id,
    string Kind,
    long Epoch,
    string Actor,
    long BaseRevision,
    string WriteId,
    bool Deleted,
    byte[]? Ciphertext)
{
    [JsonIgnore]
    public RecordEnvelope Envelope => new(Id, Kind, Epoch, Actor, BaseRevision, WriteId, Deleted);
}

/// <summary>
/// A record as the store holds it: the envelope as written, the revision the store assigned on
/// acceptance, and the revision of the record's latest deletion. That one is kept for good (9.5): an
/// edit based on a revision before it is a conflict even after the name was created again.
/// </summary>
public sealed record StoredRecord(
    string Id,
    string Kind,
    long Epoch,
    string Actor,
    long BaseRevision,
    string WriteId,
    bool Deleted,
    byte[]? Ciphertext,
    long Revision,
    DateTimeOffset StoredAt,
    long? DeletedRevision)
{
    [JsonIgnore]
    public RecordEnvelope Envelope => new(Id, Kind, Epoch, Actor, BaseRevision, WriteId, Deleted);
}

/// <summary>One page of the feed: current records changed after the cursor, oldest first.</summary>
public sealed record FeedPage(IReadOnlyList<StoredRecord> Records, long Cursor);

/// <summary>The body of a <c>412</c>: the current record, or null when the id does not exist.</summary>
public sealed record WriteConflict(string Id, StoredRecord? Current);

/// <summary>The body of a batch <c>412</c>: every write whose base was stale.</summary>
public sealed record BatchConflict(IReadOnlyList<WriteConflict> Conflicts);

/// <summary>A batch request: all writes apply, or none.</summary>
public sealed record BatchRequest(IReadOnlyList<RecordWrite> Writes);

/// <summary>The body of <c>GET /export</c> and <c>POST /import</c>: one account's whole store.</summary>
public sealed record StoreExport(long ActiveEpoch, IReadOnlyList<StoredRecord> Records, IReadOnlyList<StoredRecord> History);

/// <summary>The store's one JSON contract (camelCase, byte arrays as base64).</summary>
public static class StoreJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web);
}
