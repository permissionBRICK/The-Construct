#Requires -Version 5.1
<#
.SYNOPSIS
    One-line web installer for The Construct Windows host setup.

.DESCRIPTION
    Bootstrapper meant to be run straight from the web:

        irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex

    It downloads this repository's latest source archive from GitHub, extracts
    it to a temp folder, and runs Auto-Install.ps1 from there. Auto-Install.ps1
    then self-elevates to Administrator, builds the Ubuntu autoinstall ISO, and
    creates + provisions the Hyper-V agent VM.

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
#>
[CmdletBinding()]
param(
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main"
)

$ErrorActionPreference = "Stop"

# Let THIS process run the downloaded .ps1 even under a restrictive machine
# execution policy. Process-scoped: needs no admin and isn't persisted.
try { Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force } catch { }
# Older Windows PowerShell defaults to TLS 1.0/1.1; GitHub needs TLS 1.2+.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$zipUrl = "https://codeload.github.com/$Repo/zip/refs/heads/$Ref"
$work   = Join-Path $env:TEMP ("construct-" + [DateTime]::Now.ToString("yyyyMMdd-HHmmss"))
$zip    = "$work.zip"

Write-Host "==> Downloading $Repo ($Ref) ..." -ForegroundColor Cyan
$oldPP = $ProgressPreference; $ProgressPreference = "SilentlyContinue"
try { Invoke-WebRequest -Uri $zipUrl -OutFile $zip -UseBasicParsing }
finally { $ProgressPreference = $oldPP }

Write-Host "==> Extracting ..." -ForegroundColor Cyan
Expand-Archive -LiteralPath $zip -DestinationPath $work -Force
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

# A GitHub source archive unpacks to a single top-level folder: <name>-<ref>.
$root = Get-ChildItem -LiteralPath $work -Directory | Select-Object -First 1
if (-not $root) { throw "Downloaded archive looked empty: $work" }
$auto = Join-Path $root.FullName "Auto-Install.ps1"
if (-not (Test-Path -LiteralPath $auto)) { throw "Auto-Install.ps1 not found in $($root.FullName)." }

Write-Host "==> Launching Auto-Install.ps1" -ForegroundColor Cyan
Write-Host "    $auto" -ForegroundColor DarkGray
& $auto
