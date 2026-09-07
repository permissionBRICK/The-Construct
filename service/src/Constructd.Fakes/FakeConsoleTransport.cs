using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Fakes;

public sealed class FakeConsoleTransport : IConsoleTransport
{
    private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
    public ConsoleCapabilities Capabilities { get; set; } = new(CapabilityLevel.Supported, CapabilityLevel.Supported, CapabilityLevel.Conditional, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported, 4 << 20, true);
    public Func<string, bool>? IsRunning { get; set; }
    public int ScreenshotCalls { get; private set; }
    public ConsoleScreen Screen { get; set; } = new(1, 1, true, true, true, false);
    public uint ScreenshotReturnValue { get; set; }
    public ConsoleInputResult KeyboardResult { get; set; } = new(true, 0, "keyboard", null);
    public ConsoleInputResult MouseResult { get; set; } = new(false, 32768, "syntheticMouse", null);
    public int KeyboardCalls { get; private set; }
    public int MouseCalls { get; private set; }
    public Task<ConsoleScreen> GetScreenAsync(string vmName, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(IsRunning?.Invoke(vmName) == false ? Screen with { NativeWidth = 0, NativeHeight = 0 } : Screen); }
    public Task<ConsoleImage> ScreenshotAsync(string vmName, int width, int height, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); ScreenshotCalls++; if (width != 1 || height != 1) throw new NotSupportedException("The fake PNG is one pixel."); return Task.FromResult(new ConsoleImage(ScreenshotReturnValue == 0, ScreenshotReturnValue, 1, 1, ScreenshotReturnValue == 0 ? Png : ReadOnlyMemory<byte>.Empty)); }
    public Task<ConsoleInputResult> KeyboardAsync(string vmName, KeyboardInput input, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); KeyboardCalls++; return Task.FromResult(!Screen.KeyboardPresent || IsRunning?.Invoke(vmName) == false ? new ConsoleInputResult(false, 32775, "keyboard", null) : KeyboardResult); }
    public Task<ConsoleInputResult> MouseAsync(string vmName, MouseInput input, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); MouseCalls++; return Task.FromResult(MouseResult); }
}
