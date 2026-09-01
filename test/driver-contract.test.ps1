#Requires -Version 5.1
<#
    Unit tests for the B4 hypervisor driver contract.  Run:

        pwsh -NoProfile -File test/driver-contract.test.ps1

    Self-contained and Hyper-V-free: the Hyper-V cmdlets (and the two lib helpers
    the driver routes to) are STUBBED in this script's scope and record every
    invocation, so the driver can be dot-sourced and driven on Linux.

    The point of the sequence assertions is the zero-change bar: New-ConstructVm +
    Start-ConstructVm must issue exactly the cmdlets, in the order, with the
    arguments Create-AgentVM.ps1 issued inline BEFORE the extraction (the expected
    sequence below is transcribed from that pre-change script).
#>
$ErrorActionPreference = "Stop"

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

# ── (a) Parser checks: every touched .ps1 must parse with zero errors ────────
Write-Host ""
Write-Host "=== Parser checks ===" -ForegroundColor Cyan
$touchedScripts = @(
    "drivers/Load-ConstructDriver.ps1",
    "drivers/hyperv-local/HyperVLocal.Driver.ps1",
    "Create-AgentVM.ps1",
    "Set-AgentVmCheckpoints.ps1",
    "Auto-Install.ps1"
)
foreach ($rel in $touchedScripts) {
    $full = Join-Path $repoRoot $rel
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($full, [ref]$null, [ref]$errors)
    ok "parse: $rel has zero errors" ($errors.Count -eq 0)
    if ($errors.Count -gt 0) {
        foreach ($e in $errors) { Write-Host "    ERROR: $($e.Message) (line $($e.Extent.StartLineNumber))" -ForegroundColor Red }
    }
}

# ── (b) Stubs: Hyper-V cmdlets + the lib helpers the driver routes to ────────
# Every stub records its name and bound parameters, so a test can assert the exact
# cmdlet sequence and arguments.
$script:calls = New-Object System.Collections.Generic.List[object]
function Record([string]$Name, $Bound) {
    $script:calls.Add([pscustomobject]@{ Name = $Name; Args = $Bound })
}
function Reset-Calls { $script:calls.Clear() }
function CallNames { return @($script:calls | ForEach-Object { $_.Name }) }
function CallAt([int]$i) { return $script:calls[$i] }

# Quiet progress helpers, defined BEFORE the driver is dot-sourced so its guarded
# fallbacks stay out of the way (exactly what the real host scripts do).
function Write-Step($msg) { }
function Write-Ok($msg)   { }
function Write-Note($msg) { }

# Get-VM behaviour is switched per test: a state string, 'missing' (Hyper-V's
# InvalidParameter error for an unknown VM), or 'boom' (any other failure).
$script:GetVmMode = 'Running'
function Get-VM {
    [CmdletBinding()]
    param([string]$Name)
    Record 'Get-VM' $PSBoundParameters
    if ($script:GetVmMode -eq 'missing') {
        $er = New-Object System.Management.Automation.ErrorRecord(
            (New-Object System.Management.Automation.ItemNotFoundException("A parameter is invalid. Hyper-V was unable to find a virtual machine with name $Name.")),
            'InvalidParameter,Microsoft.HyperV.PowerShell.Commands.GetVM',
            [System.Management.Automation.ErrorCategory]::InvalidArgument,
            $null)
        throw $er
    }
    if ($script:GetVmMode -eq 'boom') { throw "You do not have the required permission to complete this task." }
    if ($script:GetVmMode -eq 'null') { return $null }
    return [pscustomobject]@{ Name = $Name; State = $script:GetVmMode }
}

function New-VM              { [CmdletBinding()] param([string]$Name, [int]$Generation, [long]$MemoryStartupBytes, [string]$SwitchName, [string]$NewVHDPath, [long]$NewVHDSizeBytes) Record 'New-VM' $PSBoundParameters }
function Set-VM              { [CmdletBinding()] param([string]$Name, [int]$ProcessorCount, [switch]$StaticMemory, [string]$CheckpointType, [string]$AutomaticStartAction, [string]$AutomaticStopAction, [bool]$AutomaticCheckpointsEnabled) Record 'Set-VM' $PSBoundParameters }
function Set-VMProcessor     { [CmdletBinding()] param([string]$VMName, [bool]$ExposeVirtualizationExtensions) Record 'Set-VMProcessor' $PSBoundParameters }
function Set-VMFirmware      { [CmdletBinding()] param([string]$VMName, [string]$EnableSecureBoot, $BootOrder) Record 'Set-VMFirmware' $PSBoundParameters }
function Add-VMDvdDrive      { [CmdletBinding()] param([string]$VMName, [int]$ControllerNumber, [int]$ControllerLocation, [string]$Path) Record 'Add-VMDvdDrive' $PSBoundParameters }
function Set-VMDvdDrive      { [CmdletBinding()] param([string]$VMName, [int]$ControllerNumber, [int]$ControllerLocation, $Path) Record 'Set-VMDvdDrive' $PSBoundParameters }
function Remove-VMDvdDrive   { [CmdletBinding()] param([string]$VMName, [int]$ControllerNumber, [int]$ControllerLocation) Record 'Remove-VMDvdDrive' $PSBoundParameters }
function Get-VMDvdDrive      { [CmdletBinding()] param([string]$VMName) Record 'Get-VMDvdDrive' $PSBoundParameters; return 'DVD' }
function Get-VMHardDiskDrive { [CmdletBinding()] param([string]$VMName) Record 'Get-VMHardDiskDrive' $PSBoundParameters; return 'HDD' }
function Get-VMNetworkAdapter{ [CmdletBinding()] param([string]$VMName) Record 'Get-VMNetworkAdapter' $PSBoundParameters; return 'NIC' }
function Start-VM            { [CmdletBinding()] param([string]$Name) Record 'Start-VM' $PSBoundParameters }
function Stop-VM             { [CmdletBinding()] param([string]$Name, [switch]$TurnOff, [switch]$Force) Record 'Stop-VM' $PSBoundParameters }
function Save-VM             { [CmdletBinding()] param([string]$Name) Record 'Save-VM' $PSBoundParameters }

function Remove-VMSnapshot   { [CmdletBinding()] param($VMSnapshot, [switch]$Confirm) Record 'Remove-VMSnapshot' $PSBoundParameters }

# lib\AgentVm.Common.ps1 helpers the driver routes to (not re-implemented by it).
function Ensure-HyperV            { [CmdletBinding()] param() Record 'Ensure-HyperV' $PSBoundParameters }
function Add-HyperVAdminMembership{ [CmdletBinding()] param() Record 'Add-HyperVAdminMembership' $PSBoundParameters }
function Remove-AgentVm           { [CmdletBinding()] param([string]$VmName) Record 'Remove-AgentVm' $PSBoundParameters }
$script:CheckpointResult = @{ Enumerated = $true; Certain = @(); Probable = @(); All = @() }
function Get-AgentVmAutomaticCheckpoint {
    [CmdletBinding()] param([string]$VmName)
    Record 'Get-AgentVmAutomaticCheckpoint' $PSBoundParameters
    return $script:CheckpointResult
}

# ── (c) The loader ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Driver loader ===" -ForegroundColor Cyan

$driverLoader = Join-Path (Join-Path $repoRoot "drivers") "Load-ConstructDriver.ps1"
. $driverLoader -Backend "hyperv-local"

ok "loader: dot-sourcing brings the contract functions into the caller's scope" (
    (Get-Command Get-ConstructVmState -ErrorAction SilentlyContinue) -and
    (Get-Command New-ConstructVm -ErrorAction SilentlyContinue) -and
    (Get-Command Get-ConstructVmEndpoint -ErrorAction SilentlyContinue))

ok "loader: Import-ConstructDriver resolves the hyperv-local driver file" (
    (Import-ConstructDriver -Backend "hyperv-local") -eq
    (Join-Path (Join-Path (Join-Path $repoRoot "drivers") "hyperv-local") "HyperVLocal.Driver.ps1"))
ok "loader: an empty backend means hyperv-local (zero-change default)" (
    (Import-ConstructDriver -Backend "") -eq (Import-ConstructDriver -Backend "hyperv-local"))
ok "loader: the backend id is case-insensitive" (
    (Import-ConstructDriver -Backend "HyperV-Local") -eq (Import-ConstructDriver -Backend "hyperv-local"))

$threw = $false; $msg = ""
try { $null = Import-ConstructDriver -Backend "proxmox" } catch { $threw = $true; $msg = $_.Exception.Message }
ok "loader: an unknown backend throws, naming the known ones" ($threw -and $msg -match "proxmox" -and $msg -match "hyperv-local")

# ── (d) Capabilities ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Capabilities ===" -ForegroundColor Cyan
$caps = Get-ConstructDriverCapabilities
ok "caps: Checkpoints = true"      ($caps.Checkpoints -eq $true)
ok "caps: Console = vmconnect"     ($caps.Console -eq 'vmconnect')
ok "caps: Suspend = true"          ($caps.Suspend -eq $true)
ok "caps: Backend = hyperv-local"  ($caps.Backend -eq 'hyperv-local')

# ── (e) Get-ConstructVmState mapping ────────────────────────────────────────
Write-Host ""
Write-Host "=== Get-ConstructVmState ===" -ForegroundColor Cyan
foreach ($pair in @(
    @{ Raw = 'Running'; Expected = 'running' },
    @{ Raw = 'Off';     Expected = 'off' },
    @{ Raw = 'Paused';  Expected = 'paused' },
    @{ Raw = 'Saved';   Expected = 'saved' },
    @{ Raw = 'running'; Expected = 'running' },   # case-insensitive
    @{ Raw = 'Starting';Expected = 'unknown' },   # transient
    @{ Raw = 'Stopping';Expected = 'unknown' }
)) {
    $script:GetVmMode = $pair.Raw
    ok "state: Hyper-V '$($pair.Raw)' -> '$($pair.Expected)'" ((Get-ConstructVmState -Name 'Agent-VM') -eq $pair.Expected)
}
$script:GetVmMode = 'missing'
ok "state: InvalidParameter* (no such VM) -> absent" ((Get-ConstructVmState -Name 'Agent-VM') -eq 'absent')
$script:GetVmMode = 'boom'
ok "state: any other failure -> unknown (never 'absent')" ((Get-ConstructVmState -Name 'Agent-VM') -eq 'unknown')
$script:GetVmMode = 'null'
ok "state: no object returned -> absent" ((Get-ConstructVmState -Name 'Agent-VM') -eq 'absent')

$script:GetVmMode = 'Running'

# Test-ConstructVmPresent: three-valued, so callers can reproduce the old
# `Get-VM -ErrorAction SilentlyContinue` test exactly.
ok "presence: an existing VM -> true" ((Test-ConstructVmPresent -Name 'Agent-VM') -eq $true)
$script:GetVmMode = 'Starting'
ok "presence: a TRANSIENT state still counts as present (state would say 'unknown')" (
    (Test-ConstructVmPresent -Name 'Agent-VM') -eq $true -and
    (Get-ConstructVmState -Name 'Agent-VM') -eq 'unknown')
$script:GetVmMode = 'missing'
ok "presence: no such VM -> false" ((Test-ConstructVmPresent -Name 'Agent-VM') -eq $false)
$script:GetVmMode = 'boom'
ok "presence: unreadable Hyper-V -> null ('can't tell', never false)" (
    $null -eq (Test-ConstructVmPresent -Name 'Agent-VM'))
$script:GetVmMode = 'Running'

# ── (f) Get-ConstructVmEndpoint ─────────────────────────────────────────────
Write-Host ""
Write-Host "=== Get-ConstructVmEndpoint ===" -ForegroundColor Cyan
$ep = Get-ConstructVmEndpoint -Name 'Agent-VM'
ok "endpoint: default VM -> agent-vm.mshome.net" ($ep.SshHost -eq 'agent-vm.mshome.net')
ok "endpoint: default VM -> port 22"             ([int]$ep.SshPort -eq 22)
ok "endpoint: the name is lowercased (mshome DNS label)" ((Get-ConstructVmEndpoint -Name 'Work-VM').SshHost -eq 'work-vm.mshome.net')

# ── (g) New-ConstructVm + Start-ConstructVm: the pre-change cmdlet sequence ──
Write-Host ""
Write-Host "=== New-ConstructVm (pre-change sequence) ===" -ForegroundColor Cyan

# The values Create-AgentVM.ps1 computed before calling into Hyper-V.
$vmName      = 'Agent-VM'
$memoryBytes = [long]21474836480          # 20 GB, already 2 MB-aligned
$diskGB      = 50
$cpus        = 16
$vhdPath     = "C:\ProgramData\Microsoft\Windows\Virtual Hard Disks\$vmName.vhdx"
$isoPath     = "C:\Users\dev\ubuntu-24.04-autoinstall.iso"

Reset-Calls
New-ConstructVm -Descriptor @{
    Name                 = $vmName
    MemoryBytes          = $memoryBytes
    DiskGB               = $diskGB
    ProcessorCount       = $cpus
    VhdPath              = $vhdPath
    SwitchName           = "Default Switch"
    Generation           = 2
    IsoPath              = $isoPath
    Nested               = $true
    AutomaticCheckpoints = $false
    CheckpointType       = "Standard"
    AutomaticStartAction = "StartIfRunning"
    AutomaticStopAction  = "Save"
}
Start-ConstructVm -Name $vmName

# Transcribed from Create-AgentVM.ps1 sections 6-8 as they were BEFORE B4.
$expectedSequence = @(
    'New-VM', 'Set-VM', 'Set-VMProcessor', 'Set-VMFirmware',
    'Add-VMDvdDrive', 'Get-VMDvdDrive', 'Get-VMHardDiskDrive', 'Get-VMNetworkAdapter',
    'Set-VMFirmware', 'Start-VM'
)
$actualSequence = CallNames
ok "sequence: same cmdlets in the same order" (
    ($actualSequence -join ',') -eq ($expectedSequence -join ','))
if (($actualSequence -join ',') -ne ($expectedSequence -join ',')) {
    Write-Host "    expected: $($expectedSequence -join ', ')" -ForegroundColor Red
    Write-Host "    actual  : $($actualSequence -join ', ')" -ForegroundColor Red
}

$newVm = (CallAt 0).Args
ok "New-VM: -Name"                ($newVm['Name'] -eq $vmName)
ok "New-VM: -Generation 2"        ([int]$newVm['Generation'] -eq 2)
ok "New-VM: -MemoryStartupBytes is the exact byte count (no GB round-trip)" ([long]$newVm['MemoryStartupBytes'] -eq $memoryBytes)
ok "New-VM: -SwitchName 'Default Switch'" ($newVm['SwitchName'] -eq 'Default Switch')
ok "New-VM: -NewVHDPath"          ($newVm['NewVHDPath'] -eq $vhdPath)
ok "New-VM: -NewVHDSizeBytes = DiskGB * 1GB" ([long]$newVm['NewVHDSizeBytes'] -eq ([long]$diskGB * 1GB))

$setVm = (CallAt 1).Args
ok "Set-VM: -ProcessorCount"      ([int]$setVm['ProcessorCount'] -eq $cpus)
ok "Set-VM: -StaticMemory"        ([bool]$setVm['StaticMemory'])
ok "Set-VM: -CheckpointType Standard" ($setVm['CheckpointType'] -eq 'Standard')
ok "Set-VM: -AutomaticStartAction StartIfRunning" ($setVm['AutomaticStartAction'] -eq 'StartIfRunning')
ok "Set-VM: -AutomaticStopAction Save" ($setVm['AutomaticStopAction'] -eq 'Save')
ok "Set-VM: -AutomaticCheckpointsEnabled false (Construct's default)" ([bool]$setVm['AutomaticCheckpointsEnabled'] -eq $false)

$proc = (CallAt 2).Args
ok "Set-VMProcessor: nested virtualization exposed" ([bool]$proc['ExposeVirtualizationExtensions'] -eq $true)

$fw1 = (CallAt 3).Args
ok "Set-VMFirmware: Secure Boot off" ($fw1['EnableSecureBoot'] -eq 'Off')

$dvd = (CallAt 4).Args
ok "Add-VMDvdDrive: SCSI 0:1 with the ISO" (
    [int]$dvd['ControllerNumber'] -eq 0 -and [int]$dvd['ControllerLocation'] -eq 1 -and $dvd['Path'] -eq $isoPath)

$fw2 = (CallAt 8).Args
ok "Set-VMFirmware: boot order DVD -> HDD -> NIC" (
    (@($fw2['BootOrder']) -join ',') -eq 'DVD,HDD,NIC')

ok "Start-VM: starts the right VM" ((CallAt 9).Args['Name'] -eq $vmName)

# Automatic checkpoints ON must reach Hyper-V as $true (the -AutomaticCheckpoints
# "true" path of Create-AgentVM.ps1).
Reset-Calls
New-ConstructVm -Descriptor @{
    Name = $vmName; MemoryBytes = $memoryBytes; DiskGB = $diskGB; ProcessorCount = $cpus
    IsoPath = $isoPath; AutomaticCheckpoints = $true
}
ok "descriptor: AutomaticCheckpoints = true reaches Set-VM" ([bool](CallAt 1).Args['AutomaticCheckpointsEnabled'] -eq $true)
ok "descriptor: defaults match Create-AgentVM (switch/generation/checkpoint type)" (
    (CallAt 0).Args['SwitchName'] -eq 'Default Switch' -and
    [int](CallAt 0).Args['Generation'] -eq 2 -and
    (CallAt 1).Args['CheckpointType'] -eq 'Standard')

# Memory alignment: a byte count that isn't on a 2 MB boundary is rounded DOWN,
# the same arithmetic Create-AgentVM.ps1 applies to the prompted value.
Reset-Calls
New-ConstructVm -Descriptor @{ Name = $vmName; MemoryBytes = ([long]4GB + 1234567); DiskGB = 10; ProcessorCount = 2 }
ok "descriptor: memory is aligned down to a 2 MB boundary" (
    ([long](CallAt 0).Args['MemoryStartupBytes'] % 2MB) -eq 0 -and
    [long](CallAt 0).Args['MemoryStartupBytes'] -eq ([long]4GB))
Reset-Calls
New-ConstructVm -Descriptor @{ Name = $vmName; MemoryGB = 8; DiskGB = 10; ProcessorCount = 2 }
ok "descriptor: MemoryGB works when no exact byte count is supplied" (
    [long](CallAt 0).Args['MemoryStartupBytes'] -eq ([long]8GB))

# Nested virtualization is non-fatal: an unsupported host must not fail the create.
function Set-VMProcessor { [CmdletBinding()] param([string]$VMName, [bool]$ExposeVirtualizationExtensions) Record 'Set-VMProcessor' $PSBoundParameters; throw "not supported on this host" }
Reset-Calls
$nestedThrew = $false
try {
    New-ConstructVm -Descriptor @{ Name = $vmName; MemoryBytes = $memoryBytes; DiskGB = 10; ProcessorCount = 2; IsoPath = $isoPath } -WarningAction SilentlyContinue
} catch { $nestedThrew = $true }
ok "nested: an unsupported host is a warning, not a failure" (-not $nestedThrew -and (CallNames) -contains 'Set-VMFirmware')
function Set-VMProcessor { [CmdletBinding()] param([string]$VMName, [bool]$ExposeVirtualizationExtensions) Record 'Set-VMProcessor' $PSBoundParameters }

# ── (h) Detach-ConstructInstallMedia (pre-change section 9) ─────────────────
Write-Host ""
Write-Host "=== Detach-ConstructInstallMedia ===" -ForegroundColor Cyan
Reset-Calls
Detach-ConstructInstallMedia -Name $vmName
ok "detach: same cmdlets in the same order" (
    ((CallNames) -join ',') -eq 'Set-VMDvdDrive,Remove-VMDvdDrive,Get-VMHardDiskDrive,Get-VMNetworkAdapter,Set-VMFirmware')
ok "detach: the ISO is unmounted (-Path `$null) on SCSI 0:1" (
    $null -eq (CallAt 0).Args['Path'] -and
    [int](CallAt 0).Args['ControllerNumber'] -eq 0 -and [int](CallAt 0).Args['ControllerLocation'] -eq 1)
ok "detach: boot order reset to HDD -> NIC" ((@((CallAt 4).Args['BootOrder']) -join ',') -eq 'HDD,NIC')

# ── (i) Power + teardown routing ────────────────────────────────────────────
Write-Host ""
Write-Host "=== Power / teardown ===" -ForegroundColor Cyan
Reset-Calls
Start-ConstructVm -Name $vmName
Stop-ConstructVm  -Name $vmName
Stop-ConstructVm  -Name $vmName -TurnOff -Force
Save-ConstructVm  -Name $vmName
Remove-ConstructVm -Name $vmName
ok "power: Start/Stop/Save/Remove route to the Hyper-V cmdlets + lib teardown" (
    ((CallNames) -join ',') -eq 'Start-VM,Stop-VM,Stop-VM,Save-VM,Remove-AgentVm')
ok "power: a hard stop passes -TurnOff" ([bool](CallAt 2).Args['TurnOff'])
ok "power: a graceful stop does not" (-not [bool](CallAt 1).Args['TurnOff'])
ok "teardown: Remove-ConstructVm delegates to lib Remove-AgentVm with the VM name" (
    (CallAt 4).Args['VmName'] -eq $vmName)

# ── (j) Prereqs routing ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Prereqs ===" -ForegroundColor Cyan
Reset-Calls
Ensure-ConstructDriverPrereqs
ok "prereqs: the default scope is Ensure-HyperV only (today's call sites)" (
    ((CallNames) -join ',') -eq 'Ensure-HyperV')
Reset-Calls
Ensure-ConstructDriverPrereqs -Scope HostAccess
ok "prereqs: -Scope HostAccess is the Hyper-V Administrators membership only" (
    ((CallNames) -join ',') -eq 'Add-HyperVAdminMembership')
Reset-Calls
Ensure-ConstructDriverPrereqs -Scope All
ok "prereqs: -Scope All does both, features first" (
    ((CallNames) -join ',') -eq 'Ensure-HyperV,Add-HyperVAdminMembership')
ok "prereqs: Test-ConstructDriverPrereqs is true when the Hyper-V cmdlets exist" (
    (Test-ConstructDriverPrereqs) -eq $true)

# ── (k) Checkpoints (capability-gated contract ops) ─────────────────────────
Write-Host ""
Write-Host "=== Checkpoints ===" -ForegroundColor Cyan

# Policy read: BOTH halves are probed. `Enabled` comes from the VM property (absent
# on pre-1709 Hyper-V -> $null = can't read), `Settable` from Set-VM's parameter set
# (absent there too -> the feature doesn't exist on this host). Getting either wrong
# is the "upgrade path" bug the panel's offer logic depends on.
$script:GetVmMode = 'Running'
function Get-VM {
    [CmdletBinding()] param([string]$Name)
    Record 'Get-VM' $PSBoundParameters
    if ($script:GetVmMode -eq 'missing') {
        $er = New-Object System.Management.Automation.ErrorRecord(
            (New-Object System.Management.Automation.ItemNotFoundException("no vm")),
            'InvalidParameter,Microsoft.HyperV.PowerShell.Commands.GetVM',
            [System.Management.Automation.ErrorCategory]::InvalidArgument, $null)
        throw $er
    }
    if ($script:GetVmMode -eq 'boom') { throw "permission denied" }
    if ($script:GetVmMode -eq 'noprop') { return [pscustomobject]@{ Name = $Name; State = 'Running' } }
    return [pscustomobject]@{ Name = $Name; State = $script:GetVmMode; AutomaticCheckpointsEnabled = $script:AutoChkValue }
}
# ONE lookup, not four: the whole preflight (existence + displayed state + policy)
# must cost exactly one backend call, like the single Get-VM the pre-driver script
# did -- otherwise the VM can change between the probes (the TOCTOU the old code
# didn't have).
$script:AutoChkValue = $true
Reset-Calls
$info = Get-ConstructVmCheckpointInfo -Name 'Agent-VM'
ok "info: the preflight calls Get-VM EXACTLY once" (((CallNames) -join ',') -eq 'Get-VM')
ok "info: existence, display state and policy all come from that one call" (
    $info.Present -eq $true -and $info.StateText -eq 'Running' -and
    $info.Enabled -eq $true -and $info.Settable -eq $true)
$script:AutoChkValue = $false
ok "info: the VM property is read (off)" ((Get-ConstructVmCheckpointInfo -Name 'Agent-VM').Enabled -eq $false)
$script:GetVmMode = 'noprop'
ok "info: pre-1709 Hyper-V (no property) -> Enabled null, NOT false" (
    $null -eq (Get-ConstructVmCheckpointInfo -Name 'Agent-VM').Enabled)
$script:GetVmMode = 'missing'
$gone = Get-ConstructVmCheckpointInfo -Name 'Agent-VM'
ok "info: no such VM -> Present false, no state text, no throw" (
    $gone.Present -eq $false -and $gone.StateText -eq '')
$script:GetVmMode = 'boom'
$blind = Get-ConstructVmCheckpointInfo -Name 'Agent-VM'
ok "info: an unreadable VM -> Present null ('can't tell'), Enabled null" (
    $null -eq $blind.Present -and $null -eq $blind.Enabled -and $blind.StateText -eq '')
$script:GetVmMode = 'Starting'
ok "info: a transient state is reported verbatim for display" (
    (Get-ConstructVmCheckpointInfo -Name 'Agent-VM').StateText -eq 'Starting')
$script:GetVmMode = 'Running'; $script:AutoChkValue = $false

# A Set-VM without the parameter (pre-1709) must report Settable = false rather than
# throwing a binding error at apply time.
function Set-VM { [CmdletBinding()] param([string]$Name, [int]$ProcessorCount) Record 'Set-VM' $PSBoundParameters }
ok "info: Set-VM without the parameter -> Settable false (version skew)" (
    (Get-ConstructVmCheckpointInfo -Name 'Agent-VM').Settable -eq $false)
function Set-VM { [CmdletBinding()] param([string]$Name, [int]$ProcessorCount, [switch]$StaticMemory, [string]$CheckpointType, [string]$AutomaticStartAction, [string]$AutomaticStopAction, [bool]$AutomaticCheckpointsEnabled) Record 'Set-VM' $PSBoundParameters }

Reset-Calls
Set-ConstructVmAutoCheckpointPolicy -Name 'Agent-VM' -Enabled $false
ok "policy: applying it is a single Set-VM with the policy parameter" (
    ((CallNames) -join ',') -eq 'Set-VM' -and
    (CallAt 0).Args['Name'] -eq 'Agent-VM' -and
    [bool](CallAt 0).Args['AutomaticCheckpointsEnabled'] -eq $false)

# Classification is the lib's; the driver is the contract entry point for it and must
# pass the list through unchanged (Enumerated = false means "could not read", which a
# caller must not turn into "there are none").
$snapA = [pscustomobject]@{ Name = 'Agent-VM - (1/1/2026 - 12:00:00)'; CreationTime = (Get-Date) }
$snapB = [pscustomobject]@{ Name = 'my own checkpoint'; CreationTime = (Get-Date) }
$script:CheckpointResult = @{ Enumerated = $true; Certain = @($snapA); Probable = @($snapB); All = @($snapA, $snapB) }
Reset-Calls
$found = Get-ConstructVmAutomaticCheckpoint -Name 'Agent-VM'
ok "checkpoints: routed to the lib classifier with the VM name" (
    ((CallNames) -join ',') -eq 'Get-AgentVmAutomaticCheckpoint' -and (CallAt 0).Args['VmName'] -eq 'Agent-VM')
ok "checkpoints: the classification is passed through unchanged" (
    $found.Enumerated -eq $true -and @($found.Certain).Count -eq 1 -and
    @($found.Probable).Count -eq 1 -and @($found.All).Count -eq 2)
$script:CheckpointResult = @{ Enumerated = $false; Certain = @(); Probable = @(); All = @() }
ok "checkpoints: an unreadable list stays Enumerated = false" (
    (Get-ConstructVmAutomaticCheckpoint -Name 'Agent-VM').Enumerated -eq $false)

Reset-Calls
Remove-ConstructVmCheckpoint -Name 'Agent-VM' -Checkpoint $snapA
ok "checkpoints: removal is BY OBJECT (never -Name: duplicates are allowed)" (
    ((CallNames) -join ',') -eq 'Remove-VMSnapshot' -and
    [object]::ReferenceEquals((CallAt 0).Args['VMSnapshot'], $snapA) -and
    -not (CallAt 0).Args.ContainsKey('Name'))
ok "checkpoints: removal is unattended (-Confirm:`$false)" (
    (CallAt 0).Args.ContainsKey('Confirm') -and -not [bool](CallAt 0).Args['Confirm'])

# ── (l) Wait-ConstructVmReachable ───────────────────────────────────────────
Write-Host ""
Write-Host "=== Wait-ConstructVmReachable ===" -ForegroundColor Cyan
# Override the driver's socket probe (same scope) so no real connect is attempted.
$script:portOpen = $true
$script:probed = New-Object System.Collections.Generic.List[object]
function Test-ConstructVmSshPort {
    [CmdletBinding()] param([string]$SshHost, [int]$SshPort = 22, [int]$TimeoutMs = 3000)
    $script:probed.Add("$SshHost`:$SshPort")
    return $script:portOpen
}
$reached = Wait-ConstructVmReachable -Name 'Agent-VM' -TimeoutSeconds 1200 -PollIntervalSeconds 0 -SettleSeconds 0
ok "wait: returns true once the port is open" ($reached -eq $true)
ok "wait: probes the driver's endpoint (agent-vm.mshome.net:22)" ($script:probed[0] -eq 'agent-vm.mshome.net:22')

$script:portOpen = $false
$reached = Wait-ConstructVmReachable -Name 'Agent-VM' -TimeoutSeconds 0 -PollIntervalSeconds 0 -SettleSeconds 0
ok "wait: an expired wait is NON-FATAL and returns false" ($reached -eq $false)
$script:portOpen = $true

# ── (m) Caller purity: no direct hypervisor calls left outside the driver ───
Write-Host ""
Write-Host "=== Caller purity (AST) ===" -ForegroundColor Cyan

# Walk each caller's AST and collect every command it INVOKES (CommandAst), then
# assert none of them is a hypervisor cmdlet. A regex over the source can't do this
# honestly -- it can't tell an invocation from a comment, a string, or a same-shaped
# helper name like Test-VmReachable -- so the check is done on parsed commands.
$hypervCommands = @(
    'Get-VM','New-VM','Set-VM','Start-VM','Stop-VM','Save-VM','Restart-VM','Suspend-VM','Resume-VM',
    'Remove-VM','Rename-VM','Export-VM','Import-VM','Measure-VM','Compare-VM','Repair-VM','Debug-VM',
    'Checkpoint-VM','Get-VMSnapshot','Remove-VMSnapshot','Restore-VMSnapshot','Rename-VMSnapshot',
    'Get-VMDvdDrive','Set-VMDvdDrive','Add-VMDvdDrive','Remove-VMDvdDrive',
    'Get-VMHardDiskDrive','Add-VMHardDiskDrive','Remove-VMHardDiskDrive','Set-VMHardDiskDrive',
    'Get-VMNetworkAdapter','Add-VMNetworkAdapter','Remove-VMNetworkAdapter','Set-VMNetworkAdapter',
    'Connect-VMNetworkAdapter','Disconnect-VMNetworkAdapter',
    'Get-VMFirmware','Set-VMFirmware','Get-VMBios','Set-VMBios',
    'Get-VMProcessor','Set-VMProcessor','Get-VMMemory','Set-VMMemory',
    'Get-VMSwitch','New-VMSwitch','Remove-VMSwitch','Set-VMSwitch',
    'Get-VMHost','Set-VMHost','Get-VMIntegrationService','Enable-VMIntegrationService',
    'Get-VHD','New-VHD','Remove-VHD','Set-VHD','Resize-VHD','Merge-VHD','Convert-VHD','Mount-VHD','Dismount-VHD'
)
function Get-InvokedCommandNames {
    param([string]$Path)
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$errors)
    $cmds = $ast.FindAll({ param($a) $a -is [System.Management.Automation.Language.CommandAst] }, $true)
    return @($cmds | ForEach-Object { $_.GetCommandName() } | Where-Object { $_ })
}
foreach ($rel in @("Create-AgentVM.ps1", "Auto-Install.ps1", "Set-AgentVmCheckpoints.ps1")) {
    $invoked = Get-InvokedCommandNames -Path (Join-Path $repoRoot $rel)
    $leaks = @($invoked | Where-Object { $hypervCommands -contains $_ } | Sort-Object -Unique)
    ok "purity: $rel invokes no hypervisor cmdlet directly" ($leaks.Count -eq 0)
    if ($leaks.Count -gt 0) { Write-Host "    leaked: $($leaks -join ', ')" -ForegroundColor Red }
}
# The one deliberate exception, asserted so it can't disappear unnoticed: the manual
# (non-autoinstall) branch still opens a VM console with vmconnect.exe, which is a
# local-console launch, not a hypervisor management call.
$createInvoked = Get-InvokedCommandNames -Path (Join-Path $repoRoot "Create-AgentVM.ps1")
ok "purity: the manual-install vmconnect launch is the only hypervisor-adjacent call left" (
    $createInvoked -contains 'vmconnect.exe')
# Auto-Install's Test-VmReachable is network-only (a raw socket to the driver's
# endpoint), so it stays a plain function rather than a driver op.
$autoInvoked = Get-InvokedCommandNames -Path (Join-Path $repoRoot "Auto-Install.ps1")
ok "purity: Auto-Install still uses its network-only reachability helper" (
    $autoInvoked -contains 'Test-VmReachable')

# ── (n) Zero-change wiring in the callers ───────────────────────────────────
Write-Host ""
Write-Host "=== Caller wiring ===" -ForegroundColor Cyan
$createSrc = Get-Content -LiteralPath (Join-Path $repoRoot "Create-AgentVM.ps1") -Raw
ok "Create-AgentVM: loads the driver through the loader" ($createSrc -match 'Load-ConstructDriver\.ps1')
ok "Create-AgentVM: -Backend defaults to hyperv-local" ($createSrc -match '\$Backend\s*=\s*"hyperv-local"')
ok "Create-AgentVM: the existence probe is the three-valued driver one" (
    $createSrc -match "Test-ConstructVmPresent -Name \`$VmName\) -eq \`$true")

$autoSrc = Get-Content -LiteralPath (Join-Path $repoRoot "Auto-Install.ps1") -Raw
ok "Auto-Install: Hyper-V touches routed through the driver" (
    $autoSrc -match 'Load-ConstructDriver\.ps1' -and
    $autoSrc -match 'Test-ConstructVmPresent -Name \$HyperVmName\) -eq \$true' -and
    $autoSrc -match 'Remove-ConstructVm -Name \$HyperVmName' -and
    $autoSrc -match 'Ensure-ConstructDriverPrereqs')
ok "Auto-Install: the 'can this host drive the backend' guard is the driver's" (
    $autoSrc -match '\(Test-ConstructDriverPrereqs\)')
ok "Auto-Install: the reachability probe asks the driver for the endpoint" (
    $autoSrc -match 'Get-ConstructVmEndpoint')

$chkSrc = Get-Content -LiteralPath (Join-Path $repoRoot "Set-AgentVmCheckpoints.ps1") -Raw
ok "Set-AgentVmCheckpoints: capability-gated + every checkpoint op is a driver op" (
    $chkSrc -match 'Get-ConstructDriverCapabilities' -and
    $chkSrc -match 'if \(-not \$caps\.Checkpoints\)' -and
    $chkSrc -match 'Get-ConstructVmCheckpointInfo -Name \$VmName' -and
    $chkSrc -match 'Set-ConstructVmAutoCheckpointPolicy -Name \$VmName -Enabled \$wantEnabled' -and
    $chkSrc -match 'Get-ConstructVmAutomaticCheckpoint -Name \$VmName' -and
    $chkSrc -match 'Remove-ConstructVmCheckpoint -Name \$VmName -Checkpoint \$snap')
ok "Set-AgentVmCheckpoints: the safe-delete contract is intact (per-checkpoint prompt, loud unreadable list)" (
    $chkSrc -match 'Read-Host "      Remove this checkpoint\?' -and
    $chkSrc -match 'if \(-not \$found\.Enumerated\)')

# The destructive invariant, asserted on the AST rather than on "both strings exist":
# EVERY actual Remove-ConstructVmCheckpoint invocation must sit after the
# `if (-not $found.Enumerated) { throw ... }` guard, so a checkpoint list we could not
# read can never reach a deletion. (test/host-lib.test.ps1 pins the same ordering by
# text, but it is outside this package's ownership and still anchors on the old call
# name -- this is the owned check.)
$chkErrors = $null
$chkAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot "Set-AgentVmCheckpoints.ps1"), [ref]$null, [ref]$chkErrors)
$guardIf = @($chkAst.FindAll({
    param($a) $a -is [System.Management.Automation.Language.IfStatementAst] -and
              $a.Clauses[0].Item1.Extent.Text -match '\$found\.Enumerated'
}, $true))
$guardThrows = @()
if ($guardIf.Count -eq 1) {
    $guardThrows = @($guardIf[0].FindAll({
        param($a) $a -is [System.Management.Automation.Language.ThrowStatementAst] }, $true))
}
$removals = @($chkAst.FindAll({
    param($a) $a -is [System.Management.Automation.Language.CommandAst] -and
              $a.GetCommandName() -eq 'Remove-ConstructVmCheckpoint'
}, $true))
ok "safe delete: the unreadable-list guard exists and throws" (
    $guardIf.Count -eq 1 -and $guardThrows.Count -eq 1)
ok "safe delete: both removal call sites are present (certain loop + prompted probable)" (
    $removals.Count -eq 2)
ok "safe delete: EVERY removal is invoked after the guard throws" (
    $guardIf.Count -eq 1 -and $removals.Count -gt 0 -and
    @($removals | Where-Object { $_.Extent.StartOffset -le $guardIf[0].Extent.EndOffset }).Count -eq 0)

# ...and the second one only inside the per-checkpoint "yes" branch, so a name-matched
# checkpoint is never deleted without the user typing it.
$yesIf = @($chkAst.FindAll({
    param($a) $a -is [System.Management.Automation.Language.IfStatementAst] -and
              $a.Clauses[0].Item1.Extent.Text -match '\$answer -eq "yes"'
}, $true))
ok "safe delete: the probable-checkpoint removal is inside the typed-'yes' branch only" (
    $yesIf.Count -eq 1 -and
    @($removals | Where-Object {
        $_.Extent.StartOffset -gt $yesIf[0].Extent.StartOffset -and
        $_.Extent.EndOffset   -lt $yesIf[0].Extent.EndOffset }).Count -eq 1)

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "  $($script:pass) passed, $($script:fail) failed" -ForegroundColor $(if ($script:fail -gt 0) { "Red" } else { "Green" })
Write-Host "==============================" -ForegroundColor Cyan

if ($script:fail -gt 0) { exit 1 }
exit 0
