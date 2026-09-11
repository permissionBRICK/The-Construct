#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Uninstall,
    # auto/release select by shared runtimes; only local invokes an SDK build.
    [ValidateSet('auto', 'local', 'release')]
    [string]$Source = 'auto',
    [switch]$Force
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib/Construct.Companion.ps1')
if ($Uninstall) {
    Uninstall-ConstructCompanion
} else {
    Install-ConstructCompanion -ScriptsDir $PSScriptRoot -Source $Source -Force:$Force
}
