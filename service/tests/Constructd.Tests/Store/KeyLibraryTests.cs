using System.Security.Cryptography;
using Construct.Keys;

namespace Constructd.Tests.Store;

/// <summary>The key library (plan 8.2 to 8.5, 8.7) on its own.</summary>
public sealed class KeyLibraryTests
{
    [Fact]
    public void A_sealed_value_opens_only_for_its_key_and_context()
    {
        using var device = ParticipantKey.Generate();
        using var other = ParticipantKey.Generate();
        var sealedBytes = SealedBox.Seal(device.PublicKey, [1, 2, 3], "ctx/1");

        Assert.Equal([1, 2, 3], device.Open(sealedBytes, "ctx/1"));
        Assert.ThrowsAny<CryptographicException>(() => device.Open(sealedBytes, "ctx/2"));
        Assert.ThrowsAny<CryptographicException>(() => other.Open(sealedBytes, "ctx/1"));
    }

    [Fact]
    public void A_participant_key_survives_export_and_its_public_half_parses()
    {
        using var key = ParticipantKey.Generate();
        using var again = ParticipantKey.Import(key.Export());
        Assert.Equal(key.PublicKey, again.PublicKey);
        Assert.Equal(key.PublicKey, ParticipantPublicKey.Parse(key.PublicKey.ToString()));
        Assert.Equal(20, key.PublicKey.Id.Length);
        Assert.True(again.PublicKey.Verify("x", key.Sign("x")));
        Assert.False(ParticipantPublicKey.TryParse("not-a-key", out _));
    }

    [Fact]
    public void Account_keys_unwrap_under_their_root_only_and_carry_the_epoch()
    {
        using var keys = AccountKeys.Create(epoch: 3);
        var wrapped = keys.Wrap();
        Assert.Equal(3, wrapped.Epoch);
        Assert.Equal(AccountKeys.RsaBits, keys.Backup.KeySize);

        using var opened = AccountKeys.Open(keys.Root, wrapped);
        Assert.Equal(keys.Vault, opened.Vault);
        Assert.Equal(keys.Records, opened.Records);
        Assert.Equal(keys.Backup.ExportSubjectPublicKeyInfo(), opened.Backup.ExportSubjectPublicKeyInfo());

        Assert.ThrowsAny<CryptographicException>(() => AccountKeys.Open(RandomNumberGenerator.GetBytes(32), wrapped));
        // The epoch is bound: the same wraps claimed under another epoch do not open.
        Assert.ThrowsAny<CryptographicException>(() => AccountKeys.Open(keys.Root, wrapped with { Epoch = 4 }));

        using var device = ParticipantKey.Generate();
        Assert.Equal(keys.Root, AccountKeys.OpenRoot(device, keys.SealRootTo(device.PublicKey)));
    }

    [Fact]
    public void Account_keys_keep_an_existing_vault_key()
    {
        var vault = RandomNumberGenerator.GetBytes(32);
        using var keys = AccountKeys.Create(vaultKey: vault);
        Assert.Equal(vault, keys.Vault);
    }

    [Fact]
    public void A_recovery_code_is_160_bits_in_groups_and_reads_back_as_typed()
    {
        var code = RecoveryCode.Generate();
        var text = code.ToString();
        Assert.Matches("^([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4}$", text);

        var typed = RecoveryCode.Parse(text.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal));
        Assert.Equal(text, typed.ToString());
        Assert.Equal(code.Secret(), typed.Secret());
        Assert.NotEqual(code.Secret(), RecoveryCode.Generate().Secret());
        Assert.Throws<FormatException>(() => RecoveryCode.Parse("ABCD"));

        using var keys = AccountKeys.Create();
        var wrap = code.SealRoot(keys, "alice");
        Assert.Equal(keys.Root, typed.OpenRoot(wrap, "alice"));
        Assert.ThrowsAny<CryptographicException>(() => RecoveryCode.Generate().OpenRoot(wrap, "alice"));
        Assert.ThrowsAny<CryptographicException>(() => typed.OpenRoot(wrap, "bob"));
    }

    [Fact]
    public void Comparison_words_come_from_the_public_key()
    {
        using var a = ParticipantKey.Generate();
        using var b = ParticipantKey.Generate();
        var words = ComparisonWords.For(a.PublicKey);
        Assert.Equal(ComparisonWords.Count, words.Count);
        Assert.Equal(words, ComparisonWords.For(ParticipantPublicKey.Parse(a.PublicKey.ToString())));
        Assert.NotEqual(words, ComparisonWords.For(b.PublicKey));
    }

    [Fact]
    public void A_request_proof_binds_key_subject_and_time()
    {
        using var key = ParticipantKey.Generate();
        using var other = ParticipantKey.Generate();
        var now = DateTimeOffset.UtcNow;
        var proof = RequestProof.Create(key, "token", now);

        Assert.True(RequestProof.Verify(key.PublicKey, "token", proof, now));
        Assert.False(RequestProof.Verify(other.PublicKey, "token", proof, now));
        Assert.False(RequestProof.Verify(key.PublicKey, "other-token", proof, now));
        Assert.False(RequestProof.Verify(key.PublicKey, "token", proof, now + RequestProof.Window + TimeSpan.FromSeconds(2)));
        Assert.False(RequestProof.Verify(key.PublicKey, "token", null, now));
    }

    [Fact]
    public void A_pairing_signature_covers_pairing_device_and_grant()
    {
        using var approver = ParticipantKey.Generate();
        using var device = ParticipantKey.Generate();
        using var keys = AccountKeys.Create();
        var grant = Pairing.Seal(new PairingGrant("alice", 1, keys.Root, [new EngineEndpoint("https://engine", "pin")]), device.PublicKey, "p1");
        var signature = Pairing.Sign(approver, "p1", device.PublicKey, grant);

        Assert.True(Pairing.Verify(approver.PublicKey, "p1", device.PublicKey, grant, signature));
        Assert.False(Pairing.Verify(approver.PublicKey, "p2", device.PublicKey, grant, signature));
        var swapped = grant.ToArray();
        swapped[^1] ^= 1;
        Assert.False(Pairing.Verify(approver.PublicKey, "p1", device.PublicKey, swapped, signature));
        Assert.Equal(keys.Root, Pairing.Open(device, grant, "p1").Root);
        Assert.ThrowsAny<CryptographicException>(() => Pairing.Open(device, grant, "p2"));
    }

    [Fact]
    public void An_engine_gets_D_always_K_when_the_vault_is_available_and_never_U_or_B()
    {
        using var host = ParticipantKey.Generate();
        using var keys = AccountKeys.Create();
        Assert.Equal(["D"], EngineGrants.For(keys, host.PublicKey, "e1", "alice", "locked").Select(g => g.Name));
        var available = EngineGrants.For(keys, host.PublicKey, "e1", "alice", "available");
        Assert.Equal(["D", "K"], available.Select(g => g.Name));
        Assert.Equal(keys.Records, EngineGrants.Open(host, "e1", "alice", available[0]));
        Assert.ThrowsAny<CryptographicException>(() => EngineGrants.Open(host, "e2", "alice", available[0]));
        Assert.Throws<ArgumentException>(() => EngineGrants.Grant(host.PublicKey, "e1", "alice", "U", 1, keys.Root));
        Assert.Throws<ArgumentException>(() => EngineGrants.Grant(host.PublicKey, "e1", "alice", "B", 1, keys.Root));
    }
}
