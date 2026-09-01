using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

public class TokenHasherTests
{
    [Fact]
    public void Generated_secrets_are_unique_and_url_safe()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => TokenHasher.GenerateSecret()).ToList();

        Assert.Equal(50, secrets.Distinct().Count());
        Assert.All(secrets, secret =>
        {
            Assert.True(secret.Length >= 40, $"secret too short: {secret.Length}");
            Assert.DoesNotContain('+', secret);
            Assert.DoesNotContain('/', secret);
            Assert.DoesNotContain('=', secret);
        });
    }

    [Fact]
    public void Hash_is_a_stable_lowercase_hex_sha256()
    {
        var hash = TokenHasher.Hash("hunter2");

        Assert.Equal(64, hash.Length);
        Assert.Equal(hash.ToLowerInvariant(), hash);
        Assert.Equal(hash, TokenHasher.Hash("hunter2"));
        Assert.NotEqual(hash, TokenHasher.Hash("hunter3"));
    }

    [Fact]
    public void Hash_matches_the_known_sha256_of_the_secret()
    {
        // SHA-256("constructd") — pins the storage format, so changing it becomes a deliberate
        // decision (stored hashes would have to be migrated).
        Assert.Equal(
            "a43d48b97fe280f25c575a118b70fe223697e08ce1a9eed4f45f378903f646c7",
            TokenHasher.Hash("constructd"));
    }

    [Fact]
    public void Hashes_equal_compares_hashes_not_secrets()
    {
        var hash = TokenHasher.Hash("abc");

        Assert.True(TokenHasher.HashesEqual(hash, TokenHasher.Hash("abc")));
        Assert.False(TokenHasher.HashesEqual(hash, TokenHasher.Hash("abd")));
        Assert.False(TokenHasher.HashesEqual(hash, null));
        Assert.False(TokenHasher.HashesEqual(null, hash));
        Assert.False(TokenHasher.HashesEqual(hash, "short"));
    }
}
