#Requires -Version 5.1
<#
    The device agent seen from the scripts that T3 Code Desktop and the old panel still start
    (Update-T3Code.ps1, Get-ConstructT3PairingLink.ps1). Self-contained, ASCII, Windows
    PowerShell 5.1: the agent's token file, its routes, and the VM of an instance name.
    Until the one-time upgrade is converted every answer is "finish the upgrade".
#>

$script:ConstructUpgradeMessage = 'Finish the upgrade in the Construct window first.'

function Get-ConstructAgentEndpoint {
    $base = $env:LOCALAPPDATA
    if (-not $base) { $base = $env:TEMP }
    $path = Join-Path $base 'Construct\agent\endpoint.json'
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -Raw -LiteralPath $path | ConvertFrom-Json) } catch { return $null }
}

function Invoke-ConstructAgentApi {
    param([Parameter(Mandatory)]$Endpoint, [string]$Method = 'Get', [Parameter(Mandatory)][string]$Path, $Body)
    $call = @{
        UseBasicParsing = $true; Method = $Method; Uri = ("http://127.0.0.1:{0}{1}" -f $Endpoint.port, $Path)
        Headers = @{ Authorization = ('Bearer ' + $Endpoint.token) }; TimeoutSec = 120
    }
    if ($Method -ne 'Get') { $call.ContentType = 'application/json'; $call.Body = '{}'; if ($Body) { $call.Body = ($Body | ConvertTo-Json -Compress) } }
    return Invoke-RestMethod @call
}

function Resolve-ConstructAgentVm {
    <# The running agent and the VM of an instance name: @{ Endpoint; Engine; Vm } or @{ Error }. #>
    param([string]$Name)
    if (-not $Name) { $Name = 'agent-vm' }
    $endpoint = Get-ConstructAgentEndpoint
    if (-not $endpoint) { return @{ Error = 'The Construct agent is not running. ' + $script:ConstructUpgradeMessage } }
    try { $upgrade = Invoke-ConstructAgentApi -Endpoint $endpoint -Path '/api/upgrade' } catch { return @{ Error = 'The Construct agent did not answer.' } }
    if ($upgrade.legacy -and [string]$upgrade.stage -ne 'converted') { return @{ Error = $script:ConstructUpgradeMessage } }
    try { $connections = @(Invoke-ConstructAgentApi -Endpoint $endpoint -Path '/api/connections') } catch { return @{ Error = 'The Construct agent did not answer.' } }
    $match = @($connections | Where-Object { [string]$_.vm -eq $Name })
    if ($match.Count -eq 0) { return @{ Error = "No VM named $Name is known to the Construct agent." } }
    return @{ Endpoint = $endpoint; Engine = [string]$match[0].engine; Vm = $Name }
}
