namespace Construct.Companion.Core.Desktop;

public sealed record WindowBounds(int X, int Y, int Width, int Height);
public static class WindowPlacement
{
    public static WindowBounds Clamp(WindowBounds desired, WindowBounds work)
    {
        var width = Math.Min(Math.Max(1, desired.Width), work.Width); var height = Math.Min(Math.Max(1, desired.Height), work.Height);
        return new(Math.Clamp(desired.X, work.X, work.X + work.Width - width), Math.Clamp(desired.Y, work.Y, work.Y + work.Height - height), width, height);
    }
    public static WindowBounds Popup(WindowBounds tray, WindowBounds work, int width, int height)
    {
        var x = tray.X + tray.Width - width; var y = tray.Y - height;
        if (tray.Y <= work.Y) y = tray.Y + tray.Height;
        if (tray.X <= work.X) x = tray.X + tray.Width;
        return Clamp(new(x, y, width, height), work);
    }
}
