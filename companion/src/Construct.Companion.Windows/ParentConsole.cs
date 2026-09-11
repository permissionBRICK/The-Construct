using System.Runtime.InteropServices;
using System.Runtime.Versioning;
namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public static class ParentConsole
{
    [DllImport("kernel32.dll")] private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AttachConsole(uint process);
    // A WinExe has no console; --version and --selftest print into the console that launched them.
    public static void Attach()
    {
        var stdout = GetStdHandle(-11);
        if (stdout == IntPtr.Zero || stdout == new IntPtr(-1)) AttachConsole(uint.MaxValue);
    }
}
