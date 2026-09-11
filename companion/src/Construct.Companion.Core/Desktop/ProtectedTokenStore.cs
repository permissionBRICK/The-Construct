using System.Security.Cryptography;
using System.Text;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Desktop;

public sealed class ProtectedTokenStore(IFileSystem files, IDataProtection protection, string directory) : ITokenStore
{
    private string PathFor(string slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > 200 || slug.Contains("..", StringComparison.Ordinal) || slug.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("Invalid remote host slug.");
        return Path.Combine(directory, slug + ".token");
    }
    public Task<Secret?> ReadAsync(string slug, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var path = PathFor(slug);
        try
        {
            var bytes = files.ReadFile(path); if (bytes is null) return Task.FromResult<Secret?>(null);
            var raw = protection.Unprotect(Convert.FromBase64String(Encoding.UTF8.GetString(bytes).Trim().TrimStart('\ufeff')));
            try { return Task.FromResult<Secret?>(new(Encoding.UTF8.GetString(raw))); }
            finally { CryptographicOperations.ZeroMemory(raw); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FormatException or CryptographicException) { return Task.FromResult<Secret?>(null); }
    }
    public Task WriteAsync(string slug, Secret token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); var path = PathFor(slug);
        if (string.IsNullOrWhiteSpace(token.Reveal())) throw new ArgumentException("Refusing to store an empty API token.");
        var raw = Encoding.UTF8.GetBytes(token.Reveal());
        try { files.WriteFileAtomic(path, Encoding.UTF8.GetBytes(Convert.ToBase64String(protection.Protect(raw)))); }
        finally { CryptographicOperations.ZeroMemory(raw); }
        return Task.CompletedTask;
    }
    public Task DeleteAsync(string slug, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); files.DeleteFile(PathFor(slug)); return Task.CompletedTask; }
}
