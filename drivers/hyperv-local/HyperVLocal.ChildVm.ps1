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
                if ([string]$artifact.artifact -like 'media:*' -or [string]$artifact.artifact -like 'upload:*') {
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
