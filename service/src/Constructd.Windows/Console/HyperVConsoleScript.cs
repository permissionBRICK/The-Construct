namespace Constructd.Windows.Console;

/// <summary>Fixed Windows PowerShell 5.1 program. All request data is JSON on stdin.</summary>
public static class HyperVConsoleScript
{
    public const string Source = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = New-Object Text.UTF8Encoding($false)
        try {
            $q = [Console]::In.ReadToEnd() | ConvertFrom-Json
            $vm = @(Get-VM | Where-Object { $_.Name -eq $q.vm })
            if ($vm.Count -ne 1) { throw 'VM unavailable' }
            $id = $vm[0].Id.ToString()
            $cs = Get-WmiObject -Namespace root\virtualization\v2 -Class Msvm_ComputerSystem -Filter "Name='$id'"
            if ($null -eq $cs) { throw 'VM unavailable' }
            $video = @($cs.GetRelated('Msvm_VideoHead'))
            $keyboard = @($cs.GetRelated('Msvm_Keyboard'))
            $synthetic = @($cs.GetRelated('Msvm_SyntheticMouse'))
            $ps2 = @($cs.GetRelated('Msvm_Ps2Mouse'))
            $w = 0; $h = 0
            if ($video.Count -gt 0 -and $vm[0].State.ToString() -eq 'Running') {
                $w = [int]$video[0].CurrentHorizontalResolution
                $h = [int]$video[0].CurrentVerticalResolution
            }
            if ($q.action -eq 'screen') {
                @{ nativeWidth=$w; nativeHeight=$h; videoHeadPresent=($video.Count -gt 0);
                   keyboardPresent=($keyboard.Count -gt 0); syntheticMousePresent=($synthetic.Count -gt 0);
                   ps2MousePresent=($ps2.Count -gt 0) } | ConvertTo-Json -Compress
            } elseif ($q.action -eq 'screenshot') {
                $width = [int]$q.width; $height = [int]$q.height
                if ($width -lt 1 -or $height -lt 1 -or $width -gt $w -or $height -gt $h -or
                    [long]$width * $height -gt 4194304) { throw 'Screen bounds' }
                $settings = @($cs.GetRelated('Msvm_VirtualSystemSettingData') | Where-Object { $_.VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized' })
                if ($settings.Count -ne 1) { throw 'Settings unavailable' }
                $svc = Get-WmiObject -Namespace root\virtualization\v2 -Class Msvm_VirtualSystemManagementService
                $r = $svc.GetVirtualSystemThumbnailImage($settings[0].__PATH, $width, $height)
                $png = $null
                if ($r.ReturnValue -eq 0) {
                    [byte[]]$bytes = $r.ImageData
                    if ($bytes.Length -ne [long]$width * $height * 2 + 4) { throw 'Image length' }
                    $length = [uint32]$bytes[0] * 16777216 + [uint32]$bytes[1] * 65536 + [uint32]$bytes[2] * 256 + [uint32]$bytes[3]
                    if ($length -ne $bytes.Length) { throw 'Image prefix' }
                    Add-Type -AssemblyName System.Drawing
                    $bitmap = New-Object Drawing.Bitmap($width, $height, [Drawing.Imaging.PixelFormat]::Format16bppRgb565)
                    try {
                        $rect = New-Object Drawing.Rectangle(0, 0, $width, $height)
                        $locked = $bitmap.LockBits($rect, [Drawing.Imaging.ImageLockMode]::WriteOnly, [Drawing.Imaging.PixelFormat]::Format16bppRgb565)
                        try {
                            for ($y = 0; $y -lt $height; $y++) {
                                [Runtime.InteropServices.Marshal]::Copy($bytes, (4 + $y * $width * 2), [IntPtr]::Add($locked.Scan0, $y * $locked.Stride), $width * 2)
                            }
                        } finally { $bitmap.UnlockBits($locked) }
                        $stream = New-Object IO.MemoryStream
                        try {
                            $bitmap.Save($stream, [Drawing.Imaging.ImageFormat]::Png)
                            if ($stream.Length -gt 4194304) { @{ tooLarge=$true } | ConvertTo-Json -Compress; exit 0 }
                            $png = [Convert]::ToBase64String($stream.ToArray())
                        } finally { $stream.Dispose() }
                    } finally { $bitmap.Dispose() }
                }
                @{ returnValue=[uint32]$r.ReturnValue; width=$width; height=$height; png=$png } | ConvertTo-Json -Compress
            } elseif ($q.action -eq 'keyboard') {
                if ($keyboard.Count -eq 0) { throw 'Keyboard unavailable' }
                $k = $keyboard[0]; $i = $q.input
                switch ($i.kind) {
                    'text' { $r = $k.TypeText([string]$i.text) }
                    'key' {
                        if ($null -eq $i.press) { $r = $k.TypeKey([int]$i.keyCode) }
                        elseif ($i.press) {
                            try { $r = $k.PressKey([int]$i.keyCode); if ($r.ReturnValue -ne 0) { $null = $k.ReleaseKey([int]$i.keyCode) } }
                            catch { $null = $k.ReleaseKey([int]$i.keyCode); throw }
                        } else { $r = $k.ReleaseKey([int]$i.keyCode) }
                    }
                    'scancodes' { $r = $k.TypeScancodes([byte[]]$i.scancodes) }
                    'ctrlAltDel' { $r = $k.TypeCtrlAltDel() }
                    default { throw 'Invalid keyboard kind' }
                }
                @{ applied=($r.ReturnValue -eq 0); returnValue=[uint32]$r.ReturnValue; device='keyboard'; fallback=$null } | ConvertTo-Json -Compress
            } elseif ($q.action -eq 'mouse') {
                $i = $q.input; $device = 'syntheticMouse'; $m = $null; $fallback = $null
                if ($i.kind -eq 'moveRelative' -or ($i.kind -ne 'moveAbsolute' -and $synthetic.Count -eq 0)) {
                    $device = 'ps2Mouse'; if ($ps2.Count -gt 0) { $m = $ps2[0] }
                } elseif ($synthetic.Count -gt 0) { $m = $synthetic[0] }
                if ($device -eq 'syntheticMouse' -and $ps2.Count -gt 0) { $fallback = 'moveRelative' }
                if ($null -eq $m) { throw 'Mouse unavailable' }
                switch ($i.kind) {
                    'moveAbsolute' {
                        if ($i.x -lt 0 -or $i.y -lt 0 -or $i.x -ge $w -or $i.y -ge $h) { throw 'Mouse bounds' }
                        $r = $m.SetAbsolutePosition([int]$i.x, [int]$i.y)
                    }
                    'moveRelative' { $r = $m.SetRelativePosition([sbyte]$i.dx, [sbyte]$i.dy) }
                    'click' { $r = $m.ClickButton([int]$i.button) }
                    'press' { $r = $m.PressButton([int]$i.button) }
                    'release' { $r = $m.ReleaseButton([int]$i.button) }
                    default { throw 'Invalid mouse kind' }
                }
                if ($r.ReturnValue -ne 0 -and $device -eq 'syntheticMouse' -and $ps2.Count -gt 0 -and $i.kind -in @('click','press','release')) {
                    $device = 'ps2Mouse'; $fallback = $null
                    switch ($i.kind) {
                        'click' { $r = $ps2[0].ClickButton([int]$i.button) }
                        'press' { $r = $ps2[0].PressButton([int]$i.button) }
                        'release' { $r = $ps2[0].ReleaseButton([int]$i.button) }
                    }
                }
                @{ applied=($r.ReturnValue -eq 0); returnValue=[uint32]$r.ReturnValue; device=$device; fallback=$fallback } | ConvertTo-Json -Compress
            } else { throw 'Invalid action' }
        } catch {
            # Never print $_, request data, screenshots, or a WMI exception.
            [Console]::Error.WriteLine('Console operation unavailable')
            exit 1
        }
        """;
}
