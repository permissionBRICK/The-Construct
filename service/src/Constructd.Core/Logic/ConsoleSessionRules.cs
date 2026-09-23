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
        KeyboardInputKind.Text => i.Text is { Length: <= 512 } && TextScancodes(i.Text) is not null && i.KeyCode is null && i.Press is null && i.Scancodes is null,
        KeyboardInputKind.Key => i.KeyCode is >= 0 and <= 255 && i.Text is null && i.Scancodes is null,
        KeyboardInputKind.Scancodes => i.Scancodes is { Count: > 0 and <= 64 } && i.Text is null && i.KeyCode is null && i.Press is null,
        KeyboardInputKind.CtrlAltDel => i.Text is null && i.KeyCode is null && i.Press is null && i.Scancodes is null,
        _ => false
    };
    public static byte[]? TextScancodes(string text)
    {
        var codes = new List<byte>(text.Length * 4);
        const string plain = "`-=[]\\;',./";
        const string shifted = "~_+{}|:\"<>?";
        byte[] punctuation = [0x29, 0x0c, 0x0d, 0x1a, 0x1b, 0x2b, 0x27, 0x28, 0x33, 0x34, 0x35];
        foreach (var c in text)
        {
            var lower = c is >= 'A' and <= 'Z' ? (char)(c + ('a' - 'A')) : c;
            var shift = c is >= 'A' and <= 'Z';
            byte code;
            if ("1234567890".IndexOf(c) is var digit && digit >= 0) code = (byte)(digit + 2);
            else if ("qwertyuiop".IndexOf(lower) is var top && top >= 0) code = (byte)(top + 0x10);
            else if ("asdfghjkl".IndexOf(lower) is var home && home >= 0) code = (byte)(home + 0x1e);
            else if ("zxcvbnm".IndexOf(lower) is var bottom && bottom >= 0) code = (byte)(bottom + 0x2c);
            else if (plain.IndexOf(c) is var p && p >= 0) code = punctuation[p];
            else if (shifted.IndexOf(c) is var s && s >= 0) { code = punctuation[s]; shift = true; }
            else if ("!@#$%^&*()".IndexOf(c) is var symbol && symbol >= 0) { code = (byte)(symbol + 2); shift = true; }
            else code = c switch { ' ' => 0x39, '\n' or '\r' => 0x1c, '\t' => 0x0f, '\b' => 0x0e, _ => 0 };
            if (code == 0) return null;
            if (shift) codes.Add(0x2a);
            codes.Add(code);
            codes.Add((byte)(code | 0x80));
            if (shift) codes.Add(0xaa);
        }
        return codes.ToArray();
    }
    public static bool Valid(MouseInput i, int width, int height) => i.Kind switch
    {
        MouseInputKind.MoveAbsolute => i.X >= 0 && i.X < width && i.Y >= 0 && i.Y < height && i.Dx is null && i.Dy is null && i.Button is null,
        MouseInputKind.MoveRelative => i.Dx is >= -128 and <= 127 && i.Dy is >= -128 and <= 127 && i.X is null && i.Y is null && i.Button is null,
        MouseInputKind.Click or MouseInputKind.Press or MouseInputKind.Release => i.Button is >= 1 and <= 3 && i.X is null && i.Y is null && i.Dx is null && i.Dy is null,
        _ => false
    };
}
