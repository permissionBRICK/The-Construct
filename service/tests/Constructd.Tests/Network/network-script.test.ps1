$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. "$PSScriptRoot/../../../../drivers/hyperv-local/HyperVLocal.ChildVm.ps1"
$script:checks = 0
function Check($condition, $message) {
    if (-not $condition) { throw $message }; $script:checks++
}
$script:vmId = [guid]'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'
$script:lastId = $null
$script:hostReads = 0
function Get-VM { param($Id, $Name, $ErrorAction) if ($Name -eq 'missing') { throw 'sensitive dependency output' }; $script:lastId = $Id; [pscustomobject]@{ Id = $script:vmId; Name = 'child' } }
function Get-VMNetworkAdapter {
    param($VM, [switch]$ManagementOS, $ErrorAction)
    if ($ManagementOS) { return [pscustomobject]@{ MacAddress = '001122334455'; SwitchName = 'switch' } }
    Check ($VM.Id -eq $script:vmId) 'Adapter query must use the resolved immutable VM'
    [pscustomobject]@{ Id = 'nic'; MacAddress = '001122334466'; MacAddressSpoofing = 'Off'; SwitchName = 'switch'; IPAddresses = @('10.2.3.4', 'fd00::2', 'invalid') }
}
function Get-NetIPAddress {
    param($ErrorAction)
    $script:hostReads++
    [pscustomobject]@{ IPAddress = '10.2.3.1'; PrefixLength = 24; InterfaceIndex = 12; InterfaceAlias = 'renamed-host-nic' }
    [pscustomobject]@{ IPAddress = '192.168.1.5'; PrefixLength = 24; InterfaceIndex = 13; InterfaceAlias = 'physical' }
}
function Get-NetAdapter { param([switch]$IncludeHidden, $ErrorAction) [pscustomobject]@{ MacAddress = '00-11-22-33-44-55'; ifIndex = 12; Name = 'renamed-host-nic' } }
function Get-NetNeighbor { param($InterfaceIndex, $ErrorAction) Check ($InterfaceIndex -eq 12) 'Neighbor scope'; [pscustomobject]@{ IPAddress = '10.2.3.4'; LinkLayerAddress = '00-11-22-33-44-66'; State = 'Reachable' } }
$result = Get-ConstructVmAddresses -Name child -VmId $script:vmId
Check ($script:lastId -eq $script:vmId) 'Get-VM must use Id'
Check ($result.addresses.Count -eq 2) 'Malformed reported address excluded'
Check (-not $result.addresses[0].verified) 'KVP never proves ownership'
Check ($result.addresses[0].adapterId -eq 'nic') 'Address retains adapter association'
Check ($result.addresses[1].family -eq 'ipv6') 'IPv6 report'
Check ($result.adapters[0].vmId -eq [string]$script:vmId) 'VM id returned'
Check (-not $result.adapters[0].macSpoofingEnabled) 'Host MAC spoofing fact'
Check ($result.subnets.Count -eq 1) 'Only guest-facing subnets'
Check ($result.subnets[0].switchName -eq 'switch') 'Subnet tied to actual switch'
Check ($result.subnets[0].cidr -eq '10.2.3.1/24') 'Host prefix preserved'
Check ($result.hostAddresses.Count -eq 2) 'All host addresses excluded from destination selection'
Check ($result.neighbors.Count -eq 1) 'Neighbor facts returned'
$replaced = Get-ConstructVmAddresses -Name replacement -VmId $script:vmId
Check ($replaced.vms[0].error -eq 'vm-incarnation-changed' -and $replaced.vms[0].addresses.Count -eq 0) 'Name/id disagreement refused per VM'
$script:hostReads = 0
$batch = Get-ConstructVmAddresses -VmRequests @(@{ name = 'child'; vmId = $script:vmId }, @{ name = 'child'; vmId = $script:vmId })
Check ($batch.vms.Count -eq 2) 'Snapshot contains every requested VM'
Check ($batch.vms[0].addresses.Count -eq 2 -and $batch.vms[1].addresses.Count -eq 2) 'Reports retained per VM'
Check ($script:hostReads -eq 1) 'Host facts collected once for the complete snapshot'
$partial = Get-ConstructVmAddresses -VmRequests @(@{ name = 'missing'; vmId = '' }, @{ name = 'child'; vmId = $script:vmId })
Check ($partial.vms.Count -eq 2) 'Missing VM does not discard sibling'
Check ($partial.vms[0].error -eq 'vm-not-found' -and $partial.vms[0].addresses.Count -eq 0) 'Missing VM is a categorized empty result'
Check ($partial.vms[1].addresses.Count -eq 2) 'Healthy sibling addresses survive'
Check ($partial.hostAddresses.Count -eq 2 -and $partial.subnets.Count -eq 1) 'Host facts survive per-VM failure'
Check (($partial | ConvertTo-Json -Depth 10 -Compress) -notmatch 'sensitive dependency output') 'Dependency error text never enters snapshot'
Write-Host "PASS: $script:checks network script checks"
