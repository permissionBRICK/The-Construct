#Requires -Version 5.1
<#
.SYNOPSIS
    One-shot Windows entry point: download Ubuntu Server, build the unattended
    autoinstall ISO, and create + provision the Hyper-V agent VM.

.DESCRIPTION
    Does everything from a blank Windows host:

      0. If the agent VM already exists, offers an interactive menu (up front,
         before any download): reprovision the existing VM, completely
         reinstall it (delete the VM + disk after confirmation, then build +
         install fresh), or quit.
      1. Ensures WSL (with a Linux distro) is available -- the ISO remaster needs
         xorriso, which only works properly on Linux. WSL runs the existing
         bin/build-autoinstall-iso.sh unchanged.
      2. Downloads the Ubuntu Server live ISO (latest point release of the
         chosen LTS) and verifies its SHA256, unless one is supplied.
      3. Builds agent-vm-autoinstall.iso next to this script by invoking the
         bash builder inside WSL.
      4. Hands off to Create-AgentVM.ps1, which auto-detects that ISO, creates
         the Gen-2 VM, waits for the unattended install, then runs
         Provision-AgentVM.ps1.

    Run from your local checkout / unzipped copy of the construct repo:

        .\Auto-Install.ps1

.PARAMETER UbuntuRelease
    Ubuntu LTS release line to download (e.g. 24.04). If omitted, the latest
    currently-supported LTS line is detected automatically from Ubuntu's
    meta-release index (falling back to 24.04 if that can't be reached). The
    exact point-release ISO (e.g. 24.04.2) is discovered automatically. Ignored
    if -IsoPath or -IsoUrl is given.

.PARAMETER IsoPath
    Use an existing Ubuntu Server live ISO instead of downloading one.

.PARAMETER IsoUrl
    Download the source ISO from this exact URL instead of discovering it.

.PARAMETER OutputIso
    Where to write the built autoinstall ISO. Defaults to
    <script dir>\agent-vm-autoinstall.iso so Create-AgentVM.ps1 picks it up.

.PARAMETER VmUser / VmPass / VmHost
    Seed identity baked into the autoinstall ISO (defaults agent/agent/agent-vm).

.PARAMETER SourceId
    Ubuntu install source: 'ubuntu-server-minimal' (default) or 'ubuntu-server'.

.PARAMETER WslDistro
    Specific WSL distro to use (defaults to your configured default distro).

.PARAMETER VmMemoryGB
    VM RAM in GB to pass to Create-AgentVM.ps1. If omitted, you are prompted up
    front (recommendation: a third of the host RAM, capped at 24 GB).

.PARAMETER VmDiskGB
    Virtual disk size in GB to pass to Create-AgentVM.ps1. If omitted, you are
    prompted up front (default 50 GB).

.PARAMETER Projects
    Comma-separated project profiles to provision. If omitted, you are prompted
    up front from the profiles in projects/.

.PARAMETER AgentPassword
    Optional login password for the agent user (a manual-fallback credential
    only -- normal access is as root over the pre-seeded pubkey). If omitted, you
    are prompted up front; pressing Enter keeps the default 'agent'. A non-default
    value is applied to the agent user at the end of provisioning.

.PARAMETER GitUserName / GitEmail
    Git identity to apply as the VM's global git config (user.name / user.email).
    If omitted, you are prompted up front, defaulting to the saved value from a
    previous run and then to this host's own git global identity. The choice is
    saved next to the scripts so a later reprovision doesn't need it re-specified.

.PARAMETER SkipChecksum
    Skip SHA256 verification of the downloaded ISO.

.PARAMETER SkipCreateVm
    Build the autoinstall ISO only ("download only"); do not create/provision
    the VM. In this mode the script does NOT self-elevate and does NOT prompt
    for the create/provision choices.

.PARAMETER Force
    Rebuild the autoinstall ISO even if it already exists. By default, if the
    target autoinstall ISO is already in the folder, both the Ubuntu download
    and the WSL build are skipped and the script goes straight to creating the VM.

.PARAMETER Redownload
    Force a fresh download of the latest Ubuntu Server ISO (overwriting any local
    copy) and a rebuild of the autoinstall ISO, instead of reusing what's already
    on disk. Implies -Force. Also offered as a menu choice when the VM exists.
#>
[CmdletBinding()]
param(
    [string]$UbuntuRelease,
    [string]$IsoPath,
    [string]$IsoUrl,
    [string]$OutputIso,
    [string]$VmUser  = "agent",
    [string]$VmPass  = "agent",
    [string]$VmHost  = "agent-vm",
    [ValidateSet("ubuntu-server-minimal", "ubuntu-server")]
    [string]$SourceId = "ubuntu-server-minimal",
    [string]$WslDistro,
    [double]$VmMemoryGB = 0,
    [int]$VmDiskGB = 0,
    [string]$Projects,
    [string]$AgentPassword,
    [string]$GitUserName,
    [string]$GitEmail,
    [switch]$SkipChecksum,
    [switch]$SkipCreateVm,
    [switch]$Force,
    [switch]$Redownload,
    # Pre-select the existing-VM action instead of showing the interactive menu
    # (used by the control-panel extension to drive a chosen action unattended).
    # Maps 1:1 to the menu, automating the up-front choice. When paired with
    # -FromPanel the redundant confirmations are skipped too (the "type yes" delete,
    # the git-identity prompt and the agent-password prompt -- all already handled by
    # the panel); the dirty-repo scan still warns if the VM has unsaved work.
    [ValidateSet("reprovision", "reinstall", "redownload", "export")]
    [string]$Action,
    # With -Action reinstall/redownload, pre-answer the save/restore prompts:
    #   save     export the current config now and restore it afterwards (default)
    #   existing skip the new export; restore a previously saved backup if present
    #   wipe     no save and no restore -- reinstall completely blank
    [ValidateSet("save", "existing", "wipe")]
    [string]$BackupMode,
    # GitHub owner/name + ref this install came from. Forwarded down to
    # Provision-AgentVM.ps1, which records the installed-commit update marker for the
    # control panel at the end of a successful provision. Defaults to the canonical
    # repo; install.ps1 forwards these only when the caller chose a fork/mirror.
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main",
    # Launched from the control-panel extension. Two effects:
    #   1. Skips the end-of-run "Press Enter to exit" pauses so the console closes on
    #      its own and the dashboard (which auto-refreshes) shows the result. In debug
    #      the launcher keeps the console open with -NoExit regardless.
    #   2. Skips the confirmations/prompts the panel already handled: the "type yes"
    #      delete (confirmed in the panel's modal), the git-identity prompt and the
    #      agent-password prompt (both owned by the settings page). The dirty-repo
    #      scan still warns if the VM has uncommitted/unpushed work.
    # A direct PowerShell run leaves this off: it pauses and asks for each of these.
    # Forwarded across the self-elevation relaunch below.
    [switch]$FromPanel
)

$ErrorActionPreference = "Stop"

# End-of-run pause. Skipped when launched from the control panel (-FromPanel): the
# console closes on its own and the dashboard shows the result (in debug the launcher
# keeps it open with -NoExit). A direct PowerShell run pauses so the window stays
# readable. Used for every "Press Enter to exit" exit point below.
function Wait-Exit { if (-not $FromPanel) { Read-Host "Press Enter to exit" | Out-Null } }

# Any terminating error NOT handled by a try/catch below (e.g. missing WSL,
# virtualization disabled in firmware) would normally close the self-elevated
# window before its guidance can be read. Hold the window open instead.
trap {
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host ""
    Wait-Exit
    exit 1
}

# ── Self-elevate to Administrator from the start ─────────────────────────────
# Creating + provisioning the Hyper-V VM needs admin rights, so we elevate up
# front -- before the long download/build -- and run the whole chain (this
# script -> Create-AgentVM.ps1 -> Provision-AgentVM.ps1) in one elevated window.
# Running in-process is what lets the "Press Enter" pause at the very end fire
# only after provisioning finishes (and even if it throws). We skip elevation
# when only building the ISO (-SkipCreateVm needs no admin rights).
if (-not $SkipCreateVm) {
    $isAdmin = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
               ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        # Set up the HOST side of the control panel now -- while we are still the real
        # (non-admin) user -- BEFORE relaunching elevated. The VS Code extensions dir
        # and winget are per-user: in the elevated session $env:USERPROFILE is the
        # admin's profile, so the panel would land where the user's VS Code never looks.
        # This mirrors install.ps1's non-elevated pre-step, so running Auto-Install.ps1
        # directly (Option A / the autoinstall path) -- not only the install.ps1
        # one-liner -- still installs the panel + Remote-SSH. Best-effort: warns, never
        # blocks the install. (The elevated relaunch is admin, so it skips this branch.)
        try {
            . (Join-Path $PSScriptRoot "lib\AgentVm.Common.ps1")
            if (Get-Command Ensure-VSCodeRemoteSsh -ErrorAction SilentlyContinue) { Ensure-VSCodeRemoteSsh | Out-Null }
            if (Get-Command Install-ControlPanelExtension -ErrorAction SilentlyContinue) { Install-ControlPanelExtension -SourceRoot $PSScriptRoot | Out-Null }
            # Record the installed Construct commit (extension + scripts) so the panel's
            # update banner has a base to diff against -- this is the INSTALL side of the
            # marker (Provision records the separate provisionedCommit). Resolve repo/ref as
            # a pair (explicit wins; else preserve the recorded source; else defaults).
            if (Get-Command Set-ConstructInstalledMarker -ErrorAction SilentlyContinue) {
                $exR = ""; $exF = ""
                try { $s = Read-ConstructSettings -Dir $PSScriptRoot; if ($s) { $exR = [string]$s.constructRepo; $exF = [string]$s.constructRef } } catch { }
                $mk = if (Get-Command Resolve-MarkerSource -ErrorAction SilentlyContinue) {
                    Resolve-MarkerSource -Repo $Repo -Ref $Ref -RepoSupplied ($PSBoundParameters.ContainsKey('Repo')) -RefSupplied ($PSBoundParameters.ContainsKey('Ref')) -ExistingRepo $exR -ExistingRef $exF
                } else { @{ Repo = $Repo; Ref = $Ref } }
                Set-ConstructInstalledMarker -Root $PSScriptRoot -Repo $mk.Repo -Ref $mk.Ref | Out-Null
            }
            # NOTE: ffmpeg (for mic passthrough) is installed at the END of provisioning
            # (Provision-AgentVM.ps1), not here — winget can be slow and this pre-step runs
            # BEFORE the user's install prompts, so it shouldn't block the flow up front.
        } catch {
            Write-Warning "Could not set up the control panel on the host (continuing): $($_.Exception.Message)"
        }
        Write-Host "Relaunching as Administrator..." -ForegroundColor Yellow
        # Forward every bound parameter so the elevated copy keeps the caller's
        # choices (release, ISO paths, RAM/disk/projects, switches, ...).
        $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"")
        foreach ($kv in $PSBoundParameters.GetEnumerator()) {
            if ($kv.Value -is [System.Management.Automation.SwitchParameter]) {
                if ($kv.Value.IsPresent) { $argList += "-$($kv.Key)" }
            } else {
                $argList += "-$($kv.Key)"; $argList += "`"$($kv.Value)`""
            }
        }
        $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -PassThru
        # Bring the new elevated console to the foreground (best-effort): after
        # the UAC prompt it can open behind this window. We wait briefly for its
        # main window handle to appear, then focus it. With Windows Terminal as
        # the default host the window belongs to WindowsTerminal.exe (handle
        # stays 0 here), so this quietly does nothing -- hence best-effort.
        try {
            Add-Type -Namespace ConstructWin32 -Name Focus -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
[DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
'@
            $deadline = (Get-Date).AddSeconds(10)
            while ((Get-Date) -lt $deadline -and -not $elevated.HasExited) {
                $elevated.Refresh()
                if ($elevated.MainWindowHandle -ne [IntPtr]::Zero) {
                    [ConstructWin32.Focus]::ShowWindow($elevated.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
                    [ConstructWin32.Focus]::SetForegroundWindow($elevated.MainWindowHandle) | Out-Null
                    break
                }
                Start-Sleep -Milliseconds 200
            }
        } catch { }
        exit
    }
}

# Use a black console background so the coloured output here -- and the colours
# streamed back from the VM over SSH during provisioning -- render with good
# contrast (a freshly elevated window often opens on the default blue). Repaint
# the whole window with Clear-Host. Best-effort: ignored on hosts without RawUI.
try {
    $Host.UI.RawUI.BackgroundColor = [ConsoleColor]::Black
    if ($Host.UI.RawUI.ForegroundColor -eq [ConsoleColor]::Black) {
        $Host.UI.RawUI.ForegroundColor = [ConsoleColor]::Gray
    }
    Clear-Host
} catch { }

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg"   -ForegroundColor Green }
function Write-Note($msg) { Write-Host "    $msg"   -ForegroundColor DarkGray }

# Draw a friendly framed banner around the given lines. Built programmatically
# (ASCII only, so it stays aligned and renders on any console): cyan border,
# green text. Used to tell the user the interactive part is over.
function Show-Banner([string[]]$Lines) {
    $pad   = 2
    $inner = (($Lines | Measure-Object -Property Length -Maximum).Maximum) + ($pad * 2)
    $bar   = "+" + ("-" * $inner) + "+"
    Write-Host ""
    Write-Host ("    " + $bar) -ForegroundColor Cyan
    foreach ($l in $Lines) {
        Write-Host "    |" -ForegroundColor Cyan -NoNewline
        Write-Host ((" " * $pad) + $l + (" " * ($inner - $pad - $l.Length))) -ForegroundColor Green -NoNewline
        Write-Host "|" -ForegroundColor Cyan
    }
    Write-Host ("    " + $bar) -ForegroundColor Cyan
    Write-Host ""
}

# Shared helpers: TUI screens, interactive menu, reinstall confirmation,
# VM teardown, and the Matrix-style Show-ConstructHeader.
$commonLib = Join-Path $PSScriptRoot "lib\AgentVm.Common.ps1"
if (-not (Test-Path -LiteralPath $commonLib)) { throw "Required helper not found: $commonLib" }
. $commonLib

# Full-window TUI for the whole interactive phase: every choice below runs as
# its own screen (wipe + header + the current menu only). Show-AllSet turns it
# back off at the "all set" banner, after which output scrolls as a normal log.
# ISO-only mode (-SkipCreateVm) has no prompts, so it keeps plain log output.
if (-not $SkipCreateVm) { Enable-ConstructTui }

Show-ConstructHeader

# The "all set" banner marks the end of the interactive phase: draw it on a
# fresh screen, then drop out of TUI mode so everything after it -- download,
# build, create, provision -- scrolls as a normal log.
function Show-AllSet([string[]]$Lines) {
    if (Test-ConstructTui) { Clear-Host; Show-ConstructHeader }
    Show-Banner $Lines
    Disable-ConstructTui
    # Echo the collected setup choices as the first log lines (the TUI screens
    # they were entered on are gone by now).
    foreach ($s in @($script:chosenSummary)) { if ($s) { Write-Ok $s } }
}

# Release line used if the latest LTS can't be polled (offline, source changed).
$FallbackUbuntuLts = "24.04"

function Get-LatestUbuntuLts {
    # Return the newest currently-supported Ubuntu LTS line as "YY.MM"
    # (e.g. "24.04"). Source: Ubuntu's canonical meta-release-lts index -- the
    # same data the update-manager uses to detect LTS upgrades. Each stanza is a
    # blank-line-separated block with "Version:" and "Supported:" fields; we take
    # the highest Version among the blocks marked Supported.
    $metaUrl = "https://changelogs.ubuntu.com/meta-release-lts"
    $oldPref = $ProgressPreference; $ProgressPreference = "SilentlyContinue"
    try {
        $meta = (Invoke-WebRequest -Uri $metaUrl -UseBasicParsing -TimeoutSec 20).Content
    } finally {
        $ProgressPreference = $oldPref
    }
    # Invoke-WebRequest hands back the body as a byte[] (not a string) when the
    # response content type isn't recognised as text -- which is the case for
    # this file. Decode to UTF-8 so the -split / regex below operate on text.
    if ($meta -is [byte[]]) { $meta = [System.Text.Encoding]::UTF8.GetString($meta) }

    $bestRank = -1; $bestLine = $null
    foreach ($block in ($meta -split "(?:\r?\n){2,}")) {
        if ($block -notmatch '(?m)^\s*Supported:\s*1\s*$') { continue }
        $vm = [regex]::Match($block, '(?m)^\s*Version:\s*(\d+)\.(\d+)')
        if (-not $vm.Success) { continue }
        $major = [int]$vm.Groups[1].Value
        $minor = [int]$vm.Groups[2].Value
        $rank  = $major * 100 + $minor
        if ($rank -gt $bestRank) {
            $bestRank = $rank
            $bestLine = '{0:D2}.{1:D2}' -f $major, $minor
        }
    }
    if (-not $bestLine) { throw "no Supported LTS entry found in $metaUrl" }
    return $bestLine
}

# Resolve the Ubuntu release line. An explicit -UbuntuRelease always wins; so do
# -IsoPath / -IsoUrl (which bypass release-based discovery entirely). Otherwise
# poll for the latest LTS, falling back to a known-good line if the lookup fails.
if ($PSBoundParameters.ContainsKey('UbuntuRelease') -and -not [string]::IsNullOrWhiteSpace($UbuntuRelease)) {
    Write-Note "Using requested Ubuntu LTS: $UbuntuRelease"
} elseif ($IsoPath -or $IsoUrl) {
    $UbuntuRelease = $FallbackUbuntuLts   # unused for discovery, but keep it defined
} else {
    try {
        $UbuntuRelease = Get-LatestUbuntuLts
        Write-Note "Latest Ubuntu LTS detected: $UbuntuRelease"
    } catch {
        $UbuntuRelease = $FallbackUbuntuLts
        Write-Note "Could not detect latest LTS ($($_.Exception.Message)); falling back to $UbuntuRelease"
    }
}

if (-not $OutputIso) { $OutputIso = Join-Path $PSScriptRoot "$VmHost-autoinstall.iso" }
$buildScript = Join-Path $PSScriptRoot "bin\build-autoinstall-iso.sh"
$bootstrapPubKey = Join-Path $PSScriptRoot "keys\bootstrap_ed25519.pub"

# Common WSL args: the distro selector is optional.
$wslDistroArgs = @()
if ($WslDistro) { $wslDistroArgs = @("-d", $WslDistro) }

# Convert a Windows path to its /mnt/c/... WSL form.
# We map it ourselves rather than calling `wslpath`, because passing a path with
# backslashes through PowerShell -> wsl.exe strips them (wslpath then sees e.g.
# "C:UsersmeDesktop..."). The default WSL automount layout is deterministic:
#   C:\Users\me\x.iso  ->  /mnt/c/Users/me/x.iso
function ConvertTo-WslPath([string]$winPath) {
    $full = [System.IO.Path]::GetFullPath($winPath)
    if ($full -match '^([A-Za-z]):\\(.*)$') {
        $drive = $matches[1].ToLower()
        $rest  = $matches[2] -replace '\\', '/'
        return "/mnt/$drive/$rest"
    }
    throw "Cannot convert to a WSL path (expected a drive-letter path): $winPath"
}

# Prompt the user to pick which project profiles from projects/ to load.
# Returns a comma-separated PROJECTS value (or "default" if none chosen). The
# real UI is the checkbox-style Select-ProjectProfiles in the shared lib; the
# comma prompt below is only a fallback for when that lib isn't loaded.
# Mirrors Select-Projects in Provision-AgentVM.ps1 so the choice can be made up
# front here and passed straight through.
function Select-Projects {
    $projDir = Join-Path $PSScriptRoot "projects"
    if (Get-Command Select-ProjectProfiles -ErrorAction SilentlyContinue) {
        return (Select-ProjectProfiles -ProjectsDir $projDir)
    }

    # --- Fallback (shared lib unavailable): plain comma-list prompt -----------
    if (-not (Test-Path $projDir)) { return "default" }
    $skip = @("default", "project.schema")
    $available = @(Get-ChildItem -LiteralPath $projDir -Filter *.json -File |
                   Where-Object { $skip -notcontains $_.BaseName } |
                   Sort-Object Name)
    if ($available.Count -eq 0) { return "default" }

    Write-Step "Select project configs to load"
    Write-Host "    Each profile installs its runtimes (node/python/.NET) and declares its repos." -ForegroundColor White
    for ($i = 0; $i -lt $available.Count; $i++) {
        Write-Host ("      {0}. {1}" -f ($i + 1), $available[$i].BaseName) -ForegroundColor Yellow
    }
    Write-Host "    Enter numbers or names (comma-separated), 'all', or press Enter for none." -ForegroundColor White
    $sel = Read-Host "    Projects"
    if ([string]::IsNullOrWhiteSpace($sel)) { return "default" }
    if ($sel.Trim().ToLower() -eq "all") {
        return (($available | ForEach-Object { $_.BaseName }) -join ",")
    }

    $chosen = New-Object System.Collections.Generic.List[string]
    foreach ($tok in ($sel -split ",")) {
        $t = $tok.Trim()
        if ($t -eq "") { continue }
        if ($t -match '^[0-9]+$') {
            $idx = [int]$t - 1
            if ($idx -ge 0 -and $idx -lt $available.Count) { $chosen.Add($available[$idx].BaseName) }
            else { Write-Warning "No project numbered '$t' -- ignoring." }
        } else {
            $match = $available | Where-Object { $_.BaseName -eq $t } | Select-Object -First 1
            if ($match) { $chosen.Add($match.BaseName) } else { Write-Warning "Unknown project '$t' -- ignoring." }
        }
    }
    $uniq = @($chosen | Select-Object -Unique)
    if ($uniq.Count -eq 0) { return "default" }
    return ($uniq -join ",")
}

# Run Provision-AgentVM.ps1 in export mode against the existing VM: either a full
# config export to -BackupDir, or (-ScanReposOnly) just a scan of the project
# repos for unsaved work. Throws if the provisioner does (e.g. the VM is
# unreachable); callers decide how to handle that.
function Invoke-VmConfigExport {
    param(
        [Parameter(Mandatory)][string]$VmName,
        [Parameter(Mandatory)][string]$BackupDir,
        [switch]$ScanReposOnly
    )
    $ps = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
    if (-not (Test-Path -LiteralPath $ps)) { throw "Provision-AgentVM.ps1 not found in $PSScriptRoot." }
    $a = @{
        Action    = 'export'
        BackupDir = $BackupDir
        VmHost    = "$($VmName.ToLower()).mshome.net"
        HostAlias = $VmName.ToLower()
        Auto      = $true
    }
    if ($ScanReposOnly) { $a['ScanReposOnly'] = $true }
    & $ps @a
}

# Read the project profile names recorded in a saved backup's
# extracted\backup-info.json, so they can be folded back into the project
# selection after a restore. Returns @() when the file is missing or unreadable.
function Get-BackupProjectNames {
    param([Parameter(Mandatory)][string]$BackupDir)
    $infoFile = Join-Path $BackupDir "extracted\backup-info.json"
    if (-not (Test-Path -LiteralPath $infoFile)) { return @() }
    try {
        $info = Get-Content -LiteralPath $infoFile -Raw | ConvertFrom-Json
        if ($info.addedProjects) { return @($info.addedProjects) }
    } catch { }
    return @()
}

# Quick, non-interactive TCP probe of the VM's SSH port. Used to gate the
# scan/export calls so a powered-off or broken VM doesn't trap the user in the
# provisioner's interactive "enter the hostname" reachability retry loop.
function Test-VmReachable {
    param([Parameter(Mandatory)][string]$VmName, [int]$TimeoutMs = 5000)
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect("$($VmName.ToLower()).mshome.net", 22, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne($TimeoutMs)) { $client.EndConnect($iar); return $true }
        return $false
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

# ── Handle an already-installed VM (reprovision / reinstall / quit) ──────────
# Checked up front -- before the long download/build -- so the user isn't forced
# to wait just to pick "reprovision" or "quit". Skipped in ISO-only mode
# (-SkipCreateVm) and when Hyper-V isn't present yet (no VM can exist, and
# Create-AgentVM.ps1 installs Hyper-V on the fresh path). $HyperVmName must match
# $VmName in Create-AgentVM.ps1.
# Force a fresh Ubuntu download + autoinstall rebuild (overwriting local ISOs)
# rather than reusing what's on disk. Set by -Redownload or the matching menu
# choice; folded into $needBuild below.
$forceDownload = [bool]$Redownload

# Save/restore state, carried from the reinstall menu branch down to the create
# call so a saved config is auto-restored after the fresh install.
$bk                   = Get-ConstructBackupDir -Dir $PSScriptRoot
$restoreDir           = ""     # set when the reinstall flow saves a config to restore
$restoredProjectNames = @()    # project profiles that save generated, to re-provision
$chosenCloneCredB64   = ""     # git credentials for cloning private project repos
$existingVmHandled    = $false # set once the existing-VM menu runs, so the fresh-install restore offer below is skipped

$HyperVmName = "Agent-VM"
if (-not $SkipCreateVm -and (Get-Command Get-VM -ErrorAction SilentlyContinue) -and
    (Get-VM -Name $HyperVmName -ErrorAction SilentlyContinue)) {

    $existingVmHandled = $true
    Show-TuiScreen -Title "The agent VM '$HyperVmName' is already installed on this host."

    if ($PSBoundParameters.ContainsKey('Action')) {
        # The control panel pre-selects the action; skip the interactive menu.
        $choice = switch ($Action) {
            'reprovision' { 0 }
            'reinstall'   { 1 }
            'redownload'  { 2 }
            'export'      { 3 }
        }
        Write-Note "Action selected by the control panel: $Action"
    } else {
        $choice = Show-Menu -Title "What would you like to do?" -Options @(
            "Reprovision    re-run provisioning on the existing VM (keeps all data)",
            "Reinstall      DELETE the VM and its disk, then build + install fresh (reuse downloaded ISOs)",
            "Redownload     DELETE the VM, re-download the latest Ubuntu ISO, rebuild + install fresh",
            "Export config  save the VM's current agent config + auth to this host (no changes to the VM)",
            "Quit           make no changes and exit"
        ) -Default 0
    }

    if ($choice -eq 0) {
        # Reprovision only: we just need the project selection, then run the
        # provisioner against the existing VM -- no download / build / create.
        $reprovProjects = $Projects
        if (-not $PSBoundParameters.ContainsKey('Projects')) { $reprovProjects = Select-Projects }
        Write-Ok "Projects: $reprovProjects"

        # Git identity to (re)apply. Defaults to the saved value, then this host's
        # git identity; saved so it sticks across reprovisions.
        $giParams = @{ Dir = $PSScriptRoot }
        if ($PSBoundParameters.ContainsKey('GitUserName')) { $giParams['Name']  = $GitUserName }
        if ($PSBoundParameters.ContainsKey('GitEmail'))    { $giParams['Email'] = $GitEmail }
        if ($giParams.ContainsKey('Name') -and $giParams.ContainsKey('Email')) { $giParams['NoPrompt'] = $true }
        $reprovGit = Resolve-GitIdentity @giParams

        # Feature 2: if the selected projects clone repos, ask once for credentials.
        $reprovCloneCredB64 = ""
        if (Get-Command Resolve-GitCloneCredential -ErrorAction SilentlyContinue) {
            $reprovCloneCredB64 = Resolve-GitCloneCredential -ProjectsDir (Join-Path $PSScriptRoot 'projects') -Names $reprovProjects
        }

        # No download/build/create on this path -- just re-run the provisioner
        # against the existing VM, so no long time estimate.
        Show-AllSet @(
            "All set -- reprovisioning the existing VM now.",
            "",
            "This re-runs setup on your current VM and keeps all its data.",
            "It usually only takes a few seconds; no further input needed."
        )

        $provisionScript = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
        if (-not (Test-Path -LiteralPath $provisionScript)) { throw "Provision-AgentVM.ps1 not found in $PSScriptRoot." }
        $VmHostname = "$($HyperVmName.ToLower()).mshome.net"
        Write-Step "Reprovisioning the existing VM"
        # Reprovision keeps the existing password; only honour an explicit
        # -AgentPassword passed on the command line (this path has no prompt).
        # -Auto: the finally below owns the pause, so the provisioner stays quiet.
        $reprovArgs = @{ VmHost = $VmHostname; HostAlias = $HyperVmName.ToLower(); Projects = $reprovProjects; Auto = $true }
        # Pass both git values (even if empty) so the provisioner doesn't re-prompt.
        $reprovArgs['GitUserName'] = $reprovGit.Name
        $reprovArgs['GitEmail']    = $reprovGit.Email
        if ($PSBoundParameters.ContainsKey('AgentPassword')) { $reprovArgs['AgentPassword'] = $AgentPassword }
        if ($reprovCloneCredB64) { $reprovArgs['GitCloneCredentialsB64'] = $reprovCloneCredB64 }
        try {
            & $provisionScript @reprovArgs
        } catch {
            # Show the failure ABOVE the pause so it's readable even when the
            # window was launched by double-click / right-click "Run with
            # PowerShell" (where it closes the instant the pause returns).
            Write-Host ""
            Write-Host "ERROR: provisioning failed." -ForegroundColor Red
            Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
        } finally {
            Write-Host ""
            Wait-Exit
        }
        return
    }
    elseif ($choice -eq 1 -or $choice -eq 2) {
        # Complete reinstall: confirm the irreversible delete (defaults to NO),
        # tear the VM down, then fall through to the normal fresh-install flow.
        # Choice 2 additionally forces a fresh Ubuntu download + autoinstall
        # rebuild (overwriting the local ISOs) instead of reusing what's on disk.
        if ($choice -eq 2) { $forceDownload = $true }

        # The scan + save talk to the VM. Skip them (with a warning) when it isn't
        # reachable -- e.g. it's powered off or broken, which may be why the user is
        # reinstalling -- so a dead VM can't trap them in the provisioner's
        # interactive reachability retry loop.
        $doSave = $false
        if (Test-VmReachable -VmName $HyperVmName) {
            # Before wiping: scan the VM's repos for uncommitted/unpushed work that
            # the reinstall would destroy, and let the user bail. Best-effort.
            try {
                Show-TuiScreen -Title "Checking the VM's repos for unsaved work" -Body @(
                    "Scanning $HyperVmName for uncommitted or unpushed changes the reinstall would destroy..."
                )
                Invoke-VmConfigExport -VmName $HyperVmName -BackupDir $bk -ScanReposOnly
                $scanFile = Join-Path $bk "repo-scan.json"
                $repos = $null
                if (Test-Path -LiteralPath $scanFile) {
                    try { $repos = Get-Content -LiteralPath $scanFile -Raw | ConvertFrom-Json } catch { $repos = $null }
                }
                if (-not (Confirm-RepoScan -Repos $repos)) {
                    Write-Note "Reinstall cancelled (unsaved work in the VM's repos)."
                    Write-Host ""; Wait-Exit
                    return
                }
            } catch {
                Write-Warning "Could not scan the VM's repos: $($_.Exception.Message)"
                Write-Host "    Proceeding without the unsaved-work check." -ForegroundColor DarkGray
            }

            # Offer to save the current config and auto-restore it after the
            # reinstall (default yes). On success $restoreDir is handed to the
            # create/provision chain below, and the project profiles the export
            # generated are folded into the selection so their repos are re-cloned.
            if ($BackupMode) {
                # Control-panel run: the backup choice was made in the panel.
                $doSave = ($BackupMode -eq 'save')
            } else {
                $doSave = Invoke-TuiConfirm -ScreenTitle "Save & restore the agent config" -Body @(
                    "The VM's current agent config (auth, memory, chat history, skills,",
                    "instruction files, project setup) can be saved to this host and",
                    "restored automatically onto the freshly reinstalled VM."
                ) -Question "Save and auto-restore the config?" `
                  -YesLabel "Yes  save it now and restore it after the reinstall (recommended)" `
                  -NoLabel  "No   reinstall completely blank"
            }
            if ($doSave) {
                try {
                    Show-TuiScreen -Title "Saving the VM's agent config" -Body @(
                        "Exporting auth, memory, skills, instruction files, and project setup to this host..."
                    )
                    Invoke-VmConfigExport -VmName $HyperVmName -BackupDir $bk
                    $restoreDir = $bk
                    $restoredProjectNames = Get-BackupProjectNames -BackupDir $bk
                    Write-Ok "Config saved; it will be restored automatically after the reinstall."
                } catch {
                    Write-Warning "Saving the config failed: $($_.Exception.Message)"
                    # Same screen -- the failure above is context the user needs.
                    $goOn = Invoke-TuiConfirm -NoScreen -DefaultNo `
                        -Question "Continue with the reinstall WITHOUT a saved config?" `
                        -YesLabel "Continue  reinstall blank; the old config is lost" `
                        -NoLabel  "Cancel    keep the VM as it is"
                    if (-not $goOn) {
                        Write-Note "Reinstall cancelled."
                        Write-Host ""; Wait-Exit
                        return
                    }
                }
            }
        } else {
            Write-Warning "The VM isn't reachable over SSH -- skipping the unsaved-work scan and config save."
            Write-Host "    (Start the VM first if you want to save its config before reinstalling.)" -ForegroundColor DarkGray
        }

        # No fresh save -- but if an earlier run left a backup on this host, offer
        # to restore that instead (default yes), so the saved config still comes
        # back after the reinstall even when the VM is dead or the save was skipped.
        if (-not $doSave -and (Test-Path -LiteralPath (Join-Path $bk "extracted\backup-info.json"))) {
            $useBackup = if ($BackupMode) {
                # Restore the earlier backup for both 'save' (the fresh save was
                # skipped/failed -- e.g. VM unreachable) and 'existing'; only a
                # 'wipe' reinstalls blank. Matches the interactive default (yes).
                ($BackupMode -ne 'wipe')
            } else {
                Invoke-TuiConfirm -ScreenTitle "Restore a previously saved config?" -Body @(
                    "A config backup from an earlier run exists on this host. It can restore",
                    "the agent config (auth, memory, chat history, skills, instruction files,",
                    "project setup) automatically after the reinstall."
                ) -Question "Auto-restore the saved config?" `
                  -YesLabel "Yes  restore it onto the fresh VM (recommended)" `
                  -NoLabel  "No   reinstall completely blank"
            }
            if ($useBackup) {
                $restoreDir = $bk
                $restoredProjectNames = Get-BackupProjectNames -BackupDir $bk
                Write-Ok "Saved config loaded; it will be restored automatically after the reinstall."
            }
        }

        # Last-chance "type yes" confirmation for the irreversible delete. Skipped
        # when launched from the control panel (-FromPanel): the user already
        # confirmed the delete in the panel's modal before this console opened, so
        # re-typing "yes" here is just a second confirmation of the same choice. A
        # direct PowerShell run still requires the typed "yes".
        if ($FromPanel) {
            Write-Note "Delete confirmed in the control panel; proceeding with the reinstall."
        } elseif (-not (Confirm-Reinstall -VmName $HyperVmName)) {
            Write-Note "Reinstall cancelled. No changes made."
            Write-Host ""; Wait-Exit
            return
        }
        Show-TuiScreen -Title "Removing the existing VM" -Body @(
            "Powering off '$HyperVmName' and deleting its virtual disk..."
        )
        Remove-AgentVm -VmName $HyperVmName
        if ($forceDownload) {
            Write-Note "Existing VM removed; will re-download the latest Ubuntu ISO and rebuild."
        } else {
            Write-Note "Existing VM removed; continuing with a fresh install."
        }
    }
    elseif ($choice -eq 3) {
        # Export & save the current config to this host -- no changes to the VM.
        if (-not (Test-VmReachable -VmName $HyperVmName)) {
            Show-TuiScreen -Title "The VM isn't reachable over SSH" -Body @(
                "Start the VM, then re-run this script to export its config."
            )
            Write-Host ""; Wait-Exit
            return
        }
        try {
            Show-TuiScreen -Title "Exporting the VM's agent config" -Body @(
                "Saving auth, memory, skills, instruction files, and project setup to this host..."
            )
            Invoke-VmConfigExport -VmName $HyperVmName -BackupDir $bk
            Write-Host ""
            Write-Ok "Saved the VM's current agent config to:"
            Write-Host "      $bk" -ForegroundColor White
            $names = Get-BackupProjectNames -BackupDir $bk
            if ($names.Count -gt 0) {
                Write-Host "      Project profiles captured: $($names -join ', ')" -ForegroundColor White
            }
            Write-Note "It can be auto-restored when you later pick Reinstall."
        } catch {
            Write-Host ""
            Write-Host "ERROR: config export failed." -ForegroundColor Red
            Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
        }
        Write-Host ""; Wait-Exit
        return
    }
    else {
        Write-Note "No changes made."
        Write-Host ""; Wait-Exit
        return
    }
}

# ── No existing VM: offer to restore a config backup left by an earlier run ──
# A fresh install (the existing-VM menu above was skipped -- e.g. the VM was
# deleted by hand) can still pick up a backup cached on this host, exactly like
# the reinstall path's "declined the save" branch. This brings back the saved
# auth/memory/skills AND the git-credentials used to clone private repos, so the
# checkout can authenticate instead of silently failing into an empty repos dir.
if (-not $SkipCreateVm -and -not $existingVmHandled -and
    (Test-Path -LiteralPath (Join-Path $bk "extracted\backup-info.json"))) {
    $useBackup = if ($BackupMode) {
        ($BackupMode -ne 'wipe')
    } else {
        Invoke-TuiConfirm -ScreenTitle "Restore a previously saved config?" -Body @(
            "No agent VM is installed, but a config backup from an earlier run exists",
            "on this host. It can restore the agent config (auth, memory, chat history,",
            "skills, instruction files, project setup, and the credentials used to clone",
            "private repos) automatically onto the freshly installed VM."
        ) -Question "Auto-restore the saved config?" `
          -YesLabel "Yes  restore it onto the new VM (recommended)" `
          -NoLabel  "No   install completely blank"
    }
    if ($useBackup) {
        $restoreDir = $bk
        $restoredProjectNames = Get-BackupProjectNames -BackupDir $bk
        Write-Ok "Saved config loaded; it will be restored automatically after the install."
    }
}

# ── Gather all downstream decisions up front ─────────────────────────────────
# Ask for everything the create-vm + provision scripts need NOW, so the long
# download/build and the VM creation/provisioning can all run unattended. This
# is skipped entirely when only building the ISO (-SkipCreateVm). Any value
# passed on the command line is honoured and not re-prompted.
$chosenMemGB         = $VmMemoryGB
$chosenDiskGB        = $VmDiskGB
$chosenProjects      = $Projects
$chosenAgentPassword = $AgentPassword
$chosenGitName       = $GitUserName
$chosenGitEmail      = $GitEmail

if (-not $SkipCreateVm) {
    # All decisions the create-vm + provision scripts need are asked now, one
    # TUI screen per choice, so the rest of the install can run unattended.

    # VM RAM -- recommend a third of the host RAM (capped at 24 GB), but let the user
    # choose (mirrors the disk-size prompt).
    if (-not $PSBoundParameters.ContainsKey('VmMemoryGB')) {
        $totalBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        $thirdBytes = [math]::Floor($totalBytes / 3)
        $maxBytes   = 24GB
        # Recommend a third of the host RAM, capped at 24 GB but never below 4 GB.
        $recBytes   = [math]::Max([math]::Min($thirdBytes, $maxBytes), 4GB)
        $recBytes   = $recBytes - ($recBytes % 2MB)
        $recGB      = [math]::Round($recBytes / 1GB, 1)
        $ans = Invoke-TuiInput -ScreenTitle "VM memory" -Body @(
            ("System RAM: {0:N1} GB" -f ($totalBytes / 1GB)),
            "Recommended VM RAM: $recGB GB (a third of the host RAM, capped at 24 GB)"
        ) -Prompt "Enter VM RAM in GB (press Enter for $recGB)" -Default "$recGB"
        $chosenMemGB = [double]$ans
    }

    # Virtual disk size (default 50 GB).
    if (-not $PSBoundParameters.ContainsKey('VmDiskGB')) {
        $defDisk = 50
        $ans = Invoke-TuiInput -ScreenTitle "VM disk size" -Body @(
            "Recommended disk size: $defDisk GB (grows on demand; this is the cap)"
        ) -Prompt "Enter disk size in GB (press Enter for $defDisk)" -Default "$defDisk"
        $chosenDiskGB = [int]$ans
        if ($chosenDiskGB -lt 10) { Write-Warning "Minimum disk size is 10 GB. Using 10 GB."; $chosenDiskGB = 10 }
    }

    # Project profiles to provision.
    if (-not $PSBoundParameters.ContainsKey('Projects')) {
        $chosenProjects = Select-Projects
    }

    # Fold in any project profiles a pre-reinstall save generated, so their repos
    # are re-provisioned (and re-cloned) on the fresh VM.
    if ($restoredProjectNames.Count -gt 0) {
        $names = @(($chosenProjects -split ',') + $restoredProjectNames |
                   ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
        $chosenProjects = $names -join ','
        Write-Ok "Including restored project profile(s): $($restoredProjectNames -join ', ')"
    }

    # Feature 2: if any selected project clones repos, ask once for credentials
    # (Enter skips; a restore falls back to the saved git-credentials).
    if (Get-Command Resolve-GitCloneCredential -ErrorAction SilentlyContinue) {
        $chosenCloneCredB64 = Resolve-GitCloneCredential -ProjectsDir (Join-Path $PSScriptRoot 'projects') -Names $chosenProjects
    }

    # Optional login password for the agent user. This is only a manual-fallback
    # credential -- normal access is as root over the pre-seeded pubkey -- so it
    # defaults to the seeded password 'agent'. A different value is applied to the
    # agent user at the very end of provisioning.
    if (-not $PSBoundParameters.ContainsKey('AgentPassword')) {
        if ($FromPanel) {
            # Launched from the control panel: don't prompt. The panel deliberately
            # doesn't collect or store this credential (it's a manual-fallback login
            # only -- normal access is as root over the pre-seeded SSH key), so keep
            # the seeded default 'agent', exactly as pressing Enter would.
            $chosenAgentPassword = "agent"
        } else {
            $chosenAgentPassword = Invoke-TuiInput -ScreenTitle "Agent user password" -Body @(
                "Optional: login password for the 'agent' user. This is a manual-fallback",
                "credential only -- normal access is as root over the pre-seeded SSH key."
            ) -Prompt "Enter agent password (press Enter to keep default 'agent')" -Default "agent"
        }
    }

    # Git identity for the VM's global git config. Defaults to the saved value,
    # then this host's git identity; saved for future reprovisions.
    $giParams = @{ Dir = $PSScriptRoot }
    if ($PSBoundParameters.ContainsKey('GitUserName')) { $giParams['Name']  = $GitUserName }
    if ($PSBoundParameters.ContainsKey('GitEmail'))    { $giParams['Email'] = $GitEmail }
    if ($giParams.ContainsKey('Name') -and $giParams.ContainsKey('Email')) { $giParams['NoPrompt'] = $true }
    # Launched from the control panel: never prompt for git identity -- the settings
    # page owns it. Resolve silently from the passed values, else the saved settings,
    # else this host's git identity (even if only one of name/email was passed).
    if ($FromPanel) { $giParams['NoPrompt'] = $true }
    $gitId = Resolve-GitIdentity @giParams
    $chosenGitName  = $gitId.Name
    $chosenGitEmail = $gitId.Email

    # Summary of the choices, echoed into the log right after the "all set"
    # banner (printing it here would be wiped by the next TUI screen).
    $pwLabel = if ($chosenAgentPassword -and $chosenAgentPassword -ne "agent") { "custom" } else { "default" }
    $gitLabel = if ($chosenGitName -or $chosenGitEmail) { "$chosenGitName <$chosenGitEmail>" } else { "(unset)" }
    $chosenSummary = @(
        ("VM RAM: {0} GB  |  Disk: {1} GB  |  Projects: {2}  |  agent password: {3}" -f $chosenMemGB, $chosenDiskGB, $chosenProjects, $pwLabel),
        ("Git identity: {0}" -f $gitLabel)
    )

    # Confirm the host can actually run the VM BEFORE the long download. This
    # enables Hyper-V + the platform features (rebooting if needed) or aborts
    # with BIOS / Windows-Home guidance. The "all set" banner comes later, once
    # we know the unattended phase can really proceed (ISO present, or WSL OK).
    Ensure-HyperV
}

# If the target autoinstall ISO is already here, skip both the Ubuntu download
# and the WSL build entirely and go straight to creating the VM (-Force / the
# Redownload choice rebuild instead).
$needBuild = $Force -or $forceDownload -or -not (Test-Path -LiteralPath $OutputIso)
if (-not $needBuild) {
    # ISO is ready: nothing to download/build, so from here it's all unattended.
    # The banner ends the TUI phase; the notes below open the normal log.
    if (-not $SkipCreateVm) {
        Show-AllSet @(
            "All set -- sit back and relax!",
            "",
            "The autoinstall ISO is ready, so everything from here is automated:",
            "creating the VM and provisioning the agent.",
            "This takes about 10 minutes total, with no further input needed."
        )
    }

    Write-Step "Autoinstall ISO already present"
    Write-Ok "Found $OutputIso"
    Write-Note "Skipping Ubuntu download and ISO build (pass -Force to rebuild)."
}

if ($needBuild) {
# One screen for the whole pre-build phase: the WSL/xorriso checks below log
# beneath it, and the "all set" banner that follows ends the TUI phase.
Show-TuiScreen -Title "Preparing the unattended install" -Body @(
    "Checking the build prerequisites (repo files, WSL, xorriso)..."
)

# ── 0. Sanity: required repo files present ───────────────────────────────────
Write-Step "Checking repo files"
foreach ($f in @($buildScript, $bootstrapPubKey)) {
    if (-not (Test-Path -LiteralPath $f)) {
        throw "Required file missing: $f`n    Run this from your checkout/unzipped construct repo."
    }
}
Write-Ok "build script and bootstrap key found"

# ── 1. Ensure WSL + a Linux distro ───────────────────────────────────────────
Write-Step "Checking WSL"
if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
    throw @"
WSL is not installed. The ISO remaster needs Linux tooling (xorriso).
Install it once, reboot, then re-run this script:

    wsl --install -d Ubuntu

(After reboot, complete the one-time Ubuntu user setup, then re-run .\Auto-Install.ps1)

Alternatively if you do not need WSL, you can download the precompiled autoinstall ISO from the latest release.
"@
}

# `wsl -l -q` lists installed distros (UTF-16, may contain blanks).
$prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
$distros = (& wsl.exe -l -q 2>$null) | ForEach-Object { ($_ -replace "`0", "").Trim() } | Where-Object { $_ }
$ErrorActionPreference = $prevEAP
if (-not $distros) {
    throw @"
WSL is present but no Linux distribution is installed. Install one, reboot if
prompted, complete its first-run user setup, then re-run this script:

    wsl --install -d Ubuntu
"@
}
Write-Ok ("WSL distro(s): {0}" -f ($distros -join ", "))

# Ensure xorriso + whois (mkpasswd) inside WSL. Run as root so no sudo prompt.
Write-Step "Ensuring xorriso + whois inside WSL"
& wsl.exe @wslDistroArgs -u root -- bash -lc "command -v xorriso >/dev/null 2>&1 && command -v mkpasswd >/dev/null 2>&1 || { apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y xorriso whois; }"
if ($LASTEXITCODE -ne 0) { throw "Failed to install xorriso/whois inside WSL." }
Write-Ok "xorriso + whois present in WSL"

# WSL is confirmed working, so the download + build + create + provision can all
# run unattended now -- tell the user they can step away (unless we're only
# building the ISO, in which case there's nothing to sit through afterwards).
if (-not $SkipCreateVm) {
    Show-AllSet @(
        "All set -- sit back and relax!",
        "",
        "Everything from here is automated: downloading Ubuntu, building",
        "the autoinstall ISO, and creating + provisioning the VM.",
        "This takes about 10 minutes total, with no further input needed."
    )
}

# ── 2. Acquire the source Ubuntu Server ISO ──────────────────────────────────
Write-Step "Source Ubuntu Server ISO"

# Track whether WE downloaded the source ISO. A user-supplied -IsoPath is left
# untouched; only an ISO we fetched is deleted after a successful build.
$srcIsoWasDownloaded = $false

if ($IsoPath) {
    if (-not (Test-Path -LiteralPath $IsoPath)) { throw "IsoPath not found: $IsoPath" }
    $srcIso = (Resolve-Path -LiteralPath $IsoPath).Path
    Write-Ok "Using provided ISO: $srcIso"
} else {
    $srcIsoWasDownloaded = $true
    # Discover the exact point-release file name from the release directory
    # listing unless an explicit URL was given.
    $baseUrl = "https://releases.ubuntu.com/$UbuntuRelease/"
    if (-not $IsoUrl) {
        Write-Note "Looking up latest $UbuntuRelease live-server ISO at $baseUrl"
        try {
            $listing = Invoke-WebRequest -Uri $baseUrl -UseBasicParsing
        } catch {
            throw "Could not reach $baseUrl to discover the ISO. Pass -IsoUrl or -IsoPath. ($_)"
        }
        $m = [regex]::Matches($listing.Content, 'ubuntu-[0-9.]+-live-server-amd64\.iso') |
             Select-Object -First 1
        if (-not $m.Success) {
            throw "No live-server-amd64 ISO found at $baseUrl. Pass -IsoUrl explicitly."
        }
        $isoName = $m.Value
        $IsoUrl  = $baseUrl + $isoName
    } else {
        $isoName = Split-Path $IsoUrl -Leaf
    }

    $srcIso = Join-Path $PSScriptRoot $isoName
    # The Redownload choice (or -Redownload) forces a fresh fetch: drop any local
    # copy so the reuse branch below doesn't short-circuit it.
    if ($forceDownload -and (Test-Path -LiteralPath $srcIso)) {
        Write-Note "Re-downloading the source ISO (overwriting the local copy): $srcIso"
        Remove-Item -LiteralPath $srcIso -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $srcIso) {
        Write-Ok "ISO already downloaded: $srcIso"
    } else {
        Write-Note "Downloading $IsoUrl"
        Write-Note "(this is ~2-3 GB; using BITS if available)"
        $downloaded = $false
        $bits = Get-Command Start-BitsTransfer -ErrorAction SilentlyContinue
        if ($bits) {
            try {
                Start-BitsTransfer -Source $IsoUrl -Destination $srcIso -Description "Ubuntu Server $UbuntuRelease" -ErrorAction Stop
                $downloaded = $true
            } catch {
                # BITS can fail at runtime even when present — e.g. "The handle is
                # invalid (E_HANDLE)" in non-interactive / remoting / detached
                # sessions, or when the BITS service is disabled. Fall back below.
                Write-Warning "BITS transfer failed ($($_.Exception.Message)); falling back to Invoke-WebRequest."
                if (Test-Path -LiteralPath $srcIso) { Remove-Item -LiteralPath $srcIso -Force -ErrorAction SilentlyContinue }
            }
        }
        if (-not $downloaded) {
            # Fallback: disable the progress bar (it cripples Invoke-WebRequest throughput).
            $oldPref = $ProgressPreference; $ProgressPreference = "SilentlyContinue"
            try { Invoke-WebRequest -Uri $IsoUrl -OutFile $srcIso -UseBasicParsing }
            finally { $ProgressPreference = $oldPref }
        }
        Write-Ok "Downloaded: $srcIso"
    }

    # Verify SHA256 against the release SHA256SUMS file.
    if (-not $SkipChecksum) {
        try {
            $sums = (Invoke-WebRequest -Uri ($baseUrl + "SHA256SUMS") -UseBasicParsing).Content
            $line = ($sums -split "`n") | Where-Object { $_ -match [regex]::Escape($isoName) } | Select-Object -First 1
            if ($line) {
                $want = ($line -split '\s+')[0].Trim().ToLower()
                Write-Note "Verifying SHA256 ($want)"
                $got = (Get-FileHash -LiteralPath $srcIso -Algorithm SHA256).Hash.ToLower()
                if ($got -ne $want) {
                    throw "SHA256 mismatch for $isoName`n  expected $want`n  got      $got`n  Delete the file and retry, or pass -SkipChecksum."
                }
                Write-Ok "Checksum verified"
            } else {
                Write-Warning "Could not find $isoName in SHA256SUMS; skipping verification."
            }
        } catch {
            if ($_.Exception.Message -match "SHA256 mismatch") { throw }
            Write-Warning "Checksum verification skipped (couldn't fetch SHA256SUMS): $($_.Exception.Message)"
        }
    }
}

# ── 3. Build the autoinstall ISO inside WSL ──────────────────────────────────
Write-Step "Building autoinstall ISO via WSL"

$wslSrc    = ConvertTo-WslPath $srcIso
$wslOut    = ConvertTo-WslPath $OutputIso
$wslPubKey = ConvertTo-WslPath $bootstrapPubKey

# Write a LF-normalized copy of the builder next to the original (inside bin/, so
# $0's dirname still resolves the repo if anything relies on it) and run THAT
# directly. We avoid an inline multi-line `bash -lc` script entirely: passing a
# here-string through PowerShell -> wsl.exe -> bash mangles CR/quoting and breaks
# commands like `trap` ("trap: usage"). Running a real file with env + args as
# separate argv elements sidesteps all shell-quoting issues.
$normalized = (Get-Content -Raw -LiteralPath $buildScript) -replace "`r", ""
$lfScript   = Join-Path (Join-Path $PSScriptRoot "bin") ".build-autoinstall.lf.sh"
[System.IO.File]::WriteAllText($lfScript, $normalized)   # UTF-8, no BOM, LF only
$wslLfScript = ConvertTo-WslPath $lfScript

try {
    & wsl.exe @wslDistroArgs -u root -- env `
        "VM_USER=$VmUser" "VM_PASS=$VmPass" "VM_HOST=$VmHost" "SOURCE_ID=$SourceId" `
        "BOOTSTRAP_PUBKEY_FILE=$wslPubKey" `
        bash $wslLfScript $wslSrc $wslOut
    $buildExit = $LASTEXITCODE
} finally {
    Remove-Item -LiteralPath $lfScript -Force -ErrorAction SilentlyContinue
}
if ($buildExit -ne 0) { throw "autoinstall ISO build failed inside WSL (exit $buildExit)." }
if (-not (Test-Path -LiteralPath $OutputIso)) { throw "Build reported success but $OutputIso is missing." }
Write-Ok "Built: $OutputIso"

# The autoinstall ISO is built and verified present, so the large source ISO is
# no longer needed -- delete it to reclaim ~2-3 GB. Only remove an ISO we
# downloaded ourselves; a user-supplied -IsoPath is always left in place.
if ($srcIsoWasDownloaded -and (Test-Path -LiteralPath $srcIso)) {
    Write-Step "Cleaning up source Ubuntu ISO"
    try {
        Remove-Item -LiteralPath $srcIso -Force
        Write-Ok "Deleted downloaded source ISO: $srcIso"
    } catch {
        Write-Warning "Could not delete source ISO ($srcIso): $($_.Exception.Message)"
    }
}
}  # end if ($needBuild)

# ── 4. Create + provision the VM ─────────────────────────────────────────────
if ($SkipCreateVm) {
    Write-Host ""
    Write-Host "Done. Autoinstall ISO ready at:" -ForegroundColor Green
    Write-Host "    $OutputIso" -ForegroundColor White
    Write-Host "Run .\Create-AgentVM.ps1 to create and provision the VM." -ForegroundColor White
    return
}

Write-Step "Creating and provisioning the VM"
$createScript = Join-Path $PSScriptRoot "Create-AgentVM.ps1"
if (-not (Test-Path -LiteralPath $createScript)) {
    throw "Create-AgentVM.ps1 not found in $PSScriptRoot."
}
# Create-AgentVM.ps1 auto-detects the *autoinstall*.iso we just built next to
# it, then chains into Provision-AgentVM.ps1. We pass every decision we already
# collected so both run unattended. We're already elevated (see top), so this
# runs in-process -- which is what lets the "Press Enter" pause below fire only
# after provisioning finishes. The try/finally guarantees that pause runs even
# if create/provision throws.
$createArgs = @{
    MemoryGB      = $chosenMemGB
    DiskSizeGB    = $chosenDiskGB
    Projects      = $chosenProjects
    AgentPassword = $chosenAgentPassword
    GitUserName   = $chosenGitName
    GitEmail      = $chosenGitEmail
    # -Auto: the try/finally below owns the final pause, so neither
    # Create-AgentVM.ps1 nor the Provision-AgentVM.ps1 it chains into pauses too.
    Auto          = $true
}
# Auto-restore a saved config after the fresh install, and hand down any git
# credentials for cloning private project repos (both set above on the reinstall
# / decisions paths; absent for a plain fresh install with no repos).
if ($restoreDir)         { $createArgs['RestoreDir']             = $restoreDir }
if ($chosenCloneCredB64) { $createArgs['GitCloneCredentialsB64'] = $chosenCloneCredB64 }
# Pass the source repo/ref PAIR down to the provisioner (for the installed-commit
# marker) when the caller set EITHER -- forward both effective values so the recorded
# pair matches what was installed. A plain run forwards neither, leaving the
# provisioner's defaults / preserved settings to win.
if ($PSBoundParameters.ContainsKey('Repo') -or $PSBoundParameters.ContainsKey('Ref')) {
    $createArgs['Repo'] = $Repo; $createArgs['Ref'] = $Ref
}
try {
    & $createScript @createArgs

    # Host-side finalization for the control panel's Remote-SSH features (best-effort;
    # these never throw). Add the user to Hyper-V Administrators so the non-elevated
    # extension can read VM power state without a UAC prompt, then show a deep link
    # that opens the VM in VS Code Remote-SSH (the control panel opens alongside it,
    # the first time the extension activates in that remote window).
    Add-HyperVAdminMembership
    $openLink = Get-RemoteOpenLink -VmHost $VmHost -WorkspaceRoot "/root/repos"
    Show-Banner @(
        "Your Construct VM is ready.",
        "",
        "Open it in VS Code (Remote-SSH) -- the control panel opens alongside:",
        "",
        "  $openLink"
    )
    Write-Note "Tip: paste that link into a browser, or run:  start `"$openLink`""
} catch {
    # Show the failure ABOVE the pause so it's readable even when the window was
    # launched by double-click / right-click "Run with PowerShell".
    Write-Host ""
    Write-Host "ERROR: install failed." -ForegroundColor Red
    Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
} finally {
    Write-Host ""
    Wait-Exit
}
