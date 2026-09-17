# First-logon provisioning for the blank Windows Server guest.
# Runs once after unattended setup (see autounattend.xml FirstLogonCommands).
# Goal: make the guest remotely controllable (SSH) and quiet (no Defender/
# Update/lock-screen noise that makes UI tests flaky), then drop a marker
# file the host polls for.

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
# PowerShell as default shell for ssh commands
New-Item -Path 'HKLM:\SOFTWARE\OpenSSH' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\OpenSSH' -Name DefaultShell `
    -Value 'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe'

# --- WinRM as fallback channel ----------------------------------------------
winrm quickconfig -q 2>&1 | Out-Null
Set-Item WSMan:\localhost\Service\Auth\Basic $true
Set-Item WSMan:\localhost\Service\AllowUnencrypted $true

# --- Remove noise sources ----------------------------------------------------
# Defender: on Server it is a removable feature - removing it entirely is the
# only jitter-free option on a disposable test VM.
Set-MpPreference -DisableRealtimeMonitoring $true -ErrorAction SilentlyContinue
Uninstall-WindowsFeature Windows-Defender -ErrorAction SilentlyContinue

# Windows Update: disable after OpenSSH capability is installed (it needs WU).
Stop-Service wuauserv -Force -ErrorAction SilentlyContinue
Set-Service wuauserv -StartupType Disabled
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU" /v NoAutoUpdate /t REG_DWORD /d 1 /f

# Search indexing / SysMain
Set-Service WSearch -StartupType Disabled -ErrorAction SilentlyContinue
Stop-Service WSearch -Force -ErrorAction SilentlyContinue
Set-Service SysMain -StartupType Disabled -ErrorAction SilentlyContinue
Stop-Service SysMain -Force -ErrorAction SilentlyContinue

# --- Keep the interactive session usable for UI automation -------------------
# Never sleep / never blank the console session.
powercfg /change monitor-timeout-ac 0
powercfg /change standby-timeout-ac 0
# No lock screen, no screensaver.
reg add "HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization" /v NoLockScreen /t REG_DWORD /d 1 /f
reg add "HKCU\Control Panel\Desktop" /v ScreenSaveActive /t REG_SZ /d 0 /f
# Show file extensions (helps debugging via screenshots).
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v HideFileExt /t REG_DWORD /d 0 /f

# --- .NET Framework 4.8 sanity check ----------------------------------------
$rel = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full').Release
"NETFX Release: $rel (>=528040 means 4.8)" | Out-File C:\provision\netfx.txt

# --- TLS 1.2 for PowerShell downloads (choco etc.) ---------------------------
reg add "HKLM\SOFTWARE\Microsoft\.NETFramework\v4.0.30319" /v SchUseStrongCrypto /t REG_DWORD /d 1 /f
reg add "HKLM\SOFTWARE\Wow6432Node\Microsoft\.NETFramework\v4.0.30319" /v SchUseStrongCrypto /t REG_DWORD /d 1 /f

Stop-Transcript

# Marker the host polls for over SSH.
Set-Content -Path C:\provision\firstlogon.done -Value (Get-Date -Format o)
