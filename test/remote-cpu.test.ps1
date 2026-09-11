$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../lib/AgentVm.Remote.ps1')
$passed = 0
function Assert($Condition, $Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:passed++; Write-Host "PASS: $Message"
}
function Throws([scriptblock]$Action, [string]$Pattern) {
    $caught = $false
    try { & $Action | Out-Null } catch { if ($_.Exception.Message -notmatch $Pattern) { throw }; $caught = $true }
    Assert $caught "refuses $Pattern"
}
$script:reply = [pscustomobject]@{ recommendedCpus = 12 }
function Invoke-ConstructApi {
    param($BaseUrl, $Path, $Auth)
    Assert ($Path -eq '/vm-defaults') 'asks the remote host for its default'
    if ($script:reply -eq 'old-host') { throw 'HTTP 404' }
    return $script:reply
}
Assert ((Get-ConstructRemoteCpuDefault -BaseUrl 'https://host.test') -eq 12) 'uses the host recommendation instead of client CPU count or four'
foreach ($bad in @(0, -1, 65, 'broken')) {
    $script:reply = [pscustomobject]@{ recommendedCpus = $bad }
    Throws { Get-ConstructRemoteCpuDefault -BaseUrl 'https://host.test' } 'no available CPU allowance'
}
$script:reply = 'old-host'
Throws { Get-ConstructRemoteCpuDefault -BaseUrl 'https://host.test' } 'Update the host service'

$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../drivers/hyperv-local/HyperVLocal.Driver.ps1'), [ref]$tokens, [ref]$errors)
$fn = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Set-ConstructVmCpuCount' }, $true)
. ([scriptblock]::Create($fn.Extent.Text))
$script:state = 'Running'; $script:cpus = 4; $script:writes = 0; $script:ignoreWrite = $false
function Get-VM { param($Name, $ErrorAction) [pscustomobject]@{ State = $script:state; Name = $Name } }
function Set-VMProcessor { param($VM, $Count, $ErrorAction) $script:writes++; if (-not $script:ignoreWrite) { $script:cpus = $Count } }
function Get-VMProcessor { param($VM, $ErrorAction) [pscustomobject]@{ Count = $script:cpus } }
foreach ($state in @('Running', 'Saved', 'Paused')) {
    $script:state = $state
    Throws { Set-ConstructVmCpuCount -Name test-vm -ProcessorCount 12 } 'powered-off'
}
Assert ($script:writes -eq 0) 'running and saved VMs are never modified'
$script:state = 'Off'
Set-ConstructVmCpuCount -Name test-vm -ProcessorCount 12
Assert ($script:cpus -eq 12 -and $script:writes -eq 1) 'changes only the processor count when off'
$script:ignoreWrite = $true
Throws { Set-ConstructVmCpuCount -Name test-vm -ProcessorCount 8 } 'did not apply'
Write-Host "$passed passed"
