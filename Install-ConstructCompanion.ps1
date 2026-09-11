#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Uninstall,
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
