using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Fakes;
public sealed class FakeClipboard : IClipboard
{
    public string? Text { get; private set; }
    public Task WriteTextAsync(string text, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); Text = text; return Task.CompletedTask; }
}
