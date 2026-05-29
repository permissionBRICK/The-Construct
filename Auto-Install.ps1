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
    front (recommendation: half the host RAM, capped at 24 GB).

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
    [switch]$SkipChecksum,
    [switch]$SkipCreateVm,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

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
        Start-Process powershell.exe -Verb RunAs -ArgumentList $argList
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

# Matrix-style launch header: green "digital rain" (random 0/1 -- ASCII only, so
# it aligns and renders on any console code page, no katakana to mangle) framing
# the project title. Purely cosmetic; shown once when the elevated run begins.
function Show-ConstructHeader {
    $w    = 56
    $rain = { param([int]$n) -join (1..$n | ForEach-Object { @('0', '1')[(Get-Random -Maximum 2)] }) }
    $cent = {
        param([string]$s)
        $p = [int](($w - $s.Length) / 2)
        (" " * $p) + $s + (" " * ($w - $p - $s.Length))
    }
    $body = @(
        (& $cent ""),
        (& $cent "T H E   C O N S T R U C T"),
        (& $cent "agent sandbox loader"),
        (& $cent "")
    )
    Write-Host ""
    Write-Host ("   " + (& $rain ($w + 4))) -ForegroundColor DarkGreen
    Write-Host ("   " + (& $rain ($w + 4))) -ForegroundColor Green
    foreach ($l in $body) {
        Write-Host "   "          -NoNewline
        Write-Host (& $rain 1)    -ForegroundColor DarkGreen -NoNewline
        Write-Host " "            -NoNewline
        Write-Host $l             -ForegroundColor Green     -NoNewline
        Write-Host " "            -NoNewline
        Write-Host (& $rain 1)    -ForegroundColor DarkGreen
    }
    Write-Host ("   " + (& $rain ($w + 4))) -ForegroundColor Green
    Write-Host ("   " + (& $rain ($w + 4))) -ForegroundColor DarkGreen
    Write-Host ""
}

Show-ConstructHeader

# Shared helpers: interactive menu, reinstall confirmation, VM teardown.
$commonLib = Join-Path $PSScriptRoot "lib\AgentVm.Common.ps1"
if (-not (Test-Path -LiteralPath $commonLib)) { throw "Required helper not found: $commonLib" }
. $commonLib

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
# Returns a comma-separated PROJECTS value (or "default" if none chosen).
# Mirrors Select-Projects in Provision-AgentVM.ps1 so the choice can be made up
# front here and passed straight through.
function Select-Projects {
    $projDir = Join-Path $PSScriptRoot "projects"
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

# ── Handle an already-installed VM (reprovision / reinstall / quit) ──────────
# Checked up front -- before the long download/build -- so the user isn't forced
# to wait just to pick "reprovision" or "quit". Skipped in ISO-only mode
# (-SkipCreateVm) and when Hyper-V isn't present yet (no VM can exist, and
# Create-AgentVM.ps1 installs Hyper-V on the fresh path). $HyperVmName must match
# $VmName in Create-AgentVM.ps1.
$HyperVmName = "Agent-VM"
if (-not $SkipCreateVm -and (Get-Command Get-VM -ErrorAction SilentlyContinue) -and
    (Get-VM -Name $HyperVmName -ErrorAction SilentlyContinue)) {

    Write-Host ""
    Write-Warning "The agent VM '$HyperVmName' is already installed on this host."

    $choice = Show-Menu -Title "What would you like to do?" -Options @(
        "Reprovision    re-run provisioning on the existing VM (keeps all data)",
        "Reinstall      DELETE the VM and its disk, then build + install fresh",
        "Quit           make no changes and exit"
    ) -Default 0

    if ($choice -eq 0) {
        # Reprovision only: we just need the project selection, then run the
        # provisioner against the existing VM -- no download / build / create.
        $reprovProjects = $Projects
        if (-not $PSBoundParameters.ContainsKey('Projects')) { $reprovProjects = Select-Projects }
        Write-Ok "Projects: $reprovProjects"

        # No download/build/create on this path -- just re-run the provisioner
        # against the existing VM, so no long time estimate.
        Show-Banner @(
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
        $reprovArgs = @{ VmHost = $VmHostname; HostAlias = $HyperVmName.ToLower(); Projects = $reprovProjects }
        if ($PSBoundParameters.ContainsKey('AgentPassword')) { $reprovArgs['AgentPassword'] = $AgentPassword }
        try {
            & $provisionScript @reprovArgs
        } finally {
            Write-Host ""
            Read-Host "Press Enter to exit"
        }
        return
    }
    elseif ($choice -eq 1) {
        # Complete reinstall: confirm the irreversible delete (defaults to NO),
        # tear the VM down, then fall through to the normal fresh-install flow.
        if (-not (Confirm-Reinstall -VmName $HyperVmName)) {
            Write-Note "Reinstall cancelled. No changes made."
            Write-Host ""; Read-Host "Press Enter to exit"
            return
        }
        Remove-AgentVm -VmName $HyperVmName
        Write-Note "Existing VM removed; continuing with a fresh install."
    }
    else {
        Write-Note "No changes made."
        Write-Host ""; Read-Host "Press Enter to exit"
        return
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

if (-not $SkipCreateVm) {
    Write-Step "Setup choices"
    Write-Note "Answered now so the rest of the install can run unattended."

    # VM RAM -- recommend half the host RAM (capped at 24 GB), but let the user
    # choose (mirrors the disk-size prompt).
    if (-not $PSBoundParameters.ContainsKey('VmMemoryGB')) {
        $totalBytes = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
        $halfBytes  = [math]::Floor($totalBytes / 2)
        $maxBytes   = 24GB
        # Recommend half the host RAM, capped at 24 GB but never below 4 GB.
        $recBytes   = [math]::Max([math]::Min($halfBytes, $maxBytes), 4GB)
        $recBytes   = $recBytes - ($recBytes % 2MB)
        $recGB      = [math]::Round($recBytes / 1GB, 1)
        Write-Host ("    System RAM: {0:N1} GB    Recommended VM RAM: {1} GB" -f ($totalBytes / 1GB), $recGB) -ForegroundColor White
        $ans = Read-Host "    Enter VM RAM in GB (press Enter for $recGB)"
        $chosenMemGB = if ([string]::IsNullOrWhiteSpace($ans)) { $recGB } else { [double]$ans }
    }

    # Virtual disk size (default 50 GB).
    if (-not $PSBoundParameters.ContainsKey('VmDiskGB')) {
        $defDisk = 50
        Write-Host "    Recommended disk size: $defDisk GB" -ForegroundColor White
        $ans = Read-Host "    Enter disk size in GB (press Enter for $defDisk)"
        if ([string]::IsNullOrWhiteSpace($ans)) {
            $chosenDiskGB = $defDisk
        } else {
            $chosenDiskGB = [int]$ans
            if ($chosenDiskGB -lt 10) { Write-Warning "Minimum disk size is 10 GB. Using 10 GB."; $chosenDiskGB = 10 }
        }
    }

    # Project profiles to provision.
    if (-not $PSBoundParameters.ContainsKey('Projects')) {
        $chosenProjects = Select-Projects
    }

    # Optional login password for the agent user. This is only a manual-fallback
    # credential -- normal access is as root over the pre-seeded pubkey -- so it
    # defaults to the seeded password 'agent'. A different value is applied to the
    # agent user at the very end of provisioning.
    if (-not $PSBoundParameters.ContainsKey('AgentPassword')) {
        Write-Host "    Optional: login password for the 'agent' user (manual-fallback login only)." -ForegroundColor White
        $ans = Read-Host "    Enter agent password (press Enter to keep default 'agent')"
        $chosenAgentPassword = if ([string]::IsNullOrWhiteSpace($ans)) { "agent" } else { $ans }
    }

    $pwLabel = if ($chosenAgentPassword -and $chosenAgentPassword -ne "agent") { "custom" } else { "default" }
    Write-Ok ("VM RAM: {0} GB  |  Disk: {1} GB  |  Projects: {2}  |  agent password: {3}" -f $chosenMemGB, $chosenDiskGB, $chosenProjects, $pwLabel)

    # Confirm the host can actually run the VM BEFORE the long download. This
    # enables Hyper-V + the platform features (rebooting if needed) or aborts
    # with BIOS / Windows-Home guidance. The "all set" banner comes later, once
    # we know the unattended phase can really proceed (ISO present, or WSL OK).
    Ensure-HyperV
}

# If the target autoinstall ISO is already here, skip both the Ubuntu download
# and the WSL build entirely and go straight to creating the VM (-Force rebuilds).
$needBuild = $Force -or -not (Test-Path -LiteralPath $OutputIso)
if (-not $needBuild) {
    Write-Step "Autoinstall ISO already present"
    Write-Ok "Found $OutputIso"
    Write-Note "Skipping Ubuntu download and ISO build (pass -Force to rebuild)."

    # ISO is ready: nothing to download/build, so from here it's all unattended.
    if (-not $SkipCreateVm) {
        Show-Banner @(
            "All set -- sit back and relax!",
            "",
            "The autoinstall ISO is ready, so everything from here is automated:",
            "creating the VM and provisioning the agent.",
            "This takes about 10 minutes total, with no further input needed."
        )
    }
}

if ($needBuild) {
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
    Show-Banner @(
        "All set -- sit back and relax!",
        "",
        "Everything from here is automated: downloading Ubuntu, building",
        "the autoinstall ISO, and creating + provisioning the VM.",
        "This takes about 10 minutes total, with no further input needed."
    )
}

# ── 2. Acquire the source Ubuntu Server ISO ──────────────────────────────────
Write-Step "Source Ubuntu Server ISO"

if ($IsoPath) {
    if (-not (Test-Path -LiteralPath $IsoPath)) { throw "IsoPath not found: $IsoPath" }
    $srcIso = (Resolve-Path -LiteralPath $IsoPath).Path
    Write-Ok "Using provided ISO: $srcIso"
} else {
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
    if (Test-Path -LiteralPath $srcIso) {
        Write-Ok "ISO already downloaded: $srcIso"
    } else {
        Write-Note "Downloading $IsoUrl"
        Write-Note "(this is ~2-3 GB; using BITS if available)"
        $bits = Get-Command Start-BitsTransfer -ErrorAction SilentlyContinue
        if ($bits) {
            Start-BitsTransfer -Source $IsoUrl -Destination $srcIso -Description "Ubuntu Server $UbuntuRelease"
        } else {
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
}
try {
    & $createScript @createArgs
} finally {
    Write-Host ""
    Read-Host "Press Enter to exit"
}
