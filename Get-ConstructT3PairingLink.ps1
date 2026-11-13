#Requires -Version 5.1
<#
.SYNOPSIS
    T3 Code pairing for one Construct VM, through the Construct device agent.

.DESCRIPTION
    A shim kept for T3 Code Desktop and scripts that start it: the device agent owns the
    VMs now. Until the one-time upgrade is converted it answers "finish the upgrade in
    the Construct window". After that it asks the agent to open the VM's T3 Code pairing
    link (POST /api/connections/{engine}/{vm}/open {what: t3}); the agent opens the link
    in the browser and never hands its one-time token to a script.

    Output is ONE JSON line on stdout, as before:
        {"ok":false,"instance":"<name>","error":"<why>"}
    Exit code 1 (no link is printed any more).
#>
[CmdletBinding()]
param(
    [string]$InstanceName = "",
    [string]$VmHost = "",
    [string]$HostAlias = "",
    [string]$RemoteUser = "root",
    [string]$LocalKeyName = "",
    [int]$SshPort = 22,
    [string]$Ttl = "10m",
    [string]$Scopes = "administrative"
)
$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'lib\Construct.AgentShim.ps1')
$name = 'agent-vm'
if ($InstanceName) { $name = "$InstanceName".Trim().ToLowerInvariant() }
function Write-Result([string]$Why) {
    [Console]::Out.WriteLine(([pscustomobject][ordered]@{ ok = $false; instance = $name; error = $Why } | ConvertTo-Json -Compress))
    exit 1
}
$target = Resolve-ConstructAgentVm -Name $name
if ($target.Error) { Write-Result $target.Error }
try {
    Invoke-ConstructAgentApi -Endpoint $target.Endpoint -Method Post -Path ("/api/connections/{0}/{1}/open" -f $target.Engine, $target.Vm) -Body @{ what = 't3' } | Out-Null
} catch {
    Write-Result 'The Construct agent could not open T3 Code for this VM.'
}
Write-Result 'The Construct agent opened the pairing link in the browser.'
