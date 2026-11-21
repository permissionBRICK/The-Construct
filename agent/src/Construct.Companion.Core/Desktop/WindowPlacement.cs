namespace Construct.Companion.Core.Desktop;

public sealed record WindowBounds(int X, int Y, int Width, int Height);
public static class WindowPlacement
{
    public static WindowBounds Clamp(WindowBounds desired, WindowBounds work)
    {
        var width = Math.Min(Math.Max(1, desired.Width), work.Width); var height = Math.Min(Math.Max(1, desired.Height), work.Height);
        return new(Math.Clamp(desired.X, work.X, work.X + work.Width - width), Math.Clamp(desired.Y, work.Y, work.Y + work.Height - height), width, height);
    }
    /// <summary>The main window's size at 96 DPI: the upgrade's whole convert list fits without scrolling (R6).</summary>
    public const int MainWidth = 1120, MainHeight = 920;

    /// <summary>
    /// Where the main window first opens: <see cref="MainWidth"/> by <see cref="MainHeight"/> scaled to the screen's
    /// <paramref name="dpi"/> (the page inside is laid out in 96-DPI pixels), centred on the working area and never larger.
    /// </summary>
    public static WindowBounds Main(WindowBounds work, int dpi)
    {
        var scale = Math.Max(96, dpi) / 96.0;
        int width = (int)Math.Round(MainWidth * scale), height = (int)Math.Round(MainHeight * scale);
        return Clamp(new(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height), work);
    }

    public static WindowBounds Popup(WindowBounds tray, WindowBounds work, int width, int height)
    {
        var x = tray.X + tray.Width - width; var y = tray.Y - height;
        if (tray.Y <= work.Y) y = tray.Y + tray.Height;
        if (tray.X <= work.X) x = tray.X + tray.Width;
        return Clamp(new(x, y, width, height), work);
    }
}
