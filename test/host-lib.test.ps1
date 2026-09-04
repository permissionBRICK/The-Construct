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

# ── provision.sh result sentinel parser ─────────────────────────────────────
$esc = [char]27
$parsed = ConvertFrom-ConstructProvisionResult -Lines @(
    "unrelated live output",
    "${esc}[31m===CONSTRUCT-PROVISION-RESULT===${esc}[0m",
    "errors=2",
    "error=Installing .NET SDK|7|/var/log/construct/provision/step-0-Installing-.NET-SDK.log",
    "error=code serve-web setup|12|",
    "===END-CONSTRUCT-PROVISION-RESULT===",
    "human summary follows"
)
ok "provision result: finds ANSI-contaminated sentinel" ($parsed.Found -and $parsed.IsValid)
ok "provision result: reads declared error count" ($parsed.ErrorCount -eq 2)
ok "provision result: parses every title, exit code, and log path" (
    $parsed.Errors.Count -eq 2 -and
    $parsed.Errors[0].Title -eq "Installing .NET SDK" -and $parsed.Errors[0].ExitCode -eq 7 -and
    $parsed.Errors[0].LogPath -eq "/var/log/construct/provision/step-0-Installing-.NET-SDK.log" -and
    $parsed.Errors[1].Title -eq "code serve-web setup" -and $parsed.Errors[1].ExitCode -eq 12 -and
    $parsed.Errors[1].LogPath -eq "")

# Backward compatibility: a two-field error line (from an older provision.sh)
# must still parse, with LogPath defaulting to empty.
$oldVm = ConvertFrom-ConstructProvisionResult -Lines @(
    "===CONSTRUCT-PROVISION-RESULT===", "errors=1", "error=old step|3", "===END-CONSTRUCT-PROVISION-RESULT==="
)
ok "provision result: two-field error line still parses (old VM compat)" ($oldVm.IsValid -and $oldVm.Errors[0].LogPath -eq "")

$cleanResult = ConvertFrom-ConstructProvisionResult -Lines @(
    "===CONSTRUCT-PROVISION-RESULT===", "errors=0", "===END-CONSTRUCT-PROVISION-RESULT==="
)
ok "provision result: accepts exact clean sentinel" ($cleanResult.IsValid -and $cleanResult.ErrorCount -eq 0 -and $cleanResult.Errors.Count -eq 0)

$badResult = ConvertFrom-ConstructProvisionResult -Lines @(
    "===CONSTRUCT-PROVISION-RESULT===", "errors=2", "error=only one|1", "===END-CONSTRUCT-PROVISION-RESULT==="
)
ok "provision result: rejects count mismatch" ($badResult.Found -and -not $badResult.IsValid)
$missingResult = ConvertFrom-ConstructProvisionResult -Lines @("ordinary output")
ok "provision result: missing sentinel is not found" (-not $missingResult.Found -and -not $missingResult.IsValid)

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

# ── Set-ConstructProvisionedMarker: mirrors installedCommit -> provisionedCommit ──
# installedCommit tracks the installed Construct (install/update); provisionedCommit
# records what the VM was provisioned with, mirrored from the CURRENT installedCommit
# (not a fresh fetch), so the panel can flag "VM behind installed" without ever claiming
# a newer commit than what's installed. Merges (preserves other keys).
$pvDir = Join-Path ([System.IO.Path]::GetTempPath()) ("pv-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $pvDir | Out-Null
try {
    Save-ConstructSettings -Dir $pvDir -Values @{ installedCommit = "abc123"; gitUserName = "Neo" }
    $pv = Set-ConstructProvisionedMarker -Dir $pvDir
    $after = Read-ConstructSettings -Dir $pvDir
    ok "provisioned: mirrors installedCommit" ($pv -eq "abc123" -and $after.provisionedCommit -eq "abc123")
    ok "provisioned: merge preserves other keys" ($after.installedCommit -eq "abc123" -and $after.gitUserName -eq "Neo")
    $pvEmpty = Join-Path ([System.IO.Path]::GetTempPath()) ("pv2-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $pvEmpty | Out-Null
    try { ok "provisioned: no installedCommit -> empty, no throw" ((Set-ConstructProvisionedMarker -Dir $pvEmpty) -eq "") }
    finally { Remove-Item -LiteralPath $pvEmpty -Recurse -Force -ErrorAction SilentlyContinue }
} finally { Remove-Item -LiteralPath $pvDir -Recurse -Force -ErrorAction SilentlyContinue }

# ── Test-BackupHasGitCredentials: gates the redundant clone-credential prompt ──
# When a restore backup already carries a non-empty .git-credentials, Auto-Install
# skips the up-front clone-credential prompt (Provision reuses those creds), so the
# unattended control-panel reinstall no longer stops for input. No stored creds
# (blank dir / clean wipe) -> still prompt so private repos can be cloned.
ok "backup-creds: empty BackupDir -> false" (-not (Test-BackupHasGitCredentials -BackupDir ""))
ok "backup-creds: null BackupDir -> false"  (-not (Test-BackupHasGitCredentials -BackupDir $null))
$bkTest = Join-Path ([System.IO.Path]::GetTempPath()) ("bk-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $bkTest | Out-Null
try {
    ok "backup-creds: no .git-credentials file -> false" (-not (Test-BackupHasGitCredentials -BackupDir $bkTest))
    # Build the path exactly as the helper does so this works on both Windows
    # (nested extracted\home\) and the Linux CI box (one backslash-named leaf).
    $credFile = Join-Path $bkTest "extracted\home\.git-credentials"
    New-Item -ItemType Directory -Path (Split-Path -Parent $credFile) -Force | Out-Null
    Set-Content -LiteralPath $credFile -Value "   `n  " -Encoding UTF8
    ok "backup-creds: whitespace-only file -> false" (-not (Test-BackupHasGitCredentials -BackupDir $bkTest))
    Set-Content -LiteralPath $credFile -Value "https://user:token@github.com" -Encoding UTF8
    ok "backup-creds: non-empty file -> true" (Test-BackupHasGitCredentials -BackupDir $bkTest)
} finally { Remove-Item -LiteralPath $bkTest -Recurse -Force -ErrorAction SilentlyContinue }

# ── Set-ConstructInstalledMarker: a failed SHA fetch must NOT clobber the marker ──
# Regression: recording installedCommit="" on a transient GitHub blip permanently
# hid the update banner (checkConstruct treats "" as no marker). The fetch is
# injected so this is network-free.
$mkDir = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-marker-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $mkDir -Force | Out-Null
try {
    # A successful fetch records the SHA.
    $sha1 = Set-ConstructInstalledMarker -Root $mkDir -Repo "permissionBRICK/The-Construct" -Ref "main" `
        -CommitFetcher { param($r,$f) "abc1234def" }
    ok "marker: successful fetch returns the sha" ($sha1 -eq "abc1234def")
    ok "marker: successful fetch records installedCommit" ((Read-ConstructSettings -Dir $mkDir).installedCommit -eq "abc1234def")

    # A FAILED fetch (fetcher throws) must PRESERVE the prior installedCommit, not blank it.
    $sha2 = Set-ConstructInstalledMarker -Root $mkDir -Repo "permissionBRICK/The-Construct" -Ref "main" `
        -CommitFetcher { param($r,$f) throw "network down" }
    ok "marker: failed fetch returns empty" ($sha2 -eq "")
    ok "marker: failed fetch PRESERVES the prior installedCommit (no clobber)" (
        (Read-ConstructSettings -Dir $mkDir).installedCommit -eq "abc1234def")
    # Same repo/ref -> the whole tuple is intact.
    ok "marker: failed fetch preserves repo/ref" (
        (Read-ConstructSettings -Dir $mkDir).constructRef -eq "main")

    # An EMPTY-string sha (fetcher returns "") is likewise treated as no-fetch.
    $null = Set-ConstructInstalledMarker -Root $mkDir -Repo "permissionBRICK/The-Construct" -Ref "main" `
        -CommitFetcher { param($r,$f) "" }
    ok "marker: empty-string fetch also preserves the prior commit" (
        (Read-ConstructSettings -Dir $mkDir).installedCommit -eq "abc1234def")

    # ATOMIC TUPLE: a repo/ref SWITCH with a FAILED fetch must NOT pair the new
    # repo/ref with the OLD commit (that would 404 the compare -> hidden banner).
    # The whole prior tuple (A) is preserved; the new B repo/ref are NOT written.
    $null = Set-ConstructInstalledMarker -Root $mkDir -Repo "someone/a-fork" -Ref "dev" `
        -CommitFetcher { param($r,$f) throw "network down" }
    $sw = Read-ConstructSettings -Dir $mkDir
    ok "marker: switch+failed fetch keeps the old commit" ($sw.installedCommit -eq "abc1234def")
    ok "marker: switch+failed fetch does NOT adopt the new repo" ($sw.constructRepo -eq "permissionBRICK/The-Construct")
    ok "marker: switch+failed fetch does NOT adopt the new ref" ($sw.constructRef -eq "main")
    ok "marker: switch+failed fetch never pairs new repo with old commit" (
        -not (($sw.constructRepo -eq "someone/a-fork") -and ($sw.installedCommit -eq "abc1234def")))
    # A later SUCCESSFUL fetch for the switched source writes the full new tuple atomically.
    $null = Set-ConstructInstalledMarker -Root $mkDir -Repo "someone/a-fork" -Ref "dev" `
        -CommitFetcher { param($r,$f) "beef5678" }
    $sw2 = Read-ConstructSettings -Dir $mkDir
    ok "marker: successful switch writes the full new tuple" (
        ($sw2.constructRepo -eq "someone/a-fork") -and ($sw2.constructRef -eq "dev") -and ($sw2.installedCommit -eq "beef5678"))

    # First-ever install with a failed fetch: no prior value, so installedCommit stays
    # absent/empty (banner hidden until a good record) -- never throws.
    $mkDir2 = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-marker2-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $mkDir2 -Force | Out-Null
    try {
        $null = Set-ConstructInstalledMarker -Root $mkDir2 -Repo "permissionBRICK/The-Construct" -Ref "main" `
            -CommitFetcher { param($r,$f) throw "offline" }
        $s2 = Read-ConstructSettings -Dir $mkDir2
        ok "marker: first install + failed fetch leaves no phantom commit" (-not $s2.installedCommit)
        ok "marker: first install + failed fetch still records repo/ref" ($s2.constructRepo -eq "permissionBRICK/The-Construct")
    } finally { Remove-Item -LiteralPath $mkDir2 -Recurse -Force -ErrorAction SilentlyContinue }
} finally { Remove-Item -LiteralPath $mkDir -Recurse -Force -ErrorAction SilentlyContinue }

# ── Regression guard: no non-ASCII INSIDE a string literal in shipped .ps1 ────
# Windows PowerShell 5.1 reads a BOM-less .ps1 as the ANSI code page, so a UTF-8
# em-dash (etc.) inside a STRING mangles into a smart-quote that closes the string
# early -> "string is missing the terminator" (it crashed Update-Construct.ps1).
# Comment separators (the box-drawing lines) are fine -- they're ignored. Parse each
# shipped script and fail if any string-literal token carries a non-ASCII char.
$repoRoot = Split-Path -Parent $here
$shipped = @("install.ps1","Auto-Install.ps1","Create-AgentVM.ps1","Provision-AgentVM.ps1",
             "Update-Construct.ps1","Update-T3Code.ps1","Get-AgentUsage.ps1","Get-ConstructT3PairingLink.ps1",
             "lib/AgentVm.Common.ps1","lib/AgentVm.InstanceState.ps1")
foreach ($rel in $shipped) {
    $p = Join-Path $repoRoot $rel
    if (-not (Test-Path -LiteralPath $p)) { continue }
    $errs = $null; $toks = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($p, [ref]$toks, [ref]$errs)
    $strs = $ast.FindAll({ param($n)
        $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
        $n -is [System.Management.Automation.Language.ExpandableStringExpressionAst] }, $true)
    $bad = @($strs | Where-Object { $_.Extent.Text -match '[^\x00-\x7F]' })
    ok "ascii: $rel has no non-ASCII inside string literals (WinPS 5.1-safe)" ($bad.Count -eq 0)
    if ($bad.Count -gt 0) { $bad | Select-Object -First 3 | ForEach-Object { Write-Host ("        line {0}: {1}" -f $_.Extent.StartLineNumber, $_.Extent.Text) -ForegroundColor DarkYellow } }
}

# ── Wait-VmSshReady ──────────────────────────────────────────────────────────
# Real TCP against localhost: a live listener must count as ready (including the
# consecutive-probe stability window), a closed port must time out to $false.
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$lport = $listener.LocalEndpoint.Port
try {
    ok "ssh-wait: stable listener reports ready" (Wait-VmSshReady -VmHost "127.0.0.1" -Port $lport -TimeoutSec 15 -ProbeIntervalSec 0 -StableProbes 3)
} finally { $listener.Stop() }
ok "ssh-wait: closed port times out false" (-not (Wait-VmSshReady -VmHost "127.0.0.1" -Port $lport -TimeoutSec 2 -ProbeIntervalSec 0))
ok "ssh-wait: unresolvable host returns false without throwing" ((Wait-VmSshReady -VmHost "definitely-not-a-host.invalid" -TimeoutSec 2 -ProbeIntervalSec 0) -eq $false)

# ── Wait-VmSshReady: restart proof via -SshTarget ────────────────────────────
# The end-of-provisioning reboot is backgrounded on the VM, so the OLD boot's
# sshd answers for a few seconds -- a probe must only count as ready with proof
# of a NEW boot (boot id changed vs the baseline; uptime-fresh without one).
# Shim `ssh` on PATH prints a chosen boot id + /proc/uptime line and exits with
# a chosen code. -VmHost is deliberately UNRESOLVABLE in these tests: ssh mode
# must not dial the TCP host at all (field regression: the short name stopped
# resolving post-reboot, the old TCP gate never opened, and the ssh probe --
# whose alias resolves fine -- was never attempted).
function New-SshShim([string]$BootId, [string]$Uptime, [int]$ExitCode = 0) {
    $dir = Join-Path ([System.IO.Path]::GetTempPath()) ("ssh-shim-" + [guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $dir | Out-Null
    if ($IsWindows -or $env:OS -eq "Windows_NT") {
        $lines = @("@echo off", "echo $BootId", "echo $Uptime 9999.99", "exit /b $ExitCode")
        Set-Content -Path (Join-Path $dir "ssh.cmd") -Value ($lines -join "`r`n") -Encoding ASCII
    } else {
        $shim = Join-Path $dir "ssh"
        Set-Content -Path $shim -Value "#!/bin/sh`necho '$BootId'`necho '$Uptime 9999.99'`nexit $ExitCode`n" -NoNewline
        & chmod +x $shim
    }
    return $dir
}
function Test-SshWaitWithShim([string]$BootId, [string]$Uptime, [int]$ExitCode = 0, [string]$Baseline = "", [int]$TimeoutSec = 15) {
    $dir = New-SshShim -BootId $BootId -Uptime $Uptime -ExitCode $ExitCode
    $savedPath = $env:PATH
    $env:PATH = $dir + [System.IO.Path]::PathSeparator + $env:PATH
    try {
        return Wait-VmSshReady -VmHost "definitely-not-a-host.invalid" -TimeoutSec $TimeoutSec -ProbeIntervalSec 0 `
                               -SshTarget "agent-vm" -BaselineBootId $Baseline
    } finally {
        $env:PATH = $savedPath
        Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
$oldBoot = "11111111-1111-1111-1111-111111111111"
$newBoot = "22222222-2222-2222-2222-222222222222"
ok "ssh-wait: changed boot id vs baseline is ready (no TCP gate: VmHost unresolvable)" (Test-SshWaitWithShim -BootId $newBoot -Uptime "42.17" -Baseline $oldBoot)
ok "ssh-wait: SAME boot id vs baseline keeps waiting (old boot answering != restarted)" (
    -not (Test-SshWaitWithShim -BootId $oldBoot -Uptime "5432.10" -Baseline $oldBoot -TimeoutSec 2))
ok "ssh-wait: no baseline + fresh uptime is ready" (Test-SshWaitWithShim -BootId $newBoot -Uptime "42.17")
ok "ssh-wait: no baseline + long uptime keeps waiting" (
    -not (Test-SshWaitWithShim -BootId $oldBoot -Uptime "54321.09" -TimeoutSec 2))
ok "ssh-wait: failing ssh probe keeps waiting (no readiness without restart proof)" (
    -not (Test-SshWaitWithShim -BootId $newBoot -Uptime "42.17" -ExitCode 255 -Baseline $oldBoot -TimeoutSec 2))

# ── Select-VmCodeWindow (reinstall closes VM-attached VS Code windows) ───────
# Pure filter over (Title, ProcessName) records; the Win32 enumeration itself is
# not exercised here (Windows-only, needs a desktop).
$winRecords = @(
    [pscustomobject]@{ Title = "lifecycle.js - construct [SSH: agent-vm] - Visual Studio Code"; ProcessName = "Code" },
    [pscustomobject]@{ Title = "repos [SSH: agent-vm.mshome.net] - Visual Studio Code";         ProcessName = "Code" },
    [pscustomobject]@{ Title = "notes.md - stuff [SSH: other-box] - Visual Studio Code";        ProcessName = "Code" },
    [pscustomobject]@{ Title = "scratch [SSH: agent-vm2] - Visual Studio Code";                 ProcessName = "Code" },
    [pscustomobject]@{ Title = "root@agent-vm: ~";                                              ProcessName = "WindowsTerminal" },
    [pscustomobject]@{ Title = "[SSH: agent-vm] - Visual Studio Code";                          ProcessName = "Code - Insiders" },
    [pscustomobject]@{ Title = "agent-vm - local notes.txt - Visual Studio Code";               ProcessName = "Code" },
    [pscustomobject]@{ Title = "x [SSH: agent-vm.example.net] - Visual Studio Code";            ProcessName = "Code" },
    [pscustomobject]@{ Title = "x [SSH: agent-vm.mshome.net.evil] - Visual Studio Code";        ProcessName = "Code" },
    [pscustomobject]@{ Title = "y [SSH: agent-vm] - Visual Studio Code";                        ProcessName = "CodeHelper" }
)
$sel = Select-VmCodeWindow -Windows $winRecords -VmHost "agent-vm.mshome.net"
ok "vm windows: matches alias and full-host SSH titles, both Code variants" (
    $sel.Count -eq 3 -and
    $sel[0].Title -like "*agent-vm]*" -and
    $sel[1].Title -like "*agent-vm.mshome.net]*" -and
    $sel[2].ProcessName -eq "Code - Insiders")
ok "vm windows: other hosts, alias-prefixed names, and terminals excluded" (
    @($sel | Where-Object { $_.Title -match "other-box|agent-vm2" -or $_.ProcessName -eq "WindowsTerminal" }).Count -eq 0)
ok "vm windows: same alias under a foreign domain is not matched" (
    @($sel | Where-Object { $_.Title -match "example\.net|mshome\.net\.evil" }).Count -eq 0)
ok "vm windows: non-VS-Code 'Code*' process is not matched" (
    @($sel | Where-Object { $_.ProcessName -eq "CodeHelper" }).Count -eq 0)
ok "vm windows: authority with a port suffix is matched" (
    @(Select-VmCodeWindow -Windows @([pscustomobject]@{ Title = "z [SSH: agent-vm:22] - Visual Studio Code"; ProcessName = "Code" }) -VmHost "agent-vm.mshome.net").Count -eq 1)
ok "vm windows: local window mentioning the VM name is not matched" (
    @($sel | Where-Object { $_.Title -like "*local notes*" }).Count -eq 0)
ok "vm windows: case-insensitive title match" (
    @(Select-VmCodeWindow -Windows @([pscustomobject]@{ Title = "x [ssh: Agent-VM] - Visual Studio Code"; ProcessName = "Code" }) -VmHost "agent-vm.mshome.net").Count -eq 1)
ok "vm windows: empty input yields empty" ((@(Select-VmCodeWindow -Windows @() -VmHost "agent-vm")).Count -eq 0)
ok "vm windows: non-Windows Close-VmVsCodeWindow is a safe no-op returning 0" (
    ($env:OS -eq 'Windows_NT') -or ((Close-VmVsCodeWindow -VmHost "agent-vm.mshome.net") -eq 0))

# ── Build-ProvisionEncodedCommand round trip ───────────────────────────────
# Verify that parameters with spaces, quotes, booleans, and base64 values
# survive the JSON->base64 inner + UTF-16LE outer encoding.
$rtParams = @{
    VmHost            = "agent-vm.mshome.net"
    Projects          = "my project;other project"
    AgentPassword     = 'p@ss w"ord'
    Auto              = $true
    MicPassthrough    = $false
    GitCloneCredB64   = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("test:token"))
}
$rtScript = "/tmp/Provision-AgentVM.ps1"
$rtResult = "/tmp/result.json"
$rtReady  = "/tmp/ready"
$rtEncoded = Build-ProvisionEncodedCommand -ScriptPath $rtScript -Params $rtParams `
    -ResultFile $rtResult -ReadyFile $rtReady
# Decode: outer is UTF-16LE base64.
$rtDecoded = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($rtEncoded))
# The decoded script should contain the inner base64 blob. Extract it.
$rtB64Match = [regex]::Match($rtDecoded, "FromBase64String\('([A-Za-z0-9+/=]+)'\)")
$rtInnerOk = $false
if ($rtB64Match.Success) {
    $rtInnerJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($rtB64Match.Groups[1].Value))
    $rtInnerObj  = ConvertFrom-Json $rtInnerJson
    $rtInnerOk   = ($rtInnerObj.VmHost -eq "agent-vm.mshome.net") -and
                   ($rtInnerObj.Projects -eq "my project;other project") -and
                   ($rtInnerObj.AgentPassword -eq 'p@ss w"ord') -and
                   ($rtInnerObj.Auto -eq $true) -and
                   ($rtInnerObj.MicPassthrough -eq $false) -and
                   ($rtInnerObj.GitCloneCredB64 -eq $rtParams.GitCloneCredB64)
}
ok "encoded command: params with spaces, quotes, booleans, base64 round trip intact" $rtInnerOk
ok "encoded command: script path embedded correctly" ($rtDecoded -match [regex]::Escape($rtScript))
ok "encoded command: result file path embedded correctly" ($rtDecoded -match [regex]::Escape($rtResult))
ok "encoded command: ready file path embedded correctly" ($rtDecoded -match [regex]::Escape($rtReady))
ok "encoded command: ReadyFile passed to Provision (not PID from bootstrap)" (
    ($rtDecoded -match "'ReadyFile'\].*=.*'$([regex]::Escape($rtReady))'") -and
    ($rtDecoded -notmatch 'Set-Content.*\.pid'))
ok "encoded command: hashtable conversion handles booleans as switches" ($rtDecoded -match '\[switch\]')

# Apostrophes in paths and parameter values must survive the double-single-quote
# escaping used inside the here-string.
$rtAposParams = @{ VmHost = "o'brien-vm.mshome.net"; Projects = "Kate's project" }
$rtAposScript = "C:\Users\O'Brien\Construct\Provision-AgentVM.ps1"
$rtAposResult = "C:\Users\O'Brien\AppData\result.json"
$rtAposReady  = "C:\Users\O'Brien\AppData\ready"
$rtAposEncoded = Build-ProvisionEncodedCommand -ScriptPath $rtAposScript -Params $rtAposParams `
    -ResultFile $rtAposResult -ReadyFile $rtAposReady
$rtAposDecoded = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($rtAposEncoded))
$rtAposB64 = [regex]::Match($rtAposDecoded, "FromBase64String\('([A-Za-z0-9+/=]+)'\)")
$rtAposRt = $false
if ($rtAposB64.Success) {
    $rtAposJson = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($rtAposB64.Groups[1].Value))
    $rtAposObj  = ConvertFrom-Json $rtAposJson
    $rtAposRt   = ($rtAposObj.VmHost -eq "o'brien-vm.mshome.net") -and
                  ($rtAposObj.Projects -eq "Kate's project")
}
ok "encoded command: apostrophes in param values survive round trip" $rtAposRt
ok "encoded command: apostrophe in script path escaped (doubled)" (
    $rtAposDecoded -match "O''Brien")
ok "encoded command: apostrophe in result path escaped (doubled)" (
    $rtAposDecoded -match "O''Brien.*result\.json")

# ── Get-DesktopUser ───────────────────────────────────────────────────────
# On non-Windows (no explorer.exe), should return $null (not a fallback identity)
# so callers can take a loud inline fallback.
$desktopFallback = Get-DesktopUser
ok "desktop user: returns null when no explorer.exe (non-Windows)" ($null -eq $desktopFallback)

# ── Atomic result file contract ────────────────────────────────────────────
# Verify the result file includes RawSentinel (not re-serialized Errors) and
# that ConvertFrom-ConstructProvisionResult can parse it end-to-end.
$rtSentinelLines = @(
    "live output line 1",
    "===CONSTRUCT-PROVISION-RESULT===",
    "errors=1",
    "error=Step X failed|5|/var/log/construct/provision/step-x.log",
    "===END-CONSTRUCT-PROVISION-RESULT===",
    "trailing output"
)
$rtResultObj = @{
    ExitCode       = 3
    HadErrors      = $true
    FailureMessage = ""
    RawSentinel    = [string[]]$rtSentinelLines
}
# Round-trip through JSON (simulates file write + read).
$rtResultJson   = $rtResultObj | ConvertTo-Json -Depth 4
$rtResultParsed = ConvertFrom-Json $rtResultJson
$rtParsedResult = ConvertFrom-ConstructProvisionResult -Lines @($rtResultParsed.RawSentinel)
ok "atomic result: RawSentinel survives JSON round trip" ($rtParsedResult.Found -and $rtParsedResult.IsValid)
ok "atomic result: error items parsed from RawSentinel match originals" (
    $rtParsedResult.ErrorCount -eq 1 -and
    $rtParsedResult.Errors[0].Title -eq "Step X failed" -and
    $rtParsedResult.Errors[0].ExitCode -eq 5 -and
    $rtParsedResult.Errors[0].LogPath -eq "/var/log/construct/provision/step-x.log")

# ── Malformed RawSentinel rejection ────────────────────────────────────────
# A missing, empty, or count-mismatched sentinel must NOT be treated as valid.
$rtMalformedEmpty = ConvertFrom-ConstructProvisionResult -Lines @()
ok "malformed sentinel: empty lines -> not valid" (-not $rtMalformedEmpty.IsValid)
$rtMalformedMismatch = ConvertFrom-ConstructProvisionResult -Lines @(
    "===CONSTRUCT-PROVISION-RESULT===", "errors=2",
    "error=only one|1|", "===END-CONSTRUCT-PROVISION-RESULT===")
ok "malformed sentinel: count mismatch (declared 2, actual 1) -> not valid" (
    -not $rtMalformedMismatch.IsValid)
$rtMalformedNoEnd = ConvertFrom-ConstructProvisionResult -Lines @(
    "===CONSTRUCT-PROVISION-RESULT===", "errors=0")
ok "malformed sentinel: no end marker -> not found" (-not $rtMalformedNoEnd.Found)

# ── Atomic file publication ────────────────────────────────────────────────
# Verify the temp+rename pattern: write to temp, rename atomically, never
# expose a partial final file.
$rtAtomicDir = Join-Path ([System.IO.Path]::GetTempPath()) "construct-atomic-test-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $rtAtomicDir -Force | Out-Null
$rtAtomicFinal = Join-Path $rtAtomicDir "result.json"
$rtAtomicTmp   = "$rtAtomicFinal.tmp.$$"
$rtAtomicData  = @{ ExitCode = 0; HadErrors = $false } | ConvertTo-Json
try {
    Set-Content -LiteralPath $rtAtomicTmp -Value $rtAtomicData -Encoding UTF8 -Force
    ok "atomic file: temp file exists before rename" (Test-Path -LiteralPath $rtAtomicTmp)
    ok "atomic file: final file does NOT exist before rename" (-not (Test-Path -LiteralPath $rtAtomicFinal))
    Move-Item -LiteralPath $rtAtomicTmp -Destination $rtAtomicFinal -Force
    ok "atomic file: final file exists after rename" (Test-Path -LiteralPath $rtAtomicFinal)
    ok "atomic file: temp file gone after rename" (-not (Test-Path -LiteralPath $rtAtomicTmp))
    $rtAtomicContent = Get-Content -LiteralPath $rtAtomicFinal -Raw | ConvertFrom-Json
    ok "atomic file: content intact after rename" ($rtAtomicContent.ExitCode -eq 0)
} finally {
    Remove-Item -LiteralPath $rtAtomicDir -Recurse -Force -ErrorAction SilentlyContinue
}

# ── AutoResolve forwarding ─────────────────────────────────────────────────
# Verify Auto-Install.ps1 forwards AutoResolve on all provisioning paths by
# checking the source for the pattern in each Invoke-DeElevatedProvision call
# site. (Static analysis: grep the script text.)
$aiSource = Get-Content -LiteralPath (Join-Path $here "..\Auto-Install.ps1") -Raw
# Reprovision path (line ~694) already forwards. Fresh-install path must too.
$aiAutoResolveCount = ([regex]::Matches($aiSource, "ContainsKey\('AutoResolve'\).*provArgs")).Count
ok "auto-install: AutoResolve forwarded on all provisioning paths (at least 3 sites)" ($aiAutoResolveCount -ge 3)

# ── Source-level behavioral checks ─────────────────────────────────────────
# Verify key behavioral properties by static analysis of the source.
$libSource = Get-Content -LiteralPath (Join-Path $here "..\lib\AgentVm.Common.ps1") -Raw
$provSource = Get-Content -LiteralPath (Join-Path $here "..\Provision-AgentVM.ps1") -Raw

# IPC directory uses ProgramData with explicit ACL (not elevated user's TEMP).
ok "ipc: uses ProgramData for IPC directory (not env:TEMP)" (
    $libSource -match 'ProgramData' -and
    $libSource -match 'construct-provision' -and
    $libSource -match 'SetAccessRuleProtection')
# Handshake: ready file is written by Provision-AgentVM.ps1 (not bootstrap).
ok "handshake: Provision-AgentVM.ps1 writes ReadyFile (not bootstrap)" (
    $provSource -match '\$ReadyFile.*\.tmp\.\$PID' -and
    $provSource -match 'Move-Item.*ReadyFile')
# Handshake: ready publication failure exits nonzero before provisioning.
ok "handshake: ready failure exits before provisioning" (
    $provSource -match 'Could not publish ready handshake' -and
    $provSource -match 'exit 1')
# Handshake timeout: Stop-ScheduledTask called before fallback.
ok "handshake: task stopped before inline fallback" (
    $libSource -match 'Stop-ScheduledTask.*taskName.*SilentlyContinue')
# Handshake timeout: confirmed-stopped flag required before fallback.
ok "handshake: confirmed-stopped required before fallback (no two provisioners)" (
    $libSource -match 'taskConfirmedStopped' -and
    $libSource -match 'cannot safely fall back')
# Start-catch: also stops+confirms task before fallback (Start can launch even on error).
ok "start-catch: stops task and confirms dead before inline fallback" (
    ([regex]::Matches($libSource, 'taskConfirmedStopped')).Count -ge 3)
# Result file: never written directly to the watched path (no torn read).
ok "result: no direct write to ResultFile (always temp+rename)" (
    ($provSource -match 'Move-Item.*tmpResult.*ResultFile') -and
    -not ($provSource -match 'Set-Content.*-LiteralPath \$ResultFile\b'))
# Process validation: start time captured and checked before Stop-Process.
ok "timeout: child start time captured for PID identity verification" (
    $libSource -match 'childStartTime.*StartTime' -and
    $libSource -match 'proc\.StartTime -eq \$childStartTime')
# Sentinel validation: exit 0/3 requires valid sentinel.
ok "sentinel: exit 0/3 requires IsValid sentinel" (
    $libSource -match 'parsed\.IsValid' -and
    $libSource -match 'sentinel is missing or malformed')
# HadErrors derived from ExitCode, not trusted from JSON.
ok "globals: HadErrors derived from ExitCode (not trusted from child JSON)" (
    $libSource -match 'Derive HadErrors from ExitCode' -and
    -not ($libSource -match '\$global:ConstructProvisionHadErrors\s*=\s*\[bool\]\$result\.HadErrors'))
# Desktop user: session-filtered explorer, null on ambiguity.
ok "desktop user: filters explorer by session ID" (
    $libSource -match 'SessionId.*-eq.*sessionId')
# HyperV membership: skips (not falls back to elevated SID) when no desktop shell.
ok "hyperv: skips membership when desktop user unknown (not elevated SID)" (
    $libSource -match 'skipping Hyper-V Administrators membership')
# Child lifecycle: no Read-Host pause when -ResultFile is set (parent owns final screen).
ok "lifecycle: child exits without Read-Host when ResultFile is set" (
    -not ([regex]::IsMatch($provSource, '(?s)if\s*\(\$ResultFile\).*?Read-Host.*?exit \$provExitCode')))
# Parent waits for child PID to exit before unregistering the task.
# If the child doesn't exit, it must be stopped (identity-verified) and
# confirmed terminated before cleanup; otherwise throw preserving task/IPC.
ok "lifecycle: parent waits for child exit before cleanup" (
    $libSource -match 'childExited' -and
    $libSource -match 'Wait for the child process to actually exit')
# Structural: the stop-or-throw for an un-exited child comes BEFORE the
# Unregister-ScheduledTask cleanup line (which only runs after confirmed exit).
$childStopPos = $libSource.IndexOf('could not be confirmed terminated')
$cleanupPos   = $libSource.IndexOf('Clean up the scheduled task + IPC directory')
ok "lifecycle: child stop-or-throw precedes task cleanup" (
    $childStopPos -gt 0 -and $cleanupPos -gt $childStopPos)
# Diagnostic preservation: do not unregister/delete before throwing "Check Task Scheduler".
# The throw must come BEFORE Unregister-ScheduledTask in the confirmed-stopped branches.
$throwPos = $libSource.IndexOf('cannot safely fall back. Check Task Scheduler')
$unregAfterThrow = $libSource.IndexOf('Unregister-ScheduledTask', $throwPos)
ok "lifecycle: task preserved for diagnosis when stop unconfirmed" (
    $throwPos -gt 0 -and $unregAfterThrow -gt $throwPos)
# Every safety throw must set globals so Wait-Exit renders the final screen.
# Verify: each "cannot safely fall back" / "could not be confirmed" throw is
# preceded by setting ConstructProvisionHadErrors=true and ConstructProvisionFailureMessage.
$safetyThrows = @(
    'cannot safely fall back. Check Task Scheduler',
    'could not be confirmed stopped',
    'could not be confirmed terminated'
)
$allGlobalsSet = $true
foreach ($phrase in $safetyThrows) {
    $pos = $libSource.IndexOf($phrase)
    if ($pos -lt 0) { $allGlobalsSet = $false; continue }
    # Look at the ~500 chars preceding the throw for the globals.
    $preceding = $libSource.Substring([Math]::Max(0, $pos - 500), [Math]::Min(500, $pos))
    if ($preceding -notmatch 'ConstructProvisionHadErrors\s*=\s*\$true' -or
        $preceding -notmatch 'ConstructProvisionFailureMessage') {
        $allGlobalsSet = $false
    }
}
ok "lifecycle: all safety throws set Wait-Exit globals before throwing" $allGlobalsSet

# ── Hyper-V automatic-checkpoint classification ─────────────────────────────
# Get-AgentVmAutomaticCheckpoint decides what Set-AgentVmCheckpoints.ps1 is allowed
# to DELETE, so the classification is the safety-critical part. Exercised through the
# -Snapshots/-AutomaticIds seams (no Hyper-V on a CI box).

ok "checkpoint name: matches Hyper-V's auto-naming" (
    Test-AgentVmCheckpointNamePattern -Name "Agent-VM - (7/27/2026 - 10:14:03 AM)" -VmName "Agent-VM")
ok "checkpoint name: rejects a user-named checkpoint" (
    -not (Test-AgentVmCheckpointNamePattern -Name "before upgrade" -VmName "Agent-VM"))
ok "checkpoint name: rejects another VM's automatic checkpoint" (
    -not (Test-AgentVmCheckpointNamePattern -Name "Other-VM - (7/27/2026)" -VmName "Agent-VM"))
ok "checkpoint name: rejects a prefix without the closing paren" (
    -not (Test-AgentVmCheckpointNamePattern -Name "Agent-VM - (unfinished" -VmName "Agent-VM"))
ok "checkpoint name: empty/blank inputs are not a match" (
    (-not (Test-AgentVmCheckpointNamePattern -Name "" -VmName "Agent-VM")) -and
    (-not (Test-AgentVmCheckpointNamePattern -Name "Agent-VM - (x)" -VmName "")))

# Fake checkpoint objects: the classifier only ever touches .Id/.Name/
# .IsAutomaticCheckpoint, and always through a PSObject property probe.
function New-FakeSnap($name, $id, $auto = $null) {
    $o = [pscustomobject]@{ Name = $name; Id = $id }
    if ($null -ne $auto) { $o | Add-Member -NotePropertyName IsAutomaticCheckpoint -NotePropertyValue $auto }
    return $o
}

# No WMI signal available (old build / failed query) unless a test says otherwise.
$noWmi = @{ Supported = $false; Ids = @() }

# Tier 1 -- the snapshot object reports it itself, and is believed BOTH ways: an
# explicit $false must not be demoted to Probable by an auto-looking name.
$t1 = Get-AgentVmAutomaticCheckpoint -VmName "Agent-VM" -Wmi $noWmi -Snapshots @(
    (New-FakeSnap "Agent-VM - (a)" "11111111-1111-1111-1111-111111111111" $true),
    (New-FakeSnap "Agent-VM - (b)" "22222222-2222-2222-2222-222222222222" $false)
)
ok "checkpoints tier1: IsAutomaticCheckpoint true -> Certain" (
    $t1.Certain.Count -eq 1 -and $t1.Certain[0].Name -eq "Agent-VM - (a)")
ok "checkpoints tier1: an explicit false is NOT demoted to Probable by its name" (
    $t1.Probable.Count -eq 0)
ok "checkpoints tier1: All keeps every checkpoint" ($t1.All.Count -eq 2)

# Tier 2 -- WMI's IsAutomaticSnapshot ids, joined on the checkpoint GUID. Brace/case
# differences between the two views must not break the join.
$t2 = Get-AgentVmAutomaticCheckpoint -VmName "Agent-VM" `
    -Wmi @{ Supported = $true; Ids = @("{AAAAAAAA-1111-2222-3333-444444444444}") } -Snapshots @(
    (New-FakeSnap "Agent-VM - (b)" "aaaaaaaa-1111-2222-3333-444444444444"),
    (New-FakeSnap "Agent-VM - (c)" "bbbbbbbb-1111-2222-3333-444444444444")
)
ok "checkpoints tier2: WMI id join is brace/case insensitive" (
    $t2.Certain.Count -eq 1 -and $t2.Certain[0].Name -eq "Agent-VM - (b)")
ok "checkpoints tier2: a working query is authoritative -- an unlisted id is neither certain nor probable" (
    $t2.Certain.Count -eq 1 -and $t2.Probable.Count -eq 0)

# Tier 3 -- no flag anywhere: name-matched checkpoints are PROBABLE (the script asks
# before deleting them) and everything else is left out entirely.
$t3 = Get-AgentVmAutomaticCheckpoint -VmName "Agent-VM" -Wmi $noWmi -Snapshots @(
    (New-FakeSnap "Agent-VM - (c)" "33333333-3333-3333-3333-333333333333"),
    (New-FakeSnap "pre-refactor" "44444444-4444-4444-4444-444444444444")
)
ok "checkpoints tier3: name match -> Probable, never Certain" (
    $t3.Certain.Count -eq 0 -and $t3.Probable.Count -eq 1 -and $t3.Probable[0].Name -eq "Agent-VM - (c)")
ok "checkpoints tier3: a user checkpoint is in neither delete list" (
    ($t3.Probable | Where-Object { $_.Name -eq "pre-refactor" }).Count -eq 0)

# A query that WORKED but found nothing automatic: still authoritative, so an
# auto-looking name is left alone rather than prompting.
$empty = Get-AgentVmAutomaticCheckpoint -VmName "Agent-VM" -Wmi @{ Supported = $true; Ids = @() } -Snapshots @(
    (New-FakeSnap "Agent-VM - (e)" "55555555-5555-5555-5555-555555555555")
)
ok "checkpoints: supported-but-empty WMI result suppresses the heuristic" (
    $empty.Certain.Count -eq 0 -and $empty.Probable.Count -eq 0)

$none = Get-AgentVmAutomaticCheckpoint -VmName "Agent-VM" -Wmi $noWmi -Snapshots @()
ok "checkpoints: no snapshots -> three empty lists" (
    $none.All.Count -eq 0 -and $none.Certain.Count -eq 0 -and $none.Probable.Count -eq 0)
# "no checkpoints" and "couldn't read the checkpoints" must be distinguishable: the
# script reports success for the first and FAILS for the second, because an automatic
# checkpoint may still be sitting there unremoved.
ok "checkpoints: an empty-but-successful enumeration reports Enumerated" ($none.Enumerated -eq $true)
$unreadable = Get-AgentVmAutomaticCheckpoint -VmName "No-Such-VM-For-Tests"
ok "checkpoints: an enumeration failure reports Enumerated=false, not 'no checkpoints'" (
    $unreadable.Enumerated -eq $false -and $unreadable.All.Count -eq 0)

$nulls = Get-AgentVmAutomaticCheckpoint -VmName "Agent-VM" -Wmi @{ Supported = $true; Ids = @($null, "") } -Snapshots @(
    $null, (New-FakeSnap "Agent-VM - (d)" $null)
)
ok "checkpoints: null snapshot / unjoinable id / blank wmi ids don't throw or mis-classify" (
    $nulls.Certain.Count -eq 0 -and $nulls.Probable.Count -eq 1)

# ── Set-AgentVmCheckpoints.ps1 source invariants ────────────────────────────
# The classifier's Enumerated flag and the panel result file are only useful if the
# script actually ACTS on them, and neither can be exercised without Hyper-V. Pin the
# wiring at the source level (same technique as the Wait-Exit globals check above) so a
# refactor that drops either one fails here instead of in the field.
$chkScript = Get-Content -Raw (Join-Path $here "..\Set-AgentVmCheckpoints.ps1")

ok "checkpoint script: an unreadable checkpoint list is turned into a throw, not a success" (
    $chkScript -match '(?s)if\s*\(-not\s+\$found\.Enumerated\)\s*\{[^}]*throw')
# The removal loop must be reached only after that guard. Anchor on the DRIVER-NEUTRAL
# call the script actually makes (Remove-ConstructVmCheckpoint -Name): it no longer
# invokes Remove-VMSnapshot itself -- that name survives only in a comment and in the
# help text, so the old anchor matched prose and asserted nothing. The AST-based
# ordering check in test/driver-contract.test.ps1 remains the authoritative one.
ok "checkpoint script: the Enumerated guard precedes every checkpoint removal" (
    $chkScript.IndexOf('$found.Enumerated') -lt $chkScript.IndexOf('Remove-ConstructVmCheckpoint -Name'))
# The panel records "applied" from this file, so writing "ok" must be conditional on the
# failure flag -- an unconditional write would mark a failed run as applied.
ok "checkpoint script: the result file reports fail when the run failed" (
    $chkScript -match 'CONSTRUCT_CHECKPOINT_RESULT' -and
    $chkScript -match 'if\s*\(\$failed\)\s*\{\s*"fail"\s*\}\s*else\s*\{\s*"ok"\s*\}')
# Written temp+rename so the polling panel can never read a half-written value.
ok "checkpoint script: the result file is written temp+rename" (
    $chkScript -match 'Set-Content[^\r\n]*\$tmp' -and $chkScript -match 'Move-Item[^\r\n]*\$tmp')
# Every exit path that reaches finally must report, including the dependency failure --
# which is why the common-lib load lives inside the try.
ok "checkpoint script: the common-lib load is inside the guarded block" (
    $chkScript.IndexOf('try {') -lt $chkScript.IndexOf('Required helper not found'))

# ── Auto-Install.ps1 automatic-checkpoint source invariants ─────────────────
# Both guards sit on paths only a Windows host with a partially-updated scripts dir
# reaches, so pin them at the source level rather than leave them unverified.
$aiScript = Get-Content -Raw (Join-Path $here "..\Auto-Install.ps1")

# The VM is already DELETED by the time the create script is invoked, so an unsupported
# parameter must be dropped rather than splatted into a binding failure.
ok "auto-install: checks Create-AgentVM's parameters before splatting" (
    $aiScript -match "Get-Command[^\r\n]*createScript" -and
    $aiScript -match "Parameters\.ContainsKey\('AutomaticCheckpoints'\)")
ok "auto-install: an unsupported OR unknowable parameter is REMOVED, not passed" (
    ([regex]::Matches($aiScript, "createArgs\.Remove\('AutomaticCheckpoints'\)")).Count -ge 2)
ok "auto-install: the capability guard precedes the create-script call" (
    $aiScript.IndexOf("Parameters.ContainsKey('AutomaticCheckpoints')") -lt
    $aiScript.IndexOf('& $createScript @createArgs'))

# An unbound parameter (a hand run, or an older control-panel extension) must fall back
# to the saved preference instead of the parameter default.
ok "auto-install: an unbound parameter falls back to the saved preference" (
    $aiScript -match "ContainsKey\('AutomaticCheckpoints'\)" -and $aiScript -match 'vmAutoCheckpoints')
# Not [bool]: every non-empty string is truthy in PowerShell, so the string "false"
# would enable checkpoints.
ok "auto-install: the saved preference is not read through a [bool] cast" (
    $aiScript -notmatch '\[bool\]\$savedSettings\.vmAutoCheckpoints')
ok "auto-install: the create call passes the EFFECTIVE value, not the raw parameter" (
    $aiScript -match 'AutomaticCheckpoints = \$effectiveAutoCheckpoints')

# ── T3CodeChannel ValidateSet (finding #5: injection safety) ─────────────────
# Each entry point must enforce the exact ""|"stable"|"nightly" contract via
# ValidateSet so a hostile value (e.g. containing a single-quote) can't reach
# the shell boundary where $envPrefix interpolates it.
foreach ($scriptName in @("Provision-AgentVM.ps1", "Create-AgentVM.ps1", "Auto-Install.ps1")) {
    $scriptPath = Join-Path $here "..\$scriptName"
    if (-not (Test-Path $scriptPath)) {
        ok "$scriptName`: T3CodeChannel ValidateSet — SKIP (file not found)" $false
        continue
    }
    $cmd = Get-Command $scriptPath -CommandType ExternalScript -ErrorAction SilentlyContinue
    $param = if ($cmd) { $cmd.Parameters['T3CodeChannel'] } else { $null }
    ok "$scriptName`: has a T3CodeChannel parameter" ($null -ne $param)
    if ($param) {
        $vs = $param.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
        ok "$scriptName`: T3CodeChannel has ValidateSet" ($null -ne $vs)
        if ($vs) {
            $allowed = @($vs.ValidValues) | Sort-Object
            $expected = @("", "nightly", "stable") | Sort-Object
            ok "$scriptName`: T3CodeChannel ValidateSet is exactly ('','stable','nightly')" (
                ($allowed -join ",") -eq ($expected -join ","))
        }
    }
}

# ── T3CodeChannel version-skew guard on every Provision forwarding site ────
# Auto-Install.ps1 splats T3CodeChannel into Provision-AgentVM.ps1 on four paths:
# create, fresh-install provision, existing-VM reprovision, and add-config
# reprovision. Every site must guard against an older Provision that lacks the
# parameter — otherwise binding fails. Pin the guard count so a new forwarding
# site can't skip the check.
ok "auto-install: T3CodeChannel guard on all 4 Provision forwarding sites" (
    ([regex]::Matches($aiScript, "Parameters\.ContainsKey\('T3CodeChannel'\)")).Count -ge 4)
ok "auto-install: T3CodeChannel removed in both guard + catch branches (at least 8 sites)" (
    ([regex]::Matches($aiScript, "\.Remove\('T3CodeChannel'\)")).Count -ge 8)

# ── T3 HTTPS: the CA trust-import decision ─────────────────────────────────
# The VM issues its own CA for the T3 HTTPS origin (bin/setup-t3-https.sh). Which
# Root store it goes into -- and whether Windows will prompt -- is decided by this
# pure helper so the provisioning block stays a thin caller.
$planMachine = Get-T3CaImportPlan -Elevated
ok "ca-plan: elevated imports into the machine Root store" (
    $planMachine.Action -eq "import" -and $planMachine.Store -eq "Cert:\LocalMachine\Root" -and
    $planMachine.Scope -eq "LocalMachine")
ok "ca-plan: the machine import is silent (no confirmation dialog)" (-not $planMachine.Prompts)
$planUser = Get-T3CaImportPlan
ok "ca-plan: not elevated imports into the user Root store" (
    $planUser.Action -eq "import" -and $planUser.Store -eq "Cert:\CurrentUser\Root" -and
    $planUser.Scope -eq "CurrentUser")
ok "ca-plan: the user import is announced as prompting once" ($planUser.Prompts -eq $true)
ok "ca-plan: the user import reason says a dialog appears" ($planUser.Reason -match "confirmation dialog")
# Already trusted in EITHER store: importing again would add a duplicate entry
# and (unelevated) show a second dialog for a certificate the user accepted.
$planSkipMachine = Get-T3CaImportPlan -PresentInMachine
ok "ca-plan: present in the machine store -> skip" (
    $planSkipMachine.Action -eq "skip" -and -not $planSkipMachine.Prompts -and $planSkipMachine.Store -eq "")
$planSkipUser = Get-T3CaImportPlan -PresentInUser
ok "ca-plan: present in the user store -> skip" ($planSkipUser.Action -eq "skip")
ok "ca-plan: present in the user store names that scope" ($planSkipUser.Scope -eq "CurrentUser")
$planSkipElevated = Get-T3CaImportPlan -Elevated -PresentInUser
ok "ca-plan: an elevated run still skips a CA already trusted for the user" (
    $planSkipElevated.Action -eq "skip")
ok "ca-plan: every verdict carries a human reason" (
    $planMachine.Reason -and $planUser.Reason -and $planSkipMachine.Reason -and $planSkipUser.Reason)

# ── T3CodeHttps parameter contract ─────────────────────────────────────────
# ""/"true"/"false" only: empty means "keep the VM's saved choice", and the value
# is interpolated into a single-quoted remote assignment, so ValidateSet is what
# keeps a hostile string away from that shell boundary.
$provPath = Join-Path $here "..\Provision-AgentVM.ps1"
$provCmdT3 = Get-Command $provPath -CommandType ExternalScript -ErrorAction SilentlyContinue
$httpsParam = if ($provCmdT3) { $provCmdT3.Parameters['T3CodeHttps'] } else { $null }
ok "Provision-AgentVM.ps1: has a T3CodeHttps parameter" ($null -ne $httpsParam)
if ($httpsParam) {
    $httpsSet = $httpsParam.Attributes | Where-Object { $_ -is [System.Management.Automation.ValidateSetAttribute] }
    ok "Provision-AgentVM.ps1: T3CodeHttps has ValidateSet" ($null -ne $httpsSet)
    if ($httpsSet) {
        $allowedHttps = @($httpsSet.ValidValues) | Sort-Object
        $expectedHttps = @("", "false", "true") | Sort-Object
        ok "Provision-AgentVM.ps1: T3CodeHttps ValidateSet is exactly ('','true','false')" (
            ($allowedHttps -join ",") -eq ($expectedHttps -join ","))
    }
    $httpsDefault = @($provCmdT3.ScriptBlock.Ast.ParamBlock.Parameters |
        Where-Object { $_.Name.VariablePath.UserPath -eq 'T3CodeHttps' } |
        ForEach-Object { $_.DefaultValue.Extent.Text })[0]
    ok "Provision-AgentVM.ps1: T3CodeHttps defaults to empty (keep-saved)" ($httpsDefault -eq '""')
}
$provSrc = Get-Content -LiteralPath $provPath -Raw
ok "Provision-AgentVM.ps1: T3CodeHttps reaches the guest as T3CODE_HTTPS" (
    $provSrc -match "T3CODE_HTTPS='\`$T3CodeHttps'")
# ValidateSet is case-insensitive, so -T3CodeHttps FALSE binds -- and bin/provision.sh
# treats anything but the exact lowercase "false" as ENABLED. The value must be
# lowercased after binding, before it reaches the env prefix.
ok "Provision-AgentVM.ps1: normalizes T3CodeHttps to lowercase after param binding" (
    $provSrc -match '\$T3CodeHttps\s*=\s*\$T3CodeHttps\.ToLower\(\)')
ok "Provision-AgentVM.ps1: normalizes T3CodeHttps BEFORE building the env prefix" (
    $provSrc.IndexOf('$T3CodeHttps = $T3CodeHttps.ToLower()') -lt $provSrc.IndexOf("T3CODE_HTTPS='`$T3CodeHttps'"))
$httpsNormTest = {
    param([ValidateSet("", "true", "false")][string]$T3CodeHttps = "")
    if ($T3CodeHttps) { $T3CodeHttps = $T3CodeHttps.ToLower() }
    return $T3CodeHttps
}
ok "T3CodeHttps normalization: 'FALSE' lowered to 'false' (else the guest reads it as true)" (
    (& $httpsNormTest -T3CodeHttps "FALSE") -eq "false")
ok "T3CodeHttps normalization: 'TRUE' lowered to 'true'" ((& $httpsNormTest -T3CodeHttps "TRUE") -eq "true")
ok "T3CodeHttps normalization: 'False' lowered to 'false'" ((& $httpsNormTest -T3CodeHttps "False") -eq "false")
ok "T3CodeHttps normalization: empty stays empty (keep-saved semantics)" ((& $httpsNormTest -T3CodeHttps "") -eq "")
ok "T3CodeHttps normalization: omitted defaults to empty" ((& $httpsNormTest) -eq "")
$httpsHostileThrew = $false
try { & $httpsNormTest -T3CodeHttps "false'; rm -rf /" } catch { $httpsHostileThrew = $true }
ok "T3CodeHttps normalization: hostile value rejected by ValidateSet" $httpsHostileThrew
ok "Provision-AgentVM.ps1: the CA handoff reads the VM status file and imports by plan" (
    $provSrc -match 'T3CODE_HTTPS_READY=yes' -and
    $provSrc -match 'Get-T3CaImportPlan' -and
    $provSrc -match 'Import-Certificate -FilePath')
ok "Provision-AgentVM.ps1: the CA handoff never fails the provision" (
    $provSrc -match "(?s)T3CODE_HTTPS_READY=yes.*?catch \{\s*Write-Warning")
$updT3Src = Get-Content -LiteralPath (Join-Path $here "..\Update-T3Code.ps1") -Raw
ok "Update-T3Code.ps1: forwards t3codeHttps only when set and supported" (
    $updT3Src -match "settings\.t3codeHttps" -and
    $updT3Src -match "Parameters\.ContainsKey\('T3CodeHttps'\)")
# B12: the per-VM state each script reads and writes is keyed by ONE resolution point --
# built on the instance B11's name-only targeting already resolved -- so a second VM never
# replays the first VM's toggles, and B11's successors have a single line to re-point.
ok "Update-T3Code.ps1: has ONE instance-resolution point" (
    $updT3Src -match "(?m)^function Get-ConstructStateInstanceName \{" -and
    $updT3Src -match "(?m)^\`$instanceName = Get-ConstructStateInstanceName")
ok "Update-T3Code.ps1: it prefers B11's -InstanceName, then the alias, then the default" (
    $updT3Src -match "if \(\`$InstanceName\) \{ return .\`$InstanceName..Trim\(\)\.ToLowerInvariant\(\) \}" -and
    $updT3Src -match "if \(\`$HostAlias\)    \{ return .\`$HostAlias..Trim\(\)\.ToLowerInvariant\(\) \}" -and
    $updT3Src -match "return 'agent-vm'")
ok "Update-T3Code.ps1: reads that instance's state, not the checkout's file" (
    $updT3Src -match "Read-ConstructInstanceState -Name \`$name -Dir \`$dir")
ok "Update-T3Code.ps1: loads the state library in a CHILD scope" (
    $updT3Src -match "AgentVm\.InstanceState\.ps1" -and $updT3Src -match "&\s*\{")
ok "Update-T3Code.ps1: still degrades to the single-file read without the library" (
    $updT3Src -match "\.construct-settings\.json")
# B12: the provisioned marker is keyed by the instance the run provisioned.
$provSrc = Get-Content -LiteralPath (Join-Path $here "..\Provision-AgentVM.ps1") -Raw
ok "Provision-AgentVM.ps1: has ONE instance-resolution point" (
    $provSrc -match "(?m)^function Get-ConstructStateInstanceName \{" -and
    $provSrc -match "if \(\`$InstanceName\) \{ return .\`$InstanceName..Trim\(\)\.ToLowerInvariant\(\) \}")
ok "Provision-AgentVM.ps1: records the provisioned marker for that instance" (
    $provSrc -match "Set-ConstructProvisionedMarker -Dir \`$PSScriptRoot -InstanceName \(Get-ConstructStateInstanceName\)")
ok "Provision-AgentVM.ps1: saves the auto-enabled selection to that instance's state" (
    $provSrc -match "Save-ConstructInstanceState -Name \(Get-ConstructStateInstanceName\) -Dir \`$PSScriptRoot -Values @\{ projects")
ok "Provision-AgentVM.ps1: still reads installedCommit from the INSTALL-WIDE file" (
    $provSrc -match "\`$constructSettings = Read-ConstructSettings -Dir \`$PSScriptRoot")
$autoSrc = Get-Content -LiteralPath (Join-Path $here "..\Auto-Install.ps1") -Raw
ok "Auto-Install.ps1: names the instance from B11's resolved identity" (
    $autoSrc -match "(?m)^\`$VmInstanceName = \`$script:VmIdentity\.Name")
ok "Auto-Install.ps1: reads the saved checkpoint preference of the VM it is installing" (
    $autoSrc -match "Read-ConstructInstanceState -Name \`$VmInstanceName -Dir \`$PSScriptRoot")

# ── T3CodeChannel lowercase normalization ──────────────────────────────────
# ValidateSet is case-insensitive: PowerShell happily binds "NIGHTLY" to the
# param, but downstream bash matches only exact lowercase. Every entry point
# must normalize a non-empty bound value to lowercase after param binding.
foreach ($scriptName in @("Provision-AgentVM.ps1", "Create-AgentVM.ps1", "Auto-Install.ps1")) {
    $scriptPath = Join-Path $here "..\$scriptName"
    if (-not (Test-Path $scriptPath)) {
        ok "$scriptName`: T3CodeChannel .ToLower() — SKIP (file not found)" $false
        continue
    }
    $src = Get-Content -LiteralPath $scriptPath -Raw
    ok "$scriptName`: normalizes T3CodeChannel to lowercase after param binding" (
        $src -match '\$T3CodeChannel\s*=\s*\$T3CodeChannel\.ToLower\(\)')
}
# Functional verification: a scriptblock with the same param+normalize pattern
# proves that uppercase values are actually lowercased and empty stays empty.
$normTest = {
    param([ValidateSet("", "stable", "nightly")][string]$T3CodeChannel = "")
    if ($T3CodeChannel) { $T3CodeChannel = $T3CodeChannel.ToLower() }
    return $T3CodeChannel
}
ok "T3CodeChannel normalization: 'nightly' passes through" ((& $normTest -T3CodeChannel "nightly") -eq "nightly")
ok "T3CodeChannel normalization: 'NIGHTLY' lowered to 'nightly'" ((& $normTest -T3CodeChannel "NIGHTLY") -eq "nightly")
ok "T3CodeChannel normalization: 'Stable' lowered to 'stable'" ((& $normTest -T3CodeChannel "Stable") -eq "stable")
ok "T3CodeChannel normalization: empty stays empty (keep-saved semantics)" ((& $normTest -T3CodeChannel "") -eq "")
ok "T3CodeChannel normalization: omitted defaults to empty" ((& $normTest) -eq "")
# Hostile value: ValidateSet rejects anything outside the allowed set.
$hostileThrew = $false
try { & $normTest -T3CodeChannel "nightly'; rm -rf /" } catch { $hostileThrew = $true }
ok "T3CodeChannel normalization: hostile value rejected by ValidateSet" $hostileThrew

# ── B14: per-instance client-side names, the T3 Desktop install rule, the CA
#         replacement plan, the SMB letter and the temp known_hosts file ───────
Write-Host ""
Write-Host "=== B14 per-instance client state ===" -ForegroundColor Cyan

# The CA file: the default instance keeps the historical name (a single-VM install
# writes exactly the file it always wrote); every other VM gets its own.
ok "T3 CA file: default instance keeps construct-t3-ca.crt" ((Get-ConstructT3CaFileName -InstanceName 'agent-vm') -eq 'construct-t3-ca.crt')
ok "T3 CA file: an empty name is the default too" ((Get-ConstructT3CaFileName -InstanceName '') -eq 'construct-t3-ca.crt')
ok "T3 CA file: a named instance gets its own file" ((Get-ConstructT3CaFileName -InstanceName 'work-vm') -eq 'construct-t3-ca-work-vm.crt')

# The CA replacement plan.
$caSame = Get-T3CaCleanupPlan -PreviousThumbprint 'AABB' -NewThumbprint 'aabb' -PresentInUser
ok "T3 CA cleanup: the same CA (any case) is never removed" ($caSame.Action -eq 'none')
$caNone = Get-T3CaCleanupPlan -PreviousThumbprint '' -NewThumbprint 'AABB' -PresentInUser
ok "T3 CA cleanup: nothing recorded means nothing to remove" ($caNone.Action -eq 'none')
$caGone = Get-T3CaCleanupPlan -PreviousThumbprint 'OLD' -NewThumbprint 'NEW'
ok "T3 CA cleanup: a replaced CA that is in no store is not a removal" ($caGone.Action -eq 'none')
$caUser = Get-T3CaCleanupPlan -PreviousThumbprint 'OLD' -NewThumbprint 'NEW' -PresentInUser
ok "T3 CA cleanup: a replaced CA in the user store is removed there" (
    $caUser.Action -eq 'remove' -and $caUser.Thumbprint -eq 'OLD' -and
    @($caUser.Stores) -contains 'Cert:\CurrentUser\Root' -and @($caUser.Stores).Count -eq 1)
$caMachineUnelevated = Get-T3CaCleanupPlan -PreviousThumbprint 'OLD' -NewThumbprint 'NEW' -PresentInMachine
ok "T3 CA cleanup: the machine store needs elevation, and says so instead of pretending" (
    $caMachineUnelevated.Action -eq 'blocked' -and $caMachineUnelevated.Reason -match 'elevated')
$caBoth = Get-T3CaCleanupPlan -PreviousThumbprint 'OLD' -NewThumbprint 'NEW' -Elevated -PresentInMachine -PresentInUser
ok "T3 CA cleanup: elevated, both stores are cleared" (@($caBoth.Stores).Count -eq 2 -and $caBoth.Action -eq 'remove')
$caMixed = Get-T3CaCleanupPlan -PreviousThumbprint 'OLD' -NewThumbprint 'NEW' -PresentInMachine -PresentInUser
ok "T3 CA cleanup: unelevated clears the user store and reports the machine one" (
    $caMixed.Action -eq 'remove' -and @($caMixed.Stores).Count -eq 1 -and $caMixed.Reason -match 'machine Root store')

# The T3 Desktop install rule: (t3Version, channel, buildHash), last reprovision wins.
$installed = [pscustomobject]@{ t3Version = '0.0.38'; channel = 'stable'; buildHash = 'abc123'; installedAt = 'x'; sourceInstance = 'agent-vm' }
$same = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash 'abc123' -Installed $installed
ok "T3 install rule: the exact triple already installed is skipped" ((-not $same.Install) -and $same.Reason -match 'already installed')
$otherChannel = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'nightly' -BuildHash 'abc123' -Installed $installed
ok "T3 install rule: another channel installs (last reprovisioned VM wins)" ($otherChannel.Install -and $otherChannel.Reason -match 'stable channel')
$otherHash = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash 'zzz999' -Installed $installed
ok "T3 install rule: the same release with another patched build installs" ($otherHash.Install)
$otherVersion = Get-T3DesktopInstallPlan -T3Version '0.0.39' -Channel 'stable' -BuildHash 'abc123' -Installed $installed
ok "T3 install rule: another T3 version installs" ($otherVersion.Install -and $otherVersion.Reason -match '0\.0\.38')
# THE TRIPLE IS THE WHOLE DECISION: whether an exe is on disk is not consulted at all.
$gone = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash 'abc123' -Installed $installed
ok "T3 install rule: an exact triple skips, whatever is on disk" ((-not $gone.Install) -and $gone.Reason -match 'already installed')
$never = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash 'abc123' -Installed $null
ok "T3 install rule: no record at all installs" ($never.Install -and $never.Reason -match 'no record')
# The pre-B14 installed.json was a copy of the VM manifest, whose T3 version is `version`.
# The rule is the exact triple out of the CANONICAL keys. A pre-B14 record (a copy of the
# VM manifest, whose T3 version is `version`) is not that record, so it installs once and
# the canonical file is written -- rather than a compatibility substitution that would
# make "the same triple" mean two different things.
$legacy = [pscustomobject]@{ version = '0.0.38'; channel = 'stable'; buildHash = 'abc123'; sha256 = ('a' * 64) }
$legacyPlan = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash 'abc123' -Installed $legacy
ok "T3 install rule: a pre-B14 record is not an exact triple, so it installs" ($legacyPlan.Install)
ok "T3 install rule: ...and says this PC has no record of a patched build" ($legacyPlan.Reason -match 'no record')
ok "T3 install rule: a canonical record needs no rewrite" (-not $same.RecordIsStale)
ok "T3 install rule: a host with no record at all writes the canonical one" ($never.RecordIsStale)
$noHash = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash '' -Installed $installed
ok "T3 install rule: an unknown build hash never counts as a match" ($noHash.Install)
ok "T3 install rule: ...and says the manifest states none" ($noHash.Reason -match 'no build hash')
ok "T3 install rule: an empty channel or version never matches either" (
    (Get-T3DesktopInstallPlan -T3Version '' -Channel 'stable' -BuildHash 'abc123' -Installed $installed).Install -and
    (Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel '' -BuildHash 'abc123' -Installed $installed).Install)
$rec = (Get-T3DesktopInstallPlan -T3Version '0.0.39' -Channel 'nightly' -BuildHash 'h9' -Installed $null -InstanceName 'work-vm' -InstalledAt '2026-09-04T10:00:00Z').Record
ok "T3 install rule: the record carries the six documented keys" (
    @($rec.Keys) -join ',' -eq 't3Version,channel,buildHash,patchHash,installedAt,sourceInstance')
# THE PATCH HASH decides when both sides state one: the build hash folds in the Construct
# commit, so every Construct update used to reinstall a byte-identical Desktop -- and two
# VMs provisioned at different commits took turns doing it.
$installedP = [pscustomobject]@{ t3Version = '0.0.38'; channel = 'stable'; buildHash = 'abc123'; patchHash = 'p1'; installedAt = 'x'; sourceInstance = 'agent-vm' }
$sameRecipe = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash 'zzz999' -PatchHash 'p1' -Installed $installedP
ok "T3 install rule: the same patch recipe at another Construct commit is already installed" ((-not $sameRecipe.Install) -and $sameRecipe.Reason -match 'already installed')
$otherRecipe = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash 'abc123' -PatchHash 'p2' -Installed $installedP
ok "T3 install rule: a changed patch recipe installs even when the build hash happens to match" ($otherRecipe.Install)
$recordNoPatch = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash 'abc123' -PatchHash 'p1' -Installed $installed
ok "T3 install rule: a record without a patch hash falls back to the build hash (match)" (-not $recordNoPatch.Install)
$recordNoPatch2 = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash 'zzz999' -PatchHash 'p1' -Installed $installed
ok "T3 install rule: ...and installs when that build hash differs" ($recordNoPatch2.Install)
$manifestNoPatch = Get-T3DesktopInstallPlan -T3Version '0.0.38' -Channel 'stable' -BuildHash 'abc123' -Installed $installedP
ok "T3 install rule: a manifest without a patch hash falls back to the build hash too" (-not $manifestNoPatch.Install)
ok "T3 install rule: the record remembers the patch hash it installed" ($sameRecipe.Record.patchHash -eq 'p1' -and $sameRecipe.Record.buildHash -eq 'zzz999')
ok "T3 install rule: the version still decides ahead of the recipe" ((Get-T3DesktopInstallPlan -T3Version '0.0.39' -Channel 'stable' -BuildHash 'abc123' -PatchHash 'p1' -Installed $installedP).Install)
ok "provisioner: hands the manifest's patch hash to the planner" ($provSrc -match 'Get-T3DesktopInstallPlan[^\n]*\n[^\n]*\n\s*-PatchHash \(\[string\]\$manifest\.patchHash\)')
ok "T3 install rule: the record names the instance that installed it" (
    $rec.sourceInstance -eq 'work-vm' -and $rec.t3Version -eq '0.0.39' -and $rec.channel -eq 'nightly' -and
    $rec.buildHash -eq 'h9' -and $rec.installedAt -eq '2026-09-04T10:00:00Z')
ok "T3 install rule: no instance recorded means the default one" (
    (Get-T3DesktopInstallPlan -T3Version '1' -Channel 'stable' -BuildHash 'h' -Installed $null).Record.sourceInstance -eq 'agent-vm')

# ── Save-ConstructVmSpec: the installers record the size they created the VM with ────
$specRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("construct-vmspec-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $specRoot -Force | Out-Null
try {
    Save-ConstructVmSpec -Dir $specRoot -InstanceName '' -MemoryGB 16 -DiskGB 150 -CpuCount 8 | Out-Null
    $specSaved = Read-ConstructSettings -Dir $specRoot
    ok "VM spec: the default instance's size lands in .construct-settings.json" (
        $specSaved.vmMemoryGB -eq 16 -and $specSaved.vmDiskGB -eq 150 -and $specSaved.vmCpuCount -eq 8)
    Save-ConstructVmSpec -Dir $specRoot -InstanceName 'agent-vm' -MemoryGB 8.5 -DiskGB 60 | Out-Null
    $specSaved = Read-ConstructSettings -Dir $specRoot
    ok "VM spec: a later create overwrites the size, keeps a CPU count it did not choose" (
        $specSaved.vmMemoryGB -eq 8.5 -and $specSaved.vmDiskGB -eq 60 -and $specSaved.vmCpuCount -eq 8)
    Save-ConstructVmSpec -Dir $specRoot -InstanceName 'agent-vm' -MemoryGB 0 -DiskGB 0 | Out-Null
    ok "VM spec: nothing chosen writes nothing" ((Read-ConstructSettings -Dir $specRoot).vmMemoryGB -eq 8.5)
    # A non-default name without the state library is skipped, never misfiled at the top level.
    $specOut = Save-ConstructVmSpec -Dir $specRoot -InstanceName 'work-vm' -MemoryGB 4 -DiskGB 40 6>&1
    ok "VM spec: another instance is not written into the default file when the state library is absent" (
        (Read-ConstructSettings -Dir $specRoot).vmMemoryGB -eq 8.5 -and ("$specOut" -match 'not recorded'))
} finally { Remove-Item -LiteralPath $specRoot -Recurse -Force -ErrorAction SilentlyContinue }
$autoSrc = Get-Content -LiteralPath (Join-Path $here "..\Auto-Install.ps1") -Raw
$createSrc = Get-Content -LiteralPath (Join-Path $here "..\Create-AgentVM.ps1") -Raw
ok "Auto-Install.ps1: records the size for the instance on BOTH the local and the remote path" (
    $autoSrc -match 'Save-ConstructVmSpec -Dir \$PSScriptRoot -InstanceName \$VmInstanceName -MemoryGB \$chosenMemGB -DiskGB \$chosenDiskGB -CpuCount \$VmCpuCount' -and
    $autoSrc -match 'Save-ConstructVmSpec -Dir \$PSScriptRoot -InstanceName \$instName -MemoryGB \$chosenMemGB -DiskGB \$chosenDiskGB -CpuCount \$remoteCpu')
ok "Auto-Install.ps1: the remote record is written AFTER the host service created the VM" (
    $autoSrc.IndexOf('Save-ConstructVmSpec -Dir $PSScriptRoot -InstanceName $instName') -gt $autoSrc.IndexOf('New-ConstructRemoteVmRecord -Name $instName'))
ok "Auto-Install.ps1: a chosen vCPU count sizes the LOCAL VM too" ($autoSrc -match "if \(\`$VmCpuCount -gt 0\) \{ \`$createArgs\['ProcessorCount'\] = \`$VmCpuCount \}")
ok "Create-AgentVM.ps1: a hand-run create records its size (not under -Auto, which Auto-Install already recorded)" (
    $createSrc -match '(?s)if \(-not \$Auto\) \{.*?Save-ConstructVmSpec -Dir \$PSScriptRoot -InstanceName \$specInstance')

# ── Get-ConstructT3PairingLink.ps1: the Desktop app's auto-link mints links through it ──
$plSrc = Get-Content -LiteralPath (Join-Path $here "..\Get-ConstructT3PairingLink.ps1") -Raw
ok "pairing link: never prompts, pauses or draws a screen (the Desktop runs it hidden)" (
    $plSrc -notmatch 'Read-Host' -and $plSrc -notmatch 'Invoke-Tui' -and $plSrc -notmatch 'Show-ConstructHeader' -and
    $plSrc -notmatch '(?i)press enter' -and $plSrc -notmatch 'Start-Sleep')
ok "pairing link: stdout is ONE JSON line, success and failure alike" (
    $plSrc -match '\[Console\]::Out\.WriteLine\(\(\[pscustomobject\]\$ordered \| ConvertTo-Json -Compress' -and
    $plSrc -match 'Write-Result @\{ ok = \$false; instance = \$script:ResolvedName; error = \$why \}' -and
    $plSrc -match 'Write-Result @\{ ok = \$true; instance = \$script:ResolvedName; pairUrl = \$pairUrl; scopes = \$scopesUsed \}')
ok "pairing link: asks the VM's t3 for the ADMINISTRATIVE scopes when its build has the flag, standard otherwise" (
    $plSrc -match "grep -q -- '--scopes'" -and $plSrc -match 'extra="--scopes administrative"' -and $plSrc -match 'scopes=standard')
ok "pairing link: the VM decides the origin (public base URL first), the label names the instance" (
    $plSrc -match 'T3CODE_PUBLIC_BASE_URL' -and $plSrc -match 'CONSTRUCT_EXTERNAL_HOST' -and
    $plSrc -match 'construct-t3-desktop-\$\(\$script:ResolvedName\)')
ok "pairing link: the script rides to the VM base64-encoded (no CRLF, no quoting on the command line)" (
    $plSrc -match 'ToBase64String' -and $plSrc -match "base64 -d \| bash")
ok "pairing link: the same SSH conventions as Get-AgentUsage.ps1 (key file first, alias fallback, batch mode)" (
    $plSrc -match 'IdentitiesOnly=yes' -and $plSrc -match 'BatchMode=yes' -and $plSrc -match 'StrictHostKeyChecking=accept-new')

# The SMB drive letter.
$taken = @('C', 'D', 'Z', 'Y')
ok "SMB letter: the default instance still prefers Z" ((Get-ConstructSmbPreferredLetter -Requested 'Z' -InstanceName 'agent-vm' -Taken $taken) -eq 'Z')
ok "SMB letter: an unnamed instance is the default one" ((Get-ConstructSmbPreferredLetter -Requested 'Z' -InstanceName '' -Taken $taken) -eq 'Z')
ok "SMB letter: an EXPLICIT letter always wins" ((Get-ConstructSmbPreferredLetter -Requested 'Q' -InstanceName 'work-vm' -Explicit -Taken $taken) -eq 'Q')
ok "SMB letter: a non-default instance takes the next free letter" ((Get-ConstructSmbPreferredLetter -Requested 'Z' -InstanceName 'work-vm' -Taken $taken) -eq 'X')
ok "SMB letter: a colon and lower case in -Taken are still that letter" ((Get-ConstructSmbPreferredLetter -Requested 'Z' -InstanceName 'work-vm' -Taken @('z:', 'y:', 'x')) -eq 'W')
ok "SMB letter: nothing free falls back to the historical preference" (
    (Get-ConstructSmbPreferredLetter -Requested 'Z' -InstanceName 'work-vm' -Taken ([char[]](68..90) | ForEach-Object { [string]$_ })) -eq 'Z')

# The throw-away known_hosts file: per instance, so two concurrent provisions cannot
# clobber (and then delete) each other's.
ok "known_hosts temp: the default alias keeps the historical name" ((Get-ConstructKnownHostsFileName -HostAlias 'agent-vm') -eq 'construct-known_hosts')
ok "known_hosts temp: an empty alias keeps it too" ((Get-ConstructKnownHostsFileName -HostAlias '') -eq 'construct-known_hosts')
ok "known_hosts temp: another alias gets its own file" ((Get-ConstructKnownHostsFileName -HostAlias 'work-vm') -eq 'construct-known_hosts-work-vm')
$khHostile = Get-ConstructKnownHostsFileName -HostAlias '..\evil'
ok "known_hosts temp: the name can never become a path" (
    $khHostile -notmatch '[\\/:]' -and $khHostile -notmatch '\.\.' -and $khHostile -match '^construct-known_hosts-')

# The provisioner really uses them (and still writes the historical names by default).
$provB14 = Get-Content -LiteralPath (Join-Path $PSScriptRoot "..\Provision-AgentVM.ps1") -Raw
ok "provisioner: the CA file name comes from the helper" ($provB14 -match 'Get-ConstructT3CaFileName -InstanceName \(Get-ConstructRunInstanceName\)')
ok "provisioner: the replaced CA is untrusted through the plan" ($provB14 -match 'Get-T3CaCleanupPlan -PreviousThumbprint \$previousCaThumb')
ok "provisioner: the previous thumbprint is read from the file it replaces" ($provB14 -match '\$previousCaThumb = \(New-Object System\.Security\.Cryptography\.X509Certificates\.X509Certificate2')
ok "provisioner: the desktop install rule is the shared planner" ($provB14 -match 'Get-T3DesktopInstallPlan -T3Version')
ok "provisioner: the build hash comes from the guest manifest, never a substitute" (
    $provB14 -match '-BuildHash \(\[string\]\$manifest\.buildHash\)' -and
    $provB14 -notmatch '\$t3BuildHash = \$expectedSha')
# The endpoint facts go into the instance's OWN state document (B12's store) -- and NOT
# for the implicit default instance, whose state IS the install's own settings file.
ok "provisioner: the endpoint facts are written through B12's per-instance store" (
    $provB14 -match 'Save-ConstructInstanceState -Name \(Get-ConstructRunInstanceName\) -Dir \$PSScriptRoot -Values \$endpointValues')
ok "provisioner: there is no second endpoint store beside it" (
    $provB14 -notmatch 'artifacts\\t3code\\remote-' -and $provB14 -notmatch 'Get-ConstructT3EndpointFileName')
ok "provisioner: the endpoint write is gated on the shared opt-in test" (
    $provB14 -match 'Test-ConstructEndpointRecordWanted -InstanceName \(Get-ConstructRunInstanceName\)')

# ── The default path writes NO new settings key ──────────────────────────────
# The bar is byte-identical, so this is asked of the real thing: the gate says no for the
# implicit default, and Save-ConstructSettings (which is what a default-instance state
# write would land in) is therefore never reached with those keys.
ok "endpoint opt-in: the implicit default instance is NOT recorded" (
    -not (Test-ConstructEndpointRecordWanted -InstanceName 'agent-vm'))
ok "endpoint opt-in: an unnamed run is not either" (
    (-not (Test-ConstructEndpointRecordWanted -InstanceName '')) -and
    (-not (Test-ConstructEndpointRecordWanted -InstanceName $null)))
ok "endpoint opt-in: a NAMED instance is recorded" (
    (Test-ConstructEndpointRecordWanted -InstanceName 'work-vm') -and
    (Test-ConstructEndpointRecordWanted -InstanceName ' far-vm '))
# ...and the file the default instance shares with the install keeps exactly its keys.
$epDir = Join-Path ([System.IO.Path]::GetTempPath()) ("b14-ep-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $epDir -Force | Out-Null
try {
    $epFile = Join-Path $epDir ".construct-settings.json"
    $epBefore = @{ installedCommit = ('a' * 40); constructRepo = 'permissionBRICK/The-Construct'
                   constructRef = 'main'; provisionedCommit = ('b' * 40); t3codeChannel = 'stable' } | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText($epFile, $epBefore)
    $epKeysBefore = @((Get-Content -LiteralPath $epFile -Raw | ConvertFrom-Json).PSObject.Properties.Name | Sort-Object)
    # What the provisioner would do for the default instance: nothing at all.
    if (Test-ConstructEndpointRecordWanted -InstanceName 'agent-vm') {
        Save-ConstructSettings -Dir $epDir -Values @{ t3BaseUrl = 'https://x:1'; t3Port = 1; openCodeUrl = $null }
    }
    $epKeysAfter = @((Get-Content -LiteralPath $epFile -Raw | ConvertFrom-Json).PSObject.Properties.Name | Sort-Object)
    ok "endpoint opt-in: the default instance's settings KEY SET is unchanged" (
        ($epKeysBefore -join ',') -eq ($epKeysAfter -join ','))
    ok "endpoint opt-in: ...and so are its bytes" (
        ([System.IO.File]::ReadAllText($epFile)) -eq $epBefore)
    ok "endpoint opt-in: none of the B14 endpoint keys appear in it" (
        ($epKeysAfter -notcontains 't3BaseUrl') -and ($epKeysAfter -notcontains 't3Port') -and
        ($epKeysAfter -notcontains 'openCodeUrl'))
} finally {
    Remove-Item -LiteralPath $epDir -Recurse -Force -ErrorAction SilentlyContinue
}
ok "provisioner: it is written outside the HTTPS/CA branch, so a plain-HTTP forward is recorded too" (
    $provB14.IndexOf('WHERE THIS VM ANSWERS, recorded for the T3 Code Desktop app') -gt
    $provB14.IndexOf('Set-OpenCodeRemote -Url'))
ok "provisioner: a run with no usable endpoint CLEARS the recorded one" (
    $provB14 -match 'endpointValues = @\{ t3BaseUrl = \$null; t3Port = \$null; openCodeUrl = \$null \}')
ok "provisioner: the registered OpenCode url is recorded for the removal to find" (
    $provB14 -match '\$script:OpenCodeRegisteredUrl = \[string\]\$openCodePlan\.Url' -and
    $provB14 -match '-OpenCodeUrl \$script:OpenCodeRegisteredUrl')
ok "provisioner: installed.json records the plan, not the VM manifest" ($provB14 -match '\(\$t3Plan\.Record \| ConvertTo-Json')
ok "provisioner: a matching but pre-B14 record is canonicalised without reinstalling" (
    $provB14 -match 'if \(\$t3Plan\.RecordIsStale\) \{')
ok "provisioner: the replaced CA file is moved into place only AFTER the import" (
    $provB14.IndexOf('Import-Certificate -FilePath $caTemp') -gt 0 -and
    $provB14.IndexOf('Import-Certificate -FilePath $caTemp') -lt $provB14.IndexOf('Move-Item -LiteralPath $caTemp -Destination $localCa'))
ok "provisioner: the CA is imported from the DOWNLOAD, never from the record it replaces" (
    $provB14 -notmatch 'Import-Certificate -FilePath \$localCa')
ok "provisioner: the record is written only after a successful install" (
    $provB14 -match 'installer exited \$\(\$installerProcess\.ExitCode\)[\s\S]{0,400}\$t3Plan\.Record')
ok "provisioner: the temp known_hosts file is per instance" ($provB14 -match 'Get-ConstructKnownHostsFileName -HostAlias \$HostAlias')
ok "provisioner: no code path still names the shared temp file" (
    ($provB14 -split "`n" | Where-Object { $_ -match '\$env:TEMP\\construct-known_hosts' -and $_ -notmatch '^\s*#' }).Count -eq 0)
ok "provisioner: the SMB preference is the shared planner" ($provB14 -match 'Get-ConstructSmbPreferredLetter -Requested \$SmbDriveLetter')
ok "provisioner: one instance identity per run, asked of B12's single answer" (
    $provB14 -match 'function Get-ConstructRunInstanceName \{ return Get-ConstructStateInstanceName \}' -and
    $provB14 -notmatch '\$script:InstanceLabel')
ok "provisioner: the default instance skips the free-letter snapshot entirely" (
    $provB14 -match "if \(-not \`$script:SmbLetterStated -and \(Get-ConstructRunInstanceName\) -ne 'agent-vm' -and")
ok "provisioner: -SmbDriveLetter still defaults to Z" ($provB14 -match '\[string\]\$SmbDriveLetter = "Z"')

Write-Host ""
Write-Host ("  host-lib unit tests - {0}/{1} passed" -f $script:pass, ($script:pass + $script:fail))
Write-Host ""
if ($script:fail -gt 0) { exit 1 }
exit 0
