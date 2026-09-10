#Requires -Version 5.1
# Local-only identity checks. Imports one function; never runs the installer.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$t = $null; $e = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'service/host/ConvertTo-ConstructHost.ps1'), [ref]$t, [ref]$e)
if ($e.Count) { throw ($e | Out-String) }
$f = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Assert-GuestVmIdentity'}, $true)
if (-not $f) { throw 'Missing identity verifier.' }
Invoke-Expression $f.Extent.Text
$plan = @{machineId = '0123456789abcdef0123456789abcdef'}
$vm = [pscustomobject]@{Id = [guid]'10000000-0000-0000-0000-000000000001'; IPAddresses = @()}
$bios = '20000000-0000-0000-0000-000000000002'
$script:guest = $plan.machineId + "`n" + $bios
$script:settings = @([pscustomobject]@{BIOSGUID = $bios})
function Invoke-Guest($Command) {
    if ($Command -ne 'cat /etc/machine-id /sys/class/dmi/id/product_uuid') { throw 'Unexpected SSH identity probe.' }
    $script:guest
}
function Get-CimInstance($Namespace, $ClassName, $Filter, $ErrorAction) {
    if ($Namespace -ne 'root/virtualization/v2' -or $ClassName -ne 'Msvm_VirtualSystemSettingData' -or
        $Filter -ne "VirtualSystemIdentifier='$($vm.Id)' AND VirtualSystemType='Microsoft:Hyper-V:System:Realized'") {
        throw 'Identity query must select the current VM, excluding snapshots.'
    }
    $script:settings
}
function Get-VMNetworkAdapter { throw 'Identity verification must not depend on KVP IP reporting.' }
function Refuses($Expected) {
    $errorText = ''
    try { Assert-GuestVmIdentity $vm } catch { $errorText = $_.Exception.Message }
    if ($errorText -notlike $Expected) { throw "Expected refusal '$Expected', got '$errorText'." }
}
Assert-GuestVmIdentity $vm
$script:guest = $plan.machineId + "`r`n{" + $bios.ToUpperInvariant() + '}'
Assert-GuestVmIdentity $vm
$script:guest = $plan.machineId + "`n" + $vm.Id.ToString()
Refuses '*firmware identity does not match*'
$script:guest = ('f' * 32) + "`n" + $bios
Refuses '*SSH guest identity changed*'
$script:guest = $plan.machineId
Refuses '*SSH guest identity changed*'
$script:guest = $plan.machineId + "`ninvalid-uuid"
Refuses '*Could not verify*'
$script:guest = $plan.machineId + "`n" + [guid]::Empty.ToString()
Refuses '*Could not verify*'
$script:guest = $plan.machineId + "`n" + $bios
$script:settings = @()
Refuses '*Could not verify*'
$script:settings = @([pscustomobject]@{BIOSGUID = $bios}, [pscustomobject]@{BIOSGUID = $bios})
Refuses '*Could not verify*'
$script:settings = @([pscustomobject]@{BIOSGUID = ''})
Refuses '*Could not verify*'
Write-Host 'Host conversion identity checks passed: 10 cases, including empty IP reports and mismatched/missing identities.'
