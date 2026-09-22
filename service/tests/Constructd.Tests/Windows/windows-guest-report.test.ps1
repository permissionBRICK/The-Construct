$ErrorActionPreference = 'Stop'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('windows-report-' + [guid]::NewGuid().ToString('n'))
$previousProgramFiles = $env:ProgramFiles
$previousSystemRoot = $env:SystemRoot
$global:WindowsReportTest_checks = 0
function Check($condition, $message) {
    if (-not $condition) { throw $message }; $global:WindowsReportTest_checks++
}
try {
    New-Item -ItemType Directory -Path "$temp/provision" -Force | Out-Null
    # Replace only fixed Windows paths in the executable fixture; use real files
    # for reports and receipts so separate invocations exercise restart behavior.
    $env:ProgramFiles = [IO.Path]::Combine($temp, 'Program Files')
    $env:SystemRoot = "$temp/Windows"
    $scriptPath = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Construct/WindowsActivation/construct-report.ps1'))
    New-Item -ItemType Directory -Path (Split-Path $scriptPath -Parent) -Force | Out-Null
    $source = Get-Content -Raw "$PSScriptRoot/../../../src/Constructd.Core/Logic/WindowsGuestReport.ps1"
    $entryPath = "$temp/firstlogon-report.ps1"
    $source.Replace('__CONSTRUCT_PLATFORM__', 'hyperv').Replace('C:\provision', "$temp/provision") | Set-Content $entryPath
    $global:WindowsReportTest_now = [datetime]'2026-09-22T10:00:00Z'
    $global:WindowsReportTest_command = $null
    $global:WindowsReportTest_key = ''
    $global:WindowsReportTest_partial = '3V66T'
    $global:WindowsReportTest_licensed = 0
    $global:WindowsReportTest_edition = 'Professional'
    $global:WindowsReportTest_kms = $false
    $global:WindowsReportTest_ipkFails = $false
    $global:WindowsReportTest_atoFails = $false
    $global:WindowsReportTest_interruptActivation = $false
    $global:WindowsReportTest_calls = @()
    $global:WindowsReportTest_registered = @()
    function Get-Date { $global:WindowsReportTest_now }
    function Get-Service { [pscustomobject]@{ Status = 'Running' } }
    function Set-Service { param($Name, $StartupType) }
    function Start-Service { param($Name) }
    function Get-CimInstance {
        param($ClassName, $Filter)
        if ($ClassName -eq 'Win32_OperatingSystem') { [pscustomobject]@{ Caption = 'Windows 11 Pro' } }
        else { [pscustomobject]@{ OfflineInstallationId = ('2' * 63); ProductKeyID = '12345-12345-12345-12345'; LicenseFamily = $global:WindowsReportTest_edition; LicenseIsAddon = $false; ID = 'f69e9210-2b3d-4fcb-8e3e-ef673bc5ac8c'; PartialProductKey = $global:WindowsReportTest_partial; LicenseStatus = $global:WindowsReportTest_licensed; LicenseStatusReason = 0; ProductKeyChannel = 'Retail'; GracePeriodRemaining = 1440; EvaluationEndDate = [datetime]'2027-01-01T00:00:00Z' } }
    }
    function Get-ItemProperty {
        param($Path, $ErrorAction)
        if ($Path -like '*CurrentVersion') { [pscustomobject]@{ EditionID = $global:WindowsReportTest_edition; InstallationType = 'Client' } }
        else { [pscustomobject]@{ 'Construct.WindowsKey' = $global:WindowsReportTest_key; 'Construct.WindowsActivation' = $global:WindowsReportTest_command } }
    }
    function Resolve-DnsName {
        param($Name, $Type, $ErrorAction)
        if ($global:WindowsReportTest_kms) { [pscustomobject]@{ Name = $Name } } else { throw 'No KMS record' }
    }
    function New-Item {
        param($Path, $ItemType, [switch]$Force)
        if ($Path -notlike 'HKLM:*') { Microsoft.PowerShell.Management\New-Item -Path $Path -ItemType $ItemType -Force:$Force }
    }
    function New-ItemProperty {
        param($Path, $Name, $Value, $PropertyType, [switch]$Force)
        $global:WindowsReportTest_report = $Value | ConvertFrom-Json
    }
    function New-ScheduledTaskAction { param($Execute, $Argument) @{ execute = $Execute; argument = $Argument } }
    function New-ScheduledTaskTrigger {
        param([switch]$AtStartup, [switch]$Once, $At, $RepetitionInterval)
        @{ startup = [bool]$AtStartup; once = [bool]$Once; at = $At; interval = $RepetitionInterval }
    }
    function New-ScheduledTaskSettingsSet {
        param($MultipleInstances, [switch]$StartWhenAvailable, [switch]$AllowStartIfOnBatteries, [switch]$DontStopIfGoingOnBatteries, $ExecutionTimeLimit)
        @{ multiple = $MultipleInstances; catchup = [bool]$StartWhenAvailable; limit = $ExecutionTimeLimit }
    }
    function Register-ScheduledTask {
        param($TaskName, $Action, $Trigger, $Settings, $User, $RunLevel, [switch]$Force)
        $global:WindowsReportTest_registered += @{ name = $TaskName; action = $Action; triggers = $Trigger; settings = $Settings; user = $User; runLevel = $RunLevel }
    }
    function Start-ScheduledTask { param($TaskName) $global:WindowsReportTest_started = $TaskName }
    function cscript.exe {
        $global:WindowsReportTest_calls += ,@($args)
        if ($global:WindowsReportTest_interruptActivation) { throw 'Simulated interruption' }
        if ($args -contains '/ipk') {
            $global:LASTEXITCODE = if ($global:WindowsReportTest_ipkFails) { 1 } else { 0 }
            if (-not $global:WindowsReportTest_ipkFails) { $global:WindowsReportTest_partial = $global:WindowsReportTest_key.Substring($global:WindowsReportTest_key.Length - 5) }
        } else {
            $global:LASTEXITCODE = if ($global:WindowsReportTest_atoFails) { 1 } else { 0 }
            $global:WindowsReportTest_licensed = if ($global:WindowsReportTest_atoFails) { 0 } else { 1 }
        }
    }

    & $entryPath
    Check (Test-Path -LiteralPath $scriptPath) 'First logon copies the script into its protected task directory'
    Check ($global:WindowsReportTest_registered.Count -eq 1) 'First logon registers a persistent task'
    $task = $global:WindowsReportTest_registered[0]
    Check ($task.user -eq 'SYSTEM' -and $task.runLevel -eq 'Highest') 'Task runs without interactive logon'
    Check ($task.triggers.Count -eq 2 -and $task.triggers[0].startup) 'Task starts after reboot'
    Check ($task.triggers[1].interval.TotalMinutes -eq 1) 'Task keeps checking each minute'
    Check ($task.settings.multiple -eq 'IgnoreNew') 'Task executions cannot overlap'
    Check ($task.action.argument.Contains('"' + $scriptPath.Replace('/', '\') + '"') -or $task.action.argument.Contains('"' + $scriptPath + '"')) 'Task quotes its protected script path'
    Check ($task.action.argument.EndsWith('-PollKey')) 'Task checks keys without repeating first-logon setup'
    Check ($global:WindowsReportTest_started -eq $task.name -and $global:WindowsReportTest_report.firstLogonDone) 'Task starts and readiness is reported'
    Check ($global:WindowsReportTest_calls.Count -eq 0) 'Setup does not activate without a key'

    $global:WindowsReportTest_now = $global:WindowsReportTest_now.AddDays(2)
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_report.activation -eq 'not-activated' -and $global:WindowsReportTest_calls.Count -eq 0) 'Missing key remains pending days later'
    $global:WindowsReportTest_key = 'ABCDE-FGHIJ-KLMNO-PQRST-UVWXY'
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_calls.Count -eq 2 -and $global:WindowsReportTest_calls[0] -contains '/ipk' -and $global:WindowsReportTest_calls[1] -contains '/ato') 'Late key is installed and activated'
    Check ($global:WindowsReportTest_report.activation -eq 'activated' -and $global:WindowsReportTest_report.partialKey -eq 'UVWXY') 'Activation result and partial key reach host'
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_calls.Count -eq 2) 'Another task invocation does not repeat activation'
    $global:WindowsReportTest_key = ''
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_report.activation -eq 'activated' -and $global:WindowsReportTest_calls.Count -eq 2) 'Host clearing delivery preserves activation report'

    $global:WindowsReportTest_key = 'ABCDE-FGHIJ-KLMNO-PQRST-12345'
    $global:WindowsReportTest_licensed = 0
    $global:WindowsReportTest_atoFails = $true
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_calls.Count -eq 4 -and $global:WindowsReportTest_report.activation -eq 'failed') 'Failed activation is reported'
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_calls.Count -eq 4 -and $global:WindowsReportTest_report.activation -eq 'failed') 'Failed attempt is not repeated after restart'

    $global:WindowsReportTest_key = 'ABCDE-FGHIJ-KLMNO-PQRST-67890'
    $global:WindowsReportTest_ipkFails = $true
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_calls.Count -eq 5 -and $global:WindowsReportTest_report.activation -eq 'failed') 'Failed installation never calls activation'
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_calls.Count -eq 5) 'Failed installation is not repeated'

    $global:WindowsReportTest_key = 'ABCDE-FGHIJ-KLMNO-PQRST-99999'
    $global:WindowsReportTest_interruptActivation = $true
    $interrupted = $false
    try { & $scriptPath -PollKey } catch { $interrupted = $true }
    Check $interrupted 'Interrupted execution is simulated'
    $global:WindowsReportTest_interruptActivation = $false
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_calls.Count -eq 6 -and $global:WindowsReportTest_report.activation -eq 'failed') 'Receipt prevents replay after an interrupted execution'
    $receipt = Get-Content -Raw (Join-Path (Split-Path $scriptPath -Parent) 'attempted-key.sha256')
    Check (-not $receipt.Contains($global:WindowsReportTest_key) -and $receipt.Trim().Length -eq 95) 'Receipt contains only a digest'

    $global:WindowsReportTest_key = 'ABCDE-FGHIJ-KLMNO-PQRST-AAAAA'
    $global:WindowsReportTest_kms = $true
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_calls.Count -eq 6 -and $global:WindowsReportTest_report.kms) 'KMS detection skips pool activation'
    $global:WindowsReportTest_kms = $false
    $global:WindowsReportTest_edition = 'ProfessionalEval'
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_calls.Count -eq 6 -and $global:WindowsReportTest_report.evaluation) 'Evaluation edition skips pool activation'
    Check ($global:WindowsReportTest_registered.Count -eq 1) 'Polling never re-registers the task'

    $global:WindowsReportTest_edition = 'Professional'
    $proxmoxPath = $scriptPath
    $source.Replace('__CONSTRUCT_PLATFORM__', 'proxmox').Replace('C:\provision\construct-report.ps1', $proxmoxPath).Replace('C:\provision', "$temp/provision") | Set-Content $proxmoxPath
    & $proxmoxPath
    $proxmoxReport = Get-Content -Raw "$temp/provision/windows-report.json" | ConvertFrom-Json
    Check ($proxmoxReport.firstLogonDone -and $proxmoxReport.activation -eq 'not-activated') 'Proxmox still publishes its readiness report'
    Check ($global:WindowsReportTest_registered.Count -eq 2 -and $global:WindowsReportTest_calls.Count -eq 6) 'Proxmox installs the recurring reporter without running activation'
    & $proxmoxPath -PollKey
    Check ($global:WindowsReportTest_calls.Count -eq 6) 'Proxmox polling remains observational'
    $proxmoxReport = Get-Content -Raw "$temp/provision/windows-report.json" | ConvertFrom-Json
    Check ($proxmoxReport.license.status -eq 0 -and $proxmoxReport.license.graceMinutes -eq 1440) 'Actual status and grace duration are reported independently of the operation'
    Check ($proxmoxReport.license.evaluationEnd -eq $null) 'Ordinary installations do not invent evaluation expiry'
    $global:WindowsReportTest_edition = 'ProfessionalEval'
    & $proxmoxPath
    Check ($global:WindowsReportTest_registered.Count -eq 3) 'Evaluation also installs the recurring reporter'
    $proxmoxReport = Get-Content -Raw "$temp/provision/windows-report.json" | ConvertFrom-Json
    Check ($proxmoxReport.license.evaluationEnd -ne $null) 'Evaluation reports its actual expiry'
    # The new protocol performs only local key installation and CID application.
    # Model distinct task invocations using the real persisted operation receipt.
    $global:WindowsReportTest_edition = 'Professional'
    $global:WindowsReportTest_partial = '3V66T'
    $global:WindowsReportTest_licensed = 0
    $global:WindowsReportTest_localCalls = @()
    $global:WindowsReportTest_depositFails = $false
    function Invoke-CimMethod {
        param($InputObject, $MethodName, $Arguments)
        $global:WindowsReportTest_localCalls += $MethodName
        if ($MethodName -eq 'InstallProductKey') { $global:WindowsReportTest_partial = $Arguments.ProductKey.Substring(24) }
        if ($MethodName -eq 'DepositOfflineConfirmationId') {
            if ($global:WindowsReportTest_depositFails) { return [pscustomobject]@{ ReturnValue = 1 } }
            $global:WindowsReportTest_licensed = 1
        }
        [pscustomobject]@{ ReturnValue = 0 }
    }
    $source.Replace('__CONSTRUCT_PLATFORM__', 'hyperv').Replace('__CONSTRUCT_ALLOCATION__', '123456').Replace('__CONSTRUCT_SETUP_KEY__', '3V66T').Replace('C:\provision', "$temp/provision") | Set-Content $scriptPath
    $command = @{ id=('a' * 32); allocationId='old'; action='prepare'; key='ABCDE-FGHIJ-KLMNO-PQRST-UVWXY' }
    $global:WindowsReportTest_command = $command | ConvertTo-Json -Compress
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_localCalls.Count -eq 0) 'Old allocation cannot install a key'
    $command.allocationId = '123456'
    $global:WindowsReportTest_command = $command | ConvertTo-Json -Compress
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_localCalls.Count -eq 1 -and $global:WindowsReportTest_report.operation.stage -eq 'prepared') 'Prepare installs only the key and reports its IID'
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_localCalls.Count -eq 1) 'Repeated prepare does not reinstall the key'
    $command.action = 'apply'; $command.confirmationId = '1' * 48
    $global:WindowsReportTest_command = $command | ConvertTo-Json -Compress
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_localCalls.Count -eq 2 -and $global:WindowsReportTest_report.activation -eq 'activated') 'Saved CID is applied locally and actual activation verified'
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_localCalls.Count -eq 2) 'Duplicate CID delivery does not run again'
    Check ($global:WindowsReportTest_calls.Count -eq 6) 'New protocol never invokes slmgr online activation'
    $command.id = 'b' * 32
    $global:WindowsReportTest_licensed = 0
    $global:WindowsReportTest_depositFails = $true
    $global:WindowsReportTest_command = $command | ConvertTo-Json -Compress
    & $scriptPath -PollKey
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_localCalls.Count -eq 3 -and $global:WindowsReportTest_report.operation.stage -eq 'failed') 'A rejected CID is attempted once and never falls back online'
    $command.id = 'c' * 32; $command.action = 'prepare'
    $global:WindowsReportTest_partial = 'OWNED'
    $global:WindowsReportTest_command = $command | ConvertTo-Json -Compress
    & $scriptPath -PollKey
    Check ($global:WindowsReportTest_localCalls.Count -eq 3) 'Personal replacement key is not overwritten'
    Write-Host "PASS: $global:WindowsReportTest_checks Windows guest report checks"
} finally {
    Remove-Variable -Name WindowsReportTest_* -Scope Global
    $env:ProgramFiles = $previousProgramFiles
    $env:SystemRoot = $previousSystemRoot
    if (Test-Path $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
