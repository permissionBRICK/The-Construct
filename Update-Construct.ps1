#Requires -Version 5.1
<#
    "Update Construct" for an install that is moving to the device agent, or any install
    run by hand. Standalone: it runs install.ps1 from this folder, which installs and
    starts the Construct device agent (or hands over to a running one, which updates
    itself). It needs no agent and none of the old client's scripts.

    The old Construct Companion's Update button runs this file after an earlier attempt
    failed: the old updater had already replaced this folder with the new release, so
    the retry starts here. -Repo/-Ref/-SkipCompanion are the old parameters; the result
    file (-ResultFile, or the CONSTRUCT_UPDATE_RESULT environment variable) receives
    "ok" or "fail" as before.
#>
[CmdletBinding()]
param(
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main",
    [switch]$SkipCompanion,
    [string]$ResultFile = ""
)
$ErrorActionPreference = "Stop"
if (-not $ResultFile) { $ResultFile = $env:CONSTRUCT_UPDATE_RESULT }

$ok = $false
try {
    & (Join-Path $PSScriptRoot 'install.ps1') -Repo $Repo -Ref $Ref
    $ok = $true
} catch {
    Write-Warning "Update failed: $($_.Exception.Message)"
}

if ($ResultFile) {
    try { Set-Content -LiteralPath $ResultFile -Value $(if ($ok) { "ok" } else { "fail" }) -Encoding ASCII -Force } catch { }
}

Write-Host ""
if ($ok) {
    Write-Host "Update complete. The Construct window finishes the upgrade." -ForegroundColor Green
    if (-not $ResultFile -and -not [Console]::IsInputRedirected) { Read-Host "Press Enter to close" | Out-Null }
} else {
    Write-Host "The update did not complete. Try it again from the Construct Companion, or run install.ps1 again." -ForegroundColor Yellow
    if (-not [Console]::IsInputRedirected) { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}
