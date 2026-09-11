using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows10.0.14393")]
public static class DesktopDisplay
{
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint process);
    public static int DpiAt(int x,int y) { var dpi=GetDpiForWindow(WindowFromPoint(new Point { X=x,Y=y })); return dpi==0 ? 96 : (int)dpi; }
    public static void AttachParentConsole()
    {
        var stdout=GetStdHandle(-11);
        if (stdout==IntPtr.Zero || stdout==new IntPtr(-1)) AttachConsole(uint.MaxValue);
    }
}
