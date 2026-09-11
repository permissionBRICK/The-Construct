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
    private const int QueueDepth = 64;
    private const int FrameBytes = 3200; // 100 ms of S16LE 16 kHz mono
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
        // Keep the MFT stream open between callbacks. A temporary empty queue must
        // block rather than return zero (which means end-of-stream to the resampler).
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var input = Channel.CreateBounded<byte[]>(QueueDepth);
        var output = Channel.CreateBounded<ReadOnlyMemory<byte>>(QueueDepth);
        capture.DataAvailable += (_, args) =>
        {
            if (args.BytesRecorded==0) return;
            if (!input.Writer.TryWrite(args.Buffer.AsSpan(0,args.BytesRecorded).ToArray()))
                input.Writer.TryComplete(new IOException("Audio capture queue filled."));
        };
        capture.RecordingStopped += (_, args) =>
        { input.Writer.TryComplete(args.Exception is null ? null : new IOException("Audio capture stopped.")); };
        var conversion = Task.Run(() =>
        {
            try
            {
                using var resampler = new MediaFoundationResampler(new CaptureInput(input.Reader,capture.WaveFormat,stop.Token),new WaveFormat(16000,16,1)) { ResamplerQuality=60 };
                var buffer = new byte[FrameBytes]; int read;
                while ((read=resampler.Read(buffer,0,buffer.Length))>0)
                    if (!output.Writer.TryWrite(buffer.AsMemory(0,read).ToArray())) throw new IOException("Audio consumer fell behind.");
                output.Writer.TryComplete();
            }
            catch (OperationCanceledException) { output.Writer.TryComplete(); }
            catch (Exception e) { output.Writer.TryComplete(new IOException("Audio conversion failed.", e)); }
        },CancellationToken.None);
        try
        {
            capture.StartRecording();
            await foreach (var frame in output.Reader.ReadAllAsync(cancellationToken)) yield return frame;
        }
        finally
        {
            capture.StopRecording(); stop.Cancel(); input.Writer.TryComplete(); await conversion;
            // NAudio Dispose joins the recording worker and releases COM.
        }
    }
    private sealed class CaptureInput(ChannelReader<byte[]> input,WaveFormat format,CancellationToken cancellationToken) : IWaveProvider
    {
        private byte[]? current;
        private int position;
        public WaveFormat WaveFormat => format;
        public int Read(byte[] buffer,int offset,int count)
        {
            if (count==0) return 0;
            if (current is null || position==current.Length)
            {
                try { current=input.ReadAsync(cancellationToken).AsTask().GetAwaiter().GetResult(); position=0; }
                catch (ChannelClosedException) { return 0; }
            }
            var copied=Math.Min(count,current.Length-position);
            current.AsSpan(position,copied).CopyTo(buffer.AsSpan(offset,copied)); position+=copied; return copied;
        }
    }
}
