using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public static class DisplayDpi
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    // The tray lives on whichever monitor holds the taskbar; the icon and popup scale to that DPI.
    public static int At(int x, int y) { var dpi = GetDpiForWindow(WindowFromPoint(new Point { X = x, Y = y })); return dpi == 0 ? 96 : (int)dpi; }
}
