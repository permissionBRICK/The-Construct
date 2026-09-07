using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Core.Services;

public sealed class UnsupportedConsoleTransport : IConsoleTransport
{
    public ConsoleCapabilities Capabilities => UnsupportedCapabilities.Console;
    public Task<ConsoleScreen> GetScreenAsync(string vmName, CancellationToken ct) => throw new NotSupportedException("Console is unsupported.");
    public Task<ConsoleImage> ScreenshotAsync(string vmName, int width, int height, CancellationToken ct) => throw new NotSupportedException("Console is unsupported.");
    public Task<ConsoleInputResult> KeyboardAsync(string vmName, KeyboardInput input, CancellationToken ct) => Task.FromResult(new ConsoleInputResult(false, 32770, "keyboard", null));
    public Task<ConsoleInputResult> MouseAsync(string vmName, MouseInput input, CancellationToken ct) => Task.FromResult(new ConsoleInputResult(false, 32770, "mouse", null));
}
