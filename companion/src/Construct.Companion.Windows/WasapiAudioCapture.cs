using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
using NAudio.CoreAudioApi;
using NAudio.Wave;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public sealed class WasapiAudioCapture : IAudioCapture
{
    public Task<IReadOnlyList<AudioDevice>> EnumerateDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var enumerator = new MMDeviceEnumerator();
        string? defaultId = null;
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Console))
        { using var endpoint = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console); defaultId = endpoint.ID; }
        var devices = new List<AudioDevice>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            using (device) devices.Add(new(device.ID, device.FriendlyName, device.ID == defaultId));
        return Task.FromResult<IReadOnlyList<AudioDevice>>(devices);
    }
    public async IAsyncEnumerable<ReadOnlyMemory<byte>> CaptureAsync(string? deviceId, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var enumerator = new MMDeviceEnumerator();
        using var device = string.IsNullOrEmpty(deviceId) ? enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console) : enumerator.GetDevice(deviceId);
        using var capture = new WasapiCapture(device);
        var input = new BufferedWaveProvider(capture.WaveFormat) { ReadFully = false, BufferDuration = TimeSpan.FromSeconds(2) };
        using var resampler = new MediaFoundationResampler(input, new WaveFormat(16000, 16, 1)) { ResamplerQuality = 60 };
        var output = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
        var buffer = new byte[3200];
        capture.DataAvailable += (_, args) =>
        {
            try
            {
                input.AddSamples(args.Buffer, 0, args.BytesRecorded);
                int read;
                while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
                    if (!output.Writer.TryWrite(buffer.AsMemory(0, read).ToArray()))
                    { output.Writer.TryComplete(new IOException("Audio consumer fell behind.")); break; }
            }
            catch (Exception) { output.Writer.TryComplete(new IOException("Audio conversion failed.")); }
        };
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.RecordingStopped += (_, args) => { output.Writer.TryComplete(args.Exception is null ? null : new IOException("Audio capture stopped.")); stopped.TrySetResult(); };
        capture.StartRecording();
        try { await foreach (var frame in output.Reader.ReadAllAsync(cancellationToken)) yield return frame; }
        finally { capture.StopRecording(); await stopped.Task; }
    }
}
