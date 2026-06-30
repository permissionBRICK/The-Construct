#Requires -Version 5.1
<#
.SYNOPSIS
    One-line web installer for The Construct Windows host setup.

.DESCRIPTION
    Bootstrapper meant to be run straight from the web:

        irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex

    It downloads this repository's latest source archive from GitHub, extracts
    it to a stable per-repo/ref folder under %LOCALAPPDATA%, and runs
    Auto-Install.ps1 from there. Auto-Install.ps1 then self-elevates to
    Administrator, builds the Ubuntu autoinstall ISO, and creates + provisions
    the Hyper-V agent VM.

    The extraction path is fixed (not a timestamped temp folder) on purpose: the
    large Ubuntu / autoinstall ISOs that Auto-Install.ps1 writes next to itself
    are kept across runs, so re-running the one-liner reuses them instead of
    re-downloading and rebuilding.

    We extract to disk and run the real .ps1 file (rather than piping it into
    iex) on purpose: Auto-Install.ps1 self-elevates with
    Start-Process -File $PSCommandPath, and $PSCommandPath is empty for a script
    piped into iex. Running it as a file makes elevation (and $PSScriptRoot,
    which locates lib/, keys/, bin/, projects/) work.

.PARAMETER Repo
    GitHub "owner/name" to download. Defaults to permissionBRICK/The-Construct;
    pass it explicitly to install from a fork or mirror.

.PARAMETER Ref
    Branch or tag to install (default: main).

.PARAMETER RefreshOnly
    Re-download and extract the repo in place, record the update marker
    (installedCommit / constructRepo / constructRef) in .construct-settings.json,
    and DO NOT launch Auto-Install.ps1. Used by the control-panel "Update
    Construct" action to pull the latest scripts without rebuilding the VM.
#>
[CmdletBinding()]
param(
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main",
    [switch]$RefreshOnly
)

$ErrorActionPreference = "Stop"

# Let THIS process run the downloaded .ps1 even under a restrictive machine
# execution policy. Process-scoped: needs no admin and isn't persisted.
try { Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force } catch { }
# Older Windows PowerShell defaults to TLS 1.0/1.1; GitHub needs TLS 1.2+.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$zipUrl = "https://codeload.github.com/$Repo/zip/refs/heads/$Ref"

# Extract to a STABLE, per-repo/ref path (not a timestamped temp folder) so the
# large Ubuntu/autoinstall ISOs that Auto-Install.ps1 writes next to itself
# survive between runs and get reused. We anchor under %LOCALAPPDATA% (which,
# unlike %TEMP%, isn't periodically swept) and key the folder on owner-name-ref
# so different repos/branches don't collide. Expand-Archive -Force refreshes the
# repo files in place while leaving the downloaded ISOs (not part of the archive)
# untouched.
$base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { $env:TEMP }
$slug = ($Repo + "-" + $Ref) -replace '[^A-Za-z0-9._-]', '-'
$work = Join-Path $base (Join-Path "The-Construct" $slug)
$zip  = Join-Path $base ("construct-download.zip")

if (-not (Test-Path -LiteralPath $work)) { New-Item -ItemType Directory -Path $work -Force | Out-Null }

Write-Host "==> Downloading $Repo ($Ref) ..." -ForegroundColor Cyan
$oldPP = $ProgressPreference; $ProgressPreference = "SilentlyContinue"
try { Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing }
finally { $ProgressPreference = $oldPP }

Write-Host "==> Extracting to $work" -ForegroundColor Cyan
Expand-Archive -LiteralPath $zip -DestinationPath $work -Force
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

# A GitHub source archive unpacks to a single top-level folder: <name>-<ref>.
$root = Get-ChildItem -LiteralPath $work -Directory | Select-Object -First 1
if (-not $root) { throw "Downloaded archive looked empty: $work" }

if ($RefreshOnly) {
    # Control-panel "Update Construct": the repo files are now refreshed in place.
    # Record what we fetched (so the panel's update banner clears) and stop --
    # don't launch the install menu or rebuild the VM.
    $sha = ""
    try {
        $sha = (Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/commits/$Ref" `
                  -Headers @{ "User-Agent" = "construct-control-panel" } -UseBasicParsing).sha
    } catch {
        Write-Host "    (couldn't fetch the commit id for the update marker: $($_.Exception.Message))" -ForegroundColor DarkGray
    }
    # Always record installedCommit -- even as "" when the SHA lookup failed -- so a
    # successful file refresh can't leave a STALE older commit behind (which would
    # keep the panel's banner wrong). The control panel treats "" as "no marker"
    # (banner hidden) rather than a bogus comparison base.
    $values = @{ constructRepo = $Repo; constructRef = $Ref; installedCommit = $sha }
    try {
        . (Join-Path $root.FullName "lib\AgentVm.Common.ps1")
        Save-ConstructSettings -Dir $root.FullName -Values $values
        Write-Host "==> Updated Construct files in $($root.FullName)" -ForegroundColor Green
        if ($sha) { Write-Host "    installed commit: $sha" -ForegroundColor DarkGray }
    } catch {
        Write-Warning "Refreshed the files but couldn't record the update marker: $($_.Exception.Message)"
    }
    Write-Host ""
    Write-Host "Update complete. Reopen or refresh the control panel to re-check for updates." -ForegroundColor Cyan
    return
}

$auto = Join-Path $root.FullName "Auto-Install.ps1"
if (-not (Test-Path -LiteralPath $auto)) { throw "Auto-Install.ps1 not found in $($root.FullName)." }

# Make sure VS Code + the Remote-SSH extension are present so the control panel's
# "Open on VM" / project-open buttons and Auto-Install's end-of-install deep link
# work. Done HERE (non-elevated, winget user scope) before Auto-Install self-elevates
# -- winget is unreliable in an elevated session. Best-effort: never blocks the install.
try {
    . (Join-Path $root.FullName "lib\AgentVm.Common.ps1")
    Ensure-VSCodeRemoteSsh | Out-Null
} catch {
    Write-Warning "Could not ensure VS Code / Remote-SSH (continuing): $($_.Exception.Message)"
}

Write-Host "==> Launching Auto-Install.ps1" -ForegroundColor Cyan
Write-Host "    $auto" -ForegroundColor DarkGray
& $auto
