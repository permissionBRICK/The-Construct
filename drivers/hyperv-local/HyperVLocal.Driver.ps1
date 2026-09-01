#Requires -Version 5.1
<#
.SYNOPSIS
    Construct hypervisor driver for LOCAL Hyper-V (backend "hyperv-local").

.DESCRIPTION
    Every Hyper-V-specific operation The Construct performs on the user's own PC
    lives here, behind the driver contract documented in docs/drivers.md, so a
    remote-Hyper-V driver (and later a Proxmox driver) can slot in without the
    calling scripts changing.

    This file is DOT-SOURCED, not imported as a module -- like lib\AgentVm.Common.ps1
    -- and is loaded through drivers\Load-ConstructDriver.ps1:

        . (Join-Path $PSScriptRoot "drivers\Load-ConstructDriver.ps1") -Backend "hyperv-local"

    Two host-side dependencies come from lib\AgentVm.Common.ps1 (dot-source it
    first): Ensure-HyperV, Add-HyperVAdminMembership and Remove-AgentVm. They stay
    in the shared lib -- this driver only routes to them, so the version-skew and
    disk-chain handling they carry is not duplicated.

    Behaviour note: the code here was MOVED out of Create-AgentVM.ps1 unchanged --
    same cmdlets, same arguments, same order, same messages -- so an existing local
    install cannot notice the extraction. The progress lines are printed with the
    host script's Write-Step/Write-Ok/Write-Note (the repo idiom; lib's
    Remove-AgentVm does the same), with plain fallbacks defined below only when the
    host doesn't provide them.
#>

# ── Progress output ──────────────────────────────────────────────────────────
# Defined ONLY if the host script hasn't already (Create-AgentVM.ps1,
# Auto-Install.ps1 and Set-AgentVmCheckpoints.ps1 all define their own), so the
# driver is dot-sourceable on its own without changing anybody's output.
if (-not (Get-Command Write-Step -ErrorAction SilentlyContinue)) {
    function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
}
if (-not (Get-Command Write-Ok -ErrorAction SilentlyContinue)) {
    function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
}
if (-not (Get-Command Write-Note -ErrorAction SilentlyContinue)) {
    function Write-Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }
}

# ── Capabilities ─────────────────────────────────────────────────────────────

function Get-ConstructDriverCapabilities {
    <#
        What this backend can do. Callers gate optional features on these flags
        instead of testing the backend name (docs/drivers.md).
          Checkpoints : Hyper-V snapshots + the automatic-checkpoint policy
          Console     : 'vmconnect' | a URL | 'none'
          Suspend     : Save-VM / resume-on-start (state 'saved')
    #>
    [CmdletBinding()]
    param()
    return @{
        Checkpoints = $true
        Console     = 'vmconnect'
        Suspend     = $true
        Backend     = 'hyperv-local'
    }
}

# ── Prerequisites ────────────────────────────────────────────────────────────

function Test-ConstructDriverPrereqs {
    <#
        Can this host drive the backend right now? For local Hyper-V that is
        simply "the Hyper-V cmdlets are present" -- a cheap, non-elevating,
        never-throwing probe. Ensure-ConstructDriverPrereqs is what actually
        installs/enables anything.
    #>
    [CmdletBinding()]
    param()
    return [bool](Get-Command Get-VM -ErrorAction SilentlyContinue)
}

function Ensure-ConstructDriverPrereqs {
    <#
        Bring the host to a state where the backend can be used. Assumes the
        caller is already elevated.

        -Scope Platform   (default) validate hardware virtualization + enable the
                          Hyper-V Windows features, rebooting if required
                          (lib Ensure-HyperV).
        -Scope HostAccess add the desktop user to "Hyper-V Administrators" so the
                          non-elevated control panel can read VM state without a
                          UAC prompt (lib Add-HyperVAdminMembership; best-effort,
                          never throws).
        -Scope All        both, in that order.

        The default is Platform so existing call sites behave exactly as before.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('Platform', 'HostAccess', 'All')]
        [string]$Scope = 'Platform'
    )
    if ($Scope -eq 'Platform' -or $Scope -eq 'All') { Ensure-HyperV }
    if ($Scope -eq 'HostAccess' -or $Scope -eq 'All') { Add-HyperVAdminMembership }
}

# ── VM lifecycle ─────────────────────────────────────────────────────────────

function Resolve-ConstructVmMemoryBytes {
    <#
        Startup RAM in bytes from a descriptor, aligned DOWN to Hyper-V's 2 MB
        boundary. `MemoryBytes` wins over `MemoryGB` so a caller that already
        computed (and aligned) an exact byte count -- Create-AgentVM.ps1 does --
        keeps it bit-for-bit instead of round-tripping through a double.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Descriptor)

    $bytes = [long]0
    if ($Descriptor.ContainsKey('MemoryBytes') -and $Descriptor['MemoryBytes']) {
        $bytes = [long]$Descriptor['MemoryBytes']
    } elseif ($Descriptor.ContainsKey('MemoryGB') -and $Descriptor['MemoryGB']) {
        $bytes = [long]([double]$Descriptor['MemoryGB'] * 1GB)
    }
    if ($bytes -le 0) { throw "New-ConstructVm: the descriptor needs MemoryBytes or MemoryGB." }
    return ($bytes - ($bytes % 2MB))
}

function Resolve-ConstructVmDiskBytes {
    <#
        Virtual-disk size in bytes from a descriptor (`DiskBytes` wins over
        `DiskGB`).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Descriptor)

    $bytes = [long]0
    if ($Descriptor.ContainsKey('DiskBytes') -and $Descriptor['DiskBytes']) {
        $bytes = [long]$Descriptor['DiskBytes']
    } elseif ($Descriptor.ContainsKey('DiskGB') -and $Descriptor['DiskGB']) {
        $bytes = [long]([double]$Descriptor['DiskGB'] * 1GB)
    }
    if ($bytes -le 0) { throw "New-ConstructVm: the descriptor needs DiskBytes or DiskGB." }
    return $bytes
}

function New-ConstructVm {
    <#
        Create AND configure a VM from a descriptor hashtable. This is the code
        that used to sit inline in Create-AgentVM.ps1 sections 6 and 7, moved
        verbatim (same cmdlets, arguments, order and messages).

        Descriptor fields (docs/drivers.md has the table):
          Name                 (required) VM display name
          MemoryBytes|MemoryGB (required) startup RAM; bytes wins, aligned to 2 MB
          DiskBytes|DiskGB     (required) new VHD size
          ProcessorCount       (required) vCPUs
          VhdPath              new VHD path (default: Hyper-V's default folder\<Name>.vhdx)
          SwitchName           virtual switch (default "Default Switch")
          Generation           1 | 2 (default 2)
          IsoPath              install media to attach; boot order DVD -> HDD -> NIC
          Nested               expose virtualization extensions (default $true)
          AutomaticCheckpoints Hyper-V's snapshot-at-every-start policy (default $false)
          CheckpointType       default "Standard"
          AutomaticStartAction default "StartIfRunning"
          AutomaticStopAction  default "Save"

        The VM is left OFF: starting it is Start-ConstructVm, so the caller keeps
        control of when (and with what message) that happens.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Descriptor)

    $name = [string]$Descriptor['Name']
    if ([string]::IsNullOrWhiteSpace($name)) { throw "New-ConstructVm: the descriptor needs a Name." }

    $generation = if ($Descriptor.ContainsKey('Generation') -and $Descriptor['Generation']) { [int]$Descriptor['Generation'] } else { 2 }
    $switchName = if ($Descriptor['SwitchName']) { [string]$Descriptor['SwitchName'] } else { "Default Switch" }
    $vhdPath    = if ($Descriptor['VhdPath'])    { [string]$Descriptor['VhdPath'] }
                  else { "C:\ProgramData\Microsoft\Windows\Virtual Hard Disks\$name.vhdx" }
    $isoPath    = if ($Descriptor['IsoPath'])    { [string]$Descriptor['IsoPath'] } else { "" }
    $checkpointType = if ($Descriptor['CheckpointType']) { [string]$Descriptor['CheckpointType'] } else { "Standard" }
    $autoStart  = if ($Descriptor['AutomaticStartAction']) { [string]$Descriptor['AutomaticStartAction'] } else { "StartIfRunning" }
    $autoStop   = if ($Descriptor['AutomaticStopAction'])  { [string]$Descriptor['AutomaticStopAction'] }  else { "Save" }
    # Absent means "on" for Nested and "off" for AutomaticCheckpoints -- the values
    # Create-AgentVM.ps1 has always passed.
    $nested          = if ($Descriptor.ContainsKey('Nested')) { [bool]$Descriptor['Nested'] } else { $true }
    $autoCheckpoints = if ($Descriptor.ContainsKey('AutomaticCheckpoints')) { [bool]$Descriptor['AutomaticCheckpoints'] } else { $false }

    $processorCount = 0
    if ($Descriptor.ContainsKey('ProcessorCount')) { $processorCount = [int]$Descriptor['ProcessorCount'] }
    if ($processorCount -lt 1) { throw "New-ConstructVm: the descriptor needs a ProcessorCount of at least 1." }

    $memoryBytes = Resolve-ConstructVmMemoryBytes -Descriptor $Descriptor
    $diskBytes   = Resolve-ConstructVmDiskBytes   -Descriptor $Descriptor

    New-VM -Name $name `
           -Generation $generation `
           -MemoryStartupBytes $memoryBytes `
           -SwitchName $switchName `
           -NewVHDPath $vhdPath `
           -NewVHDSizeBytes $diskBytes | Out-Null

    Write-Ok "VM created"

    Write-Step "Configuring VM settings"

    Set-VM -Name $name `
           -ProcessorCount $processorCount `
           -StaticMemory `
           -CheckpointType $checkpointType `
           -AutomaticStartAction $autoStart `
           -AutomaticStopAction $autoStop `
           -AutomaticCheckpointsEnabled $autoCheckpoints

    Write-Ok "Processors: $processorCount, Dynamic Memory: off, Checkpoint: $checkpointType"
    Write-Ok "Automatic checkpoints: $(if ($autoCheckpoints) { 'on' } else { 'off' })"

    # Expose the host CPU's virtualization extensions to the guest (nested
    # virtualization) so the agent can use KVM/QEMU, containers with gVisor/Kata,
    # Android emulators, etc. inside the VM. Must be set while the VM is off, so it
    # goes here between New-VM and Start-VM; the static memory set above is already
    # what nested virtualization requires. Unsupported hosts (old Hyper-V builds,
    # some AMD/ARM configurations) throw -- treat that as non-fatal and continue
    # without nested virtualization rather than failing the whole install.
    if ($nested) {
        try {
            Set-VMProcessor -VMName $name -ExposeVirtualizationExtensions $true -ErrorAction Stop
            Write-Ok "Nested virtualization: on"
        } catch {
            Write-Warning "Nested virtualization not enabled (host doesn't support it?): $($_.Exception.Message)"
        }
    }

    if ($generation -ge 2) {
        # Disable Secure Boot (required for Ubuntu without Microsoft UEFI keys)
        Set-VMFirmware -VMName $name -EnableSecureBoot Off
        Write-Ok "Secure Boot: off"
    }

    if ($isoPath) {
        # Attach the ISO as a DVD drive on the SCSI controller
        Add-VMDvdDrive -VMName $name -ControllerNumber 0 -ControllerLocation 1 -Path $isoPath
        Write-Ok "ISO attached on SCSI 0:1"
    }

    if ($generation -ge 2) {
        if ($isoPath) {
            # Set boot order: DVD first, then hard drive, then network
            $dvd  = Get-VMDvdDrive  -VMName $name
            $hdd  = Get-VMHardDiskDrive -VMName $name
            $nic  = Get-VMNetworkAdapter -VMName $name
            Set-VMFirmware -VMName $name -BootOrder $dvd, $hdd, $nic
            Write-Ok "Boot order: DVD -> HDD -> Network"
        } else {
            # No install media: boot straight off the disk.
            $hdd  = Get-VMHardDiskDrive -VMName $name
            $nic  = Get-VMNetworkAdapter -VMName $name
            Set-VMFirmware -VMName $name -BootOrder $hdd, $nic
            Write-Ok "Boot order: HDD -> Network"
        }
    }
}

function Remove-ConstructVm {
    <#
        Tear the VM down including its disk chain. The work (power-off, VHD chain
        resolution, checkpoint-merge wait, retrying deletes) stays in lib's
        Remove-AgentVm; this is the contract entry point for it. No-op if the VM
        does not exist.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Remove-AgentVm -VmName $Name
}

function Start-ConstructVm {
    <#
        Power the VM on. Hyper-V's Start-VM also RESUMES a saved or paused VM,
        which is what the "suspend" capability relies on.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Start-VM -Name $Name
}

function Stop-ConstructVm {
    <#
        Shut the VM down. -TurnOff is the hard power-cut (no guest shutdown);
        without it Hyper-V asks the guest to shut down cleanly.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [switch]$TurnOff,
        [switch]$Force
    )
    if ($TurnOff) { Stop-VM -Name $Name -TurnOff -Force:$Force }
    else          { Stop-VM -Name $Name -Force:$Force }
}

function Save-ConstructVm {
    <#
        Suspend to disk: the VM's state is written out and its RAM is freed;
        Start-ConstructVm resumes it transparently. Capability: Suspend.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    Save-VM -Name $Name
}

function Get-ConstructVmState {
    <#
        The VM's power state as one of the contract's values:

            running | off | paused | saved | absent | unknown

        'absent' means the backend positively reported "no such VM" -- for Hyper-V
        that is Get-VM's FullyQualifiedErrorId starting with "InvalidParameter",
        the same discriminator extension/src/drivers/hyperv-local.js uses. Any
        OTHER failure (Hyper-V not installed, the permission gate, a transient
        state) is 'unknown': callers must treat that as "can't tell", never as
        "not installed".
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $vm = $null
    try {
        $vm = Get-VM -Name $Name -ErrorAction Stop
    } catch {
        if ($_.FullyQualifiedErrorId -like 'InvalidParameter*') { return 'absent' }
        return 'unknown'
    }
    if (-not $vm) { return 'absent' }
    $vm = @($vm)[0]
    $state = ""
    try { $state = ([string]$vm.State).Trim().ToLowerInvariant() } catch { $state = "" }
    switch ($state) {
        'running' { return 'running' }
        'off'     { return 'off' }
        'paused'  { return 'paused' }
        'saved'   { return 'saved' }
        default   { return 'unknown' }   # Starting/Stopping/Saving/... or unreadable
    }
}

function Test-ConstructVmPresent {
    <#
        Does this VM exist? THREE-VALUED on purpose:

            $true   the backend positively reports the VM (in ANY state, including
                    a transient one like Starting/Saving)
            $false  the backend positively reports "no such VM"
            $null   can't tell -- backend unreachable, permission gate, no Hyper-V

        That is exactly what `Get-VM -Name X -ErrorAction SilentlyContinue`
        returned (object / $null / $null), which is why the existing call sites can
        keep behaving identically by testing `-eq $true`. Get-ConstructVmState's
        enum deliberately can't express it: it collapses transient states into
        'unknown', so "unknown" there means both "mid-transition" and "unreadable".
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $vm = $null
    try {
        $vm = Get-VM -Name $Name -ErrorAction Stop
    } catch {
        if ($_.FullyQualifiedErrorId -like 'InvalidParameter*') { return $false }
        return $null
    }
    if (-not $vm) { return $false }
    return $true
}

function Get-ConstructVmEndpoint {
    <#
        Where a client dials this VM: @{ SshHost = ...; SshPort = ... }.

        THE key abstraction of the contract -- everything downstream (the
        provisioner, the extension's SSH, probes, usage) must ask for an endpoint
        instead of rebuilding a name convention. For local Hyper-V the Default
        Switch's NAT DNS gives every VM "<name>.mshome.net" on port 22.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    return @{
        SshHost = "$($Name.ToLower()).mshome.net"
        SshPort = 22
    }
}

# ── Checkpoints (capability: Checkpoints) ────────────────────────────────────
# Only meaningful when Get-ConstructDriverCapabilities().Checkpoints is $true;
# callers must gate on that flag rather than on the backend id.

function Get-ConstructVmCheckpointInfo {
    <#
        ONE inspection call for everything the checkpoint flow needs to decide:

            @{ Present   = $true | $false | $null   # as Test-ConstructVmPresent
               StateText = "Running" | ""           # backend's own text, DISPLAY only
               Enabled   = $true | $false | $null   # $null = policy not readable
               Settable  = $true | $false }         # can the policy be changed at all?

        Deliberately ONE backend lookup, not four: the pre-driver script fetched the
        VM once and read state + policy off that single object, so splitting it would
        both cost extra round trips and open a TOCTOU window (the VM disappearing, or
        permissions changing, between the presence probe and the policy read) that the
        original code did not have. Callers consume this snapshot.

        Enabled and Settable are PROBED, not assumed -- Hyper-V builds predating
        automatic checkpoints (Server 2016 / pre-1709) expose neither the VM property
        (defensive PSObject.Properties lookup, not a hard reference) nor the Set-VM
        parameter, and there the feature simply doesn't exist.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $present   = $null
    $stateText = ""
    $enabled   = $null

    $vm = $null
    try {
        $vm = Get-VM -Name $Name -ErrorAction Stop
        if ($vm) { $present = $true } else { $present = $false }
    } catch {
        # Same discriminator as Get-ConstructVmState: only Hyper-V's "no such VM"
        # counts as a positive absence; anything else is "can't tell" ($null).
        if ($_.FullyQualifiedErrorId -like 'InvalidParameter*') { $present = $false } else { $present = $null }
        $vm = $null
    }

    if ($vm) {
        $vm = @($vm)[0]
        try { $stateText = [string]$vm.State } catch { $stateText = "" }
        $prop = $vm.PSObject.Properties['AutomaticCheckpointsEnabled']
        if ($prop -and $null -ne $prop.Value) { $enabled = [bool]$prop.Value }
    }

    $setVmCmd = Get-Command Set-VM -ErrorAction SilentlyContinue
    $settable = ($setVmCmd -and $setVmCmd.Parameters.ContainsKey('AutomaticCheckpointsEnabled'))

    return @{
        Present   = $present
        StateText = $stateText
        Enabled   = $enabled
        Settable  = [bool]$settable
    }
}

function Set-ConstructVmAutoCheckpointPolicy {
    <#
        Apply the automatic-checkpoint policy. Throws on failure (the caller
        reports it). It is a VM-level policy, not virtual hardware, so it can be
        set while the VM is running; it takes effect at the next start. Call only
        when Get-ConstructVmAutoCheckpointPolicy reported Settable.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][bool]$Enabled
    )
    Set-VM -Name $Name -AutomaticCheckpointsEnabled $Enabled -ErrorAction Stop
}

function Get-ConstructVmAutomaticCheckpoint {
    <#
        The VM's checkpoints, classified by how sure we are that the backend --
        not a human -- created them:

            @{ Enumerated = $true|$false   # could the list be read at all?
               Certain    = @(...)         # the backend's own "this is automatic" flag
               Probable   = @(...)         # name-matched only; NEVER delete unasked
               All        = @(...) }

        Each checkpoint object carries at least Name and CreationTime and is
        accepted back by Remove-ConstructVmCheckpoint. Enumerated = $false means
        "could not read", which a caller must not report as "there are none".

        The classification itself (Hyper-V's IsAutomaticSnapshot flag via WMI /
        the snapshot object, with the name-pattern fallback) stays in
        lib\AgentVm.Common.ps1; this is the contract entry point for it.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    return (Get-AgentVmAutomaticCheckpoint -VmName $Name)
}

function Remove-ConstructVmCheckpoint {
    <#
        Delete one checkpoint, BY OBJECT -- pass back an entry from
        Get-ConstructVmAutomaticCheckpoint. Never by name: Hyper-V allows
        duplicate checkpoint names, and -Name would remove every one of them.
        Throws on failure.

        (Hyper-V then merges the removed checkpoint's differencing disk into the
        base in the background.)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$Checkpoint
    )
    Remove-VMSnapshot -VMSnapshot $Checkpoint -Confirm:$false -ErrorAction Stop
}

function Test-ConstructVmSshPort {
    <#
        Is the VM's SSH port open right now?

        We probe with a raw TcpClient rather than Test-NetConnection: during the
        autoinstall wait the VM name isn't resolvable yet, and Test-NetConnection
        emits progress + name-resolution/ping banners that $ProgressPreference and
        -WarningAction don't fully silence. A bare socket connect with a short
        timeout is completely silent and tests exactly what we care about. Any
        failure (no DNS, refused, timeout) is swallowed and reported as "not open".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SshHost,
        [int]$SshPort = 22,
        [int]$TimeoutMs = 3000
    )
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect($SshHost, $SshPort, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne($TimeoutMs)) {
            $client.EndConnect($iar)   # throws if the connect actually failed
            return $true
        }
        return $false
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Wait-ConstructVmReachable {
    <#
        Wait until the VM answers on its SSH endpoint, then settle briefly.
        Returns $true if the port opened, $false if the wait expired.

        We deliberately do NOT verify SSH *login* here. Authenticating with the
        bootstrap key from Windows requires locked-down file ACLs (Windows OpenSSH
        silently ignores keys with too-open permissions) -- that handling lives in
        Provision-AgentVM.ps1, which also has its own reachability wait and a
        password fallback. Here we only need to know the unattended install has
        finished and the VM is back up, which we detect purely from the SSH port.

        SSH typically comes up only ONCE -- when the freshly installed OS boots
        after the unattended install -- not during the install. So we just wait
        for the port to open, give it a short settle, and hand off. No
        reboot-detection loop, and NO HARD ERROR: the wait is bounded, and if it
        expires the caller proceeds anyway (Provision-AgentVM.ps1 has its own
        reachability wait + retries, and tolerates the VM being mid-reboot).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$TimeoutSeconds = 1200,
        [int]$PollIntervalSeconds = 15,
        [int]$SettleSeconds = 20,
        [int]$ProbeTimeoutMs = 3000
    )

    $ep       = Get-ConstructVmEndpoint -Name $Name
    $sshHost  = [string]$ep.SshHost
    $sshPort  = [int]$ep.SshPort
    $reached  = $true
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    Write-Host "    Waiting for SSH port to open..." -ForegroundColor DarkGray
    while (-not (Test-ConstructVmSshPort -SshHost $sshHost -SshPort $sshPort -TimeoutMs $ProbeTimeoutMs)) {
        if ((Get-Date) -gt $deadline) {
            Write-Note "SSH still not reachable after $([int]([math]::Round($TimeoutSeconds / 60))) min; handing off to provisioning anyway."
            $reached = $false
            break
        }
        Write-Host "    Not reachable yet -- retrying in $PollIntervalSeconds seconds..." -ForegroundColor DarkGray
        Start-Sleep -Seconds $PollIntervalSeconds
    }

    # Wait a little and then end, regardless of whether the VM happens to reboot
    # in the meantime -- provisioning re-checks reachability before it connects.
    if ($SettleSeconds -gt 0) { Start-Sleep -Seconds $SettleSeconds }
    return $reached
}

function Detach-ConstructInstallMedia {
    <#
        Post-install cleanup: unmount the ISO, remove the DVD drive, and reset the
        boot order to HDD -> Network. (Moved from Create-AgentVM.ps1 section 9;
        "Detach" is not one of PowerShell's approved verbs but it IS the contract
        name -- see docs/drivers.md.)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$ControllerNumber = 0,
        [int]$ControllerLocation = 1
    )

    Set-VMDvdDrive -VMName $Name -ControllerNumber $ControllerNumber -ControllerLocation $ControllerLocation -Path $null
    Write-Ok "ISO unmounted"

    Remove-VMDvdDrive -VMName $Name -ControllerNumber $ControllerNumber -ControllerLocation $ControllerLocation
    Write-Ok "DVD drive removed"

    $hdd = Get-VMHardDiskDrive -VMName $Name
    $nic = Get-VMNetworkAdapter -VMName $Name
    Set-VMFirmware -VMName $Name -BootOrder $hdd, $nic
    Write-Ok "Boot order updated: HDD -> Network"
}
