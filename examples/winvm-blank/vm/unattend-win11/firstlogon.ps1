# First-logon provisioning for the blank Windows 11 guest.
# Runs once after unattended setup (see autounattend.xml FirstLogonCommands).
# Goals: remote control (SSH), minimal footprint (aggressive debloat - we only
# need a desktop session and .NET), and a quiet system (no Defender/Update/
# consumer noise during UI tests).

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path C:\provision | Out-Null
Start-Transcript -Path C:\provision\firstlogon.transcript.txt

# --- OpenSSH server (primary control channel from the Linux host) -----------
$cap = Get-WindowsCapability -Online -Name 'OpenSSH.Server*'
if ($cap.State -ne 'Installed') {
    Add-WindowsCapability -Online -Name $cap.Name
}
Set-Service sshd -StartupType Automatic
Start-Service sshd
New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell `
    -Value 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'
# 25H2: the capability install does NOT create the inbound firewall rule
# (unlike Server 2022) - without this, SSH from the host times out.
netsh advfirewall firewall add rule name=sshd dir=in action=allow protocol=TCP localport=22 | Out-Null

# --- RDP (enabled here, not in the unattend - see autounattend.xml note) ----
reg add "HKLM\SYSTEM\CurrentControlSet\Control\Terminal Server" /v fDenyTSConnections /t REG_DWORD /d 0 /f
Enable-NetFirewallRule -DisplayGroup 'Remote Desktop' -ErrorAction SilentlyContinue

# --- WinRM as fallback channel ----------------------------------------------
winrm quickconfig -q 2>&1 | Out-Null
Set-Item WSMan:\localhost\Service\Auth\Basic $true
Set-Item WSMan:\localhost\Service\AllowUnencrypted $true

# --- Debloat: remove consumer Appx packages -----------------------------------
# Remove everything except frameworks (VCLibs/Xaml/AppRuntime/.NET Native) and
# the Defender UI. Edge stays (removal is fragile and it is inert when unused).
# Get Help is a Start-menu dependency on this 25H2 image: removing it
# reproducibly crashes StartMenuExperienceHost; restoring it repairs Start.
$keep = @('Microsoft.GetHelp','*VCLibs*','*UI.Xaml*','*WindowsAppRuntime*','*NET.Native*',
          '*SecHealthUI*','*DesktopAppInstaller*')
Get-AppxProvisionedPackage -Online | ForEach-Object {
    $name = $_.DisplayName
    if (-not ($keep | Where-Object { $name -like $_ })) {
        Write-Host "deprovision: $name"
        Remove-AppxProvisionedPackage -Online -PackageName $_.PackageName -ErrorAction SilentlyContinue | Out-Null
    }
}
Get-AppxPackage -AllUsers | ForEach-Object {
    $name = $_.Name
    if (-not ($keep | Where-Object { $name -like $_ }) -and $_.NonRemovable -eq $false) {
        Write-Host "remove appx: $name"
        Remove-AppxPackage -Package $_.PackageFullName -AllUsers -ErrorAction SilentlyContinue
    }
}

# OneDrive
Stop-Process -Name OneDrive -Force -ErrorAction SilentlyContinue
foreach ($od in "$env:SystemRoot\SysWOW64\OneDriveSetup.exe", "$env:SystemRoot\System32\OneDriveSetup.exe") {
    if (Test-Path $od) { Start-Process -Wait $od -ArgumentList '/uninstall' -ErrorAction SilentlyContinue }
}

# --- Debloat: consumer features, Copilot, widgets, telemetry ------------------
# No auto-installed suggested apps / consumer content
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent" /v DisableWindowsConsumerFeatures /t REG_DWORD /d 1 /f
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\CloudContent" /v DisableCloudOptimizedContent /t REG_DWORD /d 1 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SilentInstalledAppsEnabled /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SystemPaneSuggestionsEnabled /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager" /v SubscribedContent-338388Enabled /t REG_DWORD /d 0 /f
# Copilot / AI
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsCopilot" /v TurnOffWindowsCopilot /t REG_DWORD /d 1 /f
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsAI" /v DisableAIDataAnalysis /t REG_DWORD /d 1 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v ShowCopilotButton /t REG_DWORD /d 0 /f
# Widgets, chat, search box off the taskbar
reg add "HKLM\SOFTWARE\Policies\Microsoft\Dsh" /v AllowNewsAndInterests /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarDa /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarMn /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Search" /v SearchboxTaskbarMode /t REG_DWORD /d 0 /f
# Telemetry to minimum, no diagtrack
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection" /v AllowTelemetry /t REG_DWORD /d 0 /f
Set-Service DiagTrack -StartupType Disabled -ErrorAction SilentlyContinue
Stop-Service DiagTrack -Force -ErrorAction SilentlyContinue
# No second-chance OOBE nags
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement" /v ScoobeSystemSettingEnabled /t REG_DWORD /d 0 /f

# --- Defender: keep (Tamper Protection blocks removal on client SKUs) but ----
# exclude the work directories and try to disable realtime scanning.
# Add your own work directories here (build trees, DB data dirs, ...).
Add-MpPreference -ExclusionPath 'C:\provision' -ErrorAction SilentlyContinue
Add-MpPreference -ExclusionPath 'C:\work' -ErrorAction SilentlyContinue
Set-MpPreference -DisableRealtimeMonitoring $true -ErrorAction SilentlyContinue

# --- Windows Update: disable after OpenSSH capability is installed -----------
Stop-Service wuauserv -Force -ErrorAction SilentlyContinue
Set-Service wuauserv -StartupType Disabled
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU" /v NoAutoUpdate /t REG_DWORD /d 1 /f

# --- Background noise services ------------------------------------------------
foreach ($svc in 'WSearch','SysMain','MapsBroker') {
    Set-Service $svc -StartupType Disabled -ErrorAction SilentlyContinue
    Stop-Service $svc -Force -ErrorAction SilentlyContinue
}

# --- Keep the interactive session usable for UI automation -------------------
# Never blank/sleep/hibernate (AC and DC), no lock screen, no screensaver, no
# idle-lock - the desktop must stay live for FlaUI and VNC screenshots.
powercfg /change monitor-timeout-ac 0
powercfg /change monitor-timeout-dc 0
powercfg /change standby-timeout-ac 0
powercfg /change standby-timeout-dc 0
powercfg /change hibernate-timeout-ac 0
powercfg /change hibernate-timeout-dc 0
powercfg /hibernate off
# Console lock display off timeout (modern standby lock) - 0 = never.
powercfg /setacvalueindex SCHEME_CURRENT SUB_VIDEO VIDEOCONLOCK 0
powercfg /setactive SCHEME_CURRENT
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization" /v NoLockScreen /t REG_DWORD /d 1 /f
reg add "HKCU\Control Panel\Desktop" /v ScreenSaveActive /t REG_SZ /d 0 /f
reg add "HKCU\Control Panel\Desktop" /v ScreenSaveTimeOut /t REG_SZ /d 0 /f
# No machine inactivity limit (would lock the session).
reg add "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System" /v InactivityTimeoutSecs /t REG_DWORD /d 0 /f
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v HideFileExt /t REG_DWORD /d 0 /f

# --- .NET Framework 4.8 sanity check ----------------------------------------
$rel = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release
"NETFX Release: $rel (>=533320 means 4.8.1)" | Out-File C:\provision\netfx.txt

# --- TLS 1.2 for PowerShell downloads (choco etc.) ---------------------------
reg add "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" /v SchUseStrongCrypto /t REG_DWORD /d 1 /f
reg add "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" /v SchUseStrongCrypto /t REG_DWORD /d 1 /f

Stop-Transcript

# Marker the host polls for over SSH.
Set-Content -Path C:\provision\firstlogon.done -Value (Get-Date -Format o)
