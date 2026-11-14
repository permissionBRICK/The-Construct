#Requires -Version 5.1
<#
    The Hyper-V driver of constructd: the PowerShell the service runs for its primary-VM
    operations (service/src/Constructd.Windows/HyperV/HyperVScript.cs). It ships inside
    the service package (hyperv\ next to the executable) and is dot-sourced from there,
    so the service needs no Construct checkout.

    Every function below was moved unchanged from the client's driver
    (drivers/hyperv-local/HyperVLocal.Driver.ps1) and, for the removal, from
    lib/AgentVm.Common.ps1; only what the service calls came along.
    service/tests/hyperv-scripts.test.ps1 holds the copies to their sources while
    those still exist. Get-ConstructVmFacts at the end is the service's own.
#>

# ── Progress output ──────────────────────────────────────────────────────────
if (-not (Get-Command Write-Step -ErrorAction SilentlyContinue)) {
    function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
}
if (-not (Get-Command Write-Ok -ErrorAction SilentlyContinue)) {
    function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
}
if (-not (Get-Command Write-Note -ErrorAction SilentlyContinue)) {
    function Write-Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }
}

# ── Driver contract (from drivers/hyperv-local/HyperVLocal.Driver.ps1) ─────────

function Get-ConstructDriverCapabilities {
    <#
        What this backend can do. Callers gate optional features on these flags
        instead of testing the backend name (docs/drivers.md).
          Checkpoints : Hyper-V snapshots + the automatic-checkpoint policy
          Console     : 'vmconnect' | a URL | 'none'
          Suspend     : Save-VM / resume-on-start (state 'saved')
          Resources   : Set-ConstructVmMemory / Set-ConstructVmCpuCount on an EXISTING
                        (powered-off) VM -- what Set-AgentVmResources.ps1 drives
    #>
    [CmdletBinding()]
    param()
    return @{
        Checkpoints = $true
        Console     = 'vmconnect'
        Suspend     = $true
        Resources   = $true
        Backend     = 'hyperv-local'
    }
}

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

function Get-ConstructVmNested {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    [bool](Get-VMProcessor -VMName $Name -ErrorAction Stop).ExposeVirtualizationExtensions
}

function Set-ConstructVmNested {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][bool]$Enabled)
    $vm = Get-VM -Name $Name -ErrorAction Stop
    if ($vm.State -ne 'Off') { throw "VM '$Name' must be off to change nested virtualization." }
    Set-VMProcessor -VM $vm -ExposeVirtualizationExtensions $Enabled -ErrorAction Stop
}

function Set-ConstructVmCpuCount {
    <#
        Change the vCPU count of an EXISTING VM (capability: Resources). Virtual
        hardware, so the VM must be Off; the count is read back so a silently
        ignored write is reported instead of assumed.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][ValidateRange(1,64)][int]$ProcessorCount)
    $vm = Get-VM -Name $Name -ErrorAction Stop
    if ([string]$vm.State -ne 'Off') { throw 'CPU changes require a powered-off VM.' }
    Set-VMProcessor -VM $vm -Count $ProcessorCount -ErrorAction Stop
    if ([int](Get-VMProcessor -VM $vm -ErrorAction Stop).Count -ne $ProcessorCount) {
        throw 'Hyper-V did not apply the requested CPU count.'
    }
}

function Set-ConstructVmMemory {
    <#
        Change the RAM of an EXISTING VM (capability: Resources). Construct VMs run
        with STATIC memory (New-ConstructVm's -StaticMemory: nested virtualization
        needs it), so the startup size IS the VM's size and dynamic memory is kept
        off. Virtual hardware, so the VM must be Off. The size is rounded down to
        Hyper-V's 2 MB granularity and read back afterwards.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name,
          [Parameter(Mandatory)][double]$MemoryGB)
    if ($MemoryGB -le 0) { throw 'Set-ConstructVmMemory: -MemoryGB must be greater than zero.' }
    $bytes = [int64][math]::Floor($MemoryGB * 1GB)
    $bytes = $bytes - ($bytes % 2MB)
    if ($bytes -lt 32MB) { throw 'Set-ConstructVmMemory: the VM needs at least 32 MB of RAM.' }
    $vm = Get-VM -Name $Name -ErrorAction Stop
    if ([string]$vm.State -ne 'Off') { throw 'Memory changes require a powered-off VM.' }
    Set-VMMemory -VM $vm -DynamicMemoryEnabled $false -StartupBytes $bytes -ErrorAction Stop
    if ([int64](Get-VMMemory -VM $vm -ErrorAction Stop).Startup -ne $bytes) {
        throw 'Hyper-V did not apply the requested memory size.'
    }
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
        SshHost = "$($Name.ToLowerInvariant()).mshome.net"
        SshPort = 22
    }
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

# ── Removal (from lib/AgentVm.Common.ps1) ───────────────────────────────────

function Remove-FileWithRetry {
    # Delete a file, retrying for a while: Hyper-V's VMMS keeps a brief lock on
    # the .vhdx/.avhdx for several seconds after Remove-VM (or a just-finished
    # merge), so a single Remove-Item usually fails with "file in use". No-op if
    # the file is already gone.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$TimeoutSeconds = 60
    )
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        try {
            Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
            Write-Ok "Deleted virtual disk: $Path"
            return
        } catch {
            if ((Get-Date) -ge $deadline) {
                Write-Warning "Could not delete '$Path' after ${TimeoutSeconds}s ($($_.Exception.Message)). Delete it manually."
                return
            }
            Start-Sleep -Seconds 3
        }
    }
}

function Get-VhdChain {
    # Return every backing file for a virtual disk: the given file plus its full
    # parent chain (a checkpoint .avhdx references the base .vhdx via ParentPath).
    # This lets us delete the real base disk even when a merge hasn't yet
    # collapsed the chain back to a single .vhdx.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $chain = New-Object System.Collections.Generic.List[string]
    $cur = $Path
    while ($cur -and (Test-Path -LiteralPath $cur) -and ($chain -notcontains $cur)) {
        $chain.Add($cur)
        $vhd = Get-VHD -Path $cur -ErrorAction SilentlyContinue
        if (-not $vhd -or [string]::IsNullOrEmpty($vhd.ParentPath)) { break }
        $cur = $vhd.ParentPath
    }
    return $chain
}

function Remove-AgentVm {
    <#
        Tear down an existing Hyper-V VM and delete its virtual hard disk:
          1. Hard power-off if it is running.
          2. Resolve each attached disk's full backing-file chain (the base
             .vhdx plus any checkpoint .avhdx differencing files) -- captured
             before the VM is removed, since we can't query it afterwards.
          3. Remove the VM definition. This makes Hyper-V merge the checkpoint
             .avhdx files into the base and auto-delete them -- asynchronously.
          4. Wait for checkpointing to finish: the last .avhdx lingers for a few
             seconds after the VM is gone, so wait until every .avhdx we saw has
             disappeared from disk before touching the base .vhdx (deleting it
             mid-merge hits a locked file).
          5. Delete any orphaned .avhdx, then the base .vhdx, retrying past
             VMMS's brief lock.
        No-op if the VM does not exist.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$VmName)

    $vm = Get-VM -Name $VmName -ErrorAction SilentlyContinue
    if (-not $vm) { return }

    Write-Step "Deleting existing VM '$VmName'"

    # 1. Hard power-off if running (we're destroying it -- no clean save).
    if ($vm.State -ne 'Off') {
        Write-Note "Powering off the VM..."
        Stop-VM -Name $VmName -TurnOff -Force -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddMinutes(3)
        while ((Get-VM -Name $VmName -ErrorAction SilentlyContinue).State -ne 'Off' -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
        }
    }

    # 2. Resolve every backing file BEFORE removing the VM -- once it's gone we
    #    can't query them. Split into the base .vhdx (the chain root we want to
    #    delete) and any checkpoint .avhdx differencing files. Removing the VM
    #    makes Hyper-V merge the .avhdx into the base and auto-delete them, which
    #    is asynchronous -- so we wait for that below before deleting the base.
    $baseDisks = New-Object System.Collections.Generic.List[string]
    $diffDisks = New-Object System.Collections.Generic.List[string]
    foreach ($p in @(Get-VMHardDiskDrive -VMName $VmName -ErrorAction SilentlyContinue |
                     Select-Object -ExpandProperty Path)) {
        foreach ($f in (Get-VhdChain -Path $p)) {
            if ($f -match '\.avhdx$') {
                if ($diffDisks -notcontains $f) { $diffDisks.Add($f) }
            } elseif ($baseDisks -notcontains $f) {
                $baseDisks.Add($f)
            }
        }
    }

    # 3. Remove the VM definition. This triggers the checkpoint merge + auto-
    #    removal of the .avhdx files; the base .vhdx stays on disk.
    Remove-VM -Name $VmName -Force -ErrorAction Stop
    Write-Ok "VM removed from Hyper-V"

    # 4. Wait for checkpointing to finish. Even after the VM is gone the last
    #    checkpoint .avhdx lingers for a few seconds while Hyper-V merges it into
    #    the base and then deletes it; deleting the .vhdx before that finishes
    #    hits a locked file. Give it a moment, then wait until every .avhdx we
    #    saw has actually disappeared from disk (bounded), then settle briefly.
    Start-Sleep -Seconds 3
    if ($diffDisks.Count -gt 0) {
        Write-Note "Waiting for the checkpoint merge to finish (Hyper-V auto-removes the .avhdx)..."
        $deadline = (Get-Date).AddMinutes(10)
        while (($diffDisks | Where-Object { Test-Path -LiteralPath $_ }) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
        }
        Start-Sleep -Seconds 2
    }

    # 5. Delete the disk file(s): any .avhdx Hyper-V did NOT auto-remove (orphan),
    #    then the base .vhdx -- retrying past any residual VMMS lock.
    if ($baseDisks.Count -eq 0 -and $diffDisks.Count -eq 0) {
        Write-Note "No virtual disk files were attached to delete."
    }
    foreach ($f in $diffDisks) { Remove-FileWithRetry -Path $f }
    foreach ($f in $baseDisks) { Remove-FileWithRetry -Path $f }
}

# ── Adoption (the service's own) ─────────────────────────────────────────────

function Get-ConstructVmFacts {
    <#
        What adopting an existing VM needs to know about it, read only: its Hyper-V
        id, state, vCPUs, startup RAM, the virtual size of its attached disks, and the
        firmware id the guest reports as /sys/class/dmi/id/product_uuid (the same
        identity check service/host/ConvertTo-ConstructHost.ps1 makes). $null when no
        VM has this name. Changes nothing.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $vm = $null
    try {
        $vm = Get-VM -Name $Name -ErrorAction Stop
    } catch {
        if ($_.FullyQualifiedErrorId -like 'InvalidParameter*') { return $null }
        throw
    }
    $vm = @($vm)
    if ($vm.Count -ne 1) { throw "More than one VM is named '$Name'." }
    $vm = $vm[0]

    $diskBytes = [long]0
    foreach ($drive in @(Get-VMHardDiskDrive -VM $vm -ErrorAction Stop)) {
        $diskBytes += [long](Get-VHD -Path $drive.Path -ErrorAction Stop).Size
    }

    $vmId = ([guid]$vm.Id).ToString('D')
    $settings = @(Get-CimInstance -Namespace 'root/virtualization/v2' -ClassName Msvm_VirtualSystemSettingData `
        -Filter ("VirtualSystemIdentifier='$vmId' AND VirtualSystemType='Microsoft:Hyper-V:System:Realized'") -ErrorAction Stop)
    $bios = ''
    if ($settings.Count -eq 1) { $bios = [string]$settings[0].BIOSGUID }

    return @{
        Id          = $vmId
        State       = [string]$vm.State
        Cpu         = [int]$vm.ProcessorCount
        MemoryBytes = [long]$vm.MemoryStartup
        DiskBytes   = $diskBytes
        BiosGuid    = $bios
    }
}
