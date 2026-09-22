# Construct's guest channel runs after the family-specific first-logon script.
# Copy locally before sending the beacon: the host may eject every DVD immediately.
param([switch]$PollKey)
$ErrorActionPreference = 'Stop'
$local = 'C:\provision\construct-report.ps1'
if ('__CONSTRUCT_PLATFORM__' -eq 'hyperv') {
    # The SYSTEM task must execute from a directory ordinary users cannot modify.
    $local = Join-Path $env:ProgramFiles 'Construct\WindowsActivation\construct-report.ps1'
    New-Item -ItemType Directory -Path (Split-Path $local -Parent) -Force | Out-Null
}
if ($PSCommandPath -ne $local) {
    Copy-Item -LiteralPath $PSCommandPath -Destination $local -Force
    & $local -PollKey:$PollKey
    return
}
if ('__CONSTRUCT_PLATFORM__' -eq 'proxmox') {
    if (-not (Get-Service QEMU-GA -ErrorAction SilentlyContinue)) {
        $msi = Get-PSDrive -PSProvider FileSystem | ForEach-Object { Join-Path $_.Root 'guest-agent\qemu-ga-x86_64.msi' } | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
        if (-not $msi) { throw 'QEMU guest agent medium missing' }
        $driverRoot = Split-Path (Split-Path $msi -Parent) -Parent
        $driverVersion = if ((Get-CimInstance Win32_OperatingSystem).Caption -match '2022') { '2k22' } elseif ((Get-CimInstance Win32_OperatingSystem).Caption -match '2025') { '2k25' } else { 'w11' }
        $serialDriver = Join-Path $driverRoot "vioserial\$driverVersion\amd64\vioser.inf"
        if (Test-Path -LiteralPath $serialDriver) { & pnputil.exe /add-driver $serialDriver /install | Out-Null }
        $process = Start-Process msiexec.exe -ArgumentList ('/i "' + $msi + '" /quiet /norestart') -Wait -PassThru
        if ($process.ExitCode -notin @(0,3010)) { throw 'QEMU guest agent install failed' }
    }
    Set-Service QEMU-GA -StartupType Automatic
    Start-Service QEMU-GA
}
if (-not $PollKey -and (Get-Service sshd).Status -ne 'Running') { throw 'OpenSSH server is not running' }
$os = Get-CimInstance Win32_OperatingSystem
$version = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
$product = if ($os.Caption -match '2025') { 'server2025' } elseif ($os.Caption -match '2022') { 'server2022' } elseif ($os.Caption -match 'Windows 11') { 'win11' } else { 'unknown' }
$edition = ([string]$version.EditionID -replace 'Eval','').ToLowerInvariant()
$edition = switch ($edition) { 'professional' { 'pro' } 'professionaln' { 'pro-n' } 'serverstandard' { 'standard' } 'serverdatacenter' { 'datacenter' } default { $edition } }
if ($product -like 'server*' -and $version.InstallationType -eq 'Server Core') { $edition += '-core' }
$kms = $false
try { $kms = @(Resolve-DnsName -Name '_vlmcs._tcp' -Type SRV -ErrorAction Stop).Count -gt 0 } catch { }
function Send-ConstructWindowsReport([string]$Activation) {
    $license = Get-CimInstance SoftwareLicensingProduct -Filter "ApplicationID='55c92734-d682-4d71-983e-d6ec3f16059f'" | Where-Object { $_.PartialProductKey } | Select-Object -First 1
    if ($license.LicenseStatus -eq 1) { $Activation = 'activated' }
    $report = @{ product=$product; edition=$edition; firstLogonDone=$true; activation=$Activation; partialKey=[string]$license.PartialProductKey; kms=$kms; evaluation=([string]$version.EditionID -match 'Eval') } | ConvertTo-Json -Compress
    Set-Content -LiteralPath 'C:\provision\windows-report.json' -Value $report -Encoding UTF8
    if ('__CONSTRUCT_PLATFORM__' -eq 'hyperv') {
        New-Item 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest' -Force | Out-Null
        New-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\Guest' -Name 'Construct.WindowsReport' -Value $report -PropertyType String -Force | Out-Null
    }
}
if ('__CONSTRUCT_PLATFORM__' -eq 'proxmox' -or $kms -or [string]$version.EditionID -match 'Eval') {
    Send-ConstructWindowsReport 'not-activated'
    return
}
if (-not $PollKey) {
    $action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe" -Argument ('-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' + $local + '" -PollKey')
    $triggers = @(
        New-ScheduledTaskTrigger -AtStartup
        New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
    )
    $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Minutes 5)
    Register-ScheduledTask -TaskName 'Construct Windows activation' -Action $action -Trigger $triggers -Settings $settings -User 'SYSTEM' -RunLevel Highest -Force | Out-Null
    Send-ConstructWindowsReport 'not-activated'
    Start-ScheduledTask -TaskName 'Construct Windows activation'
    return
}

# One short check per task run, indefinitely, including after guest reboots.
# Persist a receipt before activation so crashes and failed activations do not
# cause repeated attempts. Store only a digest, never the product key.
$receipt = Join-Path (Split-Path $local -Parent) 'attempted-key.sha256'
$activation = if (Test-Path -LiteralPath $receipt) { 'failed' } else { 'not-activated' }
$key = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Virtual Machine\External' -ErrorAction SilentlyContinue).'Construct.WindowsKey'
if ($key -cmatch '^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$') {
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $digest = [BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($key))) }
    finally { $sha.Dispose() }
    $attempted = if (Test-Path -LiteralPath $receipt) { (Get-Content -LiteralPath $receipt -Raw).Trim() } else { '' }
    if ($attempted -ne $digest) {
        Set-Content -LiteralPath $receipt -Value $digest -Encoding ASCII
        $activation = 'failed'
        # The template's transcript has ended; never echo the key or slmgr output.
        & cscript.exe //Nologo "$env:SystemRoot\System32\slmgr.vbs" /ipk $key *> $null
        if ($LASTEXITCODE -eq 0) { & cscript.exe //Nologo "$env:SystemRoot\System32\slmgr.vbs" /ato *> $null }
    }
}
$key = $null
Send-ConstructWindowsReport $activation
