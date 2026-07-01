#Requires -Version 5.1
<#
    Plain-pwsh unit tests for the host-side helpers in lib/AgentVm.Common.ps1 that
    back the control panel's Remote-SSH features. No Pester dependency. Run:

        pwsh -NoProfile -File test/host-lib.test.ps1

    Covers the PURE / safely-testable parts: Get-RemoteOpenLink (deep-link shape),
    Find-VSCodeCli (must not throw when an install-dir base env var is null, e.g.
    32-bit Windows), and Ensure-VSCodeRemoteSsh's exit-code handling for
    `code --install-extension` (a non-zero native exit must NOT be reported as
    success -- the regression the reviewer flagged). The winget / Hyper-V paths
    aren't exercised here (no winget/Hyper-V on a CI box).
#>
$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here "..\lib\AgentVm.Common.ps1")

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

# ── Get-RemoteOpenLink ───────────────────────────────────────────────────────
ok "link: default alias + workspace root" ((Get-RemoteOpenLink) -eq "vscode://vscode-remote/ssh-remote+agent-vm/root/repos")
ok "link: strips the DNS suffix to the alias" ((Get-RemoteOpenLink -VmHost "agent-vm.mshome.net") -eq "vscode://vscode-remote/ssh-remote+agent-vm/root/repos")
ok "link: honours a custom host" ((Get-RemoteOpenLink -VmHost "myvm") -eq "vscode://vscode-remote/ssh-remote+myvm/root/repos")
ok "link: adds a leading slash to the path" ((Get-RemoteOpenLink -WorkspaceRoot "root/repos/x") -eq "vscode://vscode-remote/ssh-remote+agent-vm/root/repos/x")

# ── Find-VSCodeCli: must not throw when an install-base env var is null ───────
# (Reproduces the 32-bit-Windows case where ${env:ProgramFiles(x86)} is undefined.)
$savedX86 = ${env:ProgramFiles(x86)}
${env:ProgramFiles(x86)} = $null
try { $null = Find-VSCodeCli; ok "Find-VSCodeCli: no throw with a null base env var" $true }
catch { ok "Find-VSCodeCli: no throw with a null base env var" $false }
finally { ${env:ProgramFiles(x86)} = $savedX86 }

# ── Ensure-VSCodeRemoteSsh: native exit-code handling via a `code` shim ───────
# Put a fake `code` on PATH so Find-VSCodeCli resolves it; the shim's exit code
# drives the extension-install branch. A non-zero exit must surface a WARNING (not
# the "present" success line); a zero exit must be quiet.
function New-CodeShim([int]$ExitCode, [switch]$EmitStderrWarning) {
    # -EmitStderrWarning makes the shim write a Node-style DEP0169 deprecation warning
    # to STDERR before exiting -- exactly what the real `code` CLI does even on success.
    # In Windows PowerShell 5.1 that stderr, captured with 2>&1 under EAP=Stop, used to
    # be promoted to a terminating error and mistaken for an install failure (the bug).
    $warn = "(node:34672) [DEP0169] DeprecationWarning: url.parse() behavior is not standardized and prone to errors that have security implications. Use the WHATWG URL API instead."
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("code-shim-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $dir | Out-Null
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        $lines = @("@echo off")
        if ($EmitStderrWarning) { $lines += "echo $warn 1>&2" }
        $lines += "exit /b $ExitCode"
        Set-Content -Path (Join-Path $dir "code.cmd") -Value ($lines -join "`r`n") -Encoding ASCII
    } else {
        $shim = Join-Path $dir "code"
        $body = "#!/bin/sh`n"
        if ($EmitStderrWarning) { $body += "echo '$warn' 1>&2`n" }
        $body += "exit $ExitCode`n"
        Set-Content -Path $shim -Value $body -NoNewline
        & chmod +x $shim
    }
    return $dir
}

function Test-EnsureWithShim([int]$ExitCode, [switch]$EmitStderrWarning) {
    $dir = New-CodeShim -ExitCode $ExitCode -EmitStderrWarning:$EmitStderrWarning
    $savedPath = $env:PATH
    $env:PATH = $dir + [System.IO.Path]::PathSeparator + $env:PATH
    try {
        $warns = @()
        # 6>$null swallows the Write-Host status lines; warnings are captured in $warns.
        # $ErrorActionPreference stays Stop (as the installers set it) so the shim's
        # stderr write goes through the same promotion path the real one-liner hits.
        $r = Ensure-VSCodeRemoteSsh -WarningVariable warns -WarningAction SilentlyContinue 6>$null
        return [pscustomobject]@{ Result = $r; Warnings = @($warns) }
    } finally {
        $env:PATH = $savedPath
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

$failCase = Test-EnsureWithShim -ExitCode 1
ok "ensure: a non-zero `code --install-extension` exit warns (no false success)" (
    @($failCase.Warnings | Where-Object { $_ -match "install-extension|may not be installed" }).Count -gt 0)
ok "ensure: VS Code being present still returns `$true" ($failCase.Result -eq $true)

$okCase = Test-EnsureWithShim -ExitCode 0
ok "ensure: a zero exit raises no warning" (@($okCase.Warnings).Count -eq 0)
ok "ensure: success path returns `$true" ($okCase.Result -eq $true)

# REGRESSION (Issue 5): `code` exits 0 but writes a DEP0169 deprecation warning to
# stderr. Under EAP=Stop the old `& code ... 2>&1 | Out-Null` promoted that stderr to
# a terminating error and reported a FALSE failure. Success must be decided by the
# exit code alone -- a stderr-only warning on exit 0 raises NO warning.
$okNoisy = Test-EnsureWithShim -ExitCode 0 -EmitStderrWarning
ok "ensure: exit 0 + stderr deprecation warning is NOT a failure (no warning)" (@($okNoisy.Warnings).Count -eq 0)
ok "ensure: exit 0 + stderr warning still returns `$true" ($okNoisy.Result -eq $true)

# And a REAL failure (non-zero exit) must still be reported even when stderr also
# carries the deprecation noise -- the exit code, not the stderr text, is the verdict.
$failNoisy = Test-EnsureWithShim -ExitCode 1 -EmitStderrWarning
ok "ensure: non-zero exit + stderr warning is reported as a failure" (
    @($failNoisy.Warnings | Where-Object { $_ -match "install-extension|may not be installed" }).Count -gt 0)

# ── Invoke-VSCodeCli: returns the exit code and never throws on stderr ─────────
# The core of the Issue-5 fix. A shim that writes a stderr warning and exits 0 must
# return 0 (success) WITHOUT throwing, even with $ErrorActionPreference=Stop set (as
# the installers do); a shim that exits non-zero returns that code.
function Test-InvokeWithShim([int]$ExitCode, [switch]$EmitStderrWarning) {
    $dir = New-CodeShim -ExitCode $ExitCode -EmitStderrWarning:$EmitStderrWarning
    $code = if ($IsWindows -or $env:OS -eq "Windows_NT") { Join-Path $dir "code.cmd" } else { Join-Path $dir "code" }
    try {
        $ErrorActionPreference = "Stop"
        return Invoke-VSCodeCli -Code $code -CodeArgs @('--install-extension', 'x')
    } finally {
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
$threw = $false
try { $rc = Test-InvokeWithShim -ExitCode 0 -EmitStderrWarning } catch { $threw = $true }
ok "invoke: exit 0 + stderr warning does not throw" (-not $threw)
ok "invoke: exit 0 + stderr warning returns 0" ($rc -eq 0)
ok "invoke: non-zero exit is returned faithfully" ((Test-InvokeWithShim -ExitCode 7) -eq 7)

# Regression: if `code` can't be launched at all (path found by Find-VSCodeCli but
# since deleted/unrunnable), the invocation runs no process, so under EAP=Continue
# $LASTEXITCODE keeps a stale value (a prior 0 = false success). A baseline of 0
# reproduces the trap; Invoke-VSCodeCli must still return NON-ZERO for a bad path.
$missing = Join-Path ([System.IO.Path]::GetTempPath()) ("no-such-code-" + [guid]::NewGuid().ToString("N"))
$badThrew = $false
$global:LASTEXITCODE = 0  # prime the stale-zero trap the reviewer flagged
try {
    $ErrorActionPreference = "Stop"
    $rcMissing = Invoke-VSCodeCli -Code $missing -CodeArgs @('--version')
} catch { $badThrew = $true }
ok "invoke: missing/uninvokable code path does not throw" (-not $badThrew)
ok "invoke: missing/uninvokable code path returns non-zero (not stale 0)" ($rcMissing -ne 0)

# NODE_OPTIONS is restored (not leaked) after the call.
$savedNode = $env:NODE_OPTIONS
$env:NODE_OPTIONS = "--max-old-space-size=256"
$null = Test-InvokeWithShim -ExitCode 0 -EmitStderrWarning
ok "invoke: restores a pre-existing NODE_OPTIONS" ($env:NODE_OPTIONS -eq "--max-old-space-size=256")
Remove-Item Env:\NODE_OPTIONS -ErrorAction SilentlyContinue
$null = Test-InvokeWithShim -ExitCode 0 -EmitStderrWarning
ok "invoke: leaves NODE_OPTIONS unset when it started unset" (-not $env:NODE_OPTIONS)
if ($null -ne $savedNode) { $env:NODE_OPTIONS = $savedNode } else { Remove-Item Env:\NODE_OPTIONS -ErrorAction SilentlyContinue }

# ── The exact Windows-PowerShell-5.1 mechanism this fix neutralizes ───────────
# On 5.1, native stderr captured via 2>&1 becomes ErrorRecord objects in the pipeline;
# under EAP=Stop the first is promoted to a TERMINATING error. pwsh 7.x doesn't
# reproduce the native-stderr half, but the promotion half is identical: an ErrorRecord
# flowing into a cmdlet under EAP=Stop throws. Assert (a) the pre-fix construct still
# throws so this test is meaningful, and (b) pinning EAP=Continue (what Invoke-VSCodeCli
# does) neutralizes it -- proving the fix addresses the real trigger, not just the exit
# code.
$mechThrew = $false
$ErrorActionPreference = "Stop"
try { & { Write-Error "(node:1) [DEP0169] DeprecationWarning: url.parse() ..." } 2>&1 | Out-Null }
catch { $mechThrew = $true }
ok "mechanism: an ErrorRecord in the pipeline under EAP=Stop throws (the 5.1 trigger)" $mechThrew

$fixThrew = $false
$ErrorActionPreference = "Stop"
try {
    $eap = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & { Write-Error "(node:1) [DEP0169] DeprecationWarning: url.parse() ..." } 2>&1 | Out-Null }
    finally { $ErrorActionPreference = $eap }
} catch { $fixThrew = $true }
ok "mechanism: pinning EAP=Continue (the fix) neutralizes the promotion" (-not $fixThrew)
ok "mechanism: EAP is restored to Stop afterwards" ($ErrorActionPreference -eq 'Stop')

# ── Get-VSCodeExtensionDir + Build-ControlPanelVsix ──────────────────────────
# Modern VS Code ignores a bare folder copied into ~/.vscode/extensions, so the
# installer now PACKAGES the extension to a .vsix (Build-ControlPanelVsix -- no
# vsce/Node) and installs it with `code --install-extension`. `code` can't run here,
# so we test the packaging: a valid OPC/VSIX (forward-slash entries, both root parts,
# the extension/ payload), test/ + node_modules excluded, and a manifest whose Identity
# mirrors package.json. Paths use [IO.Path]::Combine so nesting is real on Linux too.
Add-Type -AssemblyName System.IO.Compression | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null

$fakeProfile = Join-Path ([System.IO.Path]::GetTempPath()) ("cp-home-" + [guid]::NewGuid().ToString("N"))
$fakeRepo    = Join-Path ([System.IO.Path]::GetTempPath()) ("cp-repo-" + [guid]::NewGuid().ToString("N"))
$vsixOut     = Join-Path ([System.IO.Path]::GetTempPath()) ("cp-" + [guid]::NewGuid().ToString("N") + ".vsix")
$savedProfile = $env:USERPROFILE
try {
    $ext = Join-Path $fakeRepo "extension"
    New-Item -ItemType Directory -Path (Join-Path $ext "src") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $ext "media") -Force | Out-Null
    New-Item -ItemType Directory -Path ([System.IO.Path]::Combine($ext, "test", "node_modules", "playwright")) -Force | Out-Null
    Set-Content -Path (Join-Path $ext "package.json") -Value '{"name":"construct-control-panel","version":"0.1.0","publisher":"permissionbrick","displayName":"The Construct","engines":{"vscode":"^1.80.0"},"extensionKind":["ui"]}'
    Set-Content -Path (Join-Path $ext "extension.js") -Value '// entry'
    Set-Content -Path ([System.IO.Path]::Combine($ext, "src", "remote.js")) -Value '// src'
    Set-Content -Path ([System.IO.Path]::Combine($ext, "media", "panel.css")) -Value '/* css */'
    Set-Content -Path ([System.IO.Path]::Combine($ext, "test", "ui-smoke.js")) -Value '// dev-only'
    Set-Content -Path ([System.IO.Path]::Combine($ext, "test", "node_modules", "playwright", "huge.js")) -Value '// huge dep'

    $env:USERPROFILE = $fakeProfile
    $expectDir = Join-Path $fakeProfile ".vscode\extensions\construct-control-panel"
    ok "Get-VSCodeExtensionDir: under USERPROFILE\.vscode\extensions" ((Get-VSCodeExtensionDir) -eq $expectDir)

    $built = Build-ControlPanelVsix -SourceRoot $fakeRepo -OutFile $vsixOut
    ok "vsix: returns the out path on success" ($built -eq $vsixOut)
    ok "vsix: file exists" (Test-Path -LiteralPath $vsixOut)

    $zip = [System.IO.Compression.ZipFile]::OpenRead($vsixOut)
    try { $names = @($zip.Entries | ForEach-Object { $_.FullName }) } finally { $zip.Dispose() }
    ok "vsix: extension.vsixmanifest at root" ($names -contains 'extension.vsixmanifest')
    ok "vsix: [Content_Types].xml at root" ($names -contains '[Content_Types].xml')
    ok "vsix: payload under extension/ (package.json + extension.js + src)" (
        ($names -contains 'extension/package.json') -and
        ($names -contains 'extension/extension.js') -and
        ($names -contains 'extension/src/remote.js'))
    ok "vsix: forward-slash entry names only (no backslashes)" (-not ($names -match '\\'))
    ok "vsix: EXCLUDES dev-only test/ + node_modules" (
        -not @($names | Where-Object { $_ -like 'extension/test/*' -or $_ -like '*node_modules*' }).Count)

    # Manifest Identity mirrors package.json; Content_Types covers the payload types.
    $tmpx = Join-Path ([System.IO.Path]::GetTempPath()) ("cp-x-" + [guid]::NewGuid().ToString("N"))
    [System.IO.Compression.ZipFile]::ExtractToDirectory($vsixOut, $tmpx)
    try {
        $vmText = Get-Content -LiteralPath (Join-Path $tmpx "extension.vsixmanifest") -Raw
        ok "vsix: manifest Identity matches package.json" (
            ($vmText -match 'Id="construct-control-panel"') -and ($vmText -match 'Version="0\.1\.0"') -and ($vmText -match 'Publisher="permissionbrick"'))
        ok "vsix: manifest carries the engine + ui kind" (
            ($vmText -match 'Engine"\s+Value="\^1\.80\.0"') -and ($vmText -match 'ExtensionKind"\s+Value="ui"'))
        $ctText = Get-Content -LiteralPath (Join-Path $tmpx '[Content_Types].xml') -Raw
        ok "vsix: Content_Types covers .js and .json" (($ctText -match 'Extension="\.js"') -and ($ctText -match 'Extension="\.json"'))
    } finally { Remove-Item -LiteralPath $tmpx -Recurse -Force -ErrorAction SilentlyContinue }

    # Missing extension source -> warns, returns $null, does not throw.
    $emptyRepo = Join-Path ([System.IO.Path]::GetTempPath()) ("cp-empty-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $emptyRepo -Force | Out-Null
    ok "vsix: missing source -> `$null (no throw)" ($null -eq (Build-ControlPanelVsix -SourceRoot $emptyRepo -OutFile $vsixOut -WarningAction SilentlyContinue))
    Remove-Item -LiteralPath $emptyRepo -Recurse -Force -ErrorAction SilentlyContinue
} finally {
    $env:USERPROFILE = $savedProfile
    Remove-Item -LiteralPath $fakeProfile -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fakeRepo -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $vsixOut -Force -ErrorAction SilentlyContinue
}

# ── Resolve-MarkerSource: repo/ref treated as a SOURCE PAIR ──────────────────
# The installed-commit marker must never record a mixed pair. When either -Repo or
# -Ref is supplied on an install, the FULL effective pair wins (defaults included);
# only a param-less reprovision preserves the previously-recorded source.
$mBoth = Resolve-MarkerSource -Repo "fork/X" -Ref "dev" -RepoSupplied $true -RefSupplied $true -ExistingRepo "old/Y" -ExistingRef "main"
ok "marker: both explicit -> that pair (ignores existing)" ($mBoth.Repo -eq "fork/X" -and $mBoth.Ref -eq "dev")

# THE partial-override regression the reviewer flagged: -Repo set, -Ref defaulted to
# "main"; an OLD constructRef=dev must NOT leak in -- the effective pair is fork/X@main.
$mRepoOnly = Resolve-MarkerSource -Repo "fork/X" -Ref "main" -RepoSupplied $true -RefSupplied $false -ExistingRepo "old/Y" -ExistingRef "dev"
ok "marker: -Repo only keeps the effective ref (no stale ref leak)" ($mRepoOnly.Repo -eq "fork/X" -and $mRepoOnly.Ref -eq "main")

$mRefOnly = Resolve-MarkerSource -Repo "permissionBRICK/The-Construct" -Ref "dev" -RepoSupplied $false -RefSupplied $true -ExistingRepo "old/Y" -ExistingRef "main"
ok "marker: -Ref only keeps the effective repo (no stale repo leak)" ($mRefOnly.Repo -eq "permissionBRICK/The-Construct" -and $mRefOnly.Ref -eq "dev")

$mReprov = Resolve-MarkerSource -Repo "permissionBRICK/The-Construct" -Ref "main" -RepoSupplied $false -RefSupplied $false -ExistingRepo "fork/Z" -ExistingRef "beta"
ok "marker: param-less reprovision preserves the recorded pair" ($mReprov.Repo -eq "fork/Z" -and $mReprov.Ref -eq "beta")

$mFresh = Resolve-MarkerSource -Repo "permissionBRICK/The-Construct" -Ref "main" -RepoSupplied $false -RefSupplied $false -ExistingRepo "" -ExistingRef ""
ok "marker: no explicit + no existing -> defaults" ($mFresh.Repo -eq "permissionBRICK/The-Construct" -and $mFresh.Ref -eq "main")

Write-Host ""
Write-Host ("  host-lib unit tests — {0}/{1} passed" -f $script:pass, ($script:pass + $script:fail))
Write-Host ""
if ($script:fail -gt 0) { exit 1 }
exit 0
