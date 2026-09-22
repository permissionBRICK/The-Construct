#Requires -Version 5.1
# Firmware baseline safety tests without Hyper-V or an OS boot.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../drivers/hyperv-local/HyperVLocal.WindowsLicense.ps1')
function Check($condition,[string]$message) { if(-not $condition){throw $message} }
function Reset {
    $script:state='Off';$script:disk=$false;$script:dvd=$false;$script:snapshot=$false
    $script:connected=$true;$script:started=$false;$script:stopped=$false;$script:failStart=$false;$script:failStop=$false
    $script:adapter=[pscustomobject]@{SwitchName='test-switch'}
}
function Get-VM { param($Name); [pscustomobject]@{State=$script:state;Name=$Name} }
function Get-VMHardDiskDrive { param($VM); if($script:disk){'disk'} }
function Get-VMDvdDrive { param($VM); if($script:dvd){'dvd'} }
function Get-VMSnapshot { param($VM); if($script:snapshot){'snapshot'} }
function Get-VMNetworkAdapter { param($VM); $script:adapter }
function Disconnect-VMNetworkAdapter { [CmdletBinding()]param([Parameter(ValueFromPipeline=$true)]$VMNetworkAdapter);process{$script:connected=$false} }
function Connect-VMNetworkAdapter { param($VMNetworkAdapter,$SwitchName); Check ($script:state -eq 'Off') 'Reconnected a running VM'; Check ($SwitchName -eq 'test-switch') 'Wrong switch';$script:connected=$true }
function Start-VM { param($VM); Check (-not $script:connected -and -not $script:disk -and -not $script:dvd) 'Tenant input reached firmware';$script:started=$true;$script:state='Running';if($script:failStart){throw 'injected start failure'} }
function Stop-VM { param($VM,[switch]$TurnOff,$Confirm);$script:stopped=$true;if($script:failStop){throw 'injected stop failure'};$script:state='Off' }
function Start-Sleep { param($Seconds); Check ($Seconds -eq 5) 'Unexpected firmware boot duration' }
Reset
Initialize-ConstructLicenseFirmware -Name test
Check ($script:started -and $script:stopped -and $script:state -eq 'Off' -and $script:connected) 'Firmware lifecycle failed'
foreach($unsafe in @('disk','dvd','snapshot','running')) {
    Reset
    if($unsafe -eq 'running'){$script:state='Running'}else{Set-Variable -Name $unsafe -Scope Script -Value $true}
    $refused=$false;try{Initialize-ConstructLicenseFirmware -Name test}catch{$refused=$_.Exception.Message -eq 'artifact-ownership-unverified'}
    Check ($refused -and -not $script:started) "Unsafe baseline accepted: $unsafe"
}
Reset;$script:failStart=$true
$failed=$false;try{Initialize-ConstructLicenseFirmware -Name test}catch{$failed=$true}
Check ($failed -and $script:stopped -and $script:state -eq 'Off' -and $script:connected) 'Partial start was not cleaned up'
Reset;$script:failStop=$true
$failed=$false;try{Initialize-ConstructLicenseFirmware -Name test}catch{$failed=$true}
Check ($failed -and -not $script:connected) 'Reconnected after failed shutdown'
Reset;$script:adapter.SwitchName='';$script:connected=$false
Initialize-ConstructLicenseFirmware -Name test
Check (-not $script:connected) 'Connected a previously disconnected adapter'
'PASS: firmware isolation, unsafe-input rejection, partial-start cleanup, failed-stop isolation, original connectivity'
$directory=Join-Path ([IO.Path]::GetTempPath()) ('construct-baseline-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory|Out-Null
try {
    $id=[guid]::NewGuid().ToString()
    @{id=$id;pool=$directory}|ConvertTo-Json|Set-Content (Join-Path $directory 'owner.json')
    Assert-ConstructLicensePool -Pool $directory -Incarnation $id
    $refused=$false;try{Assert-ConstructLicensePool -Pool $directory -Incarnation $id -Initialized}catch{$refused=$_.Exception.Message -eq 'artifact-ownership-unverified'}
    Check $refused 'Accepted an uninitialized legacy baseline for reuse'
    @{id=$id;pool=$directory;version=2}|ConvertTo-Json|Set-Content (Join-Path $directory 'owner.json')
    Assert-ConstructLicensePool -Pool $directory -Incarnation $id -Initialized
} finally { Remove-Item -LiteralPath $directory -Recurse }
'PASS: old baselines remain identifiable for retirement but cannot be reused'
