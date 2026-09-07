using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
namespace Constructd.Api.Contracts;

public sealed record ConsoleSessionRequest(string? OperationKey = null);
public sealed record ConsoleKeyboardRequest(string? Kind, string? Text = null, int? KeyCode = null, bool? Press = null, int[]? Scancodes = null);
public sealed record ConsoleMouseRequest(string? Kind, int? X = null, int? Y = null, int? Dx = null, int? Dy = null, int? Button = null);
public sealed record ConsoleCapabilitiesResponse(CapabilityLevel Screenshot, CapabilityLevel Keyboard, CapabilityLevel MouseAbsolute,
    CapabilityLevel MouseRelative, CapabilityLevel Interactive, int? NativeWidth, int? NativeHeight, bool VideoHeadPresent,
    bool KeyboardPresent, bool SyntheticMousePresent, bool Ps2MousePresent)
{
    public string InteractiveReason => "Interactive video requires a separate authenticated transport and is not implemented.";
    public static ConsoleCapabilitiesResponse From(ConsoleCapabilities caps, ConsoleScreen s) => new(
        Lower(caps.Screenshot, s.VideoHeadPresent), Lower(caps.Keyboard, s.KeyboardPresent),
        Lower(caps.MouseAbsolute, s.SyntheticMousePresent),
        // Gen 2 has no PS/2 device; a discovered PS/2 device enables the relative fallback.
        s.Ps2MousePresent ? CapabilityLevel.Conditional : CapabilityLevel.Unsupported,
        CapabilityLevel.Unsupported, s.NativeWidth > 0 ? s.NativeWidth : null, s.NativeHeight > 0 ? s.NativeHeight : null,
        s.VideoHeadPresent, s.KeyboardPresent, s.SyntheticMousePresent, s.Ps2MousePresent);
    private static CapabilityLevel Lower(CapabilityLevel level, bool present) =>
        !present || level == CapabilityLevel.Unsupported ? CapabilityLevel.Unsupported : CapabilityLevel.Conditional;
}
