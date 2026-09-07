# Executed by driver-contract.test.ps1; real functions, isolated cmdlet doubles.
& {
    $script:probeVm=$null; $script:probeDrives=@(); $script:probeDvds=@(); $script:probeFail=$false; $script:probeHostFailure=$false
    $root=Join-Path ([IO.Path]::GetTempPath()) ('child-driver-'+[Guid]::NewGuid().ToString('N'))
    $null=New-Item -ItemType Directory -Path $root
    function Get-VMHost { if($script:probeHostFailure){throw 'host defaults must not be queried'}; [pscustomobject]@{VirtualHardDiskPath=$root;VirtualMachinePath=$root} }
    function Get-VM { param($Name) if($script:probeVm){$script:probeVm} }
    function New-VM { param($Name,$Path,$Generation,$MemoryStartupBytes,[switch]$NoVHD,$SwitchName)
        $null=New-Item -ItemType Directory -Path $Path
        $script:probeVm=[pscustomobject]@{Name=$Name;Id=[Guid]::NewGuid();Path=$Path;ConfigurationLocation=$Path;Generation=$Generation;State='Off'}
        if($script:probeFail){throw 'simulated failure after allocation'}
        $script:probeVm
    }
    function Remove-VM { param($VM,[switch]$Force) $script:probeVm=$null; $script:probeDrives=@();$script:probeDvds=@() }
    function Set-VM { param($Name,$AutomaticCheckpointsEnabled,$AutomaticStopAction,$AutomaticStartAction) }
    function Get-VMSwitch { param($Name) }
    function Get-VMSecurity { param($VMName) [pscustomobject]@{TpmEnabled=$false} }
    function Get-VMKeyProtector { param($VMName) }
    function Set-VMKeyProtector { param($VMName,[switch]$NewLocalKeyProtector) }
    function Enable-VMTPM { param($VMName) }
    function Get-VMHardDiskDrive { param($VMName,$VMSnapshot) $script:probeDrives }
    function Get-VMSnapshot { param($VMName) }
    function Get-VHD { param($Path) [pscustomobject]@{Size=1GB;ParentPath=$null} }
    function New-VHD { param($Path,[switch]$Dynamic,$SizeBytes) [IO.File]::WriteAllText($Path,'disk') }
    function Add-VMHardDiskDrive { param($VMName,$ControllerType,$ControllerNumber,$ControllerLocation,$Path) $script:probeDrives+= [pscustomobject]@{Path=$Path} }
    function Get-VMNetworkAdapter { param($VMName) [pscustomobject]@{Name='nic'} }
    function Set-VMProcessor { param($VMName,$Count) }
    function Set-VMMemory { param($VMName,$DynamicMemoryEnabled,$StartupBytes) }
    function Set-VMFirmware { param($VMName,$EnableSecureBoot,$SecureBootTemplate,$BootOrder) if($BootOrder){$script:probeOrder=$BootOrder} }
    function Get-VMDvdDrive { param($VMName) $script:probeDvds }
    function Add-VMDvdDrive { param($VMName,$ControllerNumber,$ControllerLocation,$Path) $script:probeDvds += [pscustomobject]@{ControllerNumber=$ControllerNumber;ControllerLocation=$ControllerLocation;Path=$Path} }
    function Set-VMDvdDrive { param($VMDvdDrive,$Path) $VMDvdDrive.Path=$Path }
    $iso=Join-Path $root 'install.iso';$aux=Join-Path $root 'aux.iso'
    [IO.File]::WriteAllText($iso,'iso');[IO.File]::WriteAllText($aux,'aux')
    $h=@{cpus=1;ramMb=512;diskGb=1;generation=2;secureBoot=$true;secureBootTemplate='microsoftWindows';tpm=$true;bootOrder=@('installMedia','auxiliaryMedia','disk','network');networkAttached=$true;dynamicMemory=$null}
    $d=@{name='child';hardware=$h;installMediaPath=$iso;auxiliaryMediaPath=$aux;switchName='Default Switch';operationId='test'}
    $disk=Join-Path $root 'child.vhdx';$marker=$disk+'.childvm.json'
    try {
        New-ConstructChildVm $d
        ok 'real create allocates fixed hardware and dual media' ($script:probeVm -and (Test-Path $disk) -and $script:probeDvds.Count -eq 2 -and $script:probeOrder.Count -eq 4)
        $script:probeHostFailure=$true
        try {
            $placement=Get-ConstructChildStorage -Name child -VhdPath $disk
            ok 'existing VM storage uses its configuration location, not changed host defaults' ($placement.configVolume -eq [IO.Path]::GetPathRoot($script:probeVm.ConfigurationLocation))
        } finally { $script:probeHostFailure=$false }
        Set-ConstructChildMedia -Name child -InstallMediaPath $iso -AuxiliaryMediaPath $null -BootOrder @('installMedia','disk')
        ok 'real media detach ejects auxiliary and reapplies device order' (-not $script:probeDvds[1].Path -and $script:probeOrder.Count -eq 2)
        $originalId=$script:probeVm.Id; $script:probeVm.Id=[Guid]::NewGuid()
        $refused=$false;try{Remove-ConstructChildVm child}catch{$refused=$_.Exception.Message -eq 'vm-incarnation-conflict'}
        ok 'foreign incarnation survives removal attempt' ($refused -and (Test-Path $disk) -and $script:probeVm)
        $script:probeVm.Id=$originalId
        Remove-ConstructChildVm child
        Remove-ConstructChildVm child
        ok 'real remove and retry remove owned artifacts only' (-not $script:probeVm -and -not (Test-Path $disk) -and -not (Test-Path $marker) -and (Test-Path $iso))
        $script:probeFail=$true
        try{New-ConstructChildVm $d}catch{}
        $record=Get-Content $marker -Raw|ConvertFrom-Json
        ok 'failure after New-VM leaves durable path ownership before ID' ($script:probeVm -and -not $record.id -and $record.configPath -eq $script:probeVm.Path)
        $ownPath=$script:probeVm.Path;$script:probeVm.Path=$root
        $refused=$false;try{Remove-ConstructChildVm child}catch{$refused=$true}
        ok 'null-ID marker never owns a same-name VM at another path' ($refused -and $script:probeVm)
        $script:probeVm.Path=$ownPath
        Remove-ConstructChildVm child
        ok 'null-ID recovery removes its own partial VM and config directory' (-not $script:probeVm -and -not (Test-Path $ownPath) -and -not (Test-Path $marker))
        $script:probeVm=[pscustomobject]@{Name='child';Id=[Guid]::NewGuid();Path=$root;State='Off'}
        $refused=$false;try{New-ConstructChildVm $d}catch{$refused=$_.Exception.Message -eq 'name-taken'}
        ok 'foreign VM prevents any create allocation' ($refused -and -not (Test-Path $marker))
        $refused=$false;try{Remove-ConstructChildVm child}catch{$refused=$_.Exception.Message -eq 'artifact-ownership-unverified'}
        ok 'foreign VM without marker is never removed' ($refused -and $script:probeVm)
    } finally { Remove-Item -LiteralPath $root -Recurse -Force }
}
