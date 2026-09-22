#Requires -Version 5.1
#Requires -Modules Hyper-V
# Run elevated on a disposable Hyper-V test host. Creates only an OFF VM; no
# Windows installation, key or Microsoft activation is involved.
param([Parameter(Mandatory=$true)][string]$StorageRoot)
. (Join-Path $PSScriptRoot '../drivers/hyperv-local/HyperVLocal.ChildVm.ps1')
. (Join-Path $PSScriptRoot '../drivers/hyperv-local/HyperVLocal.WindowsLicense.ps1')
$ErrorActionPreference='Stop'
$suffix=[guid]::NewGuid().ToString('N').Substring(0,8)
$name='license-proof-'+$suffix
if ($StorageRoot.Contains("'")) { throw 'Use a test storage path without apostrophes.' }
$root=Join-Path $StorageRoot $name
$id=$null
$configs=@()
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    $helper=Join-Path $root 'functions.ps1'
    @(Get-Command -CommandType Function | Where-Object Name -like '*-Construct*' | ForEach-Object { 'function ' + $_.Name + ' {' + $_.Definition + '}' }) | Set-Content -LiteralPath $helper -Encoding UTF8
    function Isolated([string]$Code) {
        $script = '$ErrorActionPreference=''Stop''; $ProgressPreference=''SilentlyContinue''; ' + ". '$helper'; " + $Code
        & powershell.exe -NoProfile -NonInteractive -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script)))
        if ($LASTEXITCODE -ne 0) { throw 'Isolated driver operation failed' }
    }
    $h=@{ cpus=2; ramMb=4096; diskGb=1; generation=2; secureBoot=$true; secureBootTemplate='microsoftWindows'; tpm=$true; bootOrder=@('disk'); networkAttached=$true }
    $switch=(Get-VMSwitch | Select-Object -First 1).Name
    $disk=Join-Path $root 'first.vhdx'
    @{name=$name;hardware=$h;vhdPath=$disk;switchName=$switch;operationId='proof-first';licenseBaseline=$true} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'descriptor.json')
    Isolated "New-ConstructChildVm (Get-Content -Raw '$root\descriptor.json' | ConvertFrom-Json)"
    $vm=Get-VM -Name $name
    $id=[string]$vm.Id
    $configs+= (Get-Content ($disk+'.childvm.json') -Raw | ConvertFrom-Json).configPath
    $mac=(Get-VMNetworkAdapter -VM $vm).MacAddress
    Checkpoint-VM -VM $vm -SnapshotName tenant-data
    Isolated "Clear-ConstructLicenseAllocation -Name '$name' -Incarnation '$id' -VhdPath '$disk'"
    Isolated "Save-ConstructLicenseMachine -Name '$name' -Incarnation '$id' -VhdPath '$disk'"
    Isolated "Clear-ConstructLicenseAllocation -Name '$name' -Incarnation '$id' -VhdPath '$disk'"
    Isolated "Save-ConstructLicenseMachine -Name '$name' -Incarnation '$id' -VhdPath '$disk'"
    if (Test-Path $disk) { throw 'Tenant disk remains' }
    $parked=Get-VM -Id ([guid]$id)
    if (@(Get-VMHardDiskDrive -VM $parked).Count -ne 0 -or @(Get-VMSnapshot -VM $parked).Count -ne 0) { throw 'Tenant storage remains' }
    $next=$name+'-next'
    $nextDisk=Join-Path $root 'next.vhdx'
    @{name=$next;hardware=$h;vhdPath=$nextDisk;switchName=$switch} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $root 'descriptor.json')
    Isolated "Restore-ConstructLicenseMachine -Incarnation '$id' -Descriptor (Get-Content -Raw '$root\descriptor.json' | ConvertFrom-Json) -OperationId proof-next -PoolRoot '$root'"
    Isolated "`$reused=Get-VM -Name '$next'; if ([string]`$reused.Id -ne '$id' -or (Get-VMNetworkAdapter -VM `$reused).MacAddress -ne '$mac') { throw 'Identity changed' }"
    Isolated "Clear-ConstructLicenseAllocation -Name '$next' -Incarnation '$id' -VhdPath '$nextDisk'"
    Isolated "Save-ConstructLicenseMachine -Name '$next' -Incarnation '$id' -VhdPath '$nextDisk'"
    Isolated "Remove-ConstructLicenseMachine -Incarnation '$id' -PoolRoot '$root'"
    if (Get-VM -Id ([guid]$id) -ErrorAction SilentlyContinue) { throw 'Retirement left VM' }
    'PASS: create, checkpoint removal, repeated park, reuse with same VM GUID and MAC, second park, retirement'
} finally {
    $marker=Join-Path $root 'first.vhdx.childvm.json'
    if (Test-Path -LiteralPath $marker) {
        $owned=Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
        if (-not $id) { $id=$owned.id }
        $configs+= $owned.configPath
    }
    if ($id) { Get-VM -Id ([guid]$id) -ErrorAction SilentlyContinue | Remove-VM -Force -ErrorAction Stop }
    foreach ($config in @($configs | Select-Object -Unique)) { if (Test-Path -LiteralPath $config) { Remove-Item -LiteralPath $config -Recurse -ErrorAction Stop } }
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -ErrorAction Stop }
}
