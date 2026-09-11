namespace Construct.Companion.Core.Abstractions;

// Enumerates capture endpoints and streams S16LE, 16 kHz mono PCM on demand.
// Each enumeration owns capture until disposed; cancellation releases the device.
public interface IAudioCapture
{
    Task<IReadOnlyList<AudioDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken = default);
    IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(string? deviceId, CancellationToken cancellationToken = default);
}
public sealed record AudioDevice(string Id, string Name, bool IsDefault = false);
