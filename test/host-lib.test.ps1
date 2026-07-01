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
function New-CodeShim([int]$ExitCode) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("code-shim-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $dir | Out-Null
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        Set-Content -Path (Join-Path $dir "code.cmd") -Value "@echo off`r`nexit /b $ExitCode" -Encoding ASCII
    } else {
        $shim = Join-Path $dir "code"
        Set-Content -Path $shim -Value "#!/bin/sh`nexit $ExitCode`n" -NoNewline
        & chmod +x $shim
    }
    return $dir
}

function Test-EnsureWithShim([int]$ExitCode) {
    $dir = New-CodeShim -ExitCode $ExitCode
    $savedPath = $env:PATH
    $env:PATH = $dir + [System.IO.Path]::PathSeparator + $env:PATH
    try {
        $warns = @()
        # 6>$null swallows the Write-Host status lines; warnings are captured in $warns.
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
