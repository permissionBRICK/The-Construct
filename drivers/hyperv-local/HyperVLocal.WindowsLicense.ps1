#Requires -Version 5.1
# Only the host service calls these functions. Input/output envelopes never expose
# a product key, installation ID or confirmation ID in dependency diagnostics.
function Set-ConstructWindowsActivation {
    param([string]$Name, [string]$Incarnation, $Command)
    $value = ''
    if ($Command) {
        if ($Command.id -cnotmatch '^[a-f0-9]{32}$' -or $Command.allocationId -cnotmatch '^[0-9]+$' -or
            $Command.action -notin @('prepare','apply') -or $Command.key -cnotmatch '^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$' -or
            ($Command.action -eq 'apply' -and $Command.confirmationId -cnotmatch '^[0-9]{48}$')) { throw 'validation' }
        $value = $Command | ConvertTo-Json -Compress
    }
    Set-ConstructWindowsKvp -Name $Name -Incarnation $Incarnation -Slot 'Construct.WindowsActivation' -Value $value
}
function Test-ConstructVamt {
    param([string]$ModulePath)
    if (-not $ModulePath -or -not (Test-Path -LiteralPath $ModulePath -PathType Leaf)) { return $false }
    try { Import-Module -Name $ModulePath -ErrorAction Stop; return [bool](Get-Command Get-VamtConfirmationId -ErrorAction Stop) }
    catch { return $false }
}
function Get-ConstructConfirmationId {
    param([string]$ModulePath, [string]$Kind, $Report)
    if ($Kind -notin @('retail','mak') -or $Report.installationId -cnotmatch '^[0-9]{1,128}$' -or
        $Report.productKeyId -cnotmatch '^[0-9-]{1,128}$') { throw 'validation' }
    if (-not (Test-ConstructVamt $ModulePath)) { throw 'vamt-unavailable' }
    $product = New-Object Microsoft.Licensing.VolumeActivation.Product
    $product.ApplicationId = [guid]'55c92734-d682-4d71-983e-d6ec3f16059f'
    $product.Sku = [guid]$Report.activationId
    $product.InstallationId = $Report.installationId
    $product.ProductKeyId = $Report.productKeyId
    $product.ProductKeyType = if ($Kind -eq 'mak') { 'Mak' } else { 'Retail' }
    $result = @(Get-VamtConfirmationId -Products @($product) -ErrorAction Stop)
    if ($result.Count -ne 1 -or $result[0].LastErrorCode -ne 0 -or $result[0].ConfirmationId -cnotmatch '^[0-9]{48}$') { throw 'activation-result-uncertain' }
    return [string]$result[0].ConfirmationId
}
function Get-ConstructLicensePath {
    param([string]$Incarnation, [string]$PoolRoot)
    $id = ([guid]$Incarnation).ToString('N')
    if (-not $PoolRoot) { $PoolRoot = [string](Get-VMHost -ErrorAction Stop).VirtualHardDiskPath }
    return Join-Path (Join-Path $PoolRoot 'windows-license-pool') $id
}
function Assert-ConstructLicensePool {
    param([string]$Pool, [string]$Incarnation, [switch]$Initialized)
    $marker = Join-Path $Pool 'owner.json'
    if (-not (Test-Path -LiteralPath $marker)) { throw 'artifact-ownership-unverified' }
    $owner = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ([guid]$owner.id -ne [guid]$Incarnation -or $owner.pool -ine $Pool) { throw 'artifact-ownership-unverified' }
    if ($Initialized -and $owner.version -ne 2) { throw 'artifact-ownership-unverified' }
}
function Initialize-ConstructLicenseFirmware {
    param([string]$Name)
    # A never-booted vTPM has no stable endorsement identity. Initialize it using
    # only trusted firmware, before any tenant disk, media or network can run.
    # The caller must hold runtime CPU/RAM capacity, even for --no-start.
    $vm = Get-VM -Name $Name -ErrorAction Stop
    if ([string]$vm.State -ne 'Off' -or @(Get-VMHardDiskDrive -VM $vm).Count -ne 0 -or
        @(Get-VMDvdDrive -VM $vm).Count -ne 0 -or @(Get-VMSnapshot -VM $vm).Count -ne 0) { throw 'artifact-ownership-unverified' }
    $adapters = @(Get-VMNetworkAdapter -VM $vm)
    $connections = @($adapters | ForEach-Object { @{ Adapter=$_; SwitchName=[string]$_.SwitchName } })
    try {
        $adapters | Disconnect-VMNetworkAdapter -ErrorAction Stop
        Start-VM -VM $vm -ErrorAction Stop
        Start-Sleep -Seconds 5
    } finally {
        # Stop even after a partial Start-VM failure. Never reconnect while the
        # firmware VM is running, and never export a running machine's state.
        $vm = Get-VM -Name $Name -ErrorAction Stop
        if ([string]$vm.State -ne 'Off') { Stop-VM -VM $vm -TurnOff -Confirm:$false -ErrorAction Stop }
        if ([string](Get-VM -Name $Name).State -ne 'Off') { throw 'cleanup-unverified' }
        foreach ($connection in $connections) {
            if ($connection.SwitchName) { Connect-VMNetworkAdapter -VMNetworkAdapter $connection.Adapter -SwitchName $connection.SwitchName -ErrorAction Stop }
        }
    }
}
function Clear-ConstructLicenseAllocation {
    param([string]$Name, [string]$Incarnation, [string]$VhdPath)
    $disk = Get-ConstructChildDiskPath -Name $Name -VhdPath $VhdPath
    $pool = Get-ConstructLicensePath $Incarnation (Split-Path $disk -Parent)
    Assert-ConstructLicensePool $pool $Incarnation -Initialized
    $vm = Get-VM -Id ([guid]$Incarnation) -ErrorAction SilentlyContinue
    if ($vm -and $vm.Name -ine $Name) {
        # A retry after restoring the pristine definition must not destroy it.
        $working = Join-Path $pool 'working'
        if (([string]$vm.ConfigurationLocation).TrimEnd('\') -ine $working.TrimEnd('\') -or
            @(Get-VMHardDiskDrive -VM $vm).Count -ne 0 -or @(Get-VMSnapshot -VM $vm).Count -ne 0) { throw 'vm-incarnation-conflict' }
        return
    }
    Remove-ConstructChildVm -Name $Name -VhdPath $VhdPath -KeepLicenseBaseline
}
function Save-ConstructLicenseMachine {
    param([string]$Name, [string]$Incarnation, [string]$VhdPath)
    $disk = Get-ConstructChildDiskPath -Name $Name -VhdPath $VhdPath
    $pool = Get-ConstructLicensePath $Incarnation (Split-Path $disk -Parent)
    Assert-ConstructLicensePool $pool $Incarnation -Initialized
    $poolName = 'lic-' + ([guid]$Incarnation).ToString('N')
    $working = Join-Path $pool 'working'
    $vm = Get-VM -Id ([guid]$Incarnation) -ErrorAction SilentlyContinue
    if ($vm) {
        if (([string]$vm.ConfigurationLocation).TrimEnd('\') -ine $working.TrimEnd('\') -or
            @(Get-VMHardDiskDrive -VM $vm).Count -ne 0 -or @(Get-VMSnapshot -VM $vm).Count -ne 0) { throw 'cleanup-unverified' }
    } else {
        # Clear-ConstructLicenseAllocation must run in a separate PowerShell
        # process. Hyper-V caches settings by VM GUID within a process; deleting
        # and importing that GUID there can expose the old disk settings.
        $baseline = @(Get-ChildItem -LiteralPath (Join-Path $pool 'baseline') -Recurse -Filter '*.vmcx')
        if ($baseline.Count -ne 1) { throw 'artifact-ownership-unverified' }
        if (Test-Path -LiteralPath $working) { Remove-Item -LiteralPath $working -Recurse -ErrorAction Stop }
        $vm = Import-VM -Path $baseline[0].FullName -Copy -VirtualMachinePath $working -ErrorAction Stop
        if ([string]$vm.Id -ne $Incarnation) { throw 'vm-incarnation-conflict' }
    }
    Rename-VM -VM $vm -NewName $poolName -ErrorAction Stop
    Set-VM -VM $vm -AutomaticStartAction Nothing -AutomaticCheckpointsEnabled $false -ErrorAction Stop
    Get-VMNetworkAdapter -VM $vm | Disconnect-VMNetworkAdapter -ErrorAction Stop
    if ([string]$vm.State -ne 'Off' -or @(Get-VMHardDiskDrive -VM $vm).Count -ne 0 -or @(Get-VMSnapshot -VM $vm).Count -ne 0) { throw 'cleanup-unverified' }
}
function Restore-ConstructLicenseMachine {
    param([string]$Incarnation, $Descriptor, [string]$OperationId, [string]$PoolRoot)
    $name = [string]$Descriptor.name
    Assert-ConstructChildVmName $name
    Assert-ConstructChildHardware $Descriptor.hardware
    $pool = Get-ConstructLicensePath $Incarnation $PoolRoot
    Assert-ConstructLicensePool $pool $Incarnation -Initialized
    $vm = Get-VM -Id ([guid]$Incarnation) -ErrorAction Stop
    if ($vm.Name -ine ('lic-' + ([guid]$Incarnation).ToString('N')) -and $vm.Name -ine $name) { throw 'vm-incarnation-conflict' }
    if ([string]$vm.State -ne 'Off') { throw 'vm-not-off' }
    $disk = Get-ConstructChildDiskPath -Name $name -VhdPath $Descriptor.vhdPath
    $marker = $disk + '.childvm.json'
    if (Test-Path -LiteralPath $marker) {
        $record = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if ($record.operationId -ne $OperationId -or $record.id -ne $Incarnation -or $record.name -ine $name) { throw 'artifact-ownership-unverified' }
    } else {
        if (Get-ConstructChildVmObject $name) { throw 'name-taken' }
        if (Test-Path -LiteralPath $disk) { throw 'name-taken' }
        $record = @{ name=$name; id=$Incarnation; operationId=$OperationId; rootDisk=$disk; disks=@($disk); configPath=(Join-Path $pool 'working'); licensePool=$pool }
        Write-ConstructChildOwnership -Marker $marker -Record $record -CreateNew
    }
    Rename-VM -VM $vm -NewName $name -ErrorAction Stop
    $vm = Get-VM -Id ([guid]$Incarnation) -ErrorAction Stop
    if (@(Get-VMHardDiskDrive -VM $vm).Count -eq 0) {
        if (-not (Test-Path -LiteralPath $disk)) { $null = New-VHD -Path $disk -Dynamic -SizeBytes ([long]$Descriptor.hardware.diskGb * 1GB) -ErrorAction Stop }
        Add-VMHardDiskDrive -VM $vm -ControllerType SCSI -ControllerNumber 0 -ControllerLocation 0 -Path $disk -ErrorAction Stop
    }
    if ($Descriptor.hardware.networkAttached) { Get-VMNetworkAdapter -VM $vm | Connect-VMNetworkAdapter -SwitchName $Descriptor.switchName -ErrorAction Stop }
    Set-ConstructChildMedia -Name $name -InstallMediaPath $Descriptor.installMediaPath -AuxiliaryMediaPath $Descriptor.auxiliaryMediaPath -BootOrder $Descriptor.hardware.bootOrder
}
function Remove-ConstructLicenseMachine {
    param([string]$Incarnation, [string]$PoolRoot)
    $pool = Get-ConstructLicensePath $Incarnation $PoolRoot
    if (-not (Test-Path -LiteralPath $pool)) { return }
    Assert-ConstructLicensePool $pool $Incarnation
    $vm = Get-VM -Id ([guid]$Incarnation) -ErrorAction SilentlyContinue
    if ($vm) {
        if ($vm.Name -ine ('lic-' + ([guid]$Incarnation).ToString('N')) -or [string]$vm.State -ne 'Off' -or @(Get-VMHardDiskDrive -VM $vm).Count -ne 0) { throw 'key-in-use' }
        Remove-VM -VM $vm -Force -ErrorAction Stop
    }
    Remove-Item -LiteralPath $pool -Recurse -ErrorAction Stop
}
