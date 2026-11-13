#Requires -Version 5.1
<#
    Updates T3 Code on one Construct VM, through the Construct device agent: a shim kept
    for T3 Code Desktop and the old panel. Until the one-time upgrade is converted it says
    "finish the upgrade in the Construct window". After that it asks the VM's engine to
    update the VM (POST /vms/{name}/update through the agent), which reprovisions it with
    the current release, T3 Code included. -Mode and the identity overrides are accepted
    for compatibility and not needed: the engine decides.
#>
[CmdletBinding()]
param(
    [string]$VmHost,
    [string]$HostAlias,
    [int]$SshPort = 0,
    [string]$LocalKeyName,
    [string]$InstanceName = "",
    [ValidateSet('auto', 'quick', 'reprovision')]
    [string]$Mode = 'auto',
    [switch]$FromPanel
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\Construct.AgentShim.ps1')
$name = 'agent-vm'
if ($InstanceName) { $name = "$InstanceName".Trim().ToLowerInvariant() }
$target = Resolve-ConstructAgentVm -Name $name
if ($target.Error) {
    Write-Host $target.Error -ForegroundColor Yellow
    exit 1
}
try {
    Invoke-ConstructAgentApi -Endpoint $target.Endpoint -Method Post -Path ("/api/engines/{0}/api/v1/vms/{1}/update" -f $target.Engine, $target.Vm) | Out-Null
} catch {
    Write-Host "The engine did not start the update of $name." -ForegroundColor Yellow
    exit 1
}
Write-Host "The update of $name runs on its engine; the Construct window shows its progress." -ForegroundColor Green
