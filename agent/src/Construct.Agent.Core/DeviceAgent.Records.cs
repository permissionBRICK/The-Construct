using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Constructd.Store.Client;
using Constructd.Store.Records;
using Constructd.Sync.Sealing;

namespace Construct.Agent;

/// <summary>The device's read cache of its own record kinds (9.2): read through the store, written with <c>If-Match</c>.</summary>
public sealed partial class DeviceAgent
{
    /// <summary>The current records of one kind this device can open under the account's <c>D</c>, by id.</summary>
    internal async Task<IReadOnlyDictionary<string, JsonNode>> ReadRecordsAsync(string kind, CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        var store = new HttpStoreClient(http);
        var found = new SortedDictionary<string, JsonNode>(StringComparer.Ordinal);
        long since = 0;
        while (true)
        {
            var page = await store.FeedAsync(since, TimeSpan.Zero, ct).ConfigureAwait(false);
            if (page.Records.Count == 0) break;
            foreach (var record in page.Records.Where(r => r.Kind == kind))
            {
                found.Remove(record.Id);
                if (record.Deleted || record.Ciphertext is null || record.Epoch != keys.Epoch) continue;
                try
                {
                    if (RecordPayload.ValueOf(RecordSealer.Open(keys.Records, record.Envelope, record.Ciphertext)) is { } value) found[record.Id] = value.DeepClone();
                }
                catch (CryptographicException)
                {
                    // Not readable under D: skipped.
                }
            }

            since = page.Cursor;
        }

        return found;
    }

    /// <summary>
    /// One change to a record of a device kind: <paramref name="change"/> gets the current value (null: none) and gives
    /// the new one (null: delete). On a <c>412</c> the record is read again and the change made again on it (9.2: a
    /// device never merges). Nothing is written when the change leaves the value as it was.
    /// </summary>
    internal async Task UpdateRecordAsync(string id, string kind, Func<JsonNode?, JsonNode?> change, CancellationToken ct)
    {
        var keys = await OpenKeysAsync(ct).ConfigureAwait(false);
        using var http = StoreHttp();
        var store = new HttpStoreClient(http);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var current = await store.GetAsync(id, ct).ConfigureAwait(false);
            var value = current is { Deleted: false, Ciphertext: { } sealedBytes } && current.Epoch == keys.Epoch
                ? RecordPayload.ValueOf(RecordSealer.Open(keys.Records, current.Envelope, sealedBytes))
                : null;
            var next = change(value?.DeepClone());
            if (JsonNode.DeepEquals(next, value)) return;
            if (next is null && current is not { Deleted: false }) return;

            var baseRevision = current?.Revision ?? 0;
            var envelope = new RecordEnvelope(id, kind, keys.Epoch, Actor, baseRevision, Guid.NewGuid().ToString("n"), next is null);
            var write = new RecordWrite(id, kind, keys.Epoch, Actor, baseRevision, envelope.WriteId, next is null,
                next is null ? null : RecordSealer.Seal(keys.Records, envelope, RecordPayload.Rewrite(null, next, [Actor])));
            switch (await store.WriteAsync(write, ct).ConfigureAwait(false))
            {
                case WriteOutcome.Accepted:
                    return;
                case WriteOutcome.Refused refused:
                    throw new AgentFailure(502, refused.Code, $"The store refused the {kind} record: {refused.Message}");
            }
        }

        throw new AgentFailure(409, "write-conflict", $"The {kind} record kept changing; try again.");
    }
}
