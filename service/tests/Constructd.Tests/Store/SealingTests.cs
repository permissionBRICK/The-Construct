using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Constructd.Store.Records;
using Constructd.Sync.Sealing;
using static Constructd.Tests.Store.StoreWrites;

namespace Constructd.Tests.Store;

/// <summary>Sealing with D (9.2) and payload versions (9.8).</summary>
public sealed class SealingTests
{
    private static readonly RecordEnvelope Envelope = new("profile:a", "profile", 1, "engine:e1", 4, "w1", false);

    [Fact]
    public void A_payload_opens_only_under_the_envelope_it_was_sealed_with()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var payload = Json("""{"payloadVersion":1,"value":{"name":"a"}}""");
        var sealedBytes = RecordSealer.Seal(key, Envelope, payload);

        Assert.Equal(payload.ToJsonString(), RecordSealer.Open(key, Envelope, sealedBytes).ToJsonString());
        Assert.DoesNotContain("\"name\"", Encoding.UTF8.GetString(sealedBytes));

        // Swapped or replayed under another identity: every envelope field is bound.
        RecordEnvelope[] others =
        [
            Envelope with { Id = "profile:b" },
            Envelope with { Kind = "setting" },
            Envelope with { Epoch = 2 },
            Envelope with { Actor = "engine:e2" },
            Envelope with { BaseRevision = 5 },
            Envelope with { WriteId = "w2" },
            Envelope with { Deleted = true },
        ];
        foreach (var other in others)
            Assert.ThrowsAny<CryptographicException>(() => RecordSealer.Open(key, other, sealedBytes));

        Assert.ThrowsAny<CryptographicException>(() => RecordSealer.Open(RandomNumberGenerator.GetBytes(32), Envelope, sealedBytes));
        sealedBytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => RecordSealer.Open(key, Envelope, sealedBytes));
    }

    [Fact]
    public void The_associated_data_is_canonical_json()
    {
        Assert.Equal("""{"actor":"engine:e1","baseRevision":4,"deleted":false,"epoch":1,"id":"profile:a","kind":"profile","writeId":"w1"}""",
            Encoding.UTF8.GetString(RecordSealer.AssociatedData(Envelope)));
    }

    [Fact]
    public async Task The_file_key_provider_keeps_one_key_per_account_and_epoch()
    {
        using var directory = new TempDirectory();
        var keys = new FileRecordKeyProvider(directory.Path);
        var first = await keys.GetKeyAsync("alice", 1, default);
        Assert.Equal(32, first.Length);
        Assert.Equal(first, await new FileRecordKeyProvider(directory.Path).GetKeyAsync("alice", 1, default));
        Assert.NotEqual(first, await keys.GetKeyAsync("alice", 2, default));
        Assert.NotEqual(first, await keys.GetKeyAsync("bob", 1, default));
    }

    [Fact]
    public void A_rewrite_keeps_unknown_fields_and_refuses_a_newer_payload()
    {
        // Written by a newer participant at the version this build knows, with a field it does not.
        var previous = Json("""{"payloadVersion":1,"value":{"name":"a"},"origins":[],"addedLater":{"x":1}}""");
        var rewritten = RecordPayload.Rewrite(previous, Json("""{"name":"b"}"""), ["vm:one"]);
        Assert.Equal("""{"x":1}""", rewritten["addedLater"]!.ToJsonString());
        Assert.Equal("b", rewritten["value"]!["name"]!.GetValue<string>());
        Assert.Equal(1, RecordPayload.VersionOf(rewritten));

        // A payload of version 2 is refused by a version-1 participant: "update this device".
        var newer = Json("""{"payloadVersion":2,"value":{"name":"a"}}""");
        var refused = Assert.Throws<PayloadTooNewException>(() => RecordPayload.Rewrite(newer, Json("{}"), []));
        Assert.Contains("Update this device", refused.Message);

        // A version-2 participant rewrites an older payload and keeps what it did not touch.
        var upgraded = RecordPayload.Rewrite(previous, Json("""{"name":"c"}"""), [], supported: 2);
        Assert.Equal(2, RecordPayload.VersionOf(upgraded));
        Assert.NotNull(upgraded["addedLater"]);
    }
}
