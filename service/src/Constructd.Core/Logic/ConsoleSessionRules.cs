using Constructd.Core.Abstractions;
namespace Constructd.Core.Logic;

public static class ConsoleSessionRules
{
    public const int MaxScreenshotBytes = 4 << 20;
    public const int MaxPixels = 4 << 20;
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(60);
    public static bool Dimensions(int width, int height, int nativeWidth, int nativeHeight) =>
        width > 0 && height > 0 && width <= nativeWidth && height <= nativeHeight && (long)width * height <= MaxPixels;
    public static bool Valid(KeyboardInput i) => i.Kind switch
    {
        KeyboardInputKind.Text => i.Text is { Length: <= 512 } && i.KeyCode is null && i.Press is null && i.Scancodes is null,
        KeyboardInputKind.Key => i.KeyCode is >= 0 and <= 255 && i.Text is null && i.Scancodes is null,
        KeyboardInputKind.Scancodes => i.Scancodes is { Count: > 0 and <= 64 } && i.Text is null && i.KeyCode is null && i.Press is null,
        KeyboardInputKind.CtrlAltDel => i.Text is null && i.KeyCode is null && i.Press is null && i.Scancodes is null,
        _ => false
    };
    public static bool Valid(MouseInput i, int width, int height) => i.Kind switch
    {
        MouseInputKind.MoveAbsolute => i.X >= 0 && i.X < width && i.Y >= 0 && i.Y < height && i.Dx is null && i.Dy is null && i.Button is null,
        MouseInputKind.MoveRelative => i.Dx is >= -128 and <= 127 && i.Dy is >= -128 and <= 127 && i.X is null && i.Y is null && i.Button is null,
        MouseInputKind.Click or MouseInputKind.Press or MouseInputKind.Release => i.Button is >= 1 and <= 3 && i.X is null && i.Y is null && i.Dx is null && i.Dy is null,
        _ => false
    };
}
