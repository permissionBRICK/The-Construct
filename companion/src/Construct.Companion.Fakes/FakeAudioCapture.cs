using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeAudioCapture : IAudioCapture
{
    public List<AudioDevice> Devices { get; } = [];
    public List<byte[]> Frames { get; } = [];
    public List<string?> Captures { get; } = [];
    public int ActiveCaptures { get; private set; }
    public Task<IReadOnlyList<AudioDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<AudioDevice>>(Devices.ToArray()); }
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(string? deviceId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); Captures.Add(deviceId); ActiveCaptures++;
        try
        {
            foreach (var frame in Frames)
            { cancellationToken.ThrowIfCancellationRequested(); yield return frame.ToArray(); await Task.CompletedTask; }
        }
        finally { ActiveCaptures--; }
    }
}
