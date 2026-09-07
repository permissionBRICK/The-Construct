# capacity
# Read-only and PS 5.1 compatible. Failures report stable codes, never dependency text.
function Get-ConstructHostInventory {
    [CmdletBinding()]
    param([object[]]$Artifacts = @())
    $problems = New-Object 'System.Collections.Generic.List[string]'
    $allVms = New-Object 'System.Collections.Generic.List[object]'
    $volumes = New-Object 'System.Collections.Generic.List[object]'
    $observedArtifacts = New-Object 'System.Collections.Generic.List[object]'
    $saved = @{}
    function Get-CapacityVolumeRoot([string]$Path) {
        $v = Get-Volume -FilePath $Path -ErrorAction Stop
        if ($v.DriveLetter) { return ([string]$v.DriveLetter + ':\') }
        return [string]$v.Path
    }
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $computer = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
        $totalRam = [int64]$os.TotalVisibleMemorySize * 1KB
        $freeRam = [int64]$os.FreePhysicalMemory * 1KB
        $cpus = [int]$computer.NumberOfLogicalProcessors
    } catch { $problems.Add('host-resources-unavailable'); $totalRam = 0; $freeRam = 0; $cpus = 0 }
    try {
        foreach ($v in @(Get-CimInstance Win32_Volume -Filter 'DriveType=3' -ErrorAction Stop)) {
            $root = [string]$v.Name
            if ($v.DriveLetter) { $root = [string]$v.DriveLetter + '\' }
            if ($null -eq $v.Capacity -or $null -eq $v.FreeSpace) { $problems.Add('volume-unavailable'); continue }
            $volumes.Add(@{ root = $root; totalBytes = [int64]$v.Capacity; freeBytes = [int64]$v.FreeSpace })
        }
    } catch { $problems.Add('volume-enumeration-unavailable') }
    try { $vms = @(Get-VM -ErrorAction Stop) } catch { $problems.Add('vm-enumeration-unavailable'); $vms = @() }
    foreach ($vm in $vms) {
        $complete = $true
        $disks = @{}
        $configVolume = ''
        $savedBytes = $null
        $mem = $null
        try {
            $mem = Get-VMMemory -VM $vm -ErrorAction Stop
            $configVolume = Get-CapacityVolumeRoot $vm.ConfigurationLocation
            $savedPath = Join-Path (Join-Path $vm.ConfigurationLocation 'Virtual Machines') ($vm.Id.ToString() + '.vmrs')
            if (Test-Path -LiteralPath $savedPath -ErrorAction Stop) {
                $savedBytes = [int64](Get-Item -LiteralPath $savedPath -ErrorAction Stop).Length
                $saved[$vm.Id.ToString()] = @{ path = $savedPath; bytes = $savedBytes; presence = 'present'; volume = $configVolume }
            } else {
                $savedBytes = 0
                $saved[$vm.Id.ToString()] = @{ path = $savedPath; bytes = 0; presence = 'absent'; volume = $configVolume }
            }
            $attached = @(Get-VMHardDiskDrive -VM $vm -ErrorAction Stop)
            foreach ($checkpoint in @(Get-VMSnapshot -VM $vm -ErrorAction Stop)) {
                $attached += @(Get-VMHardDiskDrive -VMSnapshot $checkpoint -ErrorAction Stop)
            }
            foreach ($attachment in $attached) {
                $path = [string]$attachment.Path
                if (-not $path) { $complete = $false; $problems.Add('passthrough-disk-unavailable'); continue }
                $chain = @{}
                while ($path) {
                    try {
                        $path = [string](Resolve-Path -LiteralPath $path -ErrorAction Stop).ProviderPath
                        if ($chain.ContainsKey($path)) { throw 'Disk chain cycle' }
                        $chain[$path] = $true
                        if ($disks.ContainsKey($path)) { break }
                        $h = Get-VHD -Path $path -ErrorAction Stop
                        $volume = Get-CapacityVolumeRoot $path
                        $disks[$path] = @{ path = $path; maxBytes = [int64]$h.Size; fileBytes = [int64]$h.FileSize;
                            parentPath = [string]$h.ParentPath; volume = $volume; readable = $true }
                        $path = [string]$h.ParentPath
                    } catch {
                        $complete = $false; $problems.Add('disk-unreadable')
                        $disks[$path] = @{ path = $path; maxBytes = 0; fileBytes = 0; parentPath = $null;
                            volume = [IO.Path]::GetPathRoot($path); readable = $false }
                        break
                    }
                }
            }
        } catch { $complete = $false; $problems.Add('vm-details-unavailable') }
        $state = ([string]$vm.State).ToLowerInvariant()
        if ($state -notin @('running','off','paused','saved')) { $state = 'unknown' }
        $allVms.Add(@{ name = [string]$vm.Name; id = $vm.Id.ToString(); state = $state; rawState = [string]$vm.State;
            generation = [int]$vm.Generation; cpus = [int]$vm.ProcessorCount; memoryStartupBytes = [int64]$mem.Startup;
            memoryAssignedBytes = [int64]$vm.MemoryAssigned; dynamicMemory = [bool]$mem.DynamicMemoryEnabled;
            memoryMaximumBytes = [int64]$mem.Maximum; disks = @($disks.Values); savedStateBytes = $savedBytes;
            configVolume = $configVolume; complete = $complete })
    }
    foreach ($artifact in $Artifacts) {
        $path = [string]$artifact.path
        $presence = 'unknown'; $bytes = 0; $volume = [string]$artifact.volume
        try {
            if ([string]$artifact.artifact -like 'saved-state:*') {
                $id = ([string]$artifact.artifact).Substring(12)
                if ($saved.ContainsKey($id)) {
                    $e = $saved[$id]; $path = $e.path; $bytes = $e.bytes; $presence = $e.presence; $volume = $e.volume
                }
            } elseif ($path) {
                # A missing file is absence only on a readable volume, not a missing drive/mount.
                $probeRoot = [IO.Path]::GetPathRoot($path)
                $null = Get-Item -LiteralPath $probeRoot -ErrorAction Stop
                $volume = Get-CapacityVolumeRoot $probeRoot
                $candidates = @($path)
                if ($artifact.isMedia -or [string]$artifact.artifact -like 'media:*' -or [string]$artifact.artifact -like 'upload:*') {
                    $candidates += ($path + '.part')
                    $candidates += [IO.Path]::ChangeExtension($path, '.part')
                }
                $presence = 'absent'
                foreach ($candidate in @($candidates | Select-Object -Unique)) {
                    if (Test-Path -LiteralPath $candidate -ErrorAction Stop) {
                        $item = Get-Item -LiteralPath $candidate -ErrorAction Stop
                        if ($item.PSIsContainer) { throw 'Expected file' }
                        $bytes += [int64]$item.Length; $presence = 'present'
                        $path = [string]$item.FullName
                        $volume = Get-CapacityVolumeRoot $path
                    }
                }
            }
        } catch { $presence = 'unknown'; $problems.Add('artifact-unreadable') }
        $observedArtifacts.Add(@{ artifact = [string]$artifact.artifact; path = $path; volume = $volume; fileBytes = $bytes; presence = $presence })
    }
    $now = [DateTimeOffset]::UtcNow.ToString('o')
    return @{ epoch = 0; observedAt = $now; host = @{ logicalCpus = $cpus; totalRamBytes = $totalRam; freeRamBytes = $freeRam;
        volumes = @($volumes.ToArray()); observedAt = $now }; vms = @($allVms.ToArray()); complete = ($problems.Count -eq 0);
        problems = @($problems.ToArray()); artifacts = @($observedArtifacts.ToArray()) }
}
# end capacity

#Requires -Version 5.1
# childvm -- optional general-purpose hardware; never loaded by the primary path.
function Get-ConstructDriverExtendedCapabilities {
    @{
        backend = 'hyperv-local'; legacy = (Get-ConstructDriverCapabilities)
        generations = @(2); defaultGeneration = 2; secureBoot = 'supported'
        secureBootTemplates = @('microsoftWindows', 'microsoftUefiCertificateAuthority')
        tpm = 'supported'; secureBootTemplateLockedAfterTpmInit = $true
        maxOpticalDrives = 2; auxiliaryMedia = 'supported'; bootOrder = 'conditional'
        console = @{ screenshot = 'unsupported'; keyboard = 'unsupported'; mouseAbsolute = 'unsupported'; mouseRelative = 'unsupported'; interactive = 'unsupported'; maxScreenshotBytes = 4194304; nativeResolutionOnly = $true }
        network = @{ clientForward = 'supported'; hostForwardPrimary = 'supported'; hostForwardChild = 'unsupported'; directAddressReporting = 'conditional'; addressVerification = 'unsupported'; isolation = 'unsupported' }
        dynamicMemory = 'unsupported'; memoryOvercommit = 'unsupported'; suspend = 'supported'; gracefulShutdown = 'conditional'
        notes = @('LocalSystem execution is unverified. Guest addresses require integration services and are unverified.')
    }
}

function Assert-ConstructChildHardware {
    param($Hardware)
    if (-not $Hardware -or $Hardware.cpus -lt 1 -or $Hardware.ramMb -lt 512 -or $Hardware.ramMb % 2 -ne 0 -or $Hardware.diskGb -lt 1) { throw 'validation' }
    if ($null -ne $Hardware.dynamicMemory -or $Hardware.generation -ne 2) { throw 'unsupported-capability' }
    if ($Hardware.secureBootTemplate -and $Hardware.secureBootTemplate -notin @('microsoftWindows', 'microsoftUefiCertificateAuthority')) { throw 'unsupported-capability' }
    if ($Hardware.secureBoot -and -not $Hardware.secureBootTemplate) { throw 'validation' }
    $seen = @{}
    foreach ($device in $Hardware.bootOrder) {
        if ($device -notin @('installMedia', 'auxiliaryMedia', 'disk', 'network') -or $seen.ContainsKey($device)) { throw 'validation' }
        $seen[$device] = $true
    }
}

function Assert-ConstructChildVmName {
    param([string]$Name)
    if ($Name -cnotmatch '\A[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\z' -or $Name -like 'construct-*') { throw 'validation' }
}

function Get-ConstructChildDiskPath {
    param([string]$Name, [string]$VhdPath)
    Assert-ConstructChildVmName $Name
    if ($VhdPath) { return $VhdPath }
    Join-Path (Get-VMHost -ErrorAction Stop).VirtualHardDiskPath ($Name + '.vhdx')
}

function Get-ConstructChildVmObject {
    param([string]$Name)
    # Enumerating avoids treating a permission/provider failure as absence.
    $matchingVms = @(Get-VM -ErrorAction Stop | Where-Object { $_.Name -eq $Name })
    if ($matchingVms.Count -gt 1) { throw 'vm-identity-ambiguous' }
    if ($matchingVms.Count -eq 1) { return $matchingVms[0] }
    return $null
}

function Set-ConstructChildHardware {
    param([string]$Name, $Hardware, [bool]$ResendTemplate = $false)
    Assert-ConstructChildVmName $Name
    Assert-ConstructChildHardware $Hardware
    $vm = Get-VM -Name $Name -ErrorAction Stop
    if ([string]$vm.State -ne 'Off') { throw 'vm-not-off' }
    if ($vm.Generation -ne $Hardware.generation) { throw 'unsupported-capability' }
    $security = Get-VMSecurity -VMName $Name -ErrorAction Stop
    $protector = @(Get-VMKeyProtector -VMName $Name -ErrorAction Stop)
    $hasProtector = $protector.Count -gt 0
    if ($ResendTemplate -and $hasProtector) { throw 'secure-boot-template-locked' }
    $disks = @(Get-VMHardDiskDrive -VMName $Name -ErrorAction Stop)
    if ($disks.Count -gt 0 -and (Get-VHD -Path $disks[0].Path -ErrorAction Stop).Size -ne ([long]$Hardware.diskGb * 1GB)) { throw 'unsupported-capability' }
    $adapters = @(Get-VMNetworkAdapter -VMName $Name -ErrorAction Stop)
    if ([bool]$Hardware.networkAttached -ne ($adapters.Count -gt 0)) { throw 'unsupported-capability' }
    Set-VMProcessor -VMName $Name -Count $Hardware.cpus -ErrorAction Stop
    Set-VMMemory -VMName $Name -DynamicMemoryEnabled $false -StartupBytes ([long]$Hardware.ramMb * 1MB) -ErrorAction Stop
    $firmware = @{ VMName = $Name; EnableSecureBoot = 'Off'; ErrorAction = 'Stop' }
    if ($Hardware.secureBoot) { $firmware.EnableSecureBoot = 'On' }
    if ($ResendTemplate -and $Hardware.secureBootTemplate) { $firmware.SecureBootTemplate = 'MicrosoftWindows'; if ($Hardware.secureBootTemplate -eq 'microsoftUefiCertificateAuthority') { $firmware.SecureBootTemplate = 'MicrosoftUEFICertificateAuthority' } }
    Set-VMFirmware @firmware
    # The template MUST precede the first TPM initialization.
    if ($Hardware.tpm -and -not $security.TpmEnabled) {
        if (-not $hasProtector) { Set-VMKeyProtector -VMName $Name -NewLocalKeyProtector -ErrorAction Stop }
        Enable-VMTPM -VMName $Name -ErrorAction Stop
    } elseif (-not $Hardware.tpm -and $security.TpmEnabled) {
        Disable-VMTPM -VMName $Name -ErrorAction Stop
    }
    if ($disks.Count -gt 0) {
        $media = Get-ConstructChildAttachedMedia -Name $Name
        Set-ConstructChildMedia -Name $Name -InstallMediaPath $media.installPath -AuxiliaryMediaPath $media.auxiliaryPath -BootOrder $Hardware.bootOrder
    }
}

function Set-ConstructChildMedia {
    param([string]$Name, [string]$InstallMediaPath, [string]$AuxiliaryMediaPath, [string[]]$BootOrder)
    Assert-ConstructChildVmName $Name
    $vm = Get-VM -Name $Name -ErrorAction Stop
    if ([string]$vm.State -ne 'Off') { throw 'vm-not-off' }
    foreach ($path in @($InstallMediaPath, $AuxiliaryMediaPath)) {
        if ($path -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'media-not-ready' }
    }
    $paths = @($InstallMediaPath, $AuxiliaryMediaPath)
    for ($slot = 0; $slot -lt 2; $slot++) {
        $location = $slot + 1
        $drive = @(Get-VMDvdDrive -VMName $Name -ErrorAction Stop | Where-Object { $_.ControllerNumber -eq 0 -and $_.ControllerLocation -eq $location })
        if ($drive.Count -gt 0) {
            $path = $null; if ($paths[$slot]) { $path = $paths[$slot] }
            Set-VMDvdDrive -VMDvdDrive $drive[0] -Path $path -ErrorAction Stop
        } elseif ($paths[$slot]) {
            Add-VMDvdDrive -VMName $Name -ControllerNumber 0 -ControllerLocation $location -Path $paths[$slot] -ErrorAction Stop
        }
    }
    $order = @()
    foreach ($device in $BootOrder) {
        switch ($device) {
            'installMedia' { if ($InstallMediaPath) { $order += @(Get-VMDvdDrive -VMName $Name | Where-Object { $_.ControllerLocation -eq 1 -and $_.ControllerNumber -eq 0 }) } }
            'auxiliaryMedia' { if ($AuxiliaryMediaPath) { $order += @(Get-VMDvdDrive -VMName $Name | Where-Object { $_.ControllerLocation -eq 2 -and $_.ControllerNumber -eq 0 }) } }
            'disk' { $order += @(Get-VMHardDiskDrive -VMName $Name -ErrorAction Stop) }
            'network' { $order += @(Get-VMNetworkAdapter -VMName $Name -ErrorAction Stop) }
            default { throw 'validation' }
        }
    }
    if ($order.Count -gt 0) { Set-VMFirmware -VMName $Name -BootOrder $order -ErrorAction Stop }
}

function Write-ConstructChildOwnership {
    param([string]$Marker, $Record, [switch]$CreateNew)
    # Preserve the previous valid record across interruption of an update.
    $temporary = $Marker + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes(($Record | ConvertTo-Json -Compress))
        $stream = [IO.File]::Open($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        if ($CreateNew) { [IO.File]::Move($temporary, $Marker) }
        else { [IO.File]::Replace($temporary, $Marker, [System.Management.Automation.Language.NullString]::Value) }
    } finally {
        if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -ErrorAction Stop }
    }
}

function New-ConstructChildVm {
    param($Descriptor)
    $h = $Descriptor.hardware
    Assert-ConstructChildHardware $h
    $name = [string]$Descriptor.name
    $disk = Get-ConstructChildDiskPath -Name $name -VhdPath $Descriptor.vhdPath
    if (Get-ConstructChildVmObject $name) { throw 'name-taken' }
    foreach ($path in @($Descriptor.installMediaPath, $Descriptor.auxiliaryMediaPath)) {
        if ($path -and -not (Test-Path -LiteralPath $path -PathType Leaf)) { throw 'media-not-ready' }
    }
    if ($h.networkAttached) { $null = Get-VMSwitch -Name $Descriptor.switchName -ErrorAction Stop }
    if (Test-Path -LiteralPath $disk) { throw 'name-taken' }
    $marker = $disk + '.childvm.json'
    if ($Descriptor.ownershipPath) { $marker = [string]$Descriptor.ownershipPath }
    $configPath = Join-Path (Get-VMHost -ErrorAction Stop).VirtualMachinePath ('childvm-' + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $configPath) { throw 'name-taken' }
    $record = @{ configPath = $configPath; name = $name; id = $null; disks = @($disk); rootDisk = $disk; operationId = $Descriptor.operationId }
    # Publish a complete record exclusively BEFORE creating any VM artifacts.
    Write-ConstructChildOwnership -Marker $marker -Record $record -CreateNew
    $creationParameters = @{ Path = $configPath; Name = $name; Generation = 2; MemoryStartupBytes = ([long]$h.ramMb * 1MB); NoVHD = $true; ErrorAction = 'Stop' }
    if ($h.networkAttached) { $creationParameters.SwitchName = $Descriptor.switchName }
    $vm = New-VM @creationParameters
    $record.id = [string]$vm.Id
    Write-ConstructChildOwnership -Marker $marker -Record $record
    if (-not $h.networkAttached) { Get-VMNetworkAdapter -VMName $name | Remove-VMNetworkAdapter -ErrorAction Stop }
    Set-VM -Name $name -AutomaticCheckpointsEnabled $false -AutomaticStopAction Save -AutomaticStartAction StartIfRunning -ErrorAction Stop
    Set-ConstructChildHardware -Name $name -Hardware $h -ResendTemplate $true
    $null = New-VHD -Path $disk -Dynamic -SizeBytes ([long]$h.diskGb * 1GB) -ErrorAction Stop
    Add-VMHardDiskDrive -VMName $name -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0 -Path $disk -ErrorAction Stop
    Set-ConstructChildMedia -Name $name -InstallMediaPath $Descriptor.installMediaPath -AuxiliaryMediaPath $Descriptor.auxiliaryMediaPath -BootOrder $h.bootOrder
}

function Remove-ConstructChildVm {
    param([string]$Name, [string]$VhdPath)
    $disk = Get-ConstructChildDiskPath -Name $Name -VhdPath $VhdPath
    $marker = $disk + '.childvm.json'
    $vm = Get-ConstructChildVmObject $Name
    if (-not (Test-Path -LiteralPath $marker)) {
        if ($vm -or (Test-Path -LiteralPath $disk)) { throw 'artifact-ownership-unverified' }
        return
    }
    $record = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($record.name -ne $Name) { throw 'artifact-ownership-unverified' }
    if ($record.rootDisk) { $disk = [string]$record.rootDisk }
    if ($vm) {
        if (-not $record.id) {
            # New-VM can succeed before its ID is journalled. Its unique configuration
            # location, recorded before allocation, is independent ownership evidence.
            if (-not $record.configPath -or ([string]$vm.Path -ne [string]$record.configPath -and [string]$vm.Path -ne (Join-Path $record.configPath $Name))) { throw 'vm-incarnation-conflict' }
            $record.id = [string]$vm.Id
        } elseif ([string]$vm.Id -ne $record.id) { throw 'vm-incarnation-conflict' }
        # Record checkpoint-chain paths BEFORE deleting the VM; keep them on partial failure.
        $paths = @($record.disks)
        $drives = @(Get-VMHardDiskDrive -VMName $Name -ErrorAction Stop)
        foreach ($snapshot in @(Get-VMSnapshot -VMName $Name -ErrorAction Stop)) {
            $drives += @(Get-VMHardDiskDrive -VMSnapshot $snapshot -ErrorAction Stop)
        }
        foreach ($drive in $drives) {
            $chain = @(); $path = $drive.Path
            while ($path) {
                if ($chain -contains $path) { throw 'disk-chain-invalid' }
                $chain += $path
                $path = (Get-VHD -Path $path -ErrorAction Stop).ParentPath
            }
            if ($chain[-1] -ne $disk) { throw 'artifact-ownership-unverified' }
            $paths += $chain
        }
        $record.disks = @($paths | Select-Object -Unique)
        Write-ConstructChildOwnership -Marker $marker -Record $record
        if ([string]$vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Confirm:$false -ErrorAction Stop }
        Remove-VM -VM $vm -Force -ErrorAction Stop
    }
    foreach ($path in $record.disks) {
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -ErrorAction Stop }
    }
    if ($record.configPath -and (Test-Path -LiteralPath $record.configPath)) { Remove-Item -LiteralPath $record.configPath -Recurse -ErrorAction Stop }
    Remove-Item -LiteralPath $marker -ErrorAction Stop
}

function Get-ConstructChildAttachedMedia {
    param([string]$Name)
    $drives = @(Get-VMDvdDrive -VMName $Name -ErrorAction Stop)
    $install = $null; $auxiliary = $null
    foreach ($drive in $drives) {
        if ($drive.ControllerNumber -eq 0 -and $drive.ControllerLocation -eq 1) { $install = $drive.Path }
        if ($drive.ControllerNumber -eq 0 -and $drive.ControllerLocation -eq 2) { $auxiliary = $drive.Path }
    }
    @{ installPath = $install; auxiliaryPath = $auxiliary; complete = $true }
}

function Stop-ConstructChildVmGracefully {
    param([string]$Name, [int]$TimeoutSeconds = 300)
    Assert-ConstructChildVmName $Name
    if ($TimeoutSeconds -lt 1) { throw 'validation' }
    $vm = Get-VM -Name $Name -ErrorAction Stop
    if ([string]$vm.State -eq 'Off') { return 'completed' }
    $system = Get-WmiObject -Namespace root\virtualization\v2 -Class Msvm_ComputerSystem -Filter ("Name='" + $vm.Id + "'") -ErrorAction Stop
    $shutdown = @($system.GetRelated('Msvm_ShutdownComponent'))
    if ($shutdown.Count -eq 0) { return 'unavailable' }
    $result = $shutdown[0].InitiateShutdown($false, 'Construct graceful shutdown')
    if ($result.ReturnValue -eq 32768) { return 'unavailable' }
    if ($result.ReturnValue -ne 0 -and $result.ReturnValue -ne 4096) { return 'failed' }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        if ([string](Get-VM -Name $Name -ErrorAction Stop).State -eq 'Off') { return 'completed' }
        Start-Sleep -Milliseconds 500
    }
    return 'timeout'
}

function Get-ConstructChildVmId {
    param([string]$Name)
    $vm = Get-ConstructChildVmObject $Name
    if ($vm) { return [string]$vm.Id }
    return $null
}

function Get-ConstructChildVmCapabilities {
    param([string]$Name)
    Assert-ConstructChildVmName $Name
    $vm = Get-VM -Name $Name -ErrorAction Stop
    $system = Get-WmiObject -Namespace root\virtualization\v2 -Class Msvm_ComputerSystem -Filter ("Name='" + $vm.Id + "'") -ErrorAction Stop
    $video = @($system.GetRelated('Msvm_VideoHead'))
    $width = $null; $height = $null
    if ($video.Count -gt 0) { $width = $video[0].CurrentHorizontalResolution; $height = $video[0].CurrentVerticalResolution }
    $shutdown = 'unsupported'
    if (@($system.GetRelated('Msvm_ShutdownComponent')).Count -gt 0) { $shutdown = 'conditional' }
    @{
        vmName = $Name; state = (Get-ConstructVmState -Name $Name); generation = $vm.Generation
        videoHeadPresent = ($video.Count -gt 0); keyboardPresent = (@($system.GetRelated('Msvm_Keyboard')).Count -gt 0)
        syntheticMousePresent = (@($system.GetRelated('Msvm_SyntheticMouse')).Count -gt 0); ps2MousePresent = (@($system.GetRelated('Msvm_Ps2Mouse')).Count -gt 0)
        nativeWidth = $width; nativeHeight = $height; secureBootTemplateLocked = (@(Get-VMKeyProtector -VMName $Name -ErrorAction Stop).Count -gt 0)
        gracefulShutdown = $shutdown
        network = @{ clientForward = 'supported'; hostForward = 'unsupported'; addressVerification = 'unsupported' }
    }
}

# capacity -- Get-ConstructHostInventory is owned by the capacity pair.
# network -- KVP reports are untrusted; MAC/neighbor facts do not prove IP ownership.
function Get-ConstructVmAddresses {
    param([string]$Name, [string]$VmId, [object[]]$VmRequests = @())
    $addresses = @(); $adapters = @(); $neighbors = @(); $subnets = @(); $vmSnapshots = @()
    if ($Name) { $VmRequests = @(@{ name = $Name; vmId = $VmId }) }
    foreach ($request in $VmRequests) {
        $Name = [string]$request.name; $VmId = [string]$request.vmId
        $addresses = @(); $adapters = @(); $failureCategory = 'vm-not-found'
        try {
            # Resolve the immutable id first. Never query an arbitrary recreated child by name.
            if ($VmId) { $vm = Get-VM -Id ([Guid]$VmId) -ErrorAction Stop }
            else { $vm = Get-VM -Name $Name -ErrorAction Stop }
            $failureCategory = 'vm-incarnation-changed'
            if ($vm.Name -ine $Name) { throw 'vm-incarnation-changed' }
            $failureCategory = 'adapter-query-failed'
            foreach ($nic in @(Get-VMNetworkAdapter -VM $vm -ErrorAction Stop)) {
                $adapters += @{ vmId = [string]$vm.Id; adapterId = [string]$nic.Id; macAddress = [string]$nic.MacAddress;
                    macSpoofingEnabled = ([string]$nic.MacAddressSpoofing -eq 'On'); switchName = [string]$nic.SwitchName }
                foreach ($value in @($nic.IPAddresses)) {
                    $ip = $null
                    if (-not [Net.IPAddress]::TryParse([string]$value, [ref]$ip)) { continue }
                    $family = 'ipv4'; if ($ip.AddressFamily -eq [Net.Sockets.AddressFamily]::InterNetworkV6) { $family = 'ipv6' }
                    $addresses += @{ address = $ip.ToString(); family = $family; source = 'kvp'; observedAt = [DateTimeOffset]::UtcNow.ToString('o');
                        verified = $false; adapterId = [string]$nic.Id }
                }
            }
            $vmSnapshots += @{ name = $Name; addresses = @($addresses); adapters = @($adapters) }
        } catch {
            # An absent/in-flight/replaced VM must not discard another VM's KVP report.
            # Never return exception or cmdlet output from this boundary.
            $addresses = @(); $adapters = @()
            $vmSnapshots += @{ name = $Name; addresses = @(); adapters = @(); error = $failureCategory }
        }
    }
    $hostIps = @(Get-NetIPAddress -ErrorAction Stop)
    $management = @(Get-VMNetworkAdapter -ManagementOS -ErrorAction Stop)
    $netAdapters = @(Get-NetAdapter -IncludeHidden -ErrorAction Stop)
    foreach ($nic in $management) {
        # Bind the actual host adapter by MAC, not a guessed switch/alias string.
        $mac = ([string]$nic.MacAddress).Replace('-', '').Replace(':', '')
        foreach ($hostNic in @($netAdapters | Where-Object { ([string]$_.MacAddress).Replace('-', '').Replace(':', '') -ieq $mac })) {
            foreach ($entry in @($hostIps | Where-Object { $_.InterfaceIndex -eq $hostNic.ifIndex })) {
                $subnets += @{ cidr = ([string]$entry.IPAddress + '/' + [string]$entry.PrefixLength); switchName = [string]$nic.SwitchName; interfaceAlias = [string]$entry.InterfaceAlias }
            }
            foreach ($neighbor in @(Get-NetNeighbor -InterfaceIndex $hostNic.ifIndex -ErrorAction Stop)) {
                $neighbors += @{ address = [string]$neighbor.IPAddress; macAddress = [string]$neighbor.LinkLayerAddress;
                    interfaceAlias = [string]$hostNic.Name; state = [string]$neighbor.State }
            }
        }
    }
    @{ vms = @($vmSnapshots); addresses = @($addresses); adapters = @($adapters); neighbors = @($neighbors); subnets = @($subnets); hostAddresses = @($hostIps | ForEach-Object { [string]$_.IPAddress }) }
}

# childvm -- storage placement is read before admission, never allocated here.
function Get-ConstructChildStorage {
    param([string]$Name, [string]$VhdPath)
    $disk = Get-ConstructChildDiskPath -Name $Name -VhdPath $VhdPath
    $existing = Get-ConstructChildVmObject $Name
    $config = if ($existing) { [string]$existing.ConfigurationLocation } else { [string](Get-VMHost -ErrorAction Stop).VirtualMachinePath }
    if (-not $config) { throw 'storage-placement-unavailable' }
    @{ diskPath = $disk; diskVolume = [IO.Path]::GetPathRoot($disk); configVolume = [IO.Path]::GetPathRoot($config) }
}

function Get-ConstructChildCreationOperation {
    param([string]$Name, [string]$VhdPath)
    $marker = (Get-ConstructChildDiskPath -Name $Name -VhdPath $VhdPath) + '.childvm.json'
    if (-not (Test-Path -LiteralPath $marker)) { return $null }
    $record = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($record.name -ne $Name) { throw 'artifact-ownership-unverified' }
    return $record.operationId
}
