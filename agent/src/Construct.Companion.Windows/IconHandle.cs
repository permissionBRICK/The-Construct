using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Construct.Companion.Windows;

[SupportedOSPlatform("windows")]
public static class IconHandle
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr icon);
}
