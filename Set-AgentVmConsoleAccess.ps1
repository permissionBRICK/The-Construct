[CmdletBinding()]
param([Parameter(Mandatory)][string]$InstanceName, [string]$VmName, [switch]$Remove, [switch]$Reset, [switch]$FromPanel)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/lib/AgentVm.Common.ps1"
. "$PSScriptRoot/lib/AgentVm.Remote.ps1"
. "$PSScriptRoot/lib/AgentVm.Console.ps1"
try {
    if (-not $Remove -and -not $VmName) {
        . "$PSScriptRoot/lib/AgentVm.InstanceTarget.ps1"
        $target = Resolve-ConstructVmTarget -Name $InstanceName
        if ($target.Backend -ne "hyperv-local") { throw "Console account setup requires a local Hyper-V instance." }
        $VmName = [string]$target.VmName
    }
    if ($Remove) { Remove-ConstructConsoleAccount -InstanceName $InstanceName }
    else { Set-ConstructConsoleAccess -InstanceName $InstanceName -VmName $VmName -Reset:$Reset }
    Write-Host 'Console access updated.'
} catch { Write-Host "Console setup failed: $($_.Exception.Message)" -ForegroundColor Red; if ($FromPanel) { Read-Host 'Press Enter to close' }; exit 1 }
if (-not $FromPanel) { Read-Host 'Press Enter to close' }
