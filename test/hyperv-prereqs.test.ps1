#Requires -Version 5.1
param([string]$Case = '')
$ErrorActionPreference = 'Stop'

if (-not $Case) {
    $exe = (Get-Process -Id $PID).Path
    foreach ($scenario in @('ready', 'tools-missing', 'all-missing', 'enable-failure', 'module-failure', 'command-missing', 'pending-reboot', 'new-reboot')) {
        # Ensure-HyperV exits after a reboot prompt. Isolate each scenario so we
        # can prove that it never reaches module loading or VM creation there.
        $output = @(& $exe -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -Case $scenario 2>&1)
        if ($LASTEXITCODE -ne 0 -or $output -notcontains "PASS $scenario") {
            throw "$scenario failed: $($output -join [Environment]::NewLine)"
        }
        Write-Host "PASS $scenario"
    }
    exit 0
}

. (Join-Path $PSScriptRoot '../lib/AgentVm.Common.ps1')
$platform = 'Microsoft-Hyper-V'
$tools = 'Microsoft-Hyper-V-Management-PowerShell'
$script:states = @{ $platform = 'Enabled'; $tools = 'Enabled' }
$script:enabled = @()
$script:imports = 0
$script:commands = 0
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message } }
function Show-TuiScreen { param($Title, $Body) }
function Write-Ok { param($Message) }
function Write-Note { param($Message) }
function Get-CimInstance { [CmdletBinding()] param($ClassName)
    return [pscustomobject]@{ HypervisorPresent = $true; Caption = 'Windows 11 Pro' }
}
function Get-WindowsOptionalFeature { [CmdletBinding()] param([switch]$Online, $FeatureName)
    Assert $Online 'Feature query must be online'
    Assert ($script:states.ContainsKey($FeatureName)) "Unexpected feature $FeatureName"
    return [pscustomobject]@{ State = $script:states[$FeatureName] }
}
function Enable-WindowsOptionalFeature { [CmdletBinding()] param([switch]$Online, $FeatureName, [switch]$All, [switch]$NoRestart)
    Assert ($Online -and $All -and $NoRestart) 'Install dependencies without automatically restarting'
    $script:enabled += $FeatureName
    if ($Case -eq 'enable-failure') { throw 'Fixture feature install failed' }
    return [pscustomobject]@{ RestartNeeded = ($Case -eq 'new-reboot') }
}
function Import-Module { [CmdletBinding()] param($Name, [switch]$Global)
    Assert ($Name -eq 'Hyper-V' -and $Global) 'Load Hyper-V for subsequent VM commands'
    $script:imports++
    if ($Case -eq 'module-failure') { throw 'Fixture module unavailable' }
}
function Get-Command { [CmdletBinding()] param($Name, $Module)
    Assert ($Name -eq 'New-VM' -and $Module -eq 'Hyper-V') 'Verify the actual VM creation command'
    $script:commands++
    if ($Case -eq 'command-missing') { throw 'Fixture New-VM unavailable' }
    return [pscustomobject]@{ Name = 'New-VM' }
}
function Get-ItemProperty { [CmdletBinding()] param($Path, $Name)
    return [pscustomobject]@{ EditionID = 'Professional' }
}
function Test-ConstructTui { return $true }
function Restart-Computer { param([switch]$Force) throw 'Unexpected restart' }
function Show-Menu { param($Title, $Options, $Default)
    Assert ($Case -in @('pending-reboot', 'new-reboot')) 'Unexpected reboot prompt'
    Assert ($script:imports -eq 0 -and $script:commands -eq 0) 'Do not use Hyper-V before restarting'
    if ($Case -eq 'pending-reboot') { Assert ($script:enabled.Count -eq 0) 'Do not reinstall pending features' }
    else { Assert (($script:enabled -join ',') -eq $tools) 'Only install missing tools' }
    Write-Host "PASS $Case"
    return 1 # Reboot later. Ensure-HyperV must exit instead of continuing.
}

switch ($Case) {
    'tools-missing' { $script:states[$tools] = 'Disabled' }
    'all-missing' { $script:states[$platform] = 'Disabled'; $script:states[$tools] = 'Disabled' }
    'enable-failure' { $script:states[$tools] = 'Disabled' }
    'pending-reboot' { $script:states[$tools] = 'EnablePending' }
    'new-reboot' { $script:states[$tools] = 'Disabled' }
}
$failure = $null
try { Ensure-HyperV } catch { $failure = $_.Exception.Message }
if ($Case -in @('pending-reboot', 'new-reboot')) { throw 'Continued despite a required reboot' }
if ($Case -eq 'enable-failure') {
    Assert ($failure -like 'Required virtualization features could not be enabled*') 'Report feature installation failure'
    Assert ($script:imports -eq 0) 'Do not import after installation failure'
} elseif ($Case -in @('module-failure', 'command-missing')) {
    Assert ($failure -like 'Hyper-V PowerShell tools are not available:*') 'Report unavailable commands before VM creation'
} else {
    Assert (-not $failure) "Unexpected failure: $failure"
    $expected = switch ($Case) { 'ready' { '' }; 'tools-missing' { $tools }; 'all-missing' { "$platform,$tools" } }
    Assert (($script:enabled -join ',') -eq $expected) 'Install exactly the missing features'
    Assert ($script:imports -eq 1 -and $script:commands -eq 1) 'Load and verify VM commands'
}
Write-Host "PASS $Case"
