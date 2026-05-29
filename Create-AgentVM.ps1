#Requires -Version 5.1
<#
.SYNOPSIS
    Creates a Hyper-V VM called "Agent-VM" for the construct sandbox,
    matching the configuration of the existing agent-vm on this host.

.DESCRIPTION
    1. Self-elevates to Administrator if not already elevated.
    2. Ensures Hyper-V is installed (installs it + reboots if missing).
    2a. If the agent VM already exists, offers an interactive menu to
        reprovision it (keep data), completely reinstall it (delete the VM +
        disk after a confirmation, then create fresh), or quit.
    3. Creates a Gen-2 VM with the same processor count and settings as the
       existing agent-vm.
    4. Prompts for VM RAM (default: half host RAM, capped at 24 GB) and virtual
       disk size (default 50 GB), and for an Ubuntu Server ISO. The RAM and disk
       prompts are skipped when -MemoryGB / -DiskSizeGB are supplied.
    5. Boots the VM from the ISO so the user can install the OS.
    6. After the user confirms installation is complete, unmounts the ISO
       and removes the DVD drive.

.PARAMETER MemoryGB
    VM RAM in GB. If omitted (0), a recommendation (half the host RAM, capped at
    24 GB) is calculated and the user is prompted.

.PARAMETER DiskSizeGB
    Virtual hard disk size in GB. If omitted (0), the user is prompted
    (default 50 GB).

.PARAMETER Projects
    Comma-separated project profiles to load. When supplied it is forwarded to
    Provision-AgentVM.ps1, which then skips its own project prompt.

.PARAMETER AgentPassword
    Optional new login password for the agent user. When supplied it is forwarded
    to Provision-AgentVM.ps1, which applies it at the end of provisioning.
#>
[CmdletBinding()]
param(
    [double]$MemoryGB  = 0,
    [int]$DiskSizeGB   = 0,
    [string]$Projects,
    [string]$AgentPassword
)

# ── Self-elevate to Administrator ────────────────────────────────────────────
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Relaunching as Administrator..." -ForegroundColor Yellow
    # Forward every bound parameter so the elevated copy keeps the caller's
    # choices (RAM/disk/projects). When invoked from an already-elevated
    # Auto-Install.ps1 this branch is skipped and we run in-process.
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        if ($kv.Value -is [System.Management.Automation.SwitchParameter]) {
            if ($kv.Value.IsPresent) { $argList += "-$($kv.Key)" }
        } else {
            $argList += "-$($kv.Key)"; $argList += "`"$($kv.Value)`""
        }
    }
    Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
    exit
}

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }

# Shared helpers: interactive menu, reinstall confirmation, VM teardown.
$commonLib = Join-Path $PSScriptRoot "lib\AgentVm.Common.ps1"
if (-not (Test-Path -LiteralPath $commonLib)) { throw "Required helper not found: $commonLib" }
. $commonLib

# ── Configuration (mirrors the existing agent-vm) ────────────────────────────
$VmName            = "Agent-VM"
$SwitchName        = "Default Switch"
$ProcessorCount    = 12
$Generation        = 2
$VhdPath           = "C:\ProgramData\Microsoft\Windows\Virtual Hard Disks\$VmName.vhdx"
$CheckpointType    = "Standard"
$AutoStart         = "StartIfRunning"
$AutoStop          = "Save"

# ── 0. Ensure OpenSSH client ─────────────────────────────────────────────────
Write-Step "Checking OpenSSH client"

if (-not (Get-Command ssh.exe -ErrorAction SilentlyContinue)) {
    Write-Host "    OpenSSH client not found. Installing via winget..." -ForegroundColor Yellow
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        throw "ssh.exe not found and winget is not available to install it. Install OpenSSH manually: Settings > Apps > Optional Features > OpenSSH Client."
    }
    & winget.exe install --id Microsoft.OpenSSH.Beta --accept-source-agreements --accept-package-agreements
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("PATH", "User")
    if (-not (Get-Command ssh.exe -ErrorAction SilentlyContinue)) {
        throw "ssh.exe still not found after winget install. Restart PowerShell or install OpenSSH manually."
    }
    Write-Ok "OpenSSH client installed"
} else {
    Write-Ok "OpenSSH client is available"
}

# ── 1. Ensure Hyper-V and its platform features ──────────────────────────────
# Validate hardware virtualization + enable the required Windows features (may
# reboot, or abort with BIOS / Windows-Home guidance). Shared with Auto-Install.ps1,
# which runs the same check up front; harmless to re-confirm here when chained.
Ensure-HyperV

# ── 2. Handle an already-installed VM (reprovision / reinstall / quit) ───────
if (Get-VM -Name $VmName -ErrorAction SilentlyContinue) {
    Write-Host ""
    Write-Warning "The agent VM '$VmName' is already installed on this host."

    $choice = Show-Menu -Title "What would you like to do?" -Options @(
        "Reprovision    re-run provisioning on the existing VM (keeps all data)",
        "Reinstall      DELETE the VM and its disk, then create a fresh one",
        "Quit           make no changes and exit"
    ) -Default 0

    if ($choice -eq 0) {
        # Reprovision only -- hand straight to the provisioner and stop here.
        Write-Step "Reprovisioning the existing VM"
        $provisionScript = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
        if (-not (Test-Path -LiteralPath $provisionScript)) { throw "Provision-AgentVM.ps1 not found in $PSScriptRoot." }
        $VmHostname = "$($VmName.ToLower()).mshome.net"
        $provArgs = @{ VmHost = $VmHostname; HostAlias = $VmName.ToLower() }
        if ($PSBoundParameters.ContainsKey('Projects'))      { $provArgs['Projects']      = $Projects }
        if ($PSBoundParameters.ContainsKey('AgentPassword')) { $provArgs['AgentPassword'] = $AgentPassword }
        & $provisionScript @provArgs
        return
    }
    elseif ($choice -eq 1) {
        # Complete reinstall -- confirm the irreversible delete (defaults to NO),
        # tear the VM down, then fall through to the normal creation steps below.
        if (-not (Confirm-Reinstall -VmName $VmName)) {
            Write-Note "Reinstall cancelled. No changes made."
            return
        }
        Remove-AgentVm -VmName $VmName
        Write-Note "Existing VM removed; continuing with a fresh install."
    }
    else {
        Write-Note "No changes made."
        return
    }
}

# ── 3. VM RAM (recommend half of host, max 24 GB; prompt unless passed in) ────
Write-Step "Memory allocation"

$totalBytes     = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
$halfBytes      = [math]::Floor($totalBytes / 2)
$maxBytes       = 24GB
# Recommend half the host RAM, capped at 24 GB but never below 4 GB.
$recommendBytes = [math]::Max([math]::Min($halfBytes, $maxBytes), 4GB)
# Round down to nearest 2 MB boundary (Hyper-V requirement)
$recommendBytes = $recommendBytes - ($recommendBytes % 2MB)
$recommendGB    = [math]::Round($recommendBytes / 1GB, 1)

if ($MemoryGB -gt 0) {
    $memoryBytes = [long]($MemoryGB * 1GB)
} else {
    Write-Host ("    System RAM: {0:N1} GB" -f ($totalBytes / 1GB)) -ForegroundColor White
    Write-Host "    Recommended VM RAM: $recommendGB GB" -ForegroundColor White
    $input = Read-Host "    Enter VM RAM in GB (press Enter for $recommendGB)"
    if ([string]::IsNullOrWhiteSpace($input)) {
        $memoryBytes = $recommendBytes
    } else {
        $memoryBytes = [long]([double]$input * 1GB)
        if ($memoryBytes -lt 2GB) {
            Write-Warning "Minimum VM RAM is 2 GB. Using 2 GB."
            $memoryBytes = [long]2GB
        }
    }
}
# Round down to nearest 2 MB boundary (Hyper-V requirement)
$memoryBytes = $memoryBytes - ($memoryBytes % 2MB)

Write-Ok ("VM RAM: {0:N1} GB" -f ($memoryBytes / 1GB))

# ── 4. VHD size (prompt unless passed in) ────────────────────────────────────
Write-Step "Virtual hard disk size"

$defaultSizeGB = 50
if ($DiskSizeGB -gt 0) {
    $diskSizeGB = $DiskSizeGB
    if ($diskSizeGB -lt 10) {
        Write-Warning "Minimum disk size is 10 GB. Using 10 GB."
        $diskSizeGB = 10
    }
} else {
    Write-Host "    Recommended disk size: ${defaultSizeGB} GB" -ForegroundColor White
    $input = Read-Host "    Enter disk size in GB (press Enter for $defaultSizeGB)"
    if ([string]::IsNullOrWhiteSpace($input)) {
        $diskSizeGB = $defaultSizeGB
    } else {
        $diskSizeGB = [int]$input
        if ($diskSizeGB -lt 10) {
            Write-Warning "Minimum disk size is 10 GB. Using 10 GB."
            $diskSizeGB = 10
        }
    }
}
Write-Ok "Disk size: $diskSizeGB GB"

# ── 5. Select Ubuntu Server ISO ──────────────────────────────────────────────
Write-Step "Select Ubuntu Server ISO"

# Prefer an autoinstall ISO sitting next to this script (built by
# bin/build-autoinstall-iso.sh). If found, use it automatically; that also
# drives the unattended autoinstall + auto-provision flow further down.
$autoIso = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*autoinstall*.iso' -File -ErrorAction SilentlyContinue |
           Sort-Object LastWriteTime -Descending | Select-Object -First 1

if ($autoIso) {
    $isoPath = $autoIso.FullName
    Write-Ok "Found autoinstall ISO next to this script: $isoPath"
} else {
    Write-Host "    No autoinstall ISO found in $PSScriptRoot." -ForegroundColor White
    Write-Host "    A file picker dialog will open. Select your Ubuntu Server .iso file." -ForegroundColor White

    Add-Type -AssemblyName System.Windows.Forms
    $filePicker = New-Object System.Windows.Forms.OpenFileDialog
    $filePicker.Title  = "Select Ubuntu Server ISO"
    $filePicker.Filter = "ISO files (*.iso)|*.iso"
    $filePicker.InitialDirectory = Join-Path ([Environment]::GetFolderPath("UserProfile")) "Downloads"

    if ($filePicker.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) {
        Write-Warning "No ISO selected. Aborting."
        exit 1
    }
    $isoPath = $filePicker.FileName
}
Write-Ok "ISO: $isoPath"

# ── 6. Create the VM ────────────────────────────────────────────────────────
Write-Step "Creating VM '$VmName'"

New-VM -Name $VmName `
       -Generation $Generation `
       -MemoryStartupBytes $memoryBytes `
       -SwitchName $SwitchName `
       -NewVHDPath $VhdPath `
       -NewVHDSizeBytes ($diskSizeGB * 1GB) | Out-Null

Write-Ok "VM created"

# ── 7. Configure VM to match existing agent-vm ──────────────────────────────
Write-Step "Configuring VM settings"

Set-VM -Name $VmName `
       -ProcessorCount $ProcessorCount `
       -StaticMemory `
       -CheckpointType $CheckpointType `
       -AutomaticStartAction $AutoStart `
       -AutomaticStopAction $AutoStop `
       -AutomaticCheckpointsEnabled $true

Write-Ok "Processors: $ProcessorCount, Dynamic Memory: off, Checkpoint: $CheckpointType"

# Disable Secure Boot (required for Ubuntu without Microsoft UEFI keys)
Set-VMFirmware -VMName $VmName -EnableSecureBoot Off
Write-Ok "Secure Boot: off"

# Attach the ISO as a DVD drive on the SCSI controller
Add-VMDvdDrive -VMName $VmName -ControllerNumber 0 -ControllerLocation 1 -Path $isoPath
Write-Ok "ISO attached on SCSI 0:1"

# Set boot order: DVD first, then hard drive, then network
$dvd  = Get-VMDvdDrive  -VMName $VmName
$hdd  = Get-VMHardDiskDrive -VMName $VmName
$nic  = Get-VMNetworkAdapter -VMName $VmName
Set-VMFirmware -VMName $VmName -BootOrder $dvd, $hdd, $nic
Write-Ok "Boot order: DVD -> HDD -> Network"

# ── 8. Start the VM ─────────────────────────────────────────────────────────
Write-Step "Starting VM '$VmName'"

Start-VM -Name $VmName
Write-Ok "VM is running. The Ubuntu installer should boot from the ISO."

$VmHostname = "$($VmName.ToLower()).mshome.net"
$isAutoinstall = (Split-Path $isoPath -Leaf) -match "autoinstall"

if ($isAutoinstall) {
    # ── 8a. Autoinstall: poll SSH until the VM is reachable ──────────────────
    Write-Host ""
    Write-Host "    Autoinstall ISO detected. Waiting for the VM to finish installing" -ForegroundColor Yellow
    Write-Host "    and become reachable via SSH at $VmHostname. This takes about 5 minutes ..." -ForegroundColor Yellow
    Write-Host ""

    $pollInterval = 15

    # We deliberately do NOT verify SSH *login* here. Authenticating with the
    # bootstrap key from Windows requires locked-down file ACLs (Windows OpenSSH
    # silently ignores keys with too-open permissions) -- that handling lives in
    # Provision-AgentVM.ps1, which also has its own reachability wait and a
    # password fallback. Here we only need to know the unattended install has
    # finished and the VM is back up, which we detect purely from the SSH port.

    # Helper: is TCP/22 open on the VM right now?
    # We probe with a raw TcpClient rather than Test-NetConnection: during the
    # autoinstall wait the VM name isn't resolvable yet, and Test-NetConnection
    # emits progress + name-resolution/ping banners that $ProgressPreference and
    # -WarningAction don't fully silence. A bare socket connect with a short
    # timeout is completely silent and tests exactly what we care about. Any
    # failure (no DNS, refused, timeout) is swallowed and reported as "not open".
    function Test-SshPort {
        $client = New-Object System.Net.Sockets.TcpClient
        try {
            $iar = $client.BeginConnect($VmHostname, 22, $null, $null)
            if ($iar.AsyncWaitHandle.WaitOne(3000)) {
                $client.EndConnect($iar)   # throws if the connect actually failed
                return $true
            }
            return $false
        } catch {
            return $false
        } finally {
            $client.Close()
        }
    }

    # SSH typically comes up only ONCE -- when the freshly installed OS boots
    # after the unattended install -- not during the install. So we just wait
    # for the port to open, give it a short settle, and hand off. No
    # reboot-detection loop, and no hard error: the wait is bounded, and if it
    # expires we proceed anyway (Provision-AgentVM.ps1 has its own reachability
    # wait + retries, and tolerates the VM being mid-reboot).
    $deadline = (Get-Date).AddMinutes(20)
    Write-Host "    Waiting for SSH port to open..." -ForegroundColor DarkGray
    while (-not (Test-SshPort)) {
        if ((Get-Date) -gt $deadline) {
            Write-Note "SSH still not reachable after 20 min; handing off to provisioning anyway."
            break
        }
        Write-Host "    Not reachable yet -- retrying in $pollInterval seconds..." -ForegroundColor DarkGray
        Start-Sleep -Seconds $pollInterval
    }

    # Wait a little and then end, regardless of whether the VM happens to reboot
    # in the meantime -- provisioning re-checks reachability before it connects.
    Start-Sleep -Seconds 20
    Write-Ok "Handing off to provisioning"
} else {
    # ── 8b. Manual install: ask the user ─────────────────────────────────────
    Write-Host ""
    Write-Host "    A VM console window will open. Complete the Ubuntu Server install" -ForegroundColor Yellow
    Write-Host "    using EXACTLY these settings so provisioning works:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "      - Install variant : Ubuntu Server (minimized)" -ForegroundColor White
    Write-Host "      - Your server's name (hostname) : agent-vm" -ForegroundColor White
    Write-Host "      - Username : agent" -ForegroundColor White
    Write-Host "      - Password : agent" -ForegroundColor White
    Write-Host "      - Install OpenSSH server : YES (enable it)" -ForegroundColor White
    Write-Host ""
    Write-Host "    When the OS installation is FINISHED (and the VM has rebooted into" -ForegroundColor Yellow
    Write-Host "    the installed system), come back here and press Enter." -ForegroundColor Yellow
    Write-Host ""

    # Open a VMConnect window so the user can interact with the installer
    try { vmconnect.exe localhost $VmName } catch {}

    while ($true) {
        $answer = Read-Host "Is the Ubuntu installation complete and asking you to remove the installation medium? (y/n)"
        if ($answer -eq 'y') { break }
    }
}

# ── 9. Unmount ISO and remove DVD drive ──────────────────────────────────────
Write-Step "Cleaning up: removing ISO and DVD drive"

Set-VMDvdDrive -VMName $VmName -ControllerNumber 0 -ControllerLocation 1 -Path $null
Write-Ok "ISO unmounted"

Remove-VMDvdDrive -VMName $VmName -ControllerNumber 0 -ControllerLocation 1
Write-Ok "DVD drive removed"

$hdd = Get-VMHardDiskDrive -VMName $VmName
$nic = Get-VMNetworkAdapter -VMName $VmName
Set-VMFirmware -VMName $VmName -BootOrder $hdd, $nic
Write-Ok "Boot order updated: HDD -> Network"

if ($isAutoinstall) {
    # ── 10. Auto-provision: run Provision-AgentVM.ps1 ────────────────────────
    Write-Step "Launching Provision-AgentVM.ps1"

    $provisionScript = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
    if (-not (Test-Path $provisionScript)) {
        Write-Warning "Provision-AgentVM.ps1 not found in $PSScriptRoot. Skipping provisioning."
    } else {
        $provArgs = @{ VmHost = $VmHostname; HostAlias = $VmName.ToLower() }
        # Forward the project selection if we were given one (from Auto-Install.ps1
        # or the caller) so Provision-AgentVM.ps1 skips its own project prompt.
        if ($PSBoundParameters.ContainsKey('Projects'))      { $provArgs['Projects']      = $Projects }
        if ($PSBoundParameters.ContainsKey('AgentPassword')) { $provArgs['AgentPassword'] = $AgentPassword }
        & $provisionScript @provArgs
    }
} else {
    Write-Host ""
    Write-Host "Done. VM '$VmName' is ready." -ForegroundColor Green
    Write-Host "    Hostname for SSH: $VmHostname" -ForegroundColor White
    Write-Host "    You can now run Provision-AgentVM.ps1 to provision the agent." -ForegroundColor White
    Write-Host ""
}
