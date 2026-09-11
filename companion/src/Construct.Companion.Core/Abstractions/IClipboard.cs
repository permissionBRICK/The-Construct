namespace Construct.Companion.Core.Abstractions;

public interface IClipboard
{
    Task WriteTextAsync(string text, CancellationToken cancellationToken = default);
}
