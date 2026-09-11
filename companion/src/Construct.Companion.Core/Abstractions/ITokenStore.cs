namespace Construct.Companion.Core.Abstractions;

// DPAPI CurrentUser for the token files below; the fake XORs.
public interface IDataProtection
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}

// Reads/writes the existing remote/<slug>.token DPAPI CurrentUser format.
// Tokens are opaque secrets; the implementation owns protection and file access.
public interface ITokenStore
{
    Task<Secret?> ReadAsync(string slug, CancellationToken cancellationToken = default);
    Task WriteAsync(string slug, Secret token, CancellationToken cancellationToken = default);
    Task DeleteAsync(string slug, CancellationToken cancellationToken = default);
}

// Explicit access prevents record diagnostics from accidentally printing a secret.
public sealed class Secret(string value)
{
    public string Reveal() => value;
    public override string ToString() => "[redacted]";
}
