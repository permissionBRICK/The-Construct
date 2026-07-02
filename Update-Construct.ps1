#Requires -Version 5.1
<#
    Self-update for the control panel's "Update Construct": re-download the repo in
    place, record the update marker, and reinstall the control-panel extension. Does
    NOT rebuild the VM. Launched by the panel; also runnable by hand. -Repo/-Ref pick
    the source (default: the canonical repo / main).

    Result signal: when the panel launches this, it passes a path (via the
    CONSTRUCT_UPDATE_RESULT env var - an env var, not a parameter, so an OLDER copy of
    this script simply ignores it instead of erroring on an unknown argument) that we
    write "ok"/"fail" to at the end. The panel polls it and, on "ok", RELOADS the VS Code
    window so the refreshed panel loads automatically (a detached console can't reload VS
    Code). On success with a result path we therefore DON'T pause (the reload is the
    feedback); on failure we pause and tell the user to reopen VS Code. Run by hand (no
    result path) it pauses on success too so the output stays readable. -ResultFile is
    still accepted for compatibility and takes precedence over the env var.
#>
[CmdletBinding()]
param(
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main",
    [string]$ResultFile = ""
)
$ErrorActionPreference = "Stop"
if (-not $ResultFile) { $ResultFile = $env:CONSTRUCT_UPDATE_RESULT }
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$ok = $false
try {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { $env:TEMP }
    $slug = ($Repo + "-" + $Ref) -replace '[^A-Za-z0-9._-]', '-'
    $work = Join-Path $base (Join-Path "The-Construct" $slug)
    $zip  = Join-Path $base "construct-download.zip"
    if (-not (Test-Path -LiteralPath $work)) { New-Item -ItemType Directory -Path $work -Force | Out-Null }

    Write-Host "==> Downloading $Repo ($Ref) ..." -ForegroundColor Cyan
    $oldPP = $ProgressPreference; $ProgressPreference = "SilentlyContinue"
    try { Invoke-WebRequest -Uri "https://codeload.github.com/$Repo/zip/refs/heads/$Ref" -OutFile $zip -UseBasicParsing }
    finally { $ProgressPreference = $oldPP }
    Expand-Archive -LiteralPath $zip -DestinationPath $work -Force
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

    $root = Get-ChildItem -LiteralPath $work -Directory | Select-Object -First 1
    if (-not $root) { throw "Downloaded archive looked empty: $work" }

    try { . (Join-Path $root.FullName "lib\AgentVm.Common.ps1") }
    catch { Write-Warning "Could not load helpers: $($_.Exception.Message)" }

    # Record what we fetched. Set-ConstructInstalledMarker writes the (repo, ref,
    # commit) tuple atomically: on a failed SHA lookup it PRESERVES the prior marker
    # (it does not blank installedCommit), so a transient GitHub blip during an update
    # can't hide the panel's update banner.
    if (Get-Command Set-ConstructInstalledMarker -ErrorAction SilentlyContinue) {
        $sha = Set-ConstructInstalledMarker -Root $root.FullName -Repo $Repo -Ref $Ref
        Write-Host "==> Updated Construct files in $($root.FullName)" -ForegroundColor Green
        if ($sha) { Write-Host "    installed commit: $sha" -ForegroundColor DarkGray }
    } else {
        Write-Warning "Refreshed the files but couldn't record the update marker (helpers unavailable)."
    }

    # Reinstall the control-panel extension (repackage + code --install-extension). Both a
    # MISSING helper (the dot-source above failed) and a falsey return are real failures -
    # otherwise the panel would reload into the OLD panel thinking the update succeeded.
    if (-not (Get-Command Install-ControlPanelExtension -ErrorAction SilentlyContinue)) {
        throw "Update helpers didn't load, so the control-panel extension couldn't be reinstalled."
    }
    if (-not [bool](Install-ControlPanelExtension -SourceRoot $root.FullName)) {
        throw "The control-panel extension didn't install."
    }
    $ok = $true
} catch {
    Write-Warning "Update failed: $($_.Exception.Message)"
}

# Signal the panel (if it launched us) so it can reload on success / warn on failure.
if ($ResultFile) {
    try { Set-Content -LiteralPath $ResultFile -Value $(if ($ok) { "ok" } else { "fail" }) -Encoding ASCII -Force } catch { }
}

Write-Host ""
if ($ok) {
    if ($ResultFile) {
        # The panel is polling; it will reload this window. No pause - the reload is the
        # feedback (and the window closes because it's launched without -NoExit).
        Write-Host "Update complete - reloading VS Code to load the refreshed panel..." -ForegroundColor Green
    } else {
        Write-Host "Update complete. Reload/restart VS Code to pick up the refreshed panel." -ForegroundColor Cyan
        if (-not [Console]::IsInputRedirected) { Read-Host "Press Enter to close" | Out-Null }
    }
} else {
    Write-Host "The update did not complete. Please reopen VS Code, then try the update again." -ForegroundColor Yellow
    if (-not [Console]::IsInputRedirected) { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}
