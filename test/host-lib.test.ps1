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
             "Update-Construct.ps1","Get-AgentUsage.ps1","lib/AgentVm.Common.ps1")
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

Write-Host ""
Write-Host ("  host-lib unit tests - {0}/{1} passed" -f $script:pass, ($script:pass + $script:fail))
Write-Host ""
if ($script:fail -gt 0) { exit 1 }
exit 0
