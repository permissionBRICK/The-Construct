using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

public sealed record ConsoleScreen(int NativeWidth, int NativeHeight, bool VideoHeadPresent, bool KeyboardPresent, bool SyntheticMousePresent, bool Ps2MousePresent);
public sealed record ConsoleImage(bool Ok, uint ReturnValue, int Width, int Height, ReadOnlyMemory<byte> Png);
public enum KeyboardInputKind { Text, Key, Scancodes, CtrlAltDel }
/// <param name="Press">Key kind only: true = press, false = release, null = press+release.</param>
public sealed record KeyboardInput(KeyboardInputKind Kind, string? Text, int? KeyCode, bool? Press, IReadOnlyList<byte>? Scancodes);
public enum MouseInputKind { MoveAbsolute, MoveRelative, Click, Press, Release }
public sealed record MouseInput(MouseInputKind Kind, int? X, int? Y, int? Dx, int? Dy, int? Button);
public sealed record ConsoleInputResult(bool Applied, uint ReturnValue, string? Device, string? Fallback);

public interface IConsoleTransport
{
    ConsoleCapabilities Capabilities { get; }
    Task<ConsoleScreen> GetScreenAsync(string vmName, CancellationToken ct);
    Task<ConsoleImage> ScreenshotAsync(string vmName, int width, int height, CancellationToken ct);
    /// <summary>Text reaches the host process through stdin, never argv; nothing typed is logged.</summary>
    Task<ConsoleInputResult> KeyboardAsync(string vmName, KeyboardInput input, CancellationToken ct);
    Task<ConsoleInputResult> MouseAsync(string vmName, MouseInput input, CancellationToken ct);
}

public sealed record ConsoleSession(string Id, string VmName, string Principal, DateTimeOffset Created, DateTimeOffset ExpiresAt, int NativeWidth, int NativeHeight);
public interface IConsoleSessionStore
{
    /// <summary>null when the per-VM cap (4) is reached.</summary>
    ConsoleSession? TryCreate(string vmName, string principal, int nativeWidth, int nativeHeight, TimeSpan ttl, DateTimeOffset now);
    ConsoleSession? Get(string id, DateTimeOffset now);
    ConsoleSession? Renew(string id, TimeSpan ttl, DateTimeOffset now, int? nativeWidth = null, int? nativeHeight = null);
    bool Remove(string id);
    int RemoveExpired(DateTimeOffset now);
    int RemoveForVm(string vmName);
    int RemoveForPrincipal(string principal);
    bool TryTakeRate(string id, string bucket, int perSecond, DateTimeOffset now);
}

/// <summary>Sanitized failure; no process output or input may be attached as an inner exception.</summary>
public sealed class ConsoleTransportException(bool tooLarge = false) : Exception("Console operation unavailable.")
{
    public bool TooLarge { get; } = tooLarge;
}
