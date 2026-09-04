#Requires -Version 5.1
<#
.SYNOPSIS
    Creates a Hyper-V VM called "Agent-VM" for the construct sandbox, sized
    to the host it runs on.

.DESCRIPTION
    1. Self-elevates to Administrator if not already elevated.
    2. Ensures Hyper-V is installed (installs it + reboots if missing).
    2a. If the agent VM already exists, offers an interactive menu to
        reprovision it (keep data), completely reinstall it (delete the VM +
        disk after a confirmation, then create fresh), or quit.
    3. Creates a Gen-2 VM; vCPUs default to all of the host's logical
       processors (override with -ProcessorCount).
    4. Prompts for VM RAM (default: a third of host RAM, capped at 24 GB) and virtual
       disk size (default 50 GB), and for an Ubuntu Server ISO. The RAM and disk
       prompts are skipped when -MemoryGB / -DiskSizeGB are supplied.
    5. Boots the VM from the ISO so the user can install the OS.
    6. After the user confirms installation is complete, unmounts the ISO
       and removes the DVD drive.

.PARAMETER MemoryGB
    VM RAM in GB. If omitted (0), a recommendation (a third of the host RAM, capped at
    24 GB) is calculated and the user is prompted.

.PARAMETER DiskSizeGB
    Virtual hard disk size in GB. If omitted (0), the user is prompted
    (default 50 GB).

.PARAMETER ProcessorCount
    vCPU count for the VM. If omitted (0), defaults to all of the host's
    logical processors.

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
    # vCPU count for the VM. If omitted (0), auto-scales to ALL of the host's
    # logical processors (min 2) so the VM tracks the machine it runs on instead
    # of a fixed number -- Hyper-V time-slices vCPUs, so the host keeps
    # scheduling itself alongside a fully-provisioned VM.
    [int]$ProcessorCount = 0,
    [string]$Projects,
    [string]$AgentPassword,
    # Optional git identity forwarded to Provision-AgentVM.ps1 (applied as the
    # VM's global git config). When omitted, the provisioner prompts with
    # host/saved defaults.
    [string]$GitUserName,
    [string]$GitEmail,
    # Forwarded to Provision-AgentVM.ps1: patch the Claude Code extension so it
    # streams partial assistant messages over Remote-SSH. Default on; "false"
    # reverts to stock. "true"/"false".
    [string]$ClaudePartialStreaming = "true",
    # Forwarded to Provision-AgentVM.ps1: patch the Claude Code extension for
    # microphone passthrough so the mic button survives a rebuild. Off by default.
    # "true"/"false".
    [string]$MicPassthrough = "false",
    # Forwarded to Provision-AgentVM.ps1: optional OpenCode background watcher.
    # Empty = keep the VM's saved choice; "true"/"false".
    [string]$OpenCodeBackgroundWatcher = "",
    # Forwarded to Provision-AgentVM.ps1: opt-in T3 Code web GUI. Empty = keep the
    # VM's saved choice; "true"/"false".
    [string]$T3Code = "",
    # Forwarded to Provision-AgentVM.ps1: T3 Code install channel. Empty = keep the
    # VM's saved choice; "stable"/"nightly".
    [ValidateSet("", "stable", "nightly")]
    [string]$T3CodeChannel = "",
    # Forwarded to Provision-AgentVM.ps1: opt-in T3 Code extra-feature patches
    # (legacy parameter name retained). Empty = keep the VM's saved choice.
    [string]$T3CodeLimitResume = "",
    # Hyper-V automatic checkpoints: snapshot the VM at every start. OFF by default
    # for Construct -- on a disposable agent VM the checkpoint only costs (a growing
    # .avhdx differencing disk, slower I/O, and a merge whenever it's deleted).
    # "true" re-enables Hyper-V's own default. Control-panel setting: Settings ->
    # VM resources -> Automatic checkpoints (Set-AgentVmCheckpoints.ps1 applies it
    # to an existing VM). "true"/"false".
    [ValidateSet("true", "false")]
    [string]$AutomaticCheckpoints = "false",
    # Forwarded to Provision-AgentVM.ps1: the config-sync branch this VM's host-config
    # store lives on. EMPTY (the default, and every existing install) means "let the
    # provisioner derive it from the host alias" -- nothing is forwarded and the splat
    # is exactly what it always was. A non-empty value is an instance's explicit
    # branch, which must reach Provision or the VM would be initialised on a different
    # ref than the control panel syncs.
    [string]$ConfigBranch = "",
    # Forwarded to Provision-AgentVM.ps1 for the save/restore + clone-credential
    # features. RestoreDir restores a saved config after provisioning;
    # GitCloneCredentialsB64 supplies credentials for cloning private project
    # repos; CheckoutProjects forces the repo checkout on/off.
    [string]$RestoreDir,
    [string]$GitCloneCredentialsB64,
    [string]$CheckoutProjects,
    # Hyper-V VM display name. Derived values (VHDX path, mshome DNS name, SSH
    # host alias) all follow this parameter. Must match the name Auto-Install.ps1
    # passes -- the default keeps backward compat with existing installs.
    [string]$VmName = "Agent-VM",
    # The CONSTRUCT INSTANCE this VM is (B11, plan section 4.12): a lowercase DNS label,
    # the one name rule (lib\AgentVm.Instances.ps1). Every derived value follows it --
    # the Hyper-V display name, the guest hostname, the ~\.ssh key file and the
    # config-sync branch -- so a second VM is created with ONE argument. -VmName keeps
    # working exactly as before and still names the VM on its own; giving both a name and
    # a DIFFERENT -VmName is an error rather than a silent choice between two machines.
    [string]$InstanceName = "",
    # Hypervisor backend to create the VM on. "hyperv-local" (the default) is
    # today's local Hyper-V path; the driver contract behind it (docs/drivers.md)
    # is what a remote-Hyper-V / Proxmox backend plugs into later.
    [string]$Backend = "hyperv-local",
    # Host-side saved-key file name (~\.ssh\<name>), forwarded to Provision-AgentVM.ps1.
    # Auto-Install derives an instance-scoped name for non-default VMs; omitted = the
    # provisioner's default (agent_vm_ed25519).
    [string]$LocalKeyName,
    # Explicit autoinstall ISO to attach (Auto-Install passes the one it built/reused
    # for THIS VM). Omitted: a named VM uses "<name>-autoinstall.iso" next to this
    # script; the default VM keeps the legacy "newest *autoinstall*.iso" discovery.
    [string]$AutoinstallIso,
    # Source repo/ref, forwarded to Provision-AgentVM.ps1 so it can record the
    # installed-commit update marker for the control panel. Passed only when set.
    [string]$Repo,
    [string]$Ref,
    # Set when launched by an upper script (Auto-Install.ps1), which owns the
    # final "Press Enter" pause. When run on its own this stays off and the
    # script pauses at the end -- important because a direct run self-elevates
    # into a fresh window that would otherwise vanish before it can be read.
    [switch]$Auto
)

if ($T3CodeChannel) { $T3CodeChannel = $T3CodeChannel.ToLower() }

# ── -InstanceName -> -VmName, BEFORE the self-elevation below ────────────────
# The instance name is the ONE name a VM has; the Hyper-V display name, the guest
# hostname, the key file and the config-sync branch are all derived from it
# (Get-ConstructLocalVmIdentity, which applies the one name rule from
# lib\AgentVm.Instances.ps1 -- never a second copy of it here). Resolved BEFORE the
# elevation so the elevated copy is handed the resolved -VmName and never has to look
# anything up in a profile that may not be the user's.
if ($InstanceName) {
    $instanceTargetLib = Join-Path $PSScriptRoot "lib\AgentVm.InstanceTarget.ps1"
    if (-not (Test-Path -LiteralPath $instanceTargetLib)) {
        throw "-InstanceName needs lib/AgentVm.InstanceTarget.ps1, which is missing from this install. Update The Construct, or pass -VmName instead."
    }
    . $instanceTargetLib
    $instanceIdentity = Get-ConstructLocalVmIdentity -Name $InstanceName -ParameterLabel 'InstanceName'
    if ($PSBoundParameters.ContainsKey('VmName') -and
        $VmName.ToLowerInvariant() -ne $instanceIdentity.VmName.ToLowerInvariant()) {
        throw "-InstanceName '$InstanceName' and -VmName '$VmName' name two different VMs. Pass only one (the Hyper-V name is derived from the instance name)."
    }
    $VmName = $instanceIdentity.VmName
    # The elevated relaunch forwards $PSBoundParameters verbatim: hand it the resolved
    # display name and drop the instance name, so the child follows exactly the -VmName
    # path every derivation below is already written for.
    [void]$PSBoundParameters.Remove('InstanceName')
    $PSBoundParameters['VmName'] = $VmName
}

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
    $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -PassThru
    # Bring the new elevated console to the foreground (best-effort): after the
    # UAC prompt it can open behind this window. Under Windows Terminal the
    # window belongs to WindowsTerminal.exe (handle stays 0), so this quietly
    # does nothing.
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

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }

# ── Recording this VM in the client-side instance registry (B11) ─────────────
# The control panel lists and switches between the VMs named in
# %LOCALAPPDATA%\The-Construct\instances.json; until B11 only the REMOTE installer ever
# wrote to it, so a second local VM was a hand edit and most users never saw the picker.
#
# ZERO-CHANGE RULE: a default-only install still writes NOTHING -- a missing file IS the
# `agent-vm` instance, and Save-ConstructLocalInstance only materialises the default when
# the file already exists (i.e. when some other instance put it there). The entry itself
# is written through lib\AgentVm.Instances.ps1, never by hand-rolling JSON, so what is
# written is exactly what both readers accept.
#
# Never fatal: the VM this is about has already been built, so a registry the user's
# profile will not accept is reported and the run continues.
function Register-ThisVmInstance {
    param([string]$ScriptsDir = $PSScriptRoot)
    $registryLib = Join-Path (Join-Path $ScriptsDir "lib") "AgentVm.InstanceTarget.ps1"
    if (-not (Test-Path -LiteralPath $registryLib)) { return }
    try {
        . $registryLib
        $written = Register-ConstructLocalVm -Name $script:VmGuestName -ConfigBranch $ConfigBranch
        if ($written) { Write-Note "Instance '$($script:VmGuestName)' recorded in $written" }
    } catch {
        Write-Warning "Could not record the instance '$($script:VmGuestName)' in the instance registry ($($_.Exception.Message)). The VM itself is fine; the control panel may not list it."
    }
}

# Shared helpers: interactive menu, reinstall confirmation, VM teardown.
$commonLib = Join-Path $PSScriptRoot "lib\AgentVm.Common.ps1"
if (-not (Test-Path -LiteralPath $commonLib)) { throw "Required helper not found: $commonLib" }
. $commonLib
# Per-instance state (where the control panel's VM-scoped settings live). OPTIONAL: an
# older/partial checkout without it keeps the default instance's legacy file.
$stateLib = Join-Path $PSScriptRoot "lib\AgentVm.InstanceState.ps1"
if (Test-Path -LiteralPath $stateLib) { . $stateLib }

# Hypervisor driver: every Hyper-V call below goes through the contract functions
# it defines (docs/drivers.md), so another backend can be dropped in without this
# script changing. Dot-sourced (not imported) so the driver's functions land in
# this script's scope, exactly like the lib above.
$driverLoader = Join-Path $PSScriptRoot "drivers\Load-ConstructDriver.ps1"
if (-not (Test-Path -LiteralPath $driverLoader)) { throw "Required helper not found: $driverLoader" }
. $driverLoader -Backend $Backend

# ── Configuration ────────────────────────────────────────────────────────────
$SwitchName        = "Default Switch"
$Generation        = 2

# vCPUs: default to every logical processor the host has (min 2; the old
# hardcoded 12 mirrored the original hand-built VM and ignored the host size).
# Hyper-V time-slices vCPUs rather than reserving them, so the host stays
# schedulable even with a fully-provisioned VM. -ProcessorCount overrides.
if ($ProcessorCount -lt 1) {
    $hostLPs = 0
    try { $hostLPs = [int]$env:NUMBER_OF_PROCESSORS } catch { }
    if ($hostLPs -lt 1) {
        try { $hostLPs = [int](Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).NumberOfLogicalProcessors } catch { }
    }
    $ProcessorCount = if ($hostLPs -ge 1) { $hostLPs } else { 12 }
    Write-Note "vCPUs: $ProcessorCount (all host logical processors)"
}
$VhdPath           = "C:\ProgramData\Microsoft\Windows\Virtual Hard Disks\$VmName.vhdx"
$CheckpointType    = "Standard"
# Automatic checkpoints default to OFF (Hyper-V's own default is ON) -- see the
# -AutomaticCheckpoints parameter for why.
$AutoCheckpoints   = ($AutomaticCheckpoints -eq "true")
$AutoStart         = "StartIfRunning"
$AutoStop          = "Save"

# Run the whole create/provision flow inside try/finally so that, when this
# script is run on its own (not -Auto from Auto-Install.ps1), the window pauses
# at the very end -- on success, an early return (reprovision/quit), or an error
# -- instead of vanishing. A direct run self-elevates into a fresh window, so
# without this the output would disappear the instant the work finishes.
try {

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
Ensure-ConstructDriverPrereqs

# ── Instance identity (before ANY branch that provisions or deletes) ─────────
# ONE NAME RULE, ONE DERIVATION: the alias, the guest hostname, the mshome address, the
# ~\.ssh key file and the config-sync branch all come from lib\AgentVm.Instances.ps1
# through the adapter -- this script states none of them itself, so it cannot drift from
# the registry, the extension (extension/src/instances.js) or the service's validator.
# The default VM keeps the legacy key name byte-for-byte; any other VM gets an
# instance-scoped key, so a standalone "Create-AgentVM.ps1 -VmName work-vm" never
# overwrites Agent-VM's ~/.ssh key.
$identityLib = Join-Path $PSScriptRoot "lib\AgentVm.InstanceTarget.ps1"
if (-not (Test-Path -LiteralPath $identityLib)) { throw "Required helper not found: $identityLib" }
. $identityLib
$script:VmIdentity  = Get-ConstructLocalVmIdentity -VmName $VmName -ParameterLabel 'VmName'
$script:VmGuestName = $script:VmIdentity.Name
$script:VmIsDefault = $script:VmIdentity.IsDefault
if (-not $LocalKeyName -and -not $script:VmIsDefault) {
    $LocalKeyName = $script:VmIdentity.KeyName
}
# Resolve a named VM's autoinstall ISO up front (explicit -AutoinstallIso wins, else
# "<name>-autoinstall.iso" next to this script -- never "the newest ISO", which may
# belong to another instance). A missing ISO is only an error on paths that CREATE a
# VM, and it is raised BEFORE a reinstall deletes anything. The default VM keeps the
# legacy newest-file discovery at the ISO step below.
$script:ResolvedIso   = $null
$script:NamedIsoError = $null
if ($AutoinstallIso) {
    if (-not (Test-Path -LiteralPath $AutoinstallIso)) { throw "-AutoinstallIso '$AutoinstallIso' does not exist." }
    $script:ResolvedIso = Get-Item -LiteralPath $AutoinstallIso
} elseif (-not $script:VmIsDefault) {
    $namedIso = Join-Path $PSScriptRoot "$($script:VmGuestName)-autoinstall.iso"
    if (Test-Path -LiteralPath $namedIso) { $script:ResolvedIso = Get-Item -LiteralPath $namedIso }
    else { $script:NamedIsoError = "No autoinstall ISO for VM '$VmName' ($namedIso) and no -AutoinstallIso given; refusing to guess another instance's ISO." }
}

# ── 2. Handle an already-installed VM (reprovision / reinstall / quit) ───────
# Test-ConstructVmPresent is three-valued ($true / $false / $null = can't tell),
# so `-eq $true` reproduces the previous `Get-VM -ErrorAction SilentlyContinue`
# test exactly: a VM in ANY state (including a transient one) opens the menu, and
# any failure to read Hyper-V falls through to creation as it always did.
if ((Test-ConstructVmPresent -Name $VmName) -eq $true) {
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
        # The VM exists and is about to be reprovisioned: record it, so a VM created
        # before B11 (or by an older script) enters the registry on its next run.
        Register-ThisVmInstance
        $provisionScript = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
        if (-not (Test-Path -LiteralPath $provisionScript)) { throw "Provision-AgentVM.ps1 not found in $PSScriptRoot." }
        $VmHostname = [string](Get-ConstructVmEndpoint -Name $VmName).SshHost
        # Always -Auto: this script (or its caller) owns the final pause, so the
        # provisioner shouldn't add its own.
        $provArgs = @{ Auto = $true }
        if (-not $script:VmIsDefault) { $provArgs['VmHost'] = $VmHostname; $provArgs['HostAlias'] = $script:VmGuestName }
        if ($PSBoundParameters.ContainsKey('Projects'))      { $provArgs['Projects']      = $Projects }
        if ($PSBoundParameters.ContainsKey('AgentPassword')) { $provArgs['AgentPassword'] = $AgentPassword }
        if ($PSBoundParameters.ContainsKey('GitUserName'))   { $provArgs['GitUserName']   = $GitUserName }
        if ($PSBoundParameters.ContainsKey('GitEmail'))      { $provArgs['GitEmail']      = $GitEmail }
        if ($PSBoundParameters.ContainsKey('ClaudePartialStreaming')) { $provArgs['ClaudePartialStreaming'] = $ClaudePartialStreaming }
        if ($PSBoundParameters.ContainsKey('MicPassthrough'))         { $provArgs['MicPassthrough']         = $MicPassthrough }
        if ($PSBoundParameters.ContainsKey('OpenCodeBackgroundWatcher')) { $provArgs['OpenCodeBackgroundWatcher'] = $OpenCodeBackgroundWatcher }
        if ($PSBoundParameters.ContainsKey('T3Code'))                 { $provArgs['T3Code']                 = $T3Code }
        if ($PSBoundParameters.ContainsKey('T3CodeChannel'))          { $provArgs['T3CodeChannel']          = $T3CodeChannel }
        if ($PSBoundParameters.ContainsKey('T3CodeLimitResume'))      { $provArgs['T3CodeLimitResume']      = $T3CodeLimitResume }
        if ($PSBoundParameters.ContainsKey('RestoreDir'))             { $provArgs['RestoreDir']             = $RestoreDir }
        if ($PSBoundParameters.ContainsKey('GitCloneCredentialsB64')) { $provArgs['GitCloneCredentialsB64'] = $GitCloneCredentialsB64 }
        if ($PSBoundParameters.ContainsKey('CheckoutProjects'))       { $provArgs['CheckoutProjects']       = $CheckoutProjects }
        if ($LocalKeyName)                                            { $provArgs['LocalKeyName']           = $LocalKeyName }
        # An explicit config-sync branch, and only into a provisioner that declares it:
        # dropping it silently would initialise the store on the alias-derived ref while
        # the control panel syncs the named one. Probe first, fail closed.
        if ($ConfigBranch) {
            $cbProvCmd = Get-Command -Name $provisionScript -CommandType ExternalScript -ErrorAction Stop
            if (-not $cbProvCmd.Parameters.ContainsKey('ConfigBranch')) {
                throw "This install's Provision-AgentVM.ps1 does not support -ConfigBranch; update The Construct before using the config-sync branch '$ConfigBranch'."
            }
            $provArgs['ConfigBranch'] = $ConfigBranch
        }
        # Source repo/ref PAIR for the installed-commit marker: if either was set,
        # forward both effective values so the recorded pair matches the install.
        if ($PSBoundParameters.ContainsKey('Repo') -or $PSBoundParameters.ContainsKey('Ref')) {
            $provArgs['Repo'] = $Repo; $provArgs['Ref'] = $Ref
        }
        Invoke-DeElevatedProvision -ScriptPath $provisionScript -ProvisionParams $provArgs
        return
    }
    elseif ($choice -eq 1) {
        # Complete reinstall -- confirm the irreversible delete (defaults to NO),
        # tear the VM down, then fall through to the normal creation steps below.
        # A named VM without its ISO must fail HERE, before the delete below.
        if ($script:NamedIsoError) { throw $script:NamedIsoError }
        if (-not (Confirm-Reinstall -VmName $VmName)) {
            Write-Note "Reinstall cancelled. No changes made."
            return
        }
        Remove-ConstructVm -Name $VmName
        Write-Note "Existing VM removed; continuing with a fresh install."
    }
    else {
        Write-Note "No changes made."
        return
    }
}

# ── 3. VM RAM (recommend a third of host, max 24 GB; prompt unless passed in) ─
Write-Step "Memory allocation"

$totalBytes     = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
$thirdBytes     = [math]::Floor($totalBytes / 3)
$maxBytes       = 24GB
# Recommend a third of the host RAM, capped at 24 GB but never below 4 GB.
$recommendBytes = [math]::Max([math]::Min($thirdBytes, $maxBytes), 4GB)
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

# A hand-run create records the size it chose as the control panel's settings, so the
# panel shows it and a later rebuild from there reuses it. Under -Auto the upper script
# (Auto-Install.ps1) has already recorded the same decision for the right instance; a
# custom -VmName without an instance name is a VM the panel does not know by name.
if (-not $Auto) {
    $specInstance = if ($InstanceName) { $InstanceName } elseif ($VmName -ieq 'Agent-VM') { 'agent-vm' } else { '' }
    if ($specInstance) {
        Save-ConstructVmSpec -Dir $PSScriptRoot -InstanceName $specInstance -MemoryGB ($memoryBytes / 1GB) -DiskGB $diskSizeGB -CpuCount $ProcessorCount
    }
}

# ── 5. Select Ubuntu Server ISO ──────────────────────────────────────────────
Write-Step "Select Ubuntu Server ISO"

# Which autoinstall ISO belongs to THIS VM: an explicit -AutoinstallIso wins; a named
# VM uses its own "<name>-autoinstall.iso" (never "the newest ISO in the folder",
# which may belong to another instance and would boot the guest with the wrong
# hostname); the default VM keeps the legacy newest-file discovery.
$autoIso = $null
if ($script:ResolvedIso) {
    $autoIso = $script:ResolvedIso
} elseif ($script:NamedIsoError) {
    throw $script:NamedIsoError
} else {
    # Prefer an autoinstall ISO sitting next to this script (built by
    # bin/build-autoinstall-iso.sh). If found, use it automatically; that also
    # drives the unattended autoinstall + auto-provision flow further down.
    $autoIso = Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*autoinstall*.iso' -File -ErrorAction SilentlyContinue |
               Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

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

# ── 6/7. Create + configure the VM (driver) ─────────────────────────────────
# The whole Hyper-V sequence -- New-VM, Set-VM, nested virtualization, Secure
# Boot, the install DVD and the boot order -- lives in the backend driver now
# (drivers\hyperv-local\HyperVLocal.Driver.ps1); it prints the same progress
# lines this section used to. MemoryBytes carries the exact, already 2 MB-aligned
# byte count so nothing is re-derived from a GB double.
Write-Step "Creating VM '$VmName'"

New-ConstructVm -Descriptor @{
    Name                 = $VmName
    MemoryBytes          = $memoryBytes
    DiskGB               = $diskSizeGB
    ProcessorCount       = $ProcessorCount
    VhdPath              = $VhdPath
    SwitchName           = $SwitchName
    Generation           = $Generation
    IsoPath              = $isoPath
    Nested               = $true
    AutomaticCheckpoints = $AutoCheckpoints
    CheckpointType       = $CheckpointType
    AutomaticStartAction = $AutoStart
    AutomaticStopAction  = $AutoStop
}

# ── 8. Start the VM ─────────────────────────────────────────────────────────
Write-Step "Starting VM '$VmName'"

Start-ConstructVm -Name $VmName
Write-Ok "VM is running. The Ubuntu installer should boot from the ISO."

$vmEndpoint = Get-ConstructVmEndpoint -Name $VmName
$VmHostname = [string]$vmEndpoint.SshHost
$isAutoinstall = (Split-Path $isoPath -Leaf) -match "autoinstall"

if ($isAutoinstall) {
    # ── 8a. Autoinstall: poll SSH until the VM is reachable ──────────────────
    Write-Host ""
    Write-Host "    Autoinstall ISO detected. Waiting for the VM to finish installing" -ForegroundColor Yellow
    Write-Host "    and become reachable via SSH at $VmHostname. This takes about 5 minutes ..." -ForegroundColor Yellow
    Write-Host ""

    # The raw-socket poll (plus its 20-minute bound, its non-fatal timeout and the
    # settle delay afterwards) is the driver's Wait-ConstructVmReachable -- a
    # backend knows where its VMs answer. Its return value says whether the port
    # ever opened; the flow is the same either way, so it is discarded here.
    $null = Wait-ConstructVmReachable -Name $VmName -TimeoutSeconds 1200 -PollIntervalSeconds 15 -SettleSeconds 20
    Write-Ok "Handing off to provisioning"
} else {
    # ── 8b. Manual install: ask the user ─────────────────────────────────────
    Write-Host ""
    Write-Host "    A VM console window will open. Complete the Ubuntu Server install" -ForegroundColor Yellow
    Write-Host "    using EXACTLY these settings so provisioning works:" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "      - Install variant : Ubuntu Server (minimized)" -ForegroundColor White
    Write-Host "      - Your server's name (hostname) : $($VmName.ToLowerInvariant())" -ForegroundColor White
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

Detach-ConstructInstallMedia -Name $VmName

# The VM exists and answers: record it before provisioning, so a failed provision still
# leaves an instance the control panel can reprovision.
Register-ThisVmInstance

if ($isAutoinstall) {
    if ($Auto) {
        # ── 10a. Chained from Auto-Install: return without provisioning ──────
        # Auto-Install.ps1 handles provisioning via Invoke-DeElevatedProvision
        # (currently running inline; see the kill switch in AgentVm.Common.ps1).
        Write-Step "VM created and SSH-ready"
        Write-Ok "Returning to Auto-Install for provisioning"
    } else {
        # ── 10b. Standalone: provision via Invoke-DeElevatedProvision ────────
        Write-Step "Provisioning the VM"

        $provisionScript = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
        if (-not (Test-Path $provisionScript)) {
            Write-Warning "Provision-AgentVM.ps1 not found in $PSScriptRoot. Skipping provisioning."
        } else {
            $provArgs = @{ Auto = $true }
        if (-not $script:VmIsDefault) { $provArgs['VmHost'] = $VmHostname; $provArgs['HostAlias'] = $script:VmGuestName }
            if ($PSBoundParameters.ContainsKey('Projects'))      { $provArgs['Projects']      = $Projects }
            if ($PSBoundParameters.ContainsKey('AgentPassword')) { $provArgs['AgentPassword'] = $AgentPassword }
            if ($PSBoundParameters.ContainsKey('GitUserName'))   { $provArgs['GitUserName']   = $GitUserName }
            if ($PSBoundParameters.ContainsKey('GitEmail'))      { $provArgs['GitEmail']      = $GitEmail }
            if ($PSBoundParameters.ContainsKey('ClaudePartialStreaming')) { $provArgs['ClaudePartialStreaming'] = $ClaudePartialStreaming }
            if ($PSBoundParameters.ContainsKey('MicPassthrough'))         { $provArgs['MicPassthrough']         = $MicPassthrough }
            if ($PSBoundParameters.ContainsKey('OpenCodeBackgroundWatcher')) { $provArgs['OpenCodeBackgroundWatcher'] = $OpenCodeBackgroundWatcher }
        if ($PSBoundParameters.ContainsKey('T3Code'))                 { $provArgs['T3Code']                 = $T3Code }
            if ($PSBoundParameters.ContainsKey('T3CodeChannel'))          { $provArgs['T3CodeChannel']          = $T3CodeChannel }
            if ($PSBoundParameters.ContainsKey('T3CodeLimitResume'))      { $provArgs['T3CodeLimitResume']      = $T3CodeLimitResume }
            if ($PSBoundParameters.ContainsKey('RestoreDir'))             { $provArgs['RestoreDir']             = $RestoreDir }
            if ($PSBoundParameters.ContainsKey('GitCloneCredentialsB64')) { $provArgs['GitCloneCredentialsB64'] = $GitCloneCredentialsB64 }
            if ($PSBoundParameters.ContainsKey('CheckoutProjects'))       { $provArgs['CheckoutProjects']       = $CheckoutProjects }
        if ($LocalKeyName)                                            { $provArgs['LocalKeyName']           = $LocalKeyName }
            # Same probe-before-splat rule as the reprovision path above: an explicit
            # branch either reaches the provisioner or the run fails closed.
            if ($ConfigBranch) {
                $cbProvCmd2 = Get-Command -Name $provisionScript -CommandType ExternalScript -ErrorAction Stop
                if (-not $cbProvCmd2.Parameters.ContainsKey('ConfigBranch')) {
                    throw "This install's Provision-AgentVM.ps1 does not support -ConfigBranch; update The Construct before using the config-sync branch '$ConfigBranch'."
                }
                $provArgs['ConfigBranch'] = $ConfigBranch
            }
            if ($PSBoundParameters.ContainsKey('Repo') -or $PSBoundParameters.ContainsKey('Ref')) {
                $provArgs['Repo'] = $Repo; $provArgs['Ref'] = $Ref
            }
            Invoke-DeElevatedProvision -ScriptPath $provisionScript -ProvisionParams $provArgs
        }
    }
} else {
    Write-Host ""
    Write-Host "Done. VM '$VmName' is ready." -ForegroundColor Green
    Write-Host "    Hostname for SSH: $VmHostname" -ForegroundColor White
    Write-Host "    You can now run Provision-AgentVM.ps1 to provision the agent." -ForegroundColor White
    Write-Host ""
}

}
catch {
    # Standalone: show the failure above our own pause (readable even on a
    # double-click run). Chained (-Auto): rethrow so the upper script owns the
    # single error display + pause.
    if ($Auto) { throw }
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    # Only pause when run standalone; an upper script (-Auto) does its own pause.
    if (-not $Auto) {
        Write-Host ""
        Read-Host "Press Enter to exit"
    }
}
