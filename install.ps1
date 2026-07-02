#Requires -Version 5.1
<#
    One-line web installer for The Construct:
        irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex
    Downloads the repo to %LOCALAPPDATA%\The-Construct and runs Auto-Install.ps1
    (which self-elevates, builds the autoinstall ISO, and creates + provisions the VM).
    Pass -Repo/-Ref to install from a fork/branch. Any other arguments pass straight
    through to Auto-Install.ps1 (e.g. -ConfigRepo, -ConfigDir, -Action add-config).
#>
param(
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main"
)
$ErrorActionPreference = "Stop"
try { Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force } catch { }
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

# Stable per-repo/ref folder (not a temp dir) so the ISOs Auto-Install writes survive re-runs.
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

# GitHub archives unpack to a single <name>-<ref> folder. Run the real .ps1 file (not
# iex) so Auto-Install's self-elevation ($PSCommandPath) and $PSScriptRoot resolve.
$root = Get-ChildItem -LiteralPath $work -Directory | Select-Object -First 1
if (-not $root) { throw "Downloaded archive looked empty: $work" }
$auto = Join-Path $root.FullName "Auto-Install.ps1"
if (-not (Test-Path -LiteralPath $auto)) { throw "Auto-Install.ps1 not found in $($root.FullName)." }

Write-Host "==> Launching Auto-Install.ps1" -ForegroundColor Cyan
# Forward the repo/ref PAIR only when explicitly set (fork/mirror), so the marker is
# accurate. The pair travels together: setting either forwards both. Any other args
# ($args, populated because there is no [CmdletBinding()]) pass straight through.
$fwd = @()
if ($PSBoundParameters.ContainsKey('Repo') -or $PSBoundParameters.ContainsKey('Ref')) { $fwd += '-Repo', $Repo, '-Ref', $Ref }
$fwd += $args
& $auto @fwd
