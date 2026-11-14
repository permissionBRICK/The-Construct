#Requires -Version 5.1
<#
.SYNOPSIS
    T3 Code pairing for one Construct VM, through the Construct device agent.

.DESCRIPTION
    A shim kept for T3 Code Desktop and scripts that start it: the device agent owns the
    VMs now. Until the one-time upgrade is converted it answers "finish the upgrade in
    the Construct window". After that it asks the agent for the VM's pairing link
    (POST /api/connections/{engine}/{vm}/t3-pairing, with the agent's token), which mints
    it on the VM as this script did: ten minutes, the administrative scopes where the
    VM's T3 offers them. -Ttl and -Scopes are accepted and ignored (their defaults are
    what the agent uses); the SSH parameters are ignored, the agent knows the VM.

    Output is ONE JSON line on stdout, as before:
        {"ok":true,"instance":"<name>","pairUrl":"https://...","links":[...],"scopes":"administrative"|"standard"}
        {"ok":false,"instance":"<name>","error":"<why>"}
    Exit code 0 when a link was minted, 1 otherwise. The link carries a one-time token;
    it goes to stdout only.
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
    $pairing = Invoke-ConstructAgentApi -Endpoint $target.Endpoint -Method Post -Path ("/api/connections/{0}/{1}/t3-pairing" -f $target.Engine, $target.Vm)
} catch {
    Write-Result 'The Construct agent could not get a T3 Code pairing link for this VM (is it running, with T3 Code?).'
}
if (-not $pairing -or -not [string]$pairing.pairUrl) { Write-Result 'The Construct agent returned no pairing link.' }
$links = @($pairing.links | ForEach-Object { [pscustomobject][ordered]@{ kind = [string]$_.kind; pairUrl = [string]$_.pairUrl } })
[Console]::Out.WriteLine(([pscustomobject][ordered]@{
    ok = $true; instance = $name; pairUrl = [string]$pairing.pairUrl; links = $links; scopes = [string]$pairing.scopes
} | ConvertTo-Json -Compress -Depth 3))
exit 0
