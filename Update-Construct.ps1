#Requires -Version 5.1
<#
    Self-update for the control panel's "Update Construct": re-download the repo in
    place, record the update marker, and reinstall the control-panel extension. Does
    NOT rebuild the VM. Launched by the panel; also runnable by hand. -Repo/-Ref pick
    the source (default: the canonical repo / main).
#>
[CmdletBinding()]
param(
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main"
)
$ErrorActionPreference = "Stop"
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

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

# Record what we fetched (installedCommit "" on failure -> the panel treats it as no
# marker and hides the banner, rather than diffing against a stale commit).
if (Get-Command Set-ConstructInstalledMarker -ErrorAction SilentlyContinue) {
    $sha = Set-ConstructInstalledMarker -Root $root.FullName -Repo $Repo -Ref $Ref
    Write-Host "==> Updated Construct files in $($root.FullName)" -ForegroundColor Green
    if ($sha) { Write-Host "    installed commit: $sha" -ForegroundColor DarkGray }
} else {
    Write-Warning "Refreshed the files but couldn't record the update marker (helpers unavailable)."
}
# Reinstall the control-panel extension (repackage + code --install-extension).
if (Get-Command Install-ControlPanelExtension -ErrorAction SilentlyContinue) {
    Install-ControlPanelExtension -SourceRoot $root.FullName | Out-Null
}
Write-Host ""
Write-Host "Update complete. Reload/restart VS Code to pick up the refreshed panel." -ForegroundColor Cyan
