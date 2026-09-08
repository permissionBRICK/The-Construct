$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '../../../..')).Path
$file = Join-Path $repo 'drivers/hyperv-local/HyperVLocal.ChildVm.ps1'
$tokens = $null; $errors = $null
$null = [Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
if ($errors.Count -ne 0) { throw 'PowerShell parse failed' }
. $file
$script:checks = 0
function Check($Condition, $Message) { if (-not $Condition) { throw $Message }; $script:checks++ }
function Get-CimInstance {
    param($ClassName, $Filter)
    switch ($ClassName) {
        'Win32_OperatingSystem' { @{ TotalVisibleMemorySize = 32 * 1024 * 1024; FreePhysicalMemory = 24 * 1024 * 1024 } }
        'Win32_ComputerSystem' { @{ NumberOfLogicalProcessors = 8 } }
        'Win32_Volume' { @{ Name = 'C:\'; DriveLetter = 'C:'; Capacity = 100GB; FreeSpace = 80GB } }
    }
}
function Get-Volume { param($FilePath) @{ DriveLetter = 'C'; Path = 'C:\' } }
function Get-VM { @([pscustomobject]@{ Name = 'unmanaged'; Id = [guid]'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa'; State = 'Running'; CPUUsage = 17; MemoryDemand = 3GB; Uptime = [TimeSpan]::FromSeconds(42); Generation = 2; ProcessorCount = 4; MemoryAssigned = 4GB; ConfigurationLocation = 'C:\config' }) }
function Get-VMMemory { param($VM) @{ Startup = 4GB; Maximum = 8GB; DynamicMemoryEnabled = $true } }
function Get-VMSnapshot { param($VM) @([pscustomobject]@{ Name = 'snapshot' }) }
function Get-VMHardDiskDrive { param($VM, $VMSnapshot) @([pscustomobject]@{ Path = 'C:\child.vhdx' }) }
function Get-VHD { param($Path) if ($Path -eq 'C:\child.vhdx') { @{ Size = 30GB; FileSize = 5GB; ParentPath = 'C:\parent.vhdx' } } else { @{ Size = 30GB; FileSize = 10GB; ParentPath = '' } } }
# Shadow filesystem reads only inside this bounded test process.
function Resolve-Path { param($LiteralPath) [pscustomobject]@{ ProviderPath = $LiteralPath } }
function Join-Path { param($Path, $ChildPath) $Path.TrimEnd('\') + '\' + $ChildPath }
function Test-Path { param($LiteralPath) $true }
function Get-Item { param($LiteralPath) [pscustomobject]@{ Length = 1MB; FullName = $LiteralPath; PSIsContainer = $false } }
$r = Get-ConstructHostInventory -Artifacts @(@{ artifact = 'disk:C:\orphan.vhdx'; path = 'C:\orphan.vhdx'; volume = 'C:\' })
Check $r.complete 'Complete inventory'
Check ($r.host.totalRamBytes -eq 32GB) 'RAM KiB conversion'
Check ($r.host.freeRamBytes -eq 24GB) 'Physical free RAM'
Check ($r.host.volumes[0].freeBytes -eq 80GB) 'Physical volume free'
Check ($r.vms.Count -eq 1) 'Enumerates unmanaged VM'
Check ($r.vms[0].cpuUsagePercent -eq 17) 'Observed CPU percentage'
Check ($r.vms[0].memoryDemandBytes -eq 3GB) 'Observed RAM demand'
Check ($r.vms[0].uptimeSeconds -eq 42) 'Observed uptime'
Check ($r.vms[0].disks.Count -eq 2) 'Deduplicates checkpoint and parent chains'
Check ($r.vms[0].dynamicMemory -eq $true) 'Dynamic memory flag'
Check ($r.vms[0].memoryMaximumBytes -eq 8GB) 'Dynamic maximum'
Check ($r.artifacts[0].presence -eq 'present') 'Retained artifact evidence'
function Get-VHD { param($Path) throw 'sentinel-secret' }
$r = Get-ConstructHostInventory
Check (-not $r.complete) 'Unreadable disk fails closed'
Check ($r.problems -contains 'disk-unreadable') 'Structured disk failure'
Check (-not (($r | ConvertTo-Json -Depth 12) -match 'sentinel-secret')) 'Dependency text is not disclosed'
function Get-VMHardDiskDrive { param($VM, $VMSnapshot) @([pscustomobject]@{ Path = ''; DiskNumber = 3 }) }
$r = Get-ConstructHostInventory
Check (-not $r.complete) 'Documented pass-through limitation fails closed'
Check ($r.problems -contains 'passthrough-disk-unavailable') 'Pass-through limitation is explicit'
function Get-VM { throw 'sentinel-secret' }
$r = Get-ConstructHostInventory
Check (-not $r.complete) 'Enumeration failure fails closed'
Check ($r.problems -contains 'vm-enumeration-unavailable') 'Enumeration code'
Write-Output "$script:checks capacity inventory checks passed"
