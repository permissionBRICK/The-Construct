using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeTokenStore : ITokenStore
{
    private readonly Dictionary<string, Secret> tokens = [];
    public Task<Secret?> ReadAsync(string slug, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(tokens.GetValueOrDefault(slug)); }
    public Task WriteAsync(string slug, Secret token, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); tokens[slug] = token; return Task.CompletedTask; }
    public Task DeleteAsync(string slug, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); tokens.Remove(slug); return Task.CompletedTask; }
}
