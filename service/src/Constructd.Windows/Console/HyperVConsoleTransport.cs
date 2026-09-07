using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Windows.Console;

public sealed class HyperVConsoleTransport(IProcessRunner runner) : IConsoleTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };
    public static IReadOnlyList<string> Arguments { get; } = Array.AsReadOnly(new[]
    { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(HyperVConsoleScript.Source)) });
    public ConsoleCapabilities Capabilities { get; } = new(CapabilityLevel.Supported, CapabilityLevel.Supported,
        CapabilityLevel.Conditional, CapabilityLevel.Unsupported, CapabilityLevel.Unsupported, ConsoleSessionRules.MaxScreenshotBytes, true);
    // Bounded stripes serialize input for the same VM without retaining a lock per historical name.
    private readonly SemaphoreSlim[] _gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1)).ToArray();

    private async Task<JsonDocument> Run(object payload, string vm, CancellationToken ct)
    {
        var gate = _gates[(uint)StringComparer.OrdinalIgnoreCase.GetHashCode(vm) % _gates.Length];
        await gate.WaitAsync(ct);
        try
        {
            var r = await runner.RunAsync("powershell.exe", Arguments, JsonSerializer.Serialize(payload, Json), TimeSpan.FromSeconds(20), null, ct);
            if (!r.Succeeded || r.StandardOutput.Length > 6 * 1024 * 1024) throw new ConsoleTransportException();
            return JsonDocument.Parse(r.StandardOutput);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new ConsoleTransportException(); }
        finally { gate.Release(); }
    }
    public async Task<ConsoleScreen> GetScreenAsync(string vmName, CancellationToken ct)
    {
        using var doc = await Run(new { action = "screen", vm = vmName }, vmName, ct);
        try
        {
            var r = doc.RootElement;
            var screen = new ConsoleScreen(r.GetProperty("nativeWidth").GetInt32(), r.GetProperty("nativeHeight").GetInt32(),
                r.GetProperty("videoHeadPresent").GetBoolean(), r.GetProperty("keyboardPresent").GetBoolean(),
                r.GetProperty("syntheticMousePresent").GetBoolean(), r.GetProperty("ps2MousePresent").GetBoolean());
            if (screen.NativeWidth < 0 || screen.NativeHeight < 0 || screen.NativeWidth > 65535 || screen.NativeHeight > 65535) throw new ConsoleTransportException();
            return screen;
        }
        catch { throw new ConsoleTransportException(); }
    }
    public async Task<ConsoleImage> ScreenshotAsync(string vmName, int width, int height, CancellationToken ct)
    {
        if (!ConsoleSessionRules.Dimensions(width, height, 65535, 65535)) throw new ConsoleTransportException();
        using var doc = await Run(new { action = "screenshot", vm = vmName, width, height }, vmName, ct);
        try
        {
            var r = doc.RootElement;
            if (r.TryGetProperty("tooLarge", out var large) && large.GetBoolean()) throw new ConsoleTransportException(true);
            var rv = r.GetProperty("returnValue").GetUInt32();
            if (rv != 0) return new(false, rv, width, height, ReadOnlyMemory<byte>.Empty);
            if (r.GetProperty("width").GetInt32() != width || r.GetProperty("height").GetInt32() != height) throw new ConsoleTransportException();
            var png = Convert.FromBase64String(r.GetProperty("png").GetString()!);
            if (png.Length > ConsoleSessionRules.MaxScreenshotBytes) throw new ConsoleTransportException(true);
            if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                !png.AsSpan(12, 4).SequenceEqual("IHDR"u8) || BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)) != width ||
                BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)) != height) throw new ConsoleTransportException();
            return new(true, 0, width, height, png);
        }
        catch (ConsoleTransportException) { throw; }
        catch { throw new ConsoleTransportException(); }
    }
    public Task<ConsoleInputResult> KeyboardAsync(string vmName, KeyboardInput input, CancellationToken ct)
    {
        if (!ConsoleSessionRules.Valid(input)) throw new ConsoleTransportException();
        return Input("keyboard", vmName, input, ct);
    }
    public Task<ConsoleInputResult> MouseAsync(string vmName, MouseInput input, CancellationToken ct)
    {
        if (!ConsoleSessionRules.Valid(input, 65535, 65535)) throw new ConsoleTransportException();
        return Input("mouse", vmName, input, ct);
    }
    private async Task<ConsoleInputResult> Input(string action, string vm, object input, CancellationToken ct)
    {
        using var doc = await Run(new { action, vm, input }, vm, ct);
        try
        {
            var r = doc.RootElement;
            var result = new ConsoleInputResult(r.GetProperty("applied").GetBoolean(), r.GetProperty("returnValue").GetUInt32(),
                r.GetProperty("device").GetString(), r.GetProperty("fallback").GetString());
            if (result.Applied != (result.ReturnValue == 0) ||
                (action == "keyboard" ? result.Device != "keyboard" : result.Device is not ("syntheticMouse" or "ps2Mouse")) ||
                result.Fallback is not (null or "moveRelative")) throw new ConsoleTransportException();
            return result;
        }
        catch { throw new ConsoleTransportException(); }
    }
}
