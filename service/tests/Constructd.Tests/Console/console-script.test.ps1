# Actual console program with WMI doubles; no Hyper-V or guest dependencies.
$ErrorActionPreference = 'Stop'
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../../../src/Constructd.Windows/Console/HyperVConsoleScript.cs'))
$source = ($source -split '"""')[1]
$source = ($source -split "`n" | ForEach-Object { $_ -replace '^        ', '' }) -join "`n"
$tokens = $null; $errors = $null
$null = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw 'Console script does not parse' }
$script:checks = 1
$temp = Join-Path ([IO.Path]::GetTempPath()) ('console-script-' + [guid]::NewGuid().ToString('n'))
$null = New-Item -ItemType Directory -Path $temp
try {
    # pwsh resolves GDI enum constants during compilation, even on untaken branches.
    # These tests substitute the encoder with a sentinel, never claim PNG encoding.
    $source = $source -replace '\[Drawing.Imaging.PixelFormat\]::Format16bppRgb565', '0'
    $source = $source -replace '\[Drawing.Imaging.ImageLockMode\]::WriteOnly', '0'
    $source = $source -replace '\[Drawing.Imaging.ImageFormat\]::Png', '0'
    $fixture = @'
$script:fixture = $env:CONSOLE_FIXTURE | ConvertFrom-Json
function Record-Call($device, $method, $values, $types) {
    @{device=$device;method=$method;values=@($values);types=@($types)} | ConvertTo-Json -Depth 5 -Compress | Add-Content -LiteralPath $env:CONSOLE_CALLS
    [pscustomobject]@{ReturnValue=[uint32]$script:fixture.returnValue}
}
function Add-Type {
    param($AssemblyName)
    if ($AssemblyName -ne 'System.Drawing') { throw 'Unexpected assembly' }
    [IO.File]::WriteAllText($env:CONSOLE_ENCODING_MARKER, 'reached')
    throw 'Encoding sentinel'
}
function Get-VM {
    # An unrelated VM always comes first: never select a global first device.
    [pscustomobject]@{Name='unrelated-vm';Id=[guid]'22222222-2222-2222-2222-222222222222';State='Running'}
    for ($n=0; $n -lt $script:fixture.vmCount; $n++) {
        [pscustomobject]@{Name='probe-vm';Id=[guid]'11111111-1111-1111-1111-111111111111';State=$script:fixture.state}
    }
}
function New-Keyboard {
    $k = [pscustomobject]@{}
    $k | Add-Member ScriptMethod TypeText { param($v) Record-Call 'keyboard' 'TypeText' @($v) @($v.GetType().Name) }
    $k | Add-Member ScriptMethod TypeKey { param($v) Record-Call 'keyboard' 'TypeKey' @($v) @($v.GetType().Name) }
    $k | Add-Member ScriptMethod PressKey { param($v) Record-Call 'keyboard' 'PressKey' @($v) @($v.GetType().Name) }
    $k | Add-Member ScriptMethod ReleaseKey { param($v) Record-Call 'keyboard' 'ReleaseKey' @($v) @($v.GetType().Name) }
    $k | Add-Member ScriptMethod TypeScancodes { param($v) Record-Call 'keyboard' 'TypeScancodes' $v @($v.GetType().Name) }
    $k | Add-Member ScriptMethod TypeCtrlAltDel { Record-Call 'keyboard' 'TypeCtrlAltDel' @() @() }
    $k
}
function New-Mouse($device) {
    $m = [pscustomobject]@{Device=$device}
    $m | Add-Member ScriptMethod SetAbsolutePosition { param($x,$y) Record-Call $this.Device 'SetAbsolutePosition' @($x,$y) @($x.GetType().Name,$y.GetType().Name) }
    $m | Add-Member ScriptMethod SetRelativePosition { param($x,$y) Record-Call $this.Device 'SetRelativePosition' @($x,$y) @($x.GetType().Name,$y.GetType().Name) }
    $m | Add-Member ScriptMethod ClickButton { param($b) Record-Call $this.Device 'ClickButton' @($b) @($b.GetType().Name) }
    $m | Add-Member ScriptMethod PressButton { param($b) Record-Call $this.Device 'PressButton' @($b) @($b.GetType().Name) }
    $m | Add-Member ScriptMethod ReleaseButton { param($b) Record-Call $this.Device 'ReleaseButton' @($b) @($b.GetType().Name) }
    $m
}
function Get-WmiObject {
    param($Namespace, $Class, $Filter)
    if ($Namespace -ne 'root\virtualization\v2') { throw 'Wrong namespace' }
    if ($Class -eq 'Msvm_ComputerSystem') {
        if ($Filter -ne "Name='11111111-1111-1111-1111-111111111111'") { throw 'Wrong VM selection' }
        $cs = [pscustomobject]@{}
        $cs | Add-Member ScriptMethod GetRelated {
            param($kind)
            switch ($kind) {
                'Msvm_VideoHead' { if ($script:fixture.video) { [pscustomobject]@{CurrentHorizontalResolution=1024;CurrentVerticalResolution=768} } }
                'Msvm_VirtualSystemSettingData' { [pscustomobject]@{VirtualSystemType='Microsoft:Hyper-V:System:Realized';__PATH='realized-settings'} }
                'Msvm_Keyboard' { if ($script:fixture.keyboard) { New-Keyboard } }
                'Msvm_SyntheticMouse' { if ($script:fixture.synthetic) { New-Mouse 'syntheticMouse' } }
                'Msvm_Ps2Mouse' { if ($script:fixture.ps2) { New-Mouse 'ps2Mouse' } }
                default { throw 'Unexpected association' }
            }
        }
        return $cs
    }
    if ($Class -ne 'Msvm_VirtualSystemManagementService') { throw 'Wrong class' }
    $svc=[pscustomobject]@{}
    $svc | Add-Member ScriptMethod GetVirtualSystemThumbnailImage {
        param($target,$width,$height)
        if ($target -ne 'realized-settings' -or $width -ne 1 -or $height -ne 1) { throw 'Wrong screenshot invocation' }
        [pscustomobject]@{ReturnValue=0;ImageData=[byte[]]($script:fixture.bytes -split ',' | ForEach-Object {[byte]$_})}
    }
    return $svc
}
'@
    $fixturePath = Join-Path $temp 'fixture.ps1'
    [IO.File]::WriteAllText($fixturePath, $fixture + "`n" + $source)
    function Check($condition, $message) {
        if (!$condition) { throw $message }
        $script:checks++
    }
    function Run-Console($request, $overrides = @{}, $exitCode = 0) {
        $settings = @{vmCount=1;state='Running';video=$true;keyboard=$true;synthetic=$true;ps2=$false;returnValue=0;bytes='0,0,0,6,0,248'}
        foreach ($key in $overrides.Keys) { $settings[$key] = $overrides[$key] }
        $id = [guid]::NewGuid().ToString('n')
        $callsPath = Join-Path $temp ($id + '.calls')
        $markerPath = Join-Path $temp ($id + '.marker')
        $si = New-Object Diagnostics.ProcessStartInfo
        $si.FileName = 'pwsh'; $si.Arguments = '-NoProfile -File ' + $fixturePath
        $si.UseShellExecute = $false
        $si.RedirectStandardInput = $true; $si.RedirectStandardOutput = $true; $si.RedirectStandardError = $true
        $si.EnvironmentVariables['CONSOLE_FIXTURE'] = $settings | ConvertTo-Json -Compress
        $si.EnvironmentVariables['CONSOLE_CALLS'] = $callsPath
        $si.EnvironmentVariables['CONSOLE_ENCODING_MARKER'] = $markerPath
        $p = New-Object Diagnostics.Process; $p.StartInfo = $si
        try {
            $null = $p.Start()
            $out = $p.StandardOutput.ReadToEndAsync(); $err = $p.StandardError.ReadToEndAsync()
            $p.StandardInput.Write(($request | ConvertTo-Json -Depth 5 -Compress))
            $p.StandardInput.Close()
            if (!$p.WaitForExit(10000)) { $p.Kill(); throw 'Fixture timeout' }
            $stdout = $out.GetAwaiter().GetResult().Trim(); $stderr = $err.GetAwaiter().GetResult().Trim()
            Check ($p.ExitCode -eq $exitCode) ('Unexpected fixture exit for ' + $request.action)
            if ($exitCode -eq 1) { Check ($stdout -eq '' -and $stderr -eq 'Console operation unavailable') 'Failure must be sanitized' }
            else { Check ($stderr -eq '') 'Unexpected stderr' }
            $calls = @(); if (Test-Path -LiteralPath $callsPath) { $calls = @(Get-Content -LiteralPath $callsPath | ForEach-Object { $_ | ConvertFrom-Json }) }
            $body = $null; if ($stdout) { $body = $stdout | ConvertFrom-Json }
            return @{body=$body;calls=$calls;encoding=(Test-Path -LiteralPath $markerPath)}
        } finally { $p.Dispose() }
    }
    function One-Call($result, $device, $method, $values, $types) {
        Check ($result.calls.Count -eq 1) 'Expected exactly one WMI method'
        $call = $result.calls[0]
        Check ($call.device -eq $device -and $call.method -eq $method) 'Incorrect device or WMI method'
        Check ((ConvertTo-Json @($call.values) -Compress) -ceq (ConvertTo-Json @($values) -Compress)) 'Incorrect WMI arguments'
        Check ((ConvertTo-Json @($call.types) -Compress) -ceq (ConvertTo-Json @($types) -Compress)) 'Incorrect WMI argument types'
    }
    $r = Run-Console @{action='screen';vm='probe-vm'}
    Check ($r.body.nativeWidth -eq 1024 -and $r.body.nativeHeight -eq 768 -and $r.body.videoHeadPresent -and $r.body.keyboardPresent -and $r.body.syntheticMousePresent -and !$r.body.ps2MousePresent) 'Screen fields differ'
    Check (@($r.body.PSObject.Properties).Count -eq 6) 'Expected all six screen fields'
    foreach ($state in @('Off','Saved','Paused')) {
        $r = Run-Console @{action='screen';vm='probe-vm'} @{state=$state}
        Check ($r.body.nativeWidth -eq 0 -and $r.body.nativeHeight -eq 0) 'Nonrunning VM exposes resolution'
    }
    $r = Run-Console @{action='screen';vm='probe-vm'} @{video=$false}
    Check (!$r.body.videoHeadPresent -and $r.body.nativeWidth -eq 0 -and $r.body.nativeHeight -eq 0) 'Absent video head exposed'
    foreach ($count in @(0,2)) { $r = Run-Console @{action='screen';vm='probe-vm'} @{vmCount=$count} 1; Check ($r.calls.Count -eq 0) 'Invalid lookup invoked a device' }
    foreach ($case in @(@{press=$true;method='PressKey'},@{press=$false;method='ReleaseKey'},@{press=$null;method='TypeKey'})) {
        $r = Run-Console @{action='keyboard';vm='probe-vm';input=@{kind='key';keyCode=13;press=$case.press}}
        One-Call $r 'keyboard' $case.method @(13) @('Int32')
        Check ($r.body.applied -and $r.body.returnValue -eq 0) 'Key not accepted'
    }
    $text = "Gr$([char]0x00fc)$([char]0x00df)e ' `$()"
    $r = Run-Console @{action='keyboard';vm='probe-vm';input=@{kind='text';text=$text}}
    One-Call $r 'keyboard' 'TypeText' @($text) @('String')
    $r = Run-Console @{action='keyboard';vm='probe-vm';input=@{kind='scancodes';scancodes=@(15,143)}}
    One-Call $r 'keyboard' 'TypeScancodes' @(15,143) @('Byte[]')
    $r = Run-Console @{action='keyboard';vm='probe-vm';input=@{kind='ctrlAltDel'}}
    One-Call $r 'keyboard' 'TypeCtrlAltDel' @() @()
    $r = Run-Console @{action='keyboard';vm='probe-vm';input=@{kind='ctrlAltDel'}} @{keyboard=$false} 1
    Check ($r.calls.Count -eq 0) 'Absent keyboard invoked'
    $r = Run-Console @{action='keyboard';vm='probe-vm';input=@{kind='ctrlAltDel'}} @{returnValue=32768}
    Check (!$r.body.applied -and $r.body.returnValue -eq 32768 -and $r.body.device -eq 'keyboard') 'Numeric keyboard failure hidden'
    $r = Run-Console @{action='keyboard';vm='probe-vm';input=@{kind='key';keyCode=16;press=$true}} @{returnValue=32768}
    Check ($r.calls.Count -eq 2 -and $r.calls[0].method -eq 'PressKey' -and $r.calls[1].method -eq 'ReleaseKey') 'Failed press not released'
    foreach ($ps2 in @($false,$true)) {
        $r = Run-Console @{action='mouse';vm='probe-vm';input=@{kind='moveAbsolute';x=190;y=505}} @{ps2=$ps2}
        One-Call $r 'syntheticMouse' 'SetAbsolutePosition' @(190,505) @('Int32','Int32')
        Check (($ps2 -and $r.body.fallback -eq 'moveRelative') -or (!$ps2 -and $null -eq $r.body.fallback)) 'Incorrect relative fallback hint'
    }
    $r = Run-Console @{action='mouse';vm='probe-vm';input=@{kind='moveRelative';dx=-128;dy=127}} @{ps2=$true}
    One-Call $r 'ps2Mouse' 'SetRelativePosition' @(-128,127) @('SByte','SByte')
    foreach ($synthetic in @($true,$false)) {
        foreach ($case in @(@{kind='click';method='ClickButton'},@{kind='press';method='PressButton'},@{kind='release';method='ReleaseButton'})) {
            $r = Run-Console @{action='mouse';vm='probe-vm';input=@{kind=$case.kind;button=1}} @{synthetic=$synthetic;ps2=$true}
            $device = 'ps2Mouse'; if ($synthetic) { $device = 'syntheticMouse' }
            One-Call $r $device $case.method @(1) @('Int32')
        }
    }
    $r = Run-Console @{action='mouse';vm='probe-vm';input=@{kind='moveAbsolute';x=1024;y=0}} @{} 1
    Check ($r.calls.Count -eq 0) 'Out-of-bounds mouse invoked'
    $r = Run-Console @{action='mouse';vm='probe-vm';input=@{kind='click';button=1}} @{synthetic=$false;ps2=$false} 1
    Check ($r.calls.Count -eq 0) 'Absent mice invoked'
    $r = Run-Console @{action='mouse';vm='probe-vm';input=@{kind='click';button=1}} @{returnValue=32768}
    Check (!$r.body.applied -and $r.body.returnValue -eq 32768 -and $r.body.device -eq 'syntheticMouse') 'Numeric mouse failure hidden'
    foreach ($case in @(@{kind='click';method='ClickButton'},@{kind='press';method='PressButton'},@{kind='release';method='ReleaseButton'})) {
        $r = Run-Console @{action='mouse';vm='probe-vm';input=@{kind=$case.kind;button=1}} @{ps2=$true;returnValue=32768}
        Check ($r.calls.Count -eq 2 -and $r.calls[0].device -eq 'syntheticMouse' -and $r.calls[1].device -eq 'ps2Mouse') 'Failed synthetic button did not try PS/2'
        Check ($r.calls[1].method -eq $case.method -and $r.calls[1].values[0] -eq 1) 'PS/2 fallback used wrong button method'
        Check (!$r.body.applied -and $r.body.device -eq 'ps2Mouse' -and $r.body.returnValue -eq 32768) 'PS/2 result hidden'
    }
    foreach ($bytes in @('0,0,0,5,0', '6,0,0,0,0,0', '0,0,0,7,0,0', '0,0,0,6,0,248')) {
        $r = Run-Console @{action='screenshot';vm='probe-vm';width=1;height=1} @{bytes=$bytes} 1
        Check ($r.encoding -eq ($bytes -eq '0,0,0,6,0,248')) 'RGB565 validation accepted malformed data or refused valid red pixel'
    }
    "$script:checks/$script:checks console script checks passed (WMI doubles, encoding sentinel)."
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
