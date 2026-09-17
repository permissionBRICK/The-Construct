using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Proxmox;

public sealed class ProxmoxConsoleTransport(IProcessRunner runner, ConstructdOptions options) : IConsoleTransport
{
    private readonly ProxmoxCommands commands = new(runner, options);
    private readonly ProxmoxQmp qmp = new(runner, options);
    private readonly SemaphoreSlim[] gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();
    public ConsoleCapabilities Capabilities { get; } = new(CapabilityLevel.Supported, CapabilityLevel.Supported,
        CapabilityLevel.Supported, CapabilityLevel.Supported, CapabilityLevel.Supported, ConsoleSessionRules.MaxScreenshotBytes, true);
    private async Task<T> WithVm<T>(string name, Func<int, Task<T>> action, CancellationToken ct)
    {
        var gate = gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(name) % gates.Length];
        await gate.WaitAsync(ct);
        try { return await action(await commands.RequireAsync(name, ct)); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ConsoleTransportException) { throw; }
        catch { throw new ConsoleTransportException(); }
        finally { gate.Release(); }
    }
    private async Task<PpmPngEncoder.Pixels> CaptureAsync(int id, CancellationToken ct)
    {
        var directory = Directory.CreateTempSubdirectory("construct-console-");
        var path = Path.Combine(directory.FullName, "screen.ppm");
        try
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory.FullName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            await qmp.RunAsync(id, [new { execute = "screendump", arguments = new { filename = path } }], false, ct);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            if (stream.Length > PpmPngEncoder.MaxPpmBytes) throw new ConsoleTransportException(true);
            var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, ct);
            return PpmPngEncoder.Read(bytes);
        }
        finally { if (File.Exists(path)) File.Delete(path); directory.Delete(); }
    }
    public Task<ConsoleScreen> GetScreenAsync(string vmName, CancellationToken ct) => WithVm(vmName, async id =>
    {
        var config = await commands.ConfigAsync(id, ct);
        var vga = ProxmoxCommands.String(config, "vga") ?? "std";
        var tablet = !config.TryGetProperty("tablet", out var t) || t.ToString() != "0";
        if (vga == "none" || vga.StartsWith("serial", StringComparison.Ordinal)) return new ConsoleScreen(0, 0, false, true, tablet, true);
        var pixels = await CaptureAsync(id, ct);
        return new ConsoleScreen(pixels.Width, pixels.Height, true, true, tablet, true);
    }, ct);
    public Task<ConsoleImage> ScreenshotAsync(string vmName, int width, int height, CancellationToken ct)
    {
        if (!ConsoleSessionRules.Dimensions(width, height, 65535, 65535)) throw new ConsoleTransportException();
        return WithVm(vmName, async id =>
        {
            var pixels = await CaptureAsync(id, ct);
            if (width != pixels.Width || height != pixels.Height) throw new ConsoleTransportException();
            return new ConsoleImage(true, 0, width, height, PpmPngEncoder.Encode(pixels));
        }, ct);
    }
    public Task<ConsoleInputResult> KeyboardAsync(string vmName, KeyboardInput input, CancellationToken ct)
    {
        var events = ProxmoxConsoleInput.Keyboard(input); // Validate every character before applying any.
        return WithVm(vmName, async id =>
        {
            await qmp.RunAsync(id, events, true, ct, paced: input.Kind == KeyboardInputKind.Text);
            return new ConsoleInputResult(true, 0, "keyboard", null);
        }, ct);
    }
    public Task<ConsoleInputResult> MouseAsync(string vmName, MouseInput input, CancellationToken ct)
    {
        if (!ConsoleSessionRules.Valid(input, 65535, 65535)) throw new ConsoleTransportException();
        return WithVm(vmName, async id =>
        {
            var width = 65535; var height = 65535;
            if (input.Kind == MouseInputKind.MoveAbsolute)
            {
                var config = await commands.ConfigAsync(id, ct);
                if (config.TryGetProperty("tablet", out var tablet) && tablet.ToString() == "0")
                    return new ConsoleInputResult(false, 1, "syntheticMouse", "moveRelative");
                var pixels = await CaptureAsync(id, ct); width = pixels.Width; height = pixels.Height;
            }
            await qmp.RunAsync(id, [ProxmoxConsoleInput.Mouse(input, width, height)], true, ct);
            return new ConsoleInputResult(true, 0, input.Kind == MouseInputKind.MoveRelative ? "ps2Mouse" : "syntheticMouse", null);
        }, ct);
    }
}
