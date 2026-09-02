using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

/// <summary>
/// The bootstrap key's fingerprint, which the ISO catalog records and the prebuilt strategy compares
/// against. It has to be the value <c>ssh-keygen -lf</c> prints, because an administrator compares
/// the two by eye — and it must never throw, because the key file is whatever the admin pointed at.
/// </summary>
public sealed class SshPublicKeyTests
{
    // Blob: "ssh-ed25519" + 32 bytes of 'R'. The expected fingerprint is SHA-256 over that blob,
    // base64 without padding — exactly what OpenSSH prints.
    private const string Key =
        "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIFJSUlJSUlJSUlJSUlJSUlJSUlJSUlJSUlJSUlJSUlJS bootstrap@construct";

    private const string Fingerprint = "SHA256:FIgBHSP/TTbLZdpl2KEvs7eVdxLJRzLBMDu59LANOQY";

    [Fact]
    public void The_fingerprint_is_the_one_ssh_keygen_prints()
    {
        Assert.True(SshPublicKey.TryFingerprint(Key, out var fingerprint));
        Assert.Equal(Fingerprint, fingerprint);
    }

    [Fact]
    public void The_comment_is_not_part_of_it()
    {
        // Renaming the comment must not look like a rotated key.
        Assert.True(SshPublicKey.TryFingerprint(Key.Replace("bootstrap@construct", "someone-else@host"), out var renamed));
        Assert.Equal(Fingerprint, renamed);
    }

    [Fact]
    public void A_key_with_no_comment_at_all_still_works()
    {
        Assert.True(SshPublicKey.TryFingerprint(Key[..Key.LastIndexOf(' ')], out var fingerprint));
        Assert.Equal(Fingerprint, fingerprint);
    }

    [Fact]
    public void Surrounding_whitespace_and_a_trailing_newline_do_not_matter()
    {
        Assert.True(SshPublicKey.TryFingerprint($"  {Key}\n", out var fingerprint));
        Assert.Equal(Fingerprint, fingerprint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ssh-ed25519")]
    [InlineData("ssh-ed25519 not-base64!!")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
    public void Anything_that_is_not_a_key_line_fails_instead_of_throwing(string? text)
    {
        Assert.False(SshPublicKey.TryFingerprint(text, out var fingerprint));
        Assert.Equal(string.Empty, fingerprint);
        Assert.Equal("unknown", SshPublicKey.FingerprintOrUnknown(text));
    }
}
