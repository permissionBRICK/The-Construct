using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Vault;

public sealed class VaultCoreTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void An_entry_payload_round_trips_and_uses_the_contract_format()
    {
        var payload = VaultCrypto.SealEntry(Key, "github-token", 1730000000000, "deploy", "s3cr3t \"value\" ü");
        var bytes = Convert.FromBase64String(payload);

        // nonce(12) || tag(16) || ciphertext, opened here with the plain AES-GCM primitive and the contract AAD.
        var plain = new byte[bytes.Length - 28];
        using (var aes = new AesGcm(Key, 16))
            aes.Decrypt(bytes.AsSpan(0, 12), bytes.AsSpan(28), bytes.AsSpan(12, 16), plain, Encoding.UTF8.GetBytes("entry:github-token:1730000000000"));
        var json = JsonNode.Parse(plain)!.AsObject();
        Assert.Equal("deploy", json["username"]!.GetValue<string>());
        Assert.Equal("s3cr3t \"value\" ü", json["secret"]!.GetValue<string>());

        Assert.Equal(("deploy", "s3cr3t \"value\" ü"), VaultCrypto.OpenEntry(Key, "github-token", 1730000000000, payload));
    }

    [Fact]
    public void Tampering_a_wrong_key_or_a_moved_entry_is_detected()
    {
        var payload = VaultCrypto.SealEntry(Key, "db", 1000, "", "password-123");
        var bytes = Convert.FromBase64String(payload);
        bytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.OpenEntry(Key, "db", 1000, Convert.ToBase64String(bytes)));
        bytes = Convert.FromBase64String(payload); bytes[14] ^= 1; // the tag
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.OpenEntry(Key, "db", 1000, Convert.ToBase64String(bytes)));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.OpenEntry(RandomNumberGenerator.GetBytes(32), "db", 1000, payload));
        // The AAD binds name and stamp: a payload cannot be replayed under another entry or time.
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.OpenEntry(Key, "other", 1000, payload));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.OpenEntry(Key, "db", 1001, payload));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.OpenEntry(Key, "db", 1000, "not base64!"));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.OpenEntry(Key, "db", 1000, Convert.ToBase64String(new byte[20])));
    }

    [Fact]
    public void The_key_check_accepts_only_the_right_key()
    {
        var check = VaultCrypto.KeyCheck(Key);
        Assert.True(VaultCrypto.Verify(Key, check));
        Assert.False(VaultCrypto.Verify(RandomNumberGenerator.GetBytes(32), check));
        Assert.False(VaultCrypto.Verify(Key[..16], check));
        // The check is "construct-vault-check" under the AAD "check", nothing else.
        Assert.Equal("construct-vault-check", Encoding.UTF8.GetString(VaultCrypto.Open(Key, check, "check")));
        Assert.ThrowsAny<CryptographicException>(() => VaultCrypto.Open(Key, check, "entry:x:1"));
        Assert.Null(VaultCrypto.DecodeKey(Convert.ToBase64String(new byte[31])));
        Assert.Null(VaultCrypto.DecodeKey("%%%"));
    }

    private static VaultEntry E(string name, long at, string by, bool deleted = false) => new(name, "", false, deleted ? null : "p" + at, at, by, deleted);

    [Fact]
    public void The_merge_rule_is_last_writer_wins_with_updated_by_as_tie_break()
    {
        Assert.True(VaultRules.Wins(E("a", 2, "pc:a"), E("a", 1, "pc:z")));
        Assert.False(VaultRules.Wins(E("a", 1, "pc:z"), E("a", 2, "pc:a")));
        Assert.True(VaultRules.Wins(E("a", 5, "vm:dev"), E("a", 5, "pc:x")));
        Assert.False(VaultRules.Wins(E("a", 5, "pc:x"), E("a", 5, "vm:dev")));
        Assert.False(VaultRules.Wins(E("a", 5, "pc:x"), E("a", 5, "pc:x")));
        Assert.True(VaultRules.Wins(E("a", 1, "pc:x"), null));

        var now = 100L * 24 * 3600 * 1000;
        var current = new[] { E("keep", 10, "pc:a"), E("old", 10, "pc:a") };
        var merged = VaultRules.Merge(current, [E("keep", 9, "pc:z"), E("old", 11, "pc:a", deleted: true), E("new", 3, "vm:dev"), E("new", 4, "vm:dev"),
            E("ancient", 1, "pc:a", deleted: true)], now);
        Assert.Equal(["old", "new"], merged.Select(e => e.Name));
        Assert.True(merged[0].Deleted);
        Assert.Equal(4, merged[1].UpdatedAt);
    }

    [Fact]
    public void Approval_texts_match_the_companion_with_durations_instead_of_clock_times()
    {
        var entry = new VaultEntry("github-token", "GitHub token for the release repo", true, "x", 1, "pc:a", false);
        var request = new VaultRequest("id-12345678", "request", ["github-token"], 3, 7200, false, false, "release 4.2", "", "", null, null, "root@dev");
        var prompt = VaultPrompts.Request("dev", [entry], request, TimeSpan.FromSeconds(7200));
        Assert.Equal("Key vault — access request", prompt.Title);
        Assert.Equal("The VM “dev” asks for access to:\n  • github-token — GitHub token for the release repo (with username)\n\n" +
                     "Access: 3 uses, for 2 h\nReason given: “release 4.2”\nRequested by root@dev", prompt.Message);
        Assert.Equal("Approve", prompt.Action);
        Assert.Equal("Allow once", VaultPrompts.Once("dev", entry, request).Action);
        Assert.Equal("Replace", VaultPrompts.Replace("dev", entry, request).Action);
        Assert.Equal("Delete", VaultPrompts.Delete("dev", entry, request).Action);
        Assert.Equal("for 1 day", VaultRules.Access(null, TimeSpan.FromHours(24)));
        Assert.Equal("1 use, for 1 h 30 min", VaultRules.Access(1, TimeSpan.FromMinutes(90)));
        Assert.Equal("for 10 min", VaultRules.Access(null, TimeSpan.FromMinutes(10)));
    }

    [Theory]
    [InlineData("""{"op":"explode","names":[]}""", "Unknown operation.")]
    [InlineData("""{"v":2,"op":"list"}""", "Unsupported request version; update The Construct on this VM.")]
    [InlineData("""{"op":"get","names":["a","b"]}""", "Name exactly one secret.")]
    [InlineData("""{"op":"request","names":["../etc"]}""", "Secret names use letters, digits, '.', '_' and '-' (at most 64, starting with a letter or digit).")]
    [InlineData("""{"op":"request","names":["a"],"uses":0}""", "--uses must be between 1 and 1000.")]
    [InlineData("""{"op":"request","names":["a"],"ttl":30}""", "--for must be between 1 minute and 24 hours.")]
    [InlineData("""{"op":"add","names":["a"],"secret":"!!","description":"x"}""", "The secret is not valid base64.")]
    [InlineData("""{"op":"add","names":["a"],"secret":"//79","description":"x"}""", "The secret must be UTF-8 text.")]
    [InlineData("""{"op":"add","names":["a"],"secret":"YWJj","description":" "}""", "Describe the secret with --description.")]
    [InlineData("""{"op":"release","names":[]}""", "Name a secret or pass --all.")]
    public void Requests_are_validated_like_the_companion_spool(string json, string error)
    {
        var (request, id, message) = VaultProtocol.ParseRequest(JsonNode.Parse(json)!.AsObject(), "host-id-123");
        Assert.Null(request);
        Assert.Equal("host-id-123", id);
        Assert.Equal(error, message);
    }

    [Fact]
    public void A_valid_request_keeps_the_guest_id_and_sanitizes_its_texts()
    {
        var (request, id, error) = VaultProtocol.ParseRequest(JsonNode.Parse(
            """{"v":1,"id":"guest-req-1","op":"add","names":["tok","tok"],"secret":"c2VjcmV0","description":"line\nbreak","reason":"why\u0007","username":"u","deadline":1730000000000,"replace":true}""")!.AsObject(), "host-id-123");
        Assert.Null(error);
        Assert.Equal("guest-req-1", id);
        Assert.Equal(["tok"], request!.Names);
        Assert.Equal("secret", request.Secret);
        Assert.Equal("line break", request.Description);
        Assert.Equal("why", request.Reason);
        Assert.True(request.Replace);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1730000000000), request.Deadline);
        Assert.DoesNotContain("secret", request.ToString(), StringComparison.Ordinal);
    }
}
