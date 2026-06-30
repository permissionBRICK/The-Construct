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

# ── Get-VSCodeExtensionDir / Install-ControlPanelExtension ───────────────────
# Build a fake repo (extension/ with runtime files + a dev-only test/ carrying a
# node_modules) and a fake USERPROFILE, then assert the install copies the runtime
# files but NOT test/ or node_modules (which would otherwise drag in Playwright).
$fakeProfile = Join-Path ([System.IO.Path]::GetTempPath()) ("cp-home-" + [guid]::NewGuid().ToString("N"))
$fakeRepo    = Join-Path ([System.IO.Path]::GetTempPath()) ("cp-repo-" + [guid]::NewGuid().ToString("N"))
$savedProfile = $env:USERPROFILE
try {
    $ext = Join-Path $fakeRepo "extension"
    New-Item -ItemType Directory -Path (Join-Path $ext "src") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $ext "media") -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $ext "test\node_modules\playwright") -Force | Out-Null
    Set-Content -Path (Join-Path $ext "package.json") -Value '{"name":"construct-control-panel"}'
    Set-Content -Path (Join-Path $ext "extension.js") -Value '// entry'
    Set-Content -Path (Join-Path $ext "src\remote.js") -Value '// src'
    Set-Content -Path (Join-Path $ext "media\panel.css") -Value '/* css */'
    Set-Content -Path (Join-Path $ext "test\ui-smoke.js") -Value '// dev-only'
    Set-Content -Path (Join-Path $ext "test\node_modules\playwright\huge.js") -Value '// huge dep'

    $env:USERPROFILE = $fakeProfile
    $expectDir = Join-Path $fakeProfile ".vscode\extensions\construct-control-panel"
    ok "Get-VSCodeExtensionDir: under USERPROFILE\.vscode\extensions" ((Get-VSCodeExtensionDir) -eq $expectDir)

    $r = Install-ControlPanelExtension -SourceRoot $fakeRepo
    ok "install: returns `$true on success" ($r -eq $true)
    ok "install: copies package.json" (Test-Path -LiteralPath (Join-Path $expectDir "package.json"))
    ok "install: copies extension.js + src + media" (
        (Test-Path -LiteralPath (Join-Path $expectDir "extension.js")) -and
        (Test-Path -LiteralPath (Join-Path $expectDir "src\remote.js")) -and
        (Test-Path -LiteralPath (Join-Path $expectDir "media\panel.css")))
    ok "install: EXCLUDES the dev-only test/ folder" (-not (Test-Path -LiteralPath (Join-Path $expectDir "test")))

    # Idempotent re-run must refresh BOTH a top-level file AND a NESTED src/ file,
    # with NO double-nesting (regression for the Windows PowerShell 5.1 Copy-Item
    # -Recurse quirk that would land updates at src\src\ and leave src\ stale). NB:
    # this suite runs on pwsh 7 where Copy-Item merges; the staging-then-swap impl is
    # what makes the result platform-independent, and these assertions lock it in.
    Set-Content -Path (Join-Path $ext "extension.js") -Value '// entry v2'
    Set-Content -Path (Join-Path $ext "src\remote.js") -Value '// src v2'
    Install-ControlPanelExtension -SourceRoot $fakeRepo | Out-Null
    ok "install: re-run refreshes a TOP-LEVEL file" ((Get-Content -LiteralPath (Join-Path $expectDir "extension.js") -Raw).Trim() -eq '// entry v2')
    ok "install: re-run refreshes a NESTED src/ file" ((Get-Content -LiteralPath (Join-Path $expectDir "src\remote.js") -Raw).Trim() -eq '// src v2')
    ok "install: re-run does NOT double-nest (no src\src)" (-not (Test-Path -LiteralPath (Join-Path $expectDir "src\src")))
    ok "install: leaves no staging dirs behind" (
        @(Get-ChildItem -LiteralPath (Join-Path $fakeProfile ".vscode\extensions") -Directory -ErrorAction SilentlyContinue |
          Where-Object { $_.Name -like '.construct-cp-staging-*' }).Count -eq 0)

    # Missing extension source -> warns, returns $false, does not throw.
    $emptyRepo = Join-Path ([System.IO.Path]::GetTempPath()) ("cp-empty-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $emptyRepo -Force | Out-Null
    ok "install: missing source -> `$false (no throw)" ((Install-ControlPanelExtension -SourceRoot $emptyRepo -WarningAction SilentlyContinue) -eq $false)
    Remove-Item -LiteralPath $emptyRepo -Recurse -Force -ErrorAction SilentlyContinue
} finally {
    $env:USERPROFILE = $savedProfile
    Remove-Item -LiteralPath $fakeProfile -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $fakeRepo -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host ("  host-lib unit tests — {0}/{1} passed" -f $script:pass, ($script:pass + $script:fail))
Write-Host ""
if ($script:fail -gt 0) { exit 1 }
exit 0
