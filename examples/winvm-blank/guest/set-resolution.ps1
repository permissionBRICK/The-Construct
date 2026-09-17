# Sets and persists the desktop resolution (default 1920x1080). Run in the
# interactive session. Hyper-V starts at 1024x768; QEMU provides an EDID hint.
param(
    [int]$Width = 1920,
    [int]$Height = 1080
)
Add-Type @'
using System;
using System.Runtime.InteropServices;
public class Display {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll")] public static extern int EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
    [DllImport("user32.dll")] public static extern int ChangeDisplaySettings(ref DEVMODE devMode, int flags);
}
'@
$dm = New-Object Display+DEVMODE
$dm.dmSize = [System.Runtime.InteropServices.Marshal]::SizeOf($dm)
[Display]::EnumDisplaySettings($null, -1, [ref]$dm) | Out-Null
$dm.dmPelsWidth = $Width
$dm.dmPelsHeight = $Height
$dm.dmFields = 0x80000 -bor 0x100000   # DM_PELSWIDTH | DM_PELSHEIGHT
$result = [Display]::ChangeDisplaySettings([ref]$dm, 1)   # CDS_UPDATEREGISTRY: survive a reboot
Write-Host "ChangeDisplaySettings($Width x $Height) -> $result (0 = OK)"
if ($result -ne 0) { exit 1 }
Write-Host 'RESOLUTION_OK'
