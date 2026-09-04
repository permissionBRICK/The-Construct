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
    # Hyper-V VM display name. Forwarded to Create-AgentVM.ps1 (with param probing);
    # derived DNS name and host alias follow this value. Default matches the existing
    # convention so all generated output is byte-identical when not overridden.
    [string]$VmName = "Agent-VM",
    [string]$Projects,
    [string]$AgentPassword,
    [string]$GitUserName,
    [string]$GitEmail,
    # Forwarded down (Create-AgentVM.ps1 -> Provision-AgentVM.ps1): patch the Claude
    # Code extension so it streams partial assistant messages over Remote-SSH.
    # Default on; "false" reverts the extension to stock. "true"/"false".
    [string]$ClaudePartialStreaming = "true",
    # Forwarded down (Create-AgentVM.ps1 -> Provision-AgentVM.ps1): patch the Claude
    # Code extension for microphone passthrough so the mic button survives a rebuild.
    # Off by default; "true"/"false".
    [string]$MicPassthrough = "false",
    # Forwarded down: optional dependency-free OpenCode background watcher.
    # Empty = keep the VM's saved choice; "true"/"false".
    [string]$OpenCodeBackgroundWatcher = "",
    # Forwarded down (Create-AgentVM.ps1 -> Provision-AgentVM.ps1): opt-in T3 Code
    # web GUI. Empty = keep the VM's saved choice; "true"/"false".
    [string]$T3Code = "",
    # Forwarded down: T3 Code install channel. Empty = keep the VM's saved choice;
    # "stable"/"nightly".
    [ValidateSet("", "stable", "nightly")]
    [string]$T3CodeChannel = "",
    # Forwarded down: opt-in T3 Code extra-feature patch set (legacy parameter
    # name retained). Empty = keep the VM's saved choice; "true"/"false".
    [string]$T3CodeLimitResume = "",
    # Forwarded to Create-AgentVM.ps1: Hyper-V automatic checkpoints (a snapshot at
    # every VM start). OFF by default for Construct -- on a disposable agent VM the
    # checkpoint only costs disk and I/O. Applies when the VM is CREATED (install /
    # reinstall / redownload); the control panel's Settings -> VM resources toggle
    # can also apply it to an existing VM via Set-AgentVmCheckpoints.ps1. "true"/"false".
    [ValidateSet("true", "false")]
    [string]$AutomaticCheckpoints = "false",
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
    [ValidateSet("reprovision", "reinstall", "redownload", "export", "add-config", "publish-config", "remove-instance")]
    [string]$Action,
    # With -Action remove-instance on a REMOTE instance (whose VM is DELETED on its host
    # service, disk and all): the instance name, typed back. Non-interactive runs -- the
    # control panel, which has already had the user type it -- must supply it; an
    # interactive console is asked instead. It is never a default and never inferred.
    [string]$ConfirmInstanceName,
    # With -Action reinstall/redownload, pre-answer the save/restore prompts:
    #   save     export the current config now and restore it afterwards (default)
    #   existing skip the new export; restore a previously saved backup if present
    #   wipe     no save and no restore -- reinstall completely blank
    [ValidateSet("save", "existing", "wipe")]
    [string]$BackupMode,
    # ── Config-sync v2 params (spec sections 10-12) ────────────────────────────
    # Import project configs from a remote git repo (cloned to a staging cache).
    # Requires git on the host; if absent, the pre-elevation block prompts/installs it.
    [string]$ConfigRepo,
    # Import project configs from a local directory (no git needed). Files matching
    # projects/*.json (or top-level *.json if no projects/ subdir) are imported.
    [string]$ConfigDir,
    # Comma-separated profile names to import from -ConfigRepo or -ConfigDir.
    # Omitted with -ConfigDir = import everything; omitted with -ConfigRepo =
    # interactive terminal picker (Import-ConstructConfigs' behavior per C5).
    [string]$ImportConfigs,
    # Conflict resolution strategy for the config-sync merge (spec section 8).
    # 'ours' keeps the host side, 'theirs' keeps the VM/upstream side.
    # When omitted, a conflict stops the operation with instructions to resolve manually.
    [ValidateSet("ours", "theirs")]
    [string]$AutoResolve,
    # The config-sync branch this VM's host-config store lives on. EMPTY (the default,
    # and every existing install) means "let Provision-AgentVM.ps1 derive it from the
    # host alias" -- exactly today's behaviour, with nothing extra forwarded anywhere.
    # An instance whose registry entry names a branch that does NOT match that
    # derivation passes it explicitly, so provisioning initialises and syncs the same
    # ref the control panel does (otherwise one VM ends up split across two refs).
    # Forwarded down the chain: -> Create-AgentVM.ps1 -> Provision-AgentVM.ps1, and
    # straight to Provision on the reprovision / add-config paths.
    [string]$ConfigBranch = "",
    # ── Remote host install (batch B7, docs/remote-host.md, plan §4.5) ─────────
    # Where the VM is created. "hyperv-local" (the default) is today's path, verbatim.
    # "hyperv-remote" creates it on a shared Hyper-V host running the constructd
    # service; NOTHING about the local path changes, and passing any of these three
    # parameters also SKIPS the mode prompt (so a scripted or panel-launched run never
    # sees it).
    [ValidateSet("hyperv-local", "hyperv-remote")]
    [string]$Backend = "hyperv-local",
    # The host service's base URL, e.g. https://buildbox.example.local:7462. A bare
    # host name gets https and the service's default port. Empty = not a remote install
    # (or: take it from the registry entry named by -InstanceName).
    [string]$ServiceUrl = "",
    # How to authenticate to that service. "negotiate" uses this Windows session's
    # identity (Kerberos/NTLM); "token" uses the admin-issued API token stored for the
    # host. An interactive run falls back from negotiate to a token / domain credentials
    # by itself when the service answers 401.
    [ValidateSet("negotiate", "token")]
    [string]$ServiceAuth = "negotiate",
    # The instance (VM) name on the remote host AND in the local instance registry --
    # a DNS label, e.g. "work-vm". Naming an EXISTING remote instance opens that
    # instance's menu (reprovision / reinstall / export) instead of creating one.
    [string]$InstanceName = "",
    # vCPUs for a REMOTE VM. 0 (the default) means "this script's own default", which is
    # also what every local install uses -- Create-AgentVM.ps1 decides the local VM's
    # processor count, so this parameter deliberately does nothing on the local path.
    [int]$VmCpuCount = 0,
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

if ($T3CodeChannel) { $T3CodeChannel = $T3CodeChannel.ToLower() }

# End-of-run pause. A clean control-panel run closes by itself; any provisioning
# error prints the VM result again at the true end of the parent flow and forces a
# pause so the panel-launched console cannot vanish before it is read.
function Wait-Exit {
    if ($global:ConstructProvisionHadErrors) {
        $items = @($global:ConstructProvisionErrors)
        Write-Host ""
        Write-Host "============================================================" -ForegroundColor DarkGray
        if ($global:ConstructProvisionFailureMessage) {
            Write-Host "RESULT: PROVISIONING FAILED" -ForegroundColor Red
        } else {
            Write-Host ("RESULT: PROVISIONING COMPLETED WITH {0} ERROR(S)" -f $items.Count) -ForegroundColor Red
        }
        foreach ($item in $items) {
            Write-Host ("  - {0} (exit {1})" -f $item.Title, $item.ExitCode) -ForegroundColor Red
        }
        if ($global:ConstructProvisionFailureMessage) {
            Write-Host "  - $($global:ConstructProvisionFailureMessage)" -ForegroundColor Red
        }
        Write-Host "============================================================" -ForegroundColor DarkGray

        # Copyable AI-agent fix prompt: built only for VM-side step failures (items
        # with a Title), not host-side messages. The user pastes this into their AI
        # coding agent on the VM (Claude Code over VS Code Remote-SSH) to diagnose.
        $stepLines = @()
        foreach ($item in $items) {
            if (-not $item.Title) { continue }
            $line = "- Step '$($item.Title)' failed (exit $($item.ExitCode))"
            if ($item.LogPath) {
                $line += "; log: $($item.LogPath)"
            }
            $stepLines += $line
        }
        if ($stepLines.Count -gt 0) {
            Write-Host ""
            Write-Host "  Paste this into your AI coding agent on the VM to diagnose:" -ForegroundColor Yellow
            Write-Host "  ............................................................" -ForegroundColor DarkGray
            Write-Host ""
            $logPaths = @($items | Where-Object { $_.LogPath } | ForEach-Object { $_.LogPath })
            $logRef = if ($logPaths.Count -eq 1) {
                "Read the provisioning log at $($logPaths[0])"
            } elseif ($logPaths.Count -gt 1) {
                "Read the provisioning logs at: $($logPaths -join ', ')"
            } else {
                "Check the provisioning output above"
            }
            Write-Host "  On the last Construct provisioning run, the following step(s) failed:" -ForegroundColor White
            foreach ($sl in $stepLines) {
                Write-Host "  $sl" -ForegroundColor White
            }
            Write-Host "  $logRef and diagnose and fix the underlying problem." -ForegroundColor White
            Write-Host ""
            Write-Host "  ............................................................" -ForegroundColor DarkGray
        }
    }
    if ((-not $FromPanel) -or $global:ConstructProvisionHadErrors) {
        Read-Host "Press Enter to exit" | Out-Null
    }
}

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

# ── -Action publish-config: publish local profiles to a linked config repo ───
# Plan 4.13 / B15. Import and Push back only move files that already carry a
# provenance manifest entry, so a profile born on THIS PC can never reach a
# remote. This action publishes untracked local profiles into the config repo's
# DEFAULT branch and adopts them (manifest + stored base), after which Import and
# Push back round-trip them like any other tracked file.
#
# It runs HERE, before the install-mode resolution and the self-elevation below,
# because it touches no VM and no Hyper-V: it is a git operation on
# %LOCALAPPDATA%\The-Construct\config plus one push. Non-interactive, and it
# exits without ever entering the installer. Every other run falls straight
# through this block untouched.
if ($Action -eq 'publish-config') {
    $pcLib = Join-Path $PSScriptRoot "lib\AgentVm.Common.ps1"
    if (-not (Test-Path -LiteralPath $pcLib)) { throw "Required helper not found: $pcLib" }
    . $pcLib

    if (-not $ConfigRepo) {
        throw "-Action publish-config needs -ConfigRepo <url> (the config repo to publish into)."
    }
    # Same git check/prompt as -ConfigRepo add-config: unattended, so a silent
    # install attempt and a loud abort when it fails.
    if (-not (Test-ConstructGitAvailable)) {
        $pcGitOk = $false
        if (Get-Command Ensure-ConstructGit -ErrorAction SilentlyContinue) {
            $pcGitOk = Ensure-ConstructGit -AutoMode
        }
        if (-not $pcGitOk) {
            throw "-Action publish-config requires git, but the automatic git install failed. Install git manually (winget install --id Git.Git) and re-run."
        }
    }

    $pcConfigDir = Initialize-ConstructConfigStore -ScriptsDir $PSScriptRoot
    # Publishing records the adoption as a commit in the host config store, so make
    # sure that store is a repo. Idempotent, and git is guaranteed present here.
    if (Get-Command Initialize-ConstructConfigRepo -ErrorAction SilentlyContinue) {
        $null = Initialize-ConstructConfigRepo -ConfigDir $pcConfigDir
    }
    $pcNames = $null
    if ($ImportConfigs) {
        $pcNames = @($ImportConfigs -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    }

    Write-Host ""
    Write-Host "==> Publishing local project profiles" -ForegroundColor Cyan
    # NEVER the raw URL: a publish target on the owner's own git host carries a PAT
    # in the URL, and this line would otherwise print it (and land in any transcript).
    Write-Host "    Config repo: $(Format-ConstructRemoteUrlForDisplay -Url $ConfigRepo)" -ForegroundColor DarkGray

    $pcArgs = @{ ConfigDir = $pcConfigDir; RemoteUrl = $ConfigRepo }
    if ($null -ne $pcNames -and $pcNames.Count -gt 0) { $pcArgs['Names'] = $pcNames }
    $pcResult = Publish-ConstructConfigProfiles @pcArgs

    $pcRows = @()
    foreach ($n in @($pcResult.Published)) { $pcRows += [pscustomobject]@{ Name = $n; Result = "published"; Detail = "" } }
    foreach ($r in @($pcResult.Skipped))   { $pcRows += [pscustomobject]@{ Name = $r.Name; Result = "skipped";  Detail = $r.Reason } }
    foreach ($r in @($pcResult.Refused))   { $pcRows += [pscustomobject]@{ Name = $r.Name; Result = "refused";  Detail = $r.Reason } }
    foreach ($r in @($pcResult.Invalid))   { $pcRows += [pscustomobject]@{ Name = $r.Name; Result = "invalid";  Detail = $r.Reason } }

    Write-Host ""
    if ($pcRows.Count -eq 0) {
        # Only an honest "nothing to do" -- a run that failed says so below instead.
        if (@($pcResult.Errors).Count -eq 0) {
            Write-Host "    No local project profiles to publish." -ForegroundColor DarkGray
        }
    } else {
        $pcWidth = 4
        foreach ($row in $pcRows) { if ($row.Name.Length -gt $pcWidth) { $pcWidth = $row.Name.Length } }
        foreach ($row in ($pcRows | Sort-Object Name)) {
            $pcColor = "DarkGray"
            if ($row.Result -eq "published") { $pcColor = "Green" }
            if ($row.Result -eq "refused")   { $pcColor = "Yellow" }
            if ($row.Result -eq "invalid")   { $pcColor = "Red" }
            $pcLine = "    {0}  {1}" -f $row.Name.PadRight($pcWidth), $row.Result
            if ($row.Detail) { $pcLine += "  ({0})" -f $row.Detail }
            Write-Host $pcLine -ForegroundColor $pcColor
        }
    }

    Write-Host ""
    if ($pcResult.Branch) { Write-Host "    Branch: $($pcResult.Branch)" -ForegroundColor DarkGray }
    if ($pcResult.Commit) { Write-Host "    Commit: $($pcResult.Commit)" -ForegroundColor DarkGray }
    Write-Host ("    Published {0}, skipped {1}, refused {2}, invalid {3}." -f @($pcResult.Published).Count, @($pcResult.Skipped).Count, @($pcResult.Refused).Count, @($pcResult.Invalid).Count) -ForegroundColor DarkGray

    if (@($pcResult.Errors).Count -gt 0 -or @($pcResult.Invalid).Count -gt 0) {
        Write-Host ""
        foreach ($e in @($pcResult.Errors)) { Write-Host "    ERROR: $e" -ForegroundColor Red }
        Write-Host ""
        exit 1
    }
    Write-Host ""
    exit 0
}

# ── Install mode: local Hyper-V, or a remote host service ────────────────────
# ONE entry point, one extra question, and only on a genuinely fresh machine
# (docs/plans/modular-remote-architecture.md §4.5). Every existing install -- and every
# scripted or panel-launched run -- resolves the mode from its parameters and sees NO
# new prompt at all.
#
# WHY IT IS DECIDED HERE, before the self-elevation below: a remote install creates no
# local VM and therefore needs no administrator rights, and elevating would be actively
# wrong -- on a machine where UAC switches to a DIFFERENT admin account, the token
# store, the instance registry and ~\.ssh would all land in that account's profile
# instead of the user's. So the remote path skips the relaunch entirely.
$script:ConstructInstallMode  = ""      # "" = not decided yet
$script:ConstructModePrompted = $false  # did we actually show the prompt?

function Read-ConstructInstanceRegistrySnapshot {
    <#
        A read-only view of %LOCALAPPDATA%\The-Construct\instances.json, as plain data.

        Read in a CHILD SCOPE (& { ... }) on purpose: lib\AgentVm.Instances.ps1 enables
        Set-StrictMode -Version Latest in whatever scope it is dot-sourced into, and the
        LOCAL install path below must not start running under a mode it was never
        written for. The child scope contains it.

        Returns $null when the library is missing (an older/partial install) -- callers
        treat that exactly like "no registry", i.e. today's single-VM behaviour.
    #>
    param([string]$ScriptsDir = $PSScriptRoot)
    $lib = Join-Path (Join-Path $ScriptsDir "lib") "AgentVm.Instances.ps1"
    if (-not (Test-Path -LiteralPath $lib)) { return $null }
    try {
        return & {
            param($libPath)
            . $libPath
            $reg = Read-ConstructInstances
            $entries = @{}
            foreach ($k in @($reg.Instances.Keys)) {
                $i = $reg.Instances[$k]
                $svcUrl = ""; $svcAuth = ""
                if ($i.Service) { $svcUrl = [string]$i.Service.Url; $svcAuth = [string]$i.Service.Auth }
                $entries[[string]$k] = [pscustomobject]@{
                    Name         = [string]$i.Name
                    Backend      = [string]$i.Backend
                    VmName       = [string]$i.VmName
                    VmHost       = [string]$i.VmHost
                    SshPort      = [int]$i.SshPort
                    HostAlias    = [string]$i.HostAlias
                    KeyName      = [string]$i.KeyName
                    ConfigBranch = [string]$i.ConfigBranch
                    ServiceUrl   = $svcUrl
                    ServiceAuth  = $svcAuth
                    Owner        = [string]$i.Owner
                }
            }
            [pscustomobject]@{
                Path     = [string]$reg.Path
                Exists   = [bool]$reg.Exists
                Default  = [string]$reg.Default
                Problems = @($reg.Problems)
                Entries  = $entries
            }
        } $lib
    } catch {
        return $null
    }
}

function Save-ConstructInstanceEntry {
    <#
        Write ONE instance entry into the registry, through lib\AgentVm.Instances.ps1 --
        never by hand-rolling the JSON, so the two readers (PS and the extension) can
        never disagree about what was written, and an entry the reader would refuse is
        rejected here instead of vanishing on the next load.

        Same child-scope discipline as the snapshot reader above. Throws on refusal.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][hashtable]$Entry,
        [switch]$Replace,
        [switch]$MakeDefault,
        [string]$ScriptsDir = $PSScriptRoot
    )
    $lib = Join-Path (Join-Path $ScriptsDir "lib") "AgentVm.Instances.ps1"
    if (-not (Test-Path -LiteralPath $lib)) {
        throw "Cannot record the instance '$Name': lib/AgentVm.Instances.ps1 is missing from this install. Update The Construct."
    }
    return & {
        param($libPath, $n, $e, $replace, $makeDefault)
        . $libPath
        $reg  = Read-ConstructInstances
        $next = Add-ConstructInstance -Registry $reg -Name $n -Entry $e -Replace:$replace
        if ($makeDefault) { $next.Default = $n }
        Save-ConstructInstances -Registry $next
    } $lib $Name $Entry ([bool]$Replace) ([bool]$MakeDefault)
}

function Get-ConstructDerivedVmIdentity {
    <#
        The DERIVED identity of the local VM called -VmName -- guest hostname, mshome
        address, ssh alias, ~\.ssh key file, config-sync branch -- straight from
        lib\AgentVm.Instances.ps1 through the adapter, so no function in this script
        states any of those formulas (or the name rule) a second time.

        Returns $null when this install cannot answer at all (a partial checkout, or a
        name that breaks the rule). Callers decide what that means: the best-effort
        probes treat it as "don't know", the ones that would otherwise act on the WRONG
        VM refuse. The identity block further down hard-requires the same library, so by
        the time anything destructive runs, $null is not reachable.

        Same child-scope discipline as the wrappers above: the adapter contains the
        registry module's strict mode.
    #>
    param([Parameter(Mandatory)][string]$VmName, [string]$ScriptsDir = $PSScriptRoot)
    $lib = Join-Path (Join-Path $ScriptsDir "lib") "AgentVm.InstanceTarget.ps1"
    if (-not (Test-Path -LiteralPath $lib)) { return $null }
    try {
        . $lib
        return (Get-ConstructLocalVmIdentity -VmName $VmName)
    } catch {
        return $null
    }
}

function Register-ConstructLocalVmInstance {
    <#
        Record a LOCAL VM in the instance registry (B11, plan section 4.12), through
        lib\AgentVm.InstanceTarget.ps1 -> lib\AgentVm.Instances.ps1 -- the same writer and
        the same rules the remote flow uses, never hand-rolled JSON.

        ZERO-CHANGE RULE: a default-only install writes NOTHING. A missing instances.json
        IS the `agent-vm` instance, so Save-ConstructLocalInstance materialises the
        default only when the file already exists; creating a SECOND VM writes both
        entries in one document, because the registry object always carries the default.

        Never fatal: this runs about a VM that already exists, so a registry that cannot
        be written is reported and the install carries on. It deliberately runs in the
        same process (and therefore the same user profile) that writes ~\.ssh\<key> and
        the ssh_config block, so the entry and the key it names never land in two
        different profiles.
    #>
    param([Parameter(Mandatory)][string]$Name, [string]$ConfigBranch = "", [string]$ScriptsDir = $PSScriptRoot)
    $lib = Join-Path (Join-Path $ScriptsDir "lib") "AgentVm.InstanceTarget.ps1"
    if (-not (Test-Path -LiteralPath $lib)) { return $null }
    try {
        . $lib
        return (Register-ConstructLocalVm -Name $Name -ConfigBranch $ConfigBranch)
    } catch {
        Write-Warning "Could not record the instance '$Name' in the instance registry ($($_.Exception.Message)). The VM itself is fine; the control panel may not list it."
        return $null
    }
}

function Test-ConstructPriorLocalInstall {
    <#
        Has a Construct VM ever been provisioned on THIS PC, for THIS user?

        The signal is the VM's private key in the user's own profile
        (~\.ssh\agent_vm_ed25519 for the default VM, ~\.ssh\construct_<name>_ed25519
        otherwise): Provision-AgentVM.ps1 writes it on every successful run and nothing
        else creates it. Deliberately NOT a Hyper-V question -- this runs as the
        non-elevated desktop user, where Get-VM may answer "access denied" on a machine
        that very much does have a VM.

        It exists only to SUPPRESS the mode prompt, so being wrong is one-directional and
        safe: a false $true keeps today's local path (what a machine with a Construct key
        but no VM would have got anyway), and a false $false merely leaves the decision to
        the probes around it. Never throws.
    #>
    param([string]$VmName = "Agent-VM")
    try {
        # The key file name is DERIVED, and the derivation lives in one place
        # (Get-ConstructDerivedVmIdentity). "Can't answer" is a $false here, which is the
        # safe direction: it only leaves the decision to the probes around this one.
        $identity = Get-ConstructDerivedVmIdentity -VmName $VmName
        if (-not $identity) { return $false }
        $keyName = [string]$identity.KeyName
        # NOT a variable named $home: that IS the automatic $HOME, and assigning it here
        # would shadow the very fallback the next line reads.
        $profileDir = $env:USERPROFILE
        if (-not $profileDir) { $profileDir = $HOME }
        if (-not $profileDir) { return $false }
        return (Test-Path -LiteralPath (Join-Path (Join-Path $profileDir ".ssh") $keyName) -PathType Leaf)
    } catch {
        return $false
    }
}

function Resolve-ConstructInstallMode {
    <#
        "hyperv-local" or "hyperv-remote" for THIS run, decided once and cached.

        Explicit parameters always win and never prompt:
          -Backend                      whatever it says
          -ServiceUrl                   remote
          -InstanceName <registered>    whatever that instance's backend is
          -InstanceName <new>           local (B11: a name this PC does not know yet is
                                        the LOCAL VM this run is about to build)

        Otherwise the prompt is shown only on a FRESH machine, i.e. when ALL of:
          * this run creates a VM at all (not -SkipCreateVm) and is interactive
            (not -FromPanel, no pre-selected -Action);
          * no VM identity was named (-VmName / -VmHost);
          * the instance registry names no VM (missing file, or only the synthesized
            default);
          * this host has no local Construct VM already -- asked twice, because the
            Hyper-V probe needs rights this (non-elevated) run may not have:
            Test-ConstructVmPresent AND the user-profile key
            Test-ConstructPriorLocalInstall looks for;
          * the TUI helper is actually available (a degraded install falls back to the
            local path rather than to a broken prompt).
        Anything else answers "hyperv-local" silently -- which is what makes an existing
        install's experience byte-identical.
    #>
    param([hashtable]$Bound, $Snapshot)

    if ($script:ConstructInstallMode) { return $script:ConstructInstallMode }

    if ($Bound.ContainsKey('Backend')) {
        $script:ConstructInstallMode = $Backend
        return $script:ConstructInstallMode
    }
    if ($Bound.ContainsKey('ServiceUrl') -and $ServiceUrl) {
        $script:ConstructInstallMode = 'hyperv-remote'
        return $script:ConstructInstallMode
    }
    if ($Bound.ContainsKey('InstanceName') -and $InstanceName -and
        $Snapshot -and $Snapshot.Entries.ContainsKey($InstanceName)) {
        $script:ConstructInstallMode = [string]$Snapshot.Entries[$InstanceName].Backend
        if (-not $script:ConstructInstallMode) { $script:ConstructInstallMode = 'hyperv-local' }
        return $script:ConstructInstallMode
    }

    $script:ConstructInstallMode = 'hyperv-local'
    if ($SkipCreateVm -or $FromPanel) { return $script:ConstructInstallMode }
    foreach ($p in @('Action', 'VmName', 'VmHost', 'InstanceName')) {
        if ($Bound.ContainsKey($p)) { return $script:ConstructInstallMode }
    }
    if (-not (Get-Command Show-Menu -ErrorAction SilentlyContinue)) { return $script:ConstructInstallMode }
    # A registry that names any VM means this machine is already set up -- no prompt.
    if ($Snapshot -and ($Snapshot.Exists -or @($Snapshot.Entries.Keys).Count -gt 1)) {
        return $script:ConstructInstallMode
    }
    # An existing LOCAL VM likewise. Probed through the driver contract (three-valued,
    # so an unreadable Hyper-V is "can't tell" and does NOT suppress the prompt).
    if ((Get-Command Test-ConstructDriverPrereqs -ErrorAction SilentlyContinue) -and
        (Get-Command Test-ConstructVmPresent -ErrorAction SilentlyContinue)) {
        try {
            if ((Test-ConstructDriverPrereqs) -and ((Test-ConstructVmPresent -Name $VmName) -eq $true)) {
                return $script:ConstructInstallMode
            }
        } catch { }
    }
    # ...and the same question asked WITHOUT Hyper-V, because at this point we are the
    # non-elevated desktop user: Get-VM needs administrator rights or membership of
    # "Hyper-V Administrators" (which the installer grants, but which only takes effect
    # after the next sign-in), so on a machine that already HAS a Construct VM the probe
    # above can perfectly well answer "can't tell" -- and an existing install would then
    # be asked a brand-new question. Provisioning leaves this VM's private key in the
    # user's own profile, so its presence is a permission-free "this PC has installed a
    # Construct VM before".
    if (Test-ConstructPriorLocalInstall -VmName $VmName) { return $script:ConstructInstallMode }

    $script:ConstructModePrompted = $true
    if (Get-Command Show-TuiScreen -ErrorAction SilentlyContinue) {
        Show-TuiScreen -Title "Where should this Construct VM run?" -Body @(
            "A Construct VM can live on THIS PC's Hyper-V (the usual install), or on a",
            "shared Hyper-V host that runs the Construct host service -- in which case the",
            "VM keeps running with this PC switched off."
        )
    }
    # The labels are the ones plan section 4.5 specifies, verbatim, with the explanatory
    # half after them (the same "<choice>  <what it does>" shape every other menu in this
    # script uses).
    $pick = Show-Menu -Title "How should this Construct VM run?" -Options @(
        "Local Hyper-V install   create the VM on THIS PC (the usual install)",
        "Remote host install     create it on a shared Construct host service"
    ) -Default 0
    if ($pick -eq 1) { $script:ConstructInstallMode = 'hyperv-remote' }
    return $script:ConstructInstallMode
}

# ── Self-elevate to Administrator from the start ─────────────────────────────
# The Hyper-V VM creation needs admin rights, so we elevate up front -- before
# the long download/build. After Create-AgentVM.ps1 returns (VM created, SSH up),
# provisioning goes through Invoke-DeElevatedProvision. Its de-elevation
# (scheduled task as the real desktop user) is currently DISABLED via the kill
# switch in AgentVm.Common.ps1, so provisioning runs inline in this elevated
# console until the de-elevated child's spurious prompts are fixed.
# We skip elevation when only building the ISO (-SkipCreateVm needs no admin rights),
# and for -Action remove-instance, which drives no Hyper-V at all: it edits ~\.ssh, the
# VS Code + OpenCode profiles and instances.json, every one of which belongs to the REAL
# user. Elevating would write them into the administrator's profile on a PC where UAC
# switches accounts -- the same reason the remote install path never relaunches.
if (-not $SkipCreateVm -and $Action -ne 'remove-instance') {
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

            # Config-sync v2 (spec section 10 / D15): when -ConfigRepo is bound and git
            # is not on the host, install it NOW -- while we are still the real (non-admin)
            # user -- because winget is per-user. Interactive (no -Action): prompt first.
            # Unattended (-Action add-config): silent attempt, abort loudly on failure
            # (the user explicitly asked for something that requires git).
            if ($PSBoundParameters.ContainsKey('ConfigRepo') -and
                (Get-Command Test-ConstructGitAvailable -ErrorAction SilentlyContinue) -and
                -not (Test-ConstructGitAvailable)) {
                if ($PSBoundParameters.ContainsKey('Action') -and $Action -eq 'add-config') {
                    # Unattended: silent install attempt, hard abort on failure.
                    if (Get-Command Ensure-ConstructGit -ErrorAction SilentlyContinue) {
                        $gitOk = Ensure-ConstructGit -AutoMode
                        if (-not $gitOk) { throw "-ConfigRepo requires git, but the automatic git install failed. Install git manually (winget install --id Git.Git) and re-run." }
                    }
                } else {
                    # Interactive: prompt the user.
                    $ans = Read-Host "    -ConfigRepo requires git. Install git via winget? [Y/n]"
                    if (-not $ans -or $ans -match '^[Yy]') {
                        if (Get-Command Ensure-ConstructGit -ErrorAction SilentlyContinue) {
                            Ensure-ConstructGit | Out-Null
                        }
                    }
                }
            }
        } catch {
            Write-Warning "Could not set up the control panel on the host (continuing): $($_.Exception.Message)"
        }
        # ── Local or remote? Decided BEFORE the relaunch ──────────────────────
        # A REMOTE install creates no local VM, so it needs no administrator rights --
        # and elevating would be actively harmful where UAC switches to a different
        # admin account: the DPAPI token store, instances.json and ~\.ssh would all be
        # written into THAT account's profile. So the remote path continues here, as
        # the real desktop user, and never relaunches.
        #
        # The mode is cached ($script:ConstructInstallMode), so the resolution below --
        # and the elevated child, which is handed an explicit -Backend -- never asks twice.
        try {
            # Through the driver contract, so the "is there already a local VM?" probe
            # stays the one in drivers\ rather than a second Get-VM call here.
            $modeDriverLoader = Join-Path $PSScriptRoot "drivers\Load-ConstructDriver.ps1"
            if (Test-Path -LiteralPath $modeDriverLoader) { . $modeDriverLoader -Backend "hyperv-local" }
        } catch { }
        $modeSnapshot = Read-ConstructInstanceRegistrySnapshot
        if ((Resolve-ConstructInstallMode -Bound $PSBoundParameters -Snapshot $modeSnapshot) -eq 'hyperv-remote') {
            # Write-Host, not Write-Note: this runs BEFORE this script's own output
            # helpers are defined.
            Write-Host "    Remote host install -- no administrator rights are needed on this PC." -ForegroundColor DarkGray
        } else {
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
            # If the mode PROMPT ran and the user chose local, tell the elevated child so
            # explicitly -- otherwise it would resolve the mode again (with the registry
            # and Hyper-V still saying "fresh machine") and ask the same question twice.
            # Only when the prompt actually ran, so an ordinary relaunch's command line is
            # exactly what it always was.
            if ($script:ConstructModePrompted -and -not $PSBoundParameters.ContainsKey('Backend')) {
                $argList += "-Backend"; $argList += "`"hyperv-local`""
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

# Per-instance state (the VM-scoped half of what the control panel saves). OPTIONAL: an
# older/partial checkout without it falls back to the legacy top-level keys, which is
# exactly today's single-VM behaviour.
$stateLib = Join-Path $PSScriptRoot "lib\AgentVm.InstanceState.ps1"
if (Test-Path -LiteralPath $stateLib) { . $stateLib }

# Hypervisor driver: every Hyper-V touch in this script (the existence probe, the
# prerequisite install, the teardown, the reachability endpoint) goes through the
# contract functions it defines -- see docs/drivers.md. "hyperv-local" is today's
# only backend and the zero-change default; a per-instance backend arrives with
# the instance registry.
$driverLoader = Join-Path $PSScriptRoot "drivers\Load-ConstructDriver.ps1"
if (-not (Test-Path -LiteralPath $driverLoader)) { throw "Required helper not found: $driverLoader" }
. $driverLoader -Backend "hyperv-local"

# Full-window TUI for the whole interactive phase: every choice below runs as
# its own screen (wipe + header + the current menu only). Show-AllSet turns it
# back off at the "all set" banner, after which output scrolls as a normal log.
# ISO-only mode (-SkipCreateVm) has no prompts, so it keeps plain log output.
if (-not $SkipCreateVm) { Enable-ConstructTui }

Show-ConstructHeader

# ── -Action remove-instance: forget one VM on this PC ────────────────────────
# Plan 4.12 "Cleanup" / B14. Installing a VM writes client-side state in half a dozen
# unrelated places (an ssh_config block and a key, known_hosts lines, VS Code's
# remote.SSH.remotePlatform map, the OpenCode server list, the T3 certificate authority,
# the per-instance state file, the registry entry); removing one by hand means
# remembering all of them, and a forgotten alias or a stale trusted CA is exactly the
# leftover that later points a tool at a machine that no longer exists.
#
# It runs HERE -- after the shared helpers and the driver loader, before the install
# mode, the ISO paths and any VM work -- because it touches none of them, and it runs
# UNELEVATED (see the elevation gate above): every file it edits belongs to the real
# user. lib\AgentVm.Cleanup.ps1 decides everything as data; this block prints the plan,
# gets the confirmation and reports what each step did. Every other run falls straight
# through it untouched.
function Invoke-ConstructRemoveInstanceAction {
    <#
        Remove one instance's client-side state from THIS PC, following the plan
        lib\AgentVm.Cleanup.ps1 builds. The outcome goes into
        $script:ConstructRemoveInstanceRc (0 = done, 1 = refused or a step failed) rather
        than the pipeline: this function prints, and anything a helper happened to emit
        would otherwise be indistinguishable from its result.

        -Interactive says whether there is somebody at the console to answer the typed
        confirmation a remote removal needs. An unattended run must SUPPLY it, never be
        asked for it: there is nobody to answer, and defaulting to "yes" would delete a VM.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Confirmation = "",
        [switch]$Interactive
    )
    $riTargetLib = Join-Path $PSScriptRoot "lib\AgentVm.InstanceTarget.ps1"
    $riCleanupLib = Join-Path $PSScriptRoot "lib\AgentVm.Cleanup.ps1"
    foreach ($riLib in @($riTargetLib, $riCleanupLib)) {
        if (-not (Test-Path -LiteralPath $riLib)) { throw "Required helper not found: $riLib" }
    }
    . $riTargetLib
    . $riCleanupLib
    # B12's per-instance state store: where the recorded OpenCode url lives, and what the
    # default instance's VM-scoped keys are classified by.
    $riStateLib = Join-Path $PSScriptRoot "lib\AgentVm.InstanceState.ps1"
    if (Test-Path -LiteralPath $riStateLib) { . $riStateLib }

    $script:ConstructRemoveInstanceRc = 1
    $riInventory = Get-ConstructInstanceInventory -Name $Name
    if (-not $riInventory.Known) {
        throw "'$Name' is not an instance on this PC. Known: $($riInventory.Names -join ', ')."
    }
    $riEntry = $riInventory.Entry
    $riIsDefault = ($riInventory.Default -ceq $Name)

    # A plan WITHOUT the confirmation first, so the console can print what would happen
    # and only then ask. The second call is the one that decides.
    # A URL this PC recorded for the VM's OpenCode server (a host forward's, which the
    # alias never appears in). Best-effort: the plan derives the direct URL itself, and a
    # state file that is not there is an instance whose forward this PC never saw.
    # The url the PROVISIONER recorded when it registered the entry (a host forward's,
    # which the VM's own name never appears in, and the only thing left to match on when
    # the user renamed the entry). Read from the instance's OWN state -- B12's store, the
    # same document the provisioner wrote it to. Best-effort: no record means the direct
    # url and the display names are what the removal matches on.
    $riExtraUrls = @()
    try {
        $riState = Read-ConstructInstanceState -Name $Name -Dir $PSScriptRoot
        foreach ($riKey in @('openCodeUrl', 'opencodeUrl')) {
            if ($riState -and ($riState.PSObject.Properties.Name -contains $riKey) -and $riState.$riKey -and
                $riExtraUrls -notcontains [string]$riState.$riKey) {
                $riExtraUrls += [string]$riState.$riKey
            }
        }
    } catch { $riExtraUrls = @() }

    $riPreview = Get-ConstructInstanceRemovalPlan -Name $Name -Identity $riEntry `
        -InstanceCount $riInventory.Count -IsDefault:$riIsDefault -OpenCodeUrls $riExtraUrls `
        -HomeDir $HOME -LocalAppData $env:LOCALAPPDATA -AppData $env:APPDATA -TempDir $env:TEMP `
        -ScriptsDir $PSScriptRoot
    if (-not $riPreview.Ok -and -not $riPreview.RequiresTypedConfirmation) {
        Write-Host ""
        Write-Host "REFUSED: $($riPreview.Refusal)" -ForegroundColor Red
        Write-Host ""
        Wait-Exit
        return
    }

    Write-Host ""
    Write-Host "==> Removing the instance '$Name' from this PC" -ForegroundColor Cyan
    foreach ($riStep in @($riPreview.Steps)) {
        Write-Host "    - $($riStep.Label)" -ForegroundColor DarkGray
    }
    foreach ($riKeep in @($riPreview.Keeps)) {
        Write-Host "    $riKeep" -ForegroundColor DarkGray
    }

    $riTyped = $Confirmation
    if ($riPreview.RequiresTypedConfirmation -and -not $riTyped) {
        # An unattended run must SUPPLY the confirmation, never be asked for it: there is
        # nobody at the console to answer, and defaulting to "yes" would delete a VM.
        if (-not $Interactive -or [Console]::IsInputRedirected) {
            throw "Removing '$Name' deletes its VM on the host service. An unattended run must pass -ConfirmInstanceName '$Name'."
        }
        Write-Host ""
        Write-Host "    This DELETES the VM '$Name' on its host service, including its disk." -ForegroundColor Yellow
        $riTyped = Read-Host "    Type the instance name to confirm"
    }
    $riPlan = Get-ConstructInstanceRemovalPlan -Name $Name -Identity $riEntry `
        -InstanceCount $riInventory.Count -IsDefault:$riIsDefault -Confirmation $riTyped `
        -OpenCodeUrls $riExtraUrls `
        -HomeDir $HOME -LocalAppData $env:LOCALAPPDATA -AppData $env:APPDATA -TempDir $env:TEMP `
        -ScriptsDir $PSScriptRoot
    if (-not $riPlan.Ok) {
        Write-Host ""
        Write-Host "REFUSED: $($riPlan.Refusal)" -ForegroundColor Red
        Write-Host ""
        Wait-Exit
        return
    }

    # The one step this script cannot do itself: the VM lives on somebody's host service,
    # so its DELETE goes through the remote driver -- the same contract function the
    # remote reinstall path uses. The credential is the one this PC ALREADY has for that
    # host (Windows identity, or the token stored at enrolment); a removal is not the
    # place to enrol a host afresh, so a refusal stops the run before any local state is
    # touched rather than half-cleaning an instance whose VM is still running.
    $riDeleteVm = $null
    if ($riPlan.DeletesVm) {
        $riServiceUrl = ""
        foreach ($riStep in @($riPlan.Steps)) {
            if ($riStep.Kind -eq 'remote-vm-delete') { $riServiceUrl = [string]$riStep.Target; break }
        }
        if (-not $riServiceUrl) {
            throw "The registry entry for '$Name' records no host service (service.url), so there is nothing to ask to delete the VM. Fix the entry, or remove it by hand."
        }
        $riRemoteLib = Join-Path $PSScriptRoot "lib\AgentVm.Remote.ps1"
        if (-not (Test-Path -LiteralPath $riRemoteLib)) {
            throw "Removing a remote instance needs lib/AgentVm.Remote.ps1, which is missing from this install. Update The Construct."
        }
        . $riRemoteLib
        $riAuth = $null
        $riStored = Get-ConstructRemoteToken -BaseUrl $riServiceUrl
        if ($riStored) { $riAuth = New-ConstructApiAuth -Mode token -Token $riStored }
        else { $riAuth = New-ConstructApiAuth -Mode negotiate }
        # The pinned certificate is enforced by the API client on every call, so nothing
        # here re-implements the enrolment pin ceremony.
        . $driverLoader -Backend "hyperv-remote" -ServiceUrl $riServiceUrl -Auth $riAuth
        # Invoke-ConstructApi, not the Test-ConstructRemoteAuth wrapper below it: this
        # block exits before that definition is ever reached.
        if (-not (Invoke-ConstructApi -BaseUrl $riServiceUrl -Method GET -Path '/whoami' -Auth $riAuth -NoThrow)) {
            throw "The host service at $riServiceUrl did not accept this PC's stored credential (HTTP $(Get-ConstructApiLastStatus)). Nothing was removed. Add the host again from the control panel, then retry."
        }
        $riDeleteVm = { param($name, $url) Remove-ConstructVm -Name $name }
    }

    $riResults = Invoke-ConstructInstanceRemoval -Plan $riPlan -DeleteVm $riDeleteVm `
        -RemoveRegistryEntry { param($name) Unregister-ConstructVm -Name $name }

    Write-Host ""
    $riFailed = 0
    foreach ($riResult in @($riResults)) {
        $riColor = "DarkGray"
        if ($riResult.Status -eq 'removed') { $riColor = "Green" }
        if ($riResult.Status -eq 'failed') { $riColor = "Red"; $riFailed++ }
        Write-Host ("    {0,-24} {1}" -f $riResult.Kind, $riResult.Message) -ForegroundColor $riColor
    }
    Write-Host ""
    if ($riFailed -gt 0) {
        Write-Host "    $riFailed step(s) could not be completed; everything else was removed." -ForegroundColor Yellow
        Write-Host ""
        Wait-Exit
        return
    }
    Write-Host "    Instance '$Name' removed from this PC." -ForegroundColor Green
    Write-Host ""
    $script:ConstructRemoveInstanceRc = 0
    Wait-Exit
    return
}

if ($Action -eq 'remove-instance') {
    if (-not $InstanceName) {
        throw "-Action remove-instance needs -InstanceName <name> (the instance to forget on this PC)."
    }
    Invoke-ConstructRemoveInstanceAction -Name $InstanceName -Confirmation $ConfirmInstanceName `
        -Interactive:(-not $FromPanel)
    exit $script:ConstructRemoveInstanceRc
}

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

# Resolve the install mode for the runs that never passed through the pre-elevation
# block above: an already-elevated console, and -SkipCreateVm. Cached, so a run that
# already decided (or was told by -Backend) does nothing here and asks nothing.
if (-not $script:ConstructInstallMode) {
    $modeSnapshot = Read-ConstructInstanceRegistrySnapshot
    [void](Resolve-ConstructInstallMode -Bound $PSBoundParameters -Snapshot $modeSnapshot)
}
$RemoteInstall = ($script:ConstructInstallMode -eq 'hyperv-remote')
if ($RemoteInstall -and $SkipCreateVm) {
    # -SkipCreateVm means "build the autoinstall ISO here and stop". A remote install
    # builds no ISO on this machine at all (the host service does), so the combination
    # has no meaning -- and silently ignoring one of two explicit flags is worse than
    # saying so.
    throw "-SkipCreateVm builds the autoinstall ISO on THIS PC, which a remote install never does (the host service builds it). Drop one of the two."
}
if (-not $RemoteInstall -and $PSBoundParameters.ContainsKey('InstanceName') -and $InstanceName) {
    # -InstanceName IS the name of a VM, whichever host it lives on (B11, plan section
    # 4.12). On the local path it names the Hyper-V VM this script builds: the display
    # name, the guest hostname, the ~\.ssh key file and the config-sync branch are all
    # derived from it, by the one rule in lib\AgentVm.Instances.ps1 -- never a second
    # copy here. (Before B11 this combination was refused outright, because -InstanceName
    # only meant "a VM on a host service".)
    $instanceTargetLib = Join-Path $PSScriptRoot "lib\AgentVm.InstanceTarget.ps1"
    if (-not (Test-Path -LiteralPath $instanceTargetLib)) {
        throw "-InstanceName needs lib/AgentVm.InstanceTarget.ps1, which is missing from this install. Update The Construct, or use -VmName '$InstanceName' instead."
    }
    . $instanceTargetLib
    $localInstanceIdentity = Get-ConstructLocalVmIdentity -Name $InstanceName
    if ($PSBoundParameters.ContainsKey('VmName') -and
        $VmName.ToLowerInvariant() -ne $localInstanceIdentity.VmName.ToLowerInvariant()) {
        # Never silently pick one of two machines the caller named.
        throw "-InstanceName '$InstanceName' and -VmName '$VmName' name two different VMs. Pass only one (the Hyper-V name is derived from the instance name)."
    }
    $VmName = $localInstanceIdentity.VmName
    # Bound from here on, so the -VmHost reconciliation and the skew guards below treat
    # this exactly like an explicit -VmName.
    $PSBoundParameters['VmName'] = $VmName
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
if ($RemoteInstall) {
    # The host service builds the ISO on its own machine, so nothing here needs a
    # release line -- but keep the variable defined, exactly like the -IsoPath branch.
    $UbuntuRelease = $FallbackUbuntuLts
} elseif ($PSBoundParameters.ContainsKey('UbuntuRelease') -and -not [string]::IsNullOrWhiteSpace($UbuntuRelease)) {
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

# Legacy alias: -VmHost (the guest hostname baked into the ISO) predates -VmName.
# A caller who passes -VmHost explicitly without -VmName gets the VM name derived
# from it, so guest hostname, Hyper-V name, mshome DNS name and SSH alias all agree
# (before -VmName existed such a run produced a VM the provisioner could not reach).
if ($PSBoundParameters.ContainsKey('VmHost') -and -not $PSBoundParameters.ContainsKey('VmName')) {
    $VmName = $VmHost
} elseif ($PSBoundParameters.ContainsKey('VmHost') -and $PSBoundParameters.ContainsKey('VmName') -and
          $VmHost.ToLowerInvariant() -ne $VmName.ToLowerInvariant()) {
    # Never silently discard an explicitly bound value: the guest hostname is always
    # derived from -VmName, so a differing -VmHost cannot be honoured.
    throw "-VmHost '$VmHost' conflicts with -VmName '$VmName': the guest hostname is derived from -VmName (lowercased). Pass only -VmName."
}

# ── THE ONE INSTANCE-NAME RULE, AND THE ONE DERIVATION ──────────────────────
# Both are ASKED FOR, never restated. lib\AgentVm.Instances.ps1 owns the rule (the same
# expression as NAME_RE in extension/src/instances.js and
# Constructd.Core.Logic.VmNameValidator.Pattern) and every value derived from a name --
# the guest hostname / mshome address, the ssh alias, the ~\.ssh key file and the
# config-sync branch. It may only be loaded in a CHILD scope (it turns strict mode on),
# which is exactly what lib\AgentVm.InstanceTarget.ps1 exists for, so this script holds
# no copy of either the pattern or the formulas:
#   * a LOWERCASE DNS LABEL: the lowercased VM name doubles as guest hostname, mshome
#     DNS label, SSH alias and key-name component (the display name is the same string;
#     "Work-VM" is fine, "Work VM" or "work.vm" is not);
#   * alphanumeric FIRST AND LAST: "work-" derives "work-.mshome.net", which is not a
#     host name at all, so the registry entry for such a VM could never be recorded;
#   * 1-63 chars -- the DNS label's own limit;
#   * "construct-" is RESERVED (the namespace the derived key and branch names live in).
$identityLib = Join-Path $PSScriptRoot "lib\AgentVm.InstanceTarget.ps1"
if (-not (Test-Path -LiteralPath $identityLib)) { throw "Required helper not found: $identityLib" }
. $identityLib
# Kept as script variables because Test-ConstructRemoteInstanceName asks the same
# question about a REMOTE instance name -- one source, two callers.
$script:ConstructVmNameRe   = Get-ConstructInstanceNamePattern
$script:ConstructVmNameRule = Get-ConstructInstanceNameRule
# Validates AND derives in one call; throws naming -VmName when the rule is broken.
$script:VmIdentity = Get-ConstructLocalVmIdentity -VmName $VmName -ParameterLabel 'VmName'

# Version-skew guard, BEFORE anything destructive: a non-default VM name needs a
# Create-AgentVM.ps1 that accepts -VmName. An older colocated script would silently
# create "Agent-VM" and the provisioner would then dial an address that never exists.
if (-not $SkipCreateVm -and $VmName.ToLowerInvariant() -ne 'agent-vm') {
    # Fail CLOSED: a missing sibling script is as fatal as an old one -- discovering it
    # after Remove-AgentVm would leave the user with no VM and no way to rebuild.
    $skewCreate = Join-Path $PSScriptRoot "Create-AgentVM.ps1"
    if (-not (Test-Path -LiteralPath $skewCreate)) { throw "Create-AgentVM.ps1 not found in $PSScriptRoot; cannot create a VM named '$VmName'." }
    $skewCmd = Get-Command -Name $skewCreate -CommandType ExternalScript -ErrorAction Stop
    foreach ($p in @('VmName', 'LocalKeyName', 'AutoinstallIso')) {
        if (-not $skewCmd.Parameters.ContainsKey($p)) {
            throw "This install's Create-AgentVM.ps1 does not support -$p; update The Construct before creating a VM named '$VmName'."
        }
    }
    # The provisioner must accept the instance identity too -- probe it here, before
    # any destructive step, not at the splat after the old VM is already gone.
    $skewProv = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
    if (-not (Test-Path -LiteralPath $skewProv)) { throw "Provision-AgentVM.ps1 not found in $PSScriptRoot; cannot create a VM named '$VmName'." }
    $skewProvCmd = Get-Command -Name $skewProv -CommandType ExternalScript -ErrorAction Stop
    # -ConfigBranch joins the list as the CAPABILITY MARKER for instance-keyed config
    # sync, even when this run has no explicit branch to pass: a provisioner that
    # predates it has no per-alias branch derivation either, so it would initialise and
    # sync this VM's store on the DEFAULT instance's 'vm' ref while the panel uses
    # 'vm-<name>'. Same rule as the extension's checkInstanceSupport gate -- and the
    # same exemption: EXPORT touches no config repo (Provision-AgentVM.ps1 -Action
    # export returns before repo init and before the sync tick), so it cannot land on
    # the wrong ref and must stay available on an older install. Every other action --
    # including the interactive menu, whose choice is not known yet -- is gated.
    $skewProvParams = @('VmHost', 'HostAlias', 'LocalKeyName', 'SshPort')
    if ($Action -ne 'export') { $skewProvParams += 'ConfigBranch' }
    foreach ($p in $skewProvParams) {
        if (-not $skewProvCmd.Parameters.ContainsKey($p)) {
            throw "This install's Provision-AgentVM.ps1 does not support -$p; update The Construct before creating a VM named '$VmName'."
        }
    }
}

# Same shape of guard for an EXPLICIT -ConfigBranch: honouring it half-way (the
# provisioner initialising the store on the alias-derived ref while the control panel
# syncs the named one) would split one VM across two host-config refs, so an install
# whose scripts cannot carry the parameter fails CLOSED -- here, before anything
# destructive, rather than at the splat with the old VM already deleted.
if ($ConfigBranch) {
    $cbProv = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
    if (-not (Test-Path -LiteralPath $cbProv)) { throw "Provision-AgentVM.ps1 not found in $PSScriptRoot; cannot honour -ConfigBranch '$ConfigBranch'." }
    $cbProvCmd = Get-Command -Name $cbProv -CommandType ExternalScript -ErrorAction Stop
    if (-not $cbProvCmd.Parameters.ContainsKey('ConfigBranch')) {
        throw "This install's Provision-AgentVM.ps1 does not support -ConfigBranch; update The Construct before using the config-sync branch '$ConfigBranch'."
    }
    # Only the VM-creating paths reach Create-AgentVM.ps1: -SkipCreateVm never does,
    # and neither do the panel's -Action reprovision / export (they go straight to
    # Provision). Anything else -- including the interactive menu, whose choice isn't
    # known yet -- is checked, because the check has to happen before the delete.
    if (-not $SkipCreateVm -and $Action -ne 'reprovision' -and $Action -ne 'export') {
        $cbCreate = Join-Path $PSScriptRoot "Create-AgentVM.ps1"
        if (-not (Test-Path -LiteralPath $cbCreate)) { throw "Create-AgentVM.ps1 not found in $PSScriptRoot; cannot honour -ConfigBranch '$ConfigBranch'." }
        $cbCreateCmd = Get-Command -Name $cbCreate -CommandType ExternalScript -ErrorAction Stop
        if (-not $cbCreateCmd.Parameters.ContainsKey('ConfigBranch')) {
            throw "This install's Create-AgentVM.ps1 does not support -ConfigBranch; update The Construct before using the config-sync branch '$ConfigBranch'."
        }
    }
}

if (-not $OutputIso) { $OutputIso = Join-Path $PSScriptRoot "$($VmName.ToLowerInvariant())-autoinstall.iso" }
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
    # Config-sync v2: prefer the shared config projects dir; fall back to the
    # shipped projects/ in the repo checkout (pre-migration / degraded mode).
    $projDir = if (Get-Command Get-ConstructConfigProjectsDir -ErrorAction SilentlyContinue) {
        Get-ConstructConfigProjectsDir -ScriptsDir $PSScriptRoot
    } else { Join-Path $PSScriptRoot "projects" }
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
        Auto      = $true
    }
    # Non-default VM: address it by its own endpoint/alias/key, all DERIVED in one place
    # (the default path passes no identity, so its invocation is unchanged). Fails closed
    # -- an install that cannot derive the identity would otherwise export the DEFAULT VM
    # under this VM's name.
    $exportIdentity = Get-ConstructDerivedVmIdentity -VmName $VmName
    if (-not $exportIdentity) {
        throw "Cannot derive the identity of the VM '$VmName' (lib/AgentVm.Instances.ps1 is missing or the name is unusable); refusing to export, because a run without it would export the default VM."
    }
    if (-not $exportIdentity.IsDefault) {
        $a['VmHost']       = [string]$exportIdentity.VmHost
        $a['HostAlias']    = [string]$exportIdentity.HostAlias
        $a['LocalKeyName'] = [string]$exportIdentity.KeyName
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
    # Where to dial comes from the driver (local Hyper-V: <name>.mshome.net:22), so
    # a non-local backend probes its real endpoint instead of a name convention.
    # Falls back to the local convention if the driver isn't loaded (version skew).
    $epIdentity = Get-ConstructDerivedVmIdentity -VmName $VmName
    $epHost = if ($epIdentity) { [string]$epIdentity.VmHost } else { "" }
    $epPort = if ($epIdentity) { [int]$epIdentity.SshPort } else { 22 }
    if (Get-Command Get-ConstructVmEndpoint -ErrorAction SilentlyContinue) {
        try {
            $ep = Get-ConstructVmEndpoint -Name $VmName
            if ($ep -and $ep.SshHost) { $epHost = [string]$ep.SshHost; $epPort = [int]$ep.SshPort }
        } catch { }
    }
    # No endpoint at all (no driver AND no derivable identity) means "cannot be reached
    # from here", which is what an unreachable VM answers anyway -- and BeginConnect on an
    # empty host would throw rather than say so.
    if (-not $epHost) { return $false }
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect($epHost, $epPort, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne($TimeoutMs)) { $client.EndConnect($iar); return $true }
        return $false
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}


# ═════════════ REMOTE HOST INSTALL (plan §4.5, docs/remote-host.md) ═════════════
# Everything the remote path needs lives between here and its `return`. It is a
# SEPARATE flow rather than a set of branches through the local one on purpose: the
# zero-change bar is "an existing local install behaves and prints identically", and
# the cheapest way to guarantee that is for the local code below to be unreachable
# whenever this runs -- and untouched whenever it doesn't.

function Confirm-ConstructRemotePin {
    <#
        Make sure we know -- and have confirmed -- which certificate this host service
        presents, before any credential is sent to it.

          already pinned + matches   -> silent OK
          already pinned + DIFFERENT -> HARD FAILURE naming both values. A changed
                                        certificate is either a deliberate host rebuild
                                        (re-enrol on purpose) or exactly what pinning
                                        exists to catch; it is never a prompt to click
                                        through.
          not pinned yet             -> show the fingerprint, ask ONCE, then pin it.

        http:// has no certificate at all -- allowed for a local development/fake
        service, with a warning, because there is nothing to verify.
    #>
    param([Parameter(Mandatory)][string]$BaseUrl)

    $uri = [System.Uri](ConvertTo-ConstructServiceUrl -Value $BaseUrl)
    if ($uri.Scheme -ne 'https') {
        # Refused for anything but a service on this machine, and refused HERE so the
        # message names the URL the user just typed rather than surfacing on the first
        # API call. (The client enforces the same rule again at every call.)
        Assert-ConstructTransportSafe -BaseUrl $BaseUrl
        Write-Warning "The host service URL is plain http, so its identity cannot be verified. That is accepted only because it is on this machine."
        return
    }

    $live   = Get-ConstructRemoteFingerprint -BaseUrl $BaseUrl
    $pinned = Get-ConstructRemotePin -BaseUrl $BaseUrl
    if ($pinned) {
        if (Test-ConstructFingerprintMatch -Expected $pinned -Actual $live) {
            Write-Ok "Host certificate matches the fingerprint pinned on this machine."
            return
        }
        throw ("The certificate of $($uri.Host) does not match the one pinned on this machine.`n" +
               "    pinned:    $pinned`n" +
               "    presented: $live`n" +
               "Refusing to continue. If the host's certificate was legitimately replaced, delete " +
               "$(Get-ConstructRemotePinPath -BaseUrl $BaseUrl) and add the host again.")
    }

    # -DefaultNo: on a non-interactive host Invoke-TuiConfirm answers with the default,
    # and silently trusting an unseen certificate is not an answer this flow may give.
    $ok = Invoke-TuiConfirm -ScreenTitle "Confirm the host service's certificate" -Body @(
        "The Construct host service uses a self-signed certificate, so it is identified",
        "by its SHA-256 fingerprint rather than by a certificate authority. Compare this",
        "with what the host's administrator published:",
        "",
        "    $live",
        "",
        "It is pinned once and then enforced on every later call."
    ) -Question "Is that the correct fingerprint for $($uri.Host)?" -DefaultNo `
      -YesLabel "Yes  pin it and continue" `
      -NoLabel  "No   stop; nothing has been changed"
    if (-not $ok) {
        throw "The host service's certificate was not confirmed. Nothing was created or changed."
    }
    [void](Save-ConstructRemotePin -BaseUrl $BaseUrl -Fingerprint $live)
    Write-Ok "Fingerprint pinned for $($uri.Host)."
}

function Read-ConstructSecretInput {
    <# Read a secret from the console WITHOUT echoing it. Returns "" when the host is
       non-interactive (nothing to type into) or the user just pressed Enter. #>
    param([Parameter(Mandatory)][string]$Prompt)
    if ([Console]::IsInputRedirected) { return "" }
    $secure = Read-Host "  $Prompt" -AsSecureString
    if (-not $secure -or $secure.Length -eq 0) { return "" }
    $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try { return [System.Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
}

function Test-ConstructRemoteAuth {
    <# Does this credential work? GET /whoami, and the identity it answered with (or
       $null). -NoThrow so a 401 is data, not an exception -- that is the whole point:
       the enrolment flow BRANCHES on it. #>
    param([Parameter(Mandatory)][string]$BaseUrl, [Parameter(Mandatory)]$Auth)
    return (Invoke-ConstructApi -BaseUrl $BaseUrl -Method GET -Path '/whoami' -Auth $Auth -NoThrow)
}

function Resolve-ConstructRemoteAuth {
    <#
        Get a WORKING credential for the host service, or throw.

        Order (plan §4.5 step 2): Windows/Kerberos as the current user, silently, first
        -- it is the one credential that needs no secret anywhere. Only when the service
        answers 401 does the user get asked, and then with both alternatives:

          * paste an API token   -> verified, then stored DPAPI-encrypted for this user
          * domain user+password -> verified, held for this run only, never stored

        A stored token is tried before prompting, so a token-mode host is silent too.
        Anything that is NOT a 401 (unreachable, TLS, 5xx) is fatal here: retrying with
        another credential would only produce the same failure with a confusing message.

        Returns @{ Auth; Identity; Mode }.
    #>
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [string]$PreferredMode = 'negotiate'
    )

    $tryOrder = @()
    if ($PreferredMode -eq 'token') { $tryOrder = @('token', 'negotiate') }
    else { $tryOrder = @('negotiate', 'token') }

    foreach ($mode in $tryOrder) {
        $candidate = $null
        if ($mode -eq 'negotiate') {
            $candidate = New-ConstructApiAuth -Mode negotiate
        } else {
            $stored = Get-ConstructRemoteToken -BaseUrl $BaseUrl
            if (-not $stored) { continue }
            $candidate = New-ConstructApiAuth -Mode token -Token $stored
        }
        $me = Test-ConstructRemoteAuth -BaseUrl $BaseUrl -Auth $candidate
        if ($me) {
            if ($mode -eq 'negotiate') { Write-Ok "Authenticated with this Windows session's identity." }
            else { Write-Ok "Authenticated with the API token stored for this host." }
            return @{ Auth = $candidate; Identity = $me; Mode = $mode }
        }
        $status = Get-ConstructApiLastStatus
        if ($status -ne 401) {
            throw "Could not talk to the host service at $BaseUrl (HTTP $status): $(Get-ConstructApiLastError)"
        }
        if ($mode -eq 'token') { Write-Warning "The API token stored for this host was refused." }
    }

    # 401 from everything we could try on our own: ask. But there has to be somebody to
    # ask -- on a non-interactive host Show-Menu answers with its default and the secret
    # prompt returns nothing, which would spin this loop forever. Fail with the reason
    # instead.
    if ([Console]::IsInputRedirected) {
        throw "The host service at $BaseUrl did not accept this Windows session, and this run is not interactive so no other credential can be entered. Add the host once interactively (so a token is stored), or run with -ServiceAuth token after storing one."
    }
    while ($true) {
        $pick = Show-Menu -Title "The host service did not accept this Windows session. How would you like to sign in?" -Options @(
            "API token        paste the token the host's administrator issued you",
            "Domain account   sign in with a user name and password",
            "Cancel           make no changes and exit"
        ) -Default 0
        if ($pick -eq 2) { throw "No credential for the host service. Nothing was created or changed." }

        $candidate = $null
        $modeName  = ""
        if ($pick -eq 0) {
            Show-TuiScreen -Title "API token" -Body @(
                "Paste the token your administrator issued (it is not echoed).",
                "It is verified before anything else happens, and then stored encrypted",
                "for your Windows account only -- never in plaintext."
            )
            $token = Read-ConstructSecretInput -Prompt "API token"
            if (-not $token) { continue }
            $candidate = New-ConstructApiAuth -Mode token -Token $token
            $modeName  = 'token'
        } else {
            Show-TuiScreen -Title "Domain account" -Body @(
                "Sign in with a domain user and password. The password is used for this",
                "run only and is never written anywhere."
            )
            $cred = $null
            try { $cred = Get-Credential -Message "Sign in to the Construct host service" }
            catch { $cred = $null }
            if (-not $cred) { continue }
            $candidate = New-ConstructApiAuth -Mode credential -Credential $cred
            $modeName  = 'credential'
        }

        $me = Test-ConstructRemoteAuth -BaseUrl $BaseUrl -Auth $candidate
        if ($me) {
            if ($modeName -eq 'token') {
                # Only a VERIFIED token is stored, and only when DPAPI can protect it.
                try { [void](Save-ConstructRemoteToken -BaseUrl $BaseUrl -Token $candidate['Token']); Write-Ok "API token stored (encrypted for your Windows account)." }
                catch { Write-Warning "The token works but could not be stored: $($_.Exception.Message)" }
            }
            Write-Ok "Authenticated."
            # A credential prompt is a per-run credential; a token is the durable one.
            return @{ Auth = $candidate; Identity = $me; Mode = $(if ($modeName -eq 'token') { 'token' } else { 'negotiate' }) }
        }
        $status = Get-ConstructApiLastStatus
        if ($status -eq 401) { Write-Warning "The host service refused that credential." }
        else { throw "Could not talk to the host service at $BaseUrl (HTTP $status): $(Get-ConstructApiLastError)" }
    }
}

function Show-ConstructRemoteIdentity {
    <# Report who the service says we are, and stop when it does not know us at all --
       "you authenticated fine, but nobody enrolled you" is a different problem from
       "wrong credential", and only /whoami can tell them apart. #>
    param([Parameter(Mandatory)]$Identity, [Parameter(Mandatory)][string]$BaseUrl)
    $name = ""; $role = ""; $known = $false; $quota = ""
    if ($Identity.PSObject.Properties['name'])   { $name  = [string]$Identity.name }
    if ($Identity.PSObject.Properties['role'])   { $role  = [string]$Identity.role }
    if ($Identity.PSObject.Properties['known'])  { $known = [bool]$Identity.known }
    if ($Identity.PSObject.Properties['maxVms'] -and $null -ne $Identity.maxVms) { $quota = [string]$Identity.maxVms }
    if (-not $known) {
        throw ("The host service at $BaseUrl authenticated you as '$name', but you are not enrolled on it. " +
               "Ask its administrator to add you (docs/remote-host.md, 'Admin: set the host up once').")
    }
    $line = "Signed in to $BaseUrl as $name"
    if ($role)  { $line += " (role: $role" }
    if ($quota) { $line += "$(if ($role) { ', ' } else { ' (' })VM quota: $quota" }
    if ($role -or $quota) { $line += ")" }
    Write-Ok $line
    return $name
}

function Test-ConstructRemoteInstanceName {
    <#
        The registry's name rule, which the service enforces too
        (Constructd.Core.Logic.VmNameValidator.Pattern is the same expression). Repeated
        here rather than imported because lib\AgentVm.Instances.ps1 may only be loaded in
        a child scope (it turns strict mode on) -- it is the ONE rule
        ($script:ConstructVmNameRe, defined with the -VmName check above).

        'agent-vm' is RESERVED and refused: it is the default instance, always present
        (synthesized when the registry has no entry for it) and the fallback of every
        zero-change code path -- so Add-ConstructInstance refuses to replace it. Catching
        that HERE is the point: the alternative is discovering it after a VM has been
        built on somebody else's host and can no longer be recorded. The 'construct-'
        PREFIX is reserved for the same class of reason (see the -VmName check).
    #>
    param([string]$Name)
    if (-not $Name) { return $false }
    if ($Name -ceq 'agent-vm') { return $false }
    if ($Name.ToLowerInvariant().StartsWith('construct-')) { return $false }
    return [bool]([regex]::IsMatch($Name, $script:ConstructVmNameRe))
}

function Get-ConstructEndpointPublicHost {
    <#
        The publicHost an endpoint object states (plan section 4.12), or "" when it states
        none. -Endpoint is what Get-ConstructVmEndpoint / New-ConstructVm return -- a
        hashtable on some paths, a PSCustomObject on others -- so BOTH shapes are read
        here rather than at three call sites. An older host service, and any host with no
        Constructd:PublicHostPattern, states nothing, which is exactly "use the SSH host".
        Pure.
    #>
    param($Endpoint)
    if ($null -eq $Endpoint) { return "" }
    if ($Endpoint -is [System.Collections.IDictionary]) {
        if ($Endpoint.Contains('PublicHost')) { return ([string]$Endpoint['PublicHost']).Trim() }
        return ""
    }
    if ($Endpoint.PSObject.Properties['PublicHost']) { return ([string]$Endpoint.PublicHost).Trim() }
    return ""
}

function New-ConstructRemoteInstanceEntry {
    <#
        The registry entry a remote VM is recorded as -- built in ONE place so the
        pre-create check, the post-create check and the write itself can never judge a
        different entry than the one that lands on disk.

        -SshHost/-SshPort are the endpoint the SERVICE allocated. Before it exists (the
        pre-create check) the service URL's own host stands in and the endpoint identity
        is excluded from the check instead of guessed at -- see
        Get-ConstructRemoteInstanceConflict.

        -PublicHost is the name this VM's WEB endpoints live under (plan section 4.12):
        the service's rendered Constructd:PublicHostPattern, as GET /vms/{name}/endpoint
        reported it. OPTIONAL and only recorded when it says something the SSH host does
        not -- a host with no pattern reports its own PublicHost for every VM, and writing
        that as a per-VM field would be noise the reader has to ignore anyway.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$SshHost,
        [int]$SshPort = 22,
        [string]$ServiceUrl = "",
        [string]$ServiceAuth = "",
        [string]$Owner = "",
        [string]$PublicHost = ""
    )
    $entry = @{
        backend      = 'hyperv-remote'
        vmName       = $Name          # the service addresses the VM by this name, and so
                                      # does a rebuild (-InstanceName): both readers pin
                                      # vmName = the instance name for this backend
        sshHost      = $SshHost
        sshPort      = [int]$SshPort
        hostAlias    = $Name
        keyName      = "construct_${Name}_ed25519"
        configBranch = "vm-$Name"
        owner        = $Owner
    }
    if ($ServiceUrl) { $entry['service'] = @{ url = $ServiceUrl; auth = $ServiceAuth } }
    if ($PublicHost -and $PublicHost -ne $SshHost) { $entry['publicHost'] = $PublicHost }
    return $entry
}

function Get-ConstructRemoteInstanceConflict {
    <#
        Every reason the instance registry would REFUSE this entry, as strings (@() = it
        will load). Answered BY THE REGISTRY LIBRARY ITSELF -- Get-ConstructInstanceEntryProblem
        (the per-entry rules) plus Get-ConstructInstanceCollision (the cross-entry identity
        rules) -- so the installer never re-states a rule the two readers own. Anything
        this returns is something Save-ConstructInstanceEntry would throw on later.

        -IgnoreEndpoint drops the COMPOSITE ENDPOINT rule ('sshHost/sshPort'), and only
        that one, for the PRE-CREATE call: the service has not allocated this VM's SSH
        forward yet, so the endpoint half of the identity does not exist to judge.
        Several VMs on ONE host service, told apart by the port the service allocated
        each of them, are exactly the intended multi-VM flow -- the endpoint is checked
        for real, once, against the endpoint the service actually returned.

        Same child-scope discipline as the snapshot reader (the library turns strict mode
        on), and the same degradation: with no library there is nothing to conflict with,
        and the write itself will report the missing file.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][hashtable]$Entry,
        [switch]$IgnoreEndpoint,
        [string]$ScriptsDir = $PSScriptRoot
    )
    $lib = Join-Path (Join-Path $ScriptsDir "lib") "AgentVm.Instances.ps1"
    if (-not (Test-Path -LiteralPath $lib)) { return @() }
    return @(& {
        param($libPath, $n, $e, $ignoreEndpoint)
        . $libPath
        $out = New-Object System.Collections.Generic.List[string]
        foreach ($p in @(Get-ConstructInstanceEntryProblem -Name $n -Entry $e)) { $out.Add($p) }
        # The cross-entry half: put the candidate into a COPY of the live registry (which
        # replaces an entry of the same name -- a rebuild never collides with itself) and
        # ask the shared collision rules about it.
        $reg  = Read-ConstructInstances
        $next = Copy-ConstructInstanceRegistry -Registry $reg
        $next.Instances[$n] = Resolve-ConstructInstanceDefaults -Name $n `
                                  -Entry (ConvertTo-ConstructInstanceEntryObject -Entry $e)
        $exclude = if ($ignoreEndpoint) { @('sshHost/sshPort') } else { @() }
        foreach ($p in @((Get-ConstructInstanceCollision -Instances $next.Instances -ExcludeLabel $exclude).Problems)) {
            $out.Add($p)
        }
        return @($out)
    } $lib $Name $Entry ([bool]$IgnoreEndpoint))
}

function New-ConstructRemoteVmRecord {
    <#
        CREATE the VM on the host service, CHECK the entry its endpoint yields against the
        shared registry rules, ROLL THE CREATE BACK if the registry would refuse it, and
        otherwise RECORD the instance -- all before anything is provisioned.

        It is one function rather than inline flow so the whole sequence can be driven in
        a test with fake service calls (test/remote-install.test.ps1): "two VMs on one
        service host, on the ports it allocated them, both register" and "the same
        host:port rolls back and records nothing" are the contract, and a contract that
        can only be asserted by reading source order is not really asserted.

        WHY THE RECORD HAPPENS HERE, before provisioning: the registry entry is the ONLY
        handle this PC has on a remote VM. Locally, a provision that fails still leaves a
        VM that Get-VM finds and the existing-VM menu offers to reprovision; remotely
        there is nothing to enumerate, so an unrecorded VM would be unreachable AND
        un-recreatable (the service refuses a second VM of the same name). Recording it
        first turns the common failure -- provisioning, which touches the network, apt and
        ssh -- into "re-run and pick Reprovision".

        The write itself is best-effort by design: if it fails, provisioning is still
        worth doing (it configures this PC's ssh config and key), so that warns with what
        to clean up instead of aborting. A registry CONFLICT is the opposite -- the VM
        could never be reached or rebuilt from this PC -- so it throws, after the rollback.

        Returns @{ Endpoint; VmToken; Entry; Recorded }.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][hashtable]$Descriptor,
        [Parameter(Mandatory)][string]$ServiceUrl,
        [string]$ServiceAuth = "",
        [string]$Owner = "",
        [string]$RegistryPath = "",
        [switch]$MakeDefault,
        [string]$ScriptsDir = $PSScriptRoot
    )
    $created  = New-ConstructVm -Descriptor $Descriptor
    $endpoint = $created.Endpoint
    Write-Ok "Endpoint: $($endpoint.SshHost):$($endpoint.SshPort)"
    # The entry this VM will be recorded as -- built ONCE, so what is checked below is
    # byte-for-byte what is written.
    # The endpoint's own publicHost when the service stated one (an older service, or one
    # with no PublicHostPattern, reports none and the field is simply absent from the entry).
    $endpointPublicHost = Get-ConstructEndpointPublicHost -Endpoint $endpoint
    $entry = New-ConstructRemoteInstanceEntry -Name $Name `
                 -SshHost ([string]$endpoint.SshHost) -SshPort ([int]$endpoint.SshPort) `
                 -ServiceUrl $ServiceUrl -ServiceAuth $ServiceAuth -Owner $Owner `
                 -PublicHost $endpointPublicHost
    # NOW the FULL registry check, endpoint included: this is the first moment the true
    # address is known (nothing exposes the service's allocated forward -- or its own
    # advertised PublicHost, which can differ from the URL's -- before a VM exists).
    # The composite (sshHost, sshPort) is what the two readers treat as one endpoint, so a
    # second VM on the same service host is a conflict only when the service handed it the
    # SAME PORT as an instance this PC already has.
    #
    # A conflict here means the VM cannot be recorded, i.e. cannot be reached or rebuilt
    # from this PC ever again. Rather than leave that orphan behind (holding its name, its
    # disk and the host's RAM), the create is ROLLED BACK: the same DELETE the reinstall
    # path uses, and only then the failure. The one-time VM token dies with the VM, which
    # is exactly what should happen to a credential for a machine that no longer exists.
    $conflicts = @(Get-ConstructRemoteInstanceConflict -Name $Name -Entry $entry -ScriptsDir $ScriptsDir)
    if ($conflicts.Count -gt 0) {
        $why = "This PC's instance registry would refuse '$Name': $($conflicts -join '; ')"
        Write-Warning "The VM was created, but this PC cannot record it: $why"
        Write-Note "Rolling the creation back so nothing is left stranded on the host..."
        try { Remove-ConstructVm -Name $Name } catch {
            Write-Warning "The rollback failed as well: $($_.Exception.Message)"
            throw "$why`nThe VM '$Name' still EXISTS on $ServiceUrl and could not be removed automatically -- delete it there before trying again."
        }
        throw "$why`nThe VM '$Name' was removed from $ServiceUrl again, so nothing was left behind."
    }

    # Written through lib\AgentVm.Instances.ps1 (never hand-rolled JSON) so the PS and JS
    # readers can never disagree about what is in the file.
    $recorded = $false
    try {
        [void](Save-ConstructInstanceEntry -Name $Name -Replace -MakeDefault:$MakeDefault -Entry $entry -ScriptsDir $ScriptsDir)
        $recorded = $true
        Write-Ok "Recorded the instance '$Name' in $RegistryPath"
    } catch {
        Write-Warning ("The VM '$Name' was created on $ServiceUrl but could not be recorded in the instance registry: " +
                       "$($_.Exception.Message)`n    Provisioning continues, but the control panel will not list it. " +
                       "Fix the registry ($RegistryPath) and re-run, or delete the VM on the host.")
    }
    return [pscustomobject]@{
        Endpoint = $endpoint
        VmToken  = [string]$created.VmToken
        Entry    = $entry
        Recorded = $recorded
    }
}

function Invoke-RemoteVmConfigExport {
    <# Provision-AgentVM.ps1 -Action export against a REMOTE instance's endpoint. The
       local Invoke-VmConfigExport derives "<name>.mshome.net" from the VM name, which
       is exactly the name convention a remote endpoint does not follow -- so this one
       is handed the endpoint instead. #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$Endpoint,
        [Parameter(Mandatory)][string]$BackupDir,
        [switch]$ScanReposOnly
    )
    $ps = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
    if (-not (Test-Path -LiteralPath $ps)) { throw "Provision-AgentVM.ps1 not found in $PSScriptRoot." }
    $a = @{
        Action       = 'export'
        BackupDir    = $BackupDir
        Auto         = $true
        VmHost       = [string]$Endpoint.SshHost
        SshPort      = [int]$Endpoint.SshPort
        HostAlias    = $Name
        LocalKeyName = "construct_${Name}_ed25519"
    }
    if ($ScanReposOnly) { $a['ScanReposOnly'] = $true }
    & $ps @a
}

function New-ConstructRemoteProvisionArgs {
    <#
        The splat for Provision-AgentVM.ps1 against a REMOTE instance -- the identity
        half of plan section 4.5 step 5, in ONE place so the create and the reprovision
        paths cannot drift apart:

            -VmHost/-SshPort   the endpoint the SERVICE allocated (never a name convention)
            -HostAlias         the instance name = the ssh_config Host block it writes
            -LocalKeyName      construct_<name>_ed25519, so a second VM never overwrites
                               the first VM's ~\.ssh key
            -ConfigBranch      vm-<name>, so this VM's config store is its own ref
            -ServiceUrl/-InstanceName/-VmTokenB64  the guest's link back to the service
            -PublicHost        the name the VM's WEB endpoints live under (plan 4.12),
                               when the host service renders one; SSH still goes to the
                               endpoint above

        -VmTokenB64 is added ONLY when a token was actually issued (a rebuild that could
        not consume one still provisions; the guest simply gets no heartbeat credential).
        It is base64 of the raw secret, passed as a PARAMETER VALUE -- never rendered
        into a command line, printed, or logged.

        The three late-added feature flags are dropped when the installed provisioner
        does not declare them ($script:RemoteProvCmd), exactly like the local chain's
        probe-before-splat: an unknown parameter is a BINDING failure, and by this point
        a rebuild has already deleted the old VM.
    #>
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$Endpoint,
        [Parameter(Mandatory)][string]$ServiceUrl,
        [Parameter(Mandatory)][string]$ConfigBranch,
        [string]$Projects = "",
        [string]$GitName = "",
        [string]$GitEmail = "",
        [string]$CloneCredB64 = "",
        [string]$VmToken = "",
        [string]$PublicHost = ""
    )
    $a = @{
        VmHost       = [string]$Endpoint.SshHost
        SshPort      = [int]$Endpoint.SshPort
        HostAlias    = $Name
        LocalKeyName = "construct_${Name}_ed25519"
        ConfigBranch = $ConfigBranch
        ServiceUrl   = $ServiceUrl
        InstanceName = $Name
        Auto         = $true
        GitUserName  = $GitName
        GitEmail     = $GitEmail
        ClaudePartialStreaming    = $ClaudePartialStreaming
        MicPassthrough            = $MicPassthrough
        OpenCodeBackgroundWatcher = $OpenCodeBackgroundWatcher
        T3Code                    = $T3Code
        T3CodeChannel             = $T3CodeChannel
        T3CodeLimitResume         = $T3CodeLimitResume
    }
    if ($Projects) { $a['Projects'] = $Projects }
    if ($CloneCredB64) { $a['GitCloneCredentialsB64'] = $CloneCredB64 }
    if ($VmToken) {
        $a['VmTokenB64'] = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($VmToken))
    }
    # Probe before splat, like the three feature flags below: an installed provisioner
    # without -PublicHost would fail to BIND, and by this point a rebuild has already
    # deleted the old VM. Dropping it costs the per-VM web host name, not the install.
    if ($PublicHost) {
        if ($script:RemoteProvCmd -and -not $script:RemoteProvCmd.Parameters.ContainsKey('PublicHost')) {
            Write-Note "This install's Provision-AgentVM.ps1 has no -PublicHost; the VM's web endpoints will use $($Endpoint.SshHost) instead of $PublicHost."
        } else {
            $a['PublicHost'] = $PublicHost
        }
    }
    if ($AutoResolve) { $a['AutoResolve'] = $AutoResolve }
    if ($script:RemoteBound -and ($script:RemoteBound.ContainsKey('Repo') -or $script:RemoteBound.ContainsKey('Ref'))) {
        $a['Repo'] = $Repo; $a['Ref'] = $Ref
    }
    foreach ($opt in @('T3CodeChannel', 'T3CodeLimitResume', 'OpenCodeBackgroundWatcher')) {
        if ($script:RemoteProvCmd -and -not $script:RemoteProvCmd.Parameters.ContainsKey($opt)) { $a.Remove($opt) }
    }
    return $a
}

if ($RemoteInstall) {
    # State this flow owns. The LOCAL path declares its own copies further down; they
    # are separate because that code is unreachable from here.
    $restoreDir               = ""    # set when a reinstall saved a config to restore
    $restoredProjectNames     = @()   # project profiles that save generated
    $script:RemoteRebuildName = ""    # set when this run is REBUILDING a known instance
    $script:RemoteBound       = $PSBoundParameters
    # ── The API client + the instance registry ────────────────────────────────
    $remoteLib = Join-Path $PSScriptRoot "lib\AgentVm.Remote.ps1"
    if (-not (Test-Path -LiteralPath $remoteLib)) {
        throw "A remote install needs lib/AgentVm.Remote.ps1, which is missing from this install. Update The Construct."
    }
    . $remoteLib

    $registry = Read-ConstructInstanceRegistrySnapshot
    if ($null -eq $registry) {
        throw "A remote install needs lib/AgentVm.Instances.ps1 (the instance registry), which is missing from this install. Update The Construct."
    }
    foreach ($p in @($registry.Problems)) { if ($p) { Write-Warning "Instance registry: $p" } }

    # ── Version skew, checked BEFORE anything is created or deleted ───────────
    # A provisioner that cannot be TOLD the endpoint, the key, the branch or the host
    # service would silently provision the DEFAULT local VM instead. Fail closed here,
    # while nothing has happened yet -- the same discipline (and the same reason) as the
    # local path's guard before Remove-AgentVm.
    $provisionScript = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
    if (-not (Test-Path -LiteralPath $provisionScript)) {
        throw "Provision-AgentVM.ps1 not found in $PSScriptRoot; cannot provision a remote VM."
    }
    $provCmd = Get-Command -Name $provisionScript -CommandType ExternalScript -ErrorAction Stop
    $script:RemoteProvCmd = $provCmd
    foreach ($p in @('VmHost', 'SshPort', 'HostAlias', 'LocalKeyName', 'ConfigBranch', 'ServiceUrl', 'InstanceName', 'VmTokenB64')) {
        if (-not $provCmd.Parameters.ContainsKey($p)) {
            throw "This install's Provision-AgentVM.ps1 does not support -$p, so it could not provision a remote VM. Update The Construct."
        }
    }

    # ── Which instance is this about? ─────────────────────────────────────────
    $existingEntry = $null
    if ($InstanceName -and $registry.Entries.ContainsKey($InstanceName)) {
        $existingEntry = $registry.Entries[$InstanceName]
        if ([string]$existingEntry.Backend -cne 'hyperv-remote') {
            throw "The instance '$InstanceName' is a '$($existingEntry.Backend)' instance, not a remote one. Drop -Backend hyperv-remote to manage it."
        }
    }

    # ── The host service ──────────────────────────────────────────────────────
    $svcUrl = $ServiceUrl
    if (-not $svcUrl -and $existingEntry) { $svcUrl = [string]$existingEntry.ServiceUrl }
    if (-not $svcUrl) {
        $svcUrl = Invoke-TuiInput -ScreenTitle "Construct host service" -Body @(
            "Enter the address of the Construct host service that will run this VM.",
            "Your administrator publishes it, e.g.:",
            "",
            "    https://buildbox.example.local:7462",
            "",
            "A bare host name gets https and the default port 7462."
        ) -Prompt "Host service URL"
    }
    if (-not $svcUrl) { throw "A remote install needs the host service's URL (-ServiceUrl). Nothing was changed." }
    $svcUrl = ConvertTo-ConstructServiceUrl -Value $svcUrl

    Write-Step "Connecting to the host service"
    Write-Note $svcUrl

    # ── Certificate, then credentials, then identity ──────────────────────────
    Confirm-ConstructRemotePin -BaseUrl $svcUrl

    $preferredAuth = $ServiceAuth
    if (-not $PSBoundParameters.ContainsKey('ServiceAuth') -and $existingEntry -and $existingEntry.ServiceAuth) {
        $preferredAuth = [string]$existingEntry.ServiceAuth
    }
    $authResult = Resolve-ConstructRemoteAuth -BaseUrl $svcUrl -PreferredMode $preferredAuth
    $remoteAuth = $authResult.Auth
    $remoteAuthMode = [string]$authResult.Mode
    $remoteOwner = Show-ConstructRemoteIdentity -Identity $authResult.Identity -BaseUrl $svcUrl

    # ── The remote driver ─────────────────────────────────────────────────────
    # From here every VM operation goes through the SAME contract functions the local
    # path uses (docs/drivers.md) -- they just speak HTTP now. Loading the remote driver
    # REPLACES the hyperv-local ones loaded earlier, which is safe because this block
    # returns and no local code runs afterwards.
    . $driverLoader -Backend "hyperv-remote" -ServiceUrl $svcUrl -Auth $remoteAuth

    # ═══ An instance that already exists: reprovision / reinstall / export ════
    if ($existingEntry) {
        $instName = [string]$existingEntry.Name
        $instKey  = "construct_${instName}_ed25519"
        $instBranch = [string]$existingEntry.ConfigBranch
        if (-not $instBranch) { $instBranch = "vm-$instName" }

        # The endpoint is the SERVICE's to state (the forward may have been reallocated
        # since the registry entry was written), with the registry as the fallback when
        # the service cannot answer.
        $endpoint = $null
        try { $endpoint = Get-ConstructVmEndpoint -Name $instName } catch { $endpoint = $null }
        if (-not $endpoint) {
            $entryPublicHost = ""
            if ($existingEntry.PSObject.Properties['PublicHost'] -and $existingEntry.PublicHost) { $entryPublicHost = [string]$existingEntry.PublicHost }
            $endpoint = @{ SshHost = [string]$existingEntry.VmHost; SshPort = [int]$existingEntry.SshPort; PublicHost = $entryPublicHost }
            Write-Warning "The host service did not report an endpoint for '$instName'; using the address recorded in the instance registry ($($endpoint.SshHost):$($endpoint.SshPort))."
        }

        Show-TuiScreen -Title "The remote VM '$instName' is already registered on $svcUrl."
        if ($PSBoundParameters.ContainsKey('Action')) {
            $choice = switch ($Action) {
                'reprovision' { 0 }
                'reinstall'   { 1 }
                'redownload'  { 1 }   # the host owns its source image; a rebuild is a rebuild
                'export'      { 2 }
                default       { -1 }
            }
            if ($choice -lt 0) {
                # 'add-config' is the only one that lands here. It is a local-path flow
                # (import profiles, then reprovision); doing nothing quietly would look
                # like success for something the user explicitly asked for.
                throw "-Action $Action is not available for the remote instance '$InstanceName' in this build. Use reprovision, reinstall or export."
            }
            Write-Note "Action selected by the control panel: $Action"
        } else {
            $choice = Show-Menu -Title "What would you like to do?" -Options @(
                "Reprovision      re-run provisioning on the existing VM (keeps all data)",
                "Reinstall        DELETE the VM on the host and build + install a fresh one",
                "Export config    save the VM's current agent config + auth to this host (no changes to the VM)",
                "Remove instance  DELETE the VM on the host and forget it on this PC",
                "Quit             make no changes and exit"
            ) -Default 0
        }

        if ($choice -eq 2) {
            try {
                Show-TuiScreen -Title "Exporting the VM's agent config" -Body @(
                    "Saving auth, memory, skills, instruction files, and project setup to this host..."
                )
                Invoke-RemoteVmConfigExport -Name $instName -Endpoint $endpoint -BackupDir (Get-ConstructBackupDir -Dir $PSScriptRoot)
                Write-Host ""
                Write-Ok "Saved the VM's current agent config to:"
                Write-Host "      $(Get-ConstructBackupDir -Dir $PSScriptRoot)" -ForegroundColor White
            } catch {
                Write-Host ""
                Write-Host "ERROR: config export failed." -ForegroundColor Red
                Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
            }
            Write-Host ""; Wait-Exit
            return
        }
        if ($choice -eq 3) {
            # Remove instance: the same action as -Action remove-instance, asked for from
            # the menu. This flow never elevated (a remote install needs no admin rights),
            # so the files it edits are the real user's.
            Invoke-ConstructRemoveInstanceAction -Name $instName -Interactive
            return
        }
        if ($choice -ge 4) {
            Write-Note "No changes made."
            Write-Host ""; Wait-Exit
            return
        }

        if ($choice -eq 0) {
            # Reprovision: straight to the provisioner over the endpoint. Nothing on the
            # host service is touched at all.
            $reprovProjects = $Projects
            if (-not $PSBoundParameters.ContainsKey('Projects')) { $reprovProjects = Select-Projects }
            Write-Ok "Projects: $reprovProjects"

            $giParams = @{ Dir = $PSScriptRoot }
            if ($PSBoundParameters.ContainsKey('GitUserName')) { $giParams['Name']  = $GitUserName }
            if ($PSBoundParameters.ContainsKey('GitEmail'))    { $giParams['Email'] = $GitEmail }
            if ($giParams.ContainsKey('Name') -and $giParams.ContainsKey('Email')) { $giParams['NoPrompt'] = $true }
            if ($FromPanel) { $giParams['NoPrompt'] = $true }
            $reprovGit = Resolve-GitIdentity @giParams

            $reprovCloneCredB64 = ""
            if (Get-Command Resolve-GitCloneCredential -ErrorAction SilentlyContinue) {
                $reprovProjDir = if (Get-Command Get-ConstructConfigProjectsDir -ErrorAction SilentlyContinue) {
                    Get-ConstructConfigProjectsDir -ScriptsDir $PSScriptRoot
                } else { Join-Path $PSScriptRoot 'projects' }
                $reprovCloneCredB64 = Resolve-GitCloneCredential -ProjectsDir $reprovProjDir -Names $reprovProjects
            }

            Show-AllSet @(
                "All set -- reprovisioning the remote VM now.",
                "",
                "This re-runs setup on '$instName' and keeps all its data.",
                "It usually only takes a few seconds; no further input needed."
            )
            $provArgs = New-ConstructRemoteProvisionArgs -Name $instName -Endpoint $endpoint `
                            -ServiceUrl $svcUrl -ConfigBranch $instBranch `
                            -Projects $reprovProjects -GitName $reprovGit.Name -GitEmail $reprovGit.Email `
                            -CloneCredB64 $reprovCloneCredB64 `
                            -PublicHost (Get-ConstructEndpointPublicHost -Endpoint $endpoint)
            if ($PSBoundParameters.ContainsKey('AgentPassword')) { $provArgs['AgentPassword'] = $AgentPassword }
            try {
                Write-Step "Reprovisioning '$instName'"
                & $provisionScript @provArgs
            } catch {
                Write-Host ""
                Write-Host "ERROR: provisioning failed." -ForegroundColor Red
                Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
            } finally {
                Write-Host ""
                Wait-Exit
            }
            return
        }

        # ── Reinstall: delete on the host, then create + provision a fresh VM ──
        $bk = Get-ConstructBackupDir -Dir $PSScriptRoot
        $doSave = $false
        if (Test-ConstructVmSshPort -SshHost ([string]$endpoint.SshHost) -SshPort ([int]$endpoint.SshPort) -TimeoutMs 5000) {
            try {
                Show-TuiScreen -Title "Checking the VM's repos for unsaved work" -Body @(
                    "Scanning $instName for uncommitted or unpushed changes the reinstall would destroy..."
                )
                Invoke-RemoteVmConfigExport -Name $instName -Endpoint $endpoint -BackupDir $bk -ScanReposOnly
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

            if ($BackupMode) { $doSave = ($BackupMode -eq 'save') }
            else {
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
                    Invoke-RemoteVmConfigExport -Name $instName -Endpoint $endpoint -BackupDir $bk
                    $restoreDir = $bk
                    $restoredProjectNames = Get-BackupProjectNames -BackupDir $bk
                    Write-Ok "Config saved; it will be restored automatically after the reinstall."
                } catch {
                    Write-Warning "Saving the config failed: $($_.Exception.Message)"
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
        }

        if (-not $doSave -and (Test-Path -LiteralPath (Join-Path $bk "extracted\backup-info.json"))) {
            $useBackup = if ($BackupMode) { ($BackupMode -ne 'wipe') } else {
                Invoke-TuiConfirm -ScreenTitle "Restore a previously saved config?" -Body @(
                    "A config backup from an earlier run exists on this host. It can restore",
                    "the agent config automatically after the reinstall."
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

        # The same last-chance typed confirmation as the local path -- and the same
        # -FromPanel exemption, because the panel's modal already asked.
        if ($FromPanel) {
            Write-Note "Delete confirmed in the control panel; proceeding with the reinstall."
        } elseif (-not (Confirm-Reinstall -VmName $instName)) {
            Write-Note "Reinstall cancelled. No changes made."
            Write-Host ""; Wait-Exit
            return
        }
        # Point of no return: ask any VS Code window attached to this VM to close, so it
        # doesn't degrade into reconnect popups while the VM is rebuilt.
        $closedWindows = Close-VmVsCodeWindow -VmHost $instName
        if ($closedWindows -gt 0) {
            Write-Note "Asked $closedWindows VS Code window(s) attached to $instName to close."
        }

        Show-TuiScreen -Title "Removing the remote VM" -Body @(
            "Asking $svcUrl to delete '$instName' and release its port forward..."
        )
        Remove-ConstructVm -Name $instName
        $script:RemoteRebuildName = $instName
    }

    # ═══ Create a VM on the host service ═════════════════════════════════════
    $instName = $InstanceName
    if ($script:RemoteRebuildName) { $instName = $script:RemoteRebuildName }
    while (-not (Test-ConstructRemoteInstanceName $instName)) {
        if ($instName -ceq 'agent-vm') {
            Write-Warning "'agent-vm' is reserved for this PC's default (local) instance and cannot name a remote VM. Pick another name, e.g. work-vm."
        } elseif ($instName) {
            Write-Warning "'$instName' is not a usable instance name: $($script:ConstructVmNameRule) (e.g. work-vm)"
        }
        if ([Console]::IsInputRedirected) {
            throw "A remote install needs a valid -InstanceName ($($script:ConstructVmNameRule) 'agent-vm' is reserved too)."
        }
        $instName = (Invoke-TuiInput -ScreenTitle "Name this VM" -Body @(
            "The name identifies the VM on the host service AND on this PC: it becomes the",
            "SSH alias you connect with, the name of its key file, and its config-sync",
            "branch. Lowercase letters, digits and hyphens, e.g. work-vm."
        ) -Prompt "Instance name").ToLowerInvariant()
    }
    if (-not $script:RemoteRebuildName -and $registry.Entries.ContainsKey($instName)) {
        throw "This PC already has a Construct instance named '$instName'. Pick another name, or pass -InstanceName $instName to manage the existing one."
    }
    # DnsSafeHost, not Host: .NET keeps an IPv6 literal's URL brackets, and the registry
    # records the bare address the service reports.
    $publicHost = ([System.Uri]$svcUrl).DnsSafeHost
    # PRE-check, before anything is created: would the registry refuse this instance for a
    # reason that is ALREADY KNOWABLE -- its name, and the identities derived from it
    # (vmName, hostAlias, keyName, configBranch)? Discovering one of those after a VM
    # exists on somebody else's host leaves a VM nothing on this PC can address.
    #
    # The ENDPOINT is deliberately NOT judged here: the service has not allocated this
    # VM's SSH forward yet, and the endpoint identity is the composite (sshHost, sshPort)
    # -- several VMs on ONE host service, each on the port the service gave it, are
    # exactly the intended flow. It is checked for real below, against the endpoint the
    # service actually returned.
    $preConflicts = @(Get-ConstructRemoteInstanceConflict -Name $instName -IgnoreEndpoint -Entry (
        New-ConstructRemoteInstanceEntry -Name $instName -SshHost $publicHost `
            -ServiceUrl $svcUrl -ServiceAuth $remoteAuthMode -Owner $remoteOwner))
    if ($preConflicts.Count -gt 0) {
        throw ("This PC's instance registry would refuse '$instName': $($preConflicts -join '; ')`n" +
               "Nothing was created on $svcUrl. Fix the registry ($($registry.Path)) or pick another name.")
    }

    # ── The usual questions, asked up front ───────────────────────────────────
    # Not the local path's prompts: the recommendation there is "a third of THIS PC's
    # RAM", and the machine that matters here is the host's -- which we cannot see, and
    # which has a per-user quota of its own. So the remote prompts recommend a sensible
    # fixed size and say where the real limit lives.
    $remoteCpu = if ($VmCpuCount -gt 0) { $VmCpuCount } else { 4 }
    $chosenMemGB  = $VmMemoryGB
    $chosenDiskGB = $VmDiskGB
    if (-not $PSBoundParameters.ContainsKey('VmMemoryGB') -or $chosenMemGB -le 0) {
        $ans = Invoke-TuiInput -ScreenTitle "VM memory" -Body @(
            "How much RAM should the VM get on the host? The host's administrator sets",
            "the limits; 8 GB is a comfortable default for an agent VM."
        ) -Prompt "Enter VM RAM in GB (press Enter for 8)" -Default "8"
        $chosenMemGB = [double]$ans
    }
    if (-not $PSBoundParameters.ContainsKey('VmDiskGB') -or $chosenDiskGB -le 0) {
        $ans = Invoke-TuiInput -ScreenTitle "VM disk size" -Body @(
            "Recommended disk size: 50 GB (grows on demand; this is the cap)"
        ) -Prompt "Enter disk size in GB (press Enter for 50)" -Default "50"
        $chosenDiskGB = [int]$ans
        if ($chosenDiskGB -lt 10) { Write-Warning "Minimum disk size is 10 GB. Using 10 GB."; $chosenDiskGB = 10 }
    }

    $chosenProjects = $Projects
    if (-not $PSBoundParameters.ContainsKey('Projects')) { $chosenProjects = Select-Projects }
    if (@($restoredProjectNames).Count -gt 0) {
        $names = @(($chosenProjects -split ',') + $restoredProjectNames |
                   ForEach-Object { $_.Trim() } | Where-Object { $_ } | Select-Object -Unique)
        $chosenProjects = $names -join ','
        Write-Ok "Including restored project profile(s): $($restoredProjectNames -join ', ')"
    }

    $chosenCloneCredB64 = ""
    if (Get-Command Resolve-GitCloneCredential -ErrorAction SilentlyContinue) {
        $freshProjDir = if (Get-Command Get-ConstructConfigProjectsDir -ErrorAction SilentlyContinue) {
            Get-ConstructConfigProjectsDir -ScriptsDir $PSScriptRoot
        } else { Join-Path $PSScriptRoot 'projects' }
        $ccParams = @{ ProjectsDir = $freshProjDir; Names = $chosenProjects }
        if ($restoreDir -and (Get-Command Test-BackupHasGitCredentials -ErrorAction SilentlyContinue) `
                        -and (Test-BackupHasGitCredentials -BackupDir $restoreDir)) {
            $ccParams['NoPrompt'] = $true
            Write-Note "Reusing the saved git credentials from the restore for cloning -- skipping the credential prompt."
        }
        $chosenCloneCredB64 = Resolve-GitCloneCredential @ccParams
    }

    $chosenAgentPassword = $AgentPassword
    if (-not $PSBoundParameters.ContainsKey('AgentPassword')) {
        if ($FromPanel) { $chosenAgentPassword = "agent" }
        else {
            $chosenAgentPassword = Invoke-TuiInput -ScreenTitle "Agent user password" -Body @(
                "Optional: login password for the 'agent' user. This is a manual-fallback",
                "credential only -- normal access is as root over the pre-seeded SSH key."
            ) -Prompt "Enter agent password (press Enter to keep default 'agent')" -Default "agent"
        }
    }

    $giParams = @{ Dir = $PSScriptRoot }
    if ($PSBoundParameters.ContainsKey('GitUserName')) { $giParams['Name']  = $GitUserName }
    if ($PSBoundParameters.ContainsKey('GitEmail'))    { $giParams['Email'] = $GitEmail }
    if ($giParams.ContainsKey('Name') -and $giParams.ContainsKey('Email')) { $giParams['NoPrompt'] = $true }
    if ($FromPanel) { $giParams['NoPrompt'] = $true }
    $gitId = Resolve-GitIdentity @giParams

    $pwLabel  = if ($chosenAgentPassword -and $chosenAgentPassword -ne "agent") { "custom" } else { "default" }
    $gitLabel = if ($gitId.Name -or $gitId.Email) { "$($gitId.Name) <$($gitId.Email)>" } else { "(unset)" }
    $script:chosenSummary = @(
        ("Host: {0}  |  Instance: {1}" -f $svcUrl, $instName),
        ("VM RAM: {0} GB  |  Disk: {1} GB  |  Projects: {2}  |  agent password: {3}" -f $chosenMemGB, $chosenDiskGB, $chosenProjects, $pwLabel),
        ("Git identity: {0}" -f $gitLabel)
    )
    Show-AllSet @(
        "All set -- sit back and relax!",
        "",
        "The host service now builds the ISO, creates the VM and waits for its",
        "unattended install; this PC then provisions it over SSH.",
        "This takes about 10 minutes total, with no further input needed."
    )

    # ── Create (on the host) + record (here), then provision ──────────────────
    # One function, so the create -> registry-check -> rollback-or-record sequence can be
    # driven end to end in test/remote-install.test.ps1 instead of only being described by
    # source-order assertions.
    $record   = New-ConstructRemoteVmRecord -Name $instName -ServiceUrl $svcUrl `
                    -ServiceAuth $remoteAuthMode -Owner $remoteOwner `
                    -RegistryPath ([string]$registry.Path) -MakeDefault:(-not $registry.Exists) `
                    -Descriptor @{
                        Name                 = $instName
                        ProcessorCount       = $remoteCpu
                        MemoryGB             = $chosenMemGB
                        DiskGB               = $chosenDiskGB
                        Nested               = $true
                        AutomaticCheckpoints = $false
                    }
    $endpoint = $record.Endpoint
    $vmToken  = [string]$record.VmToken

    # The service already waited for SSH inside its own network; this proves the port
    # FORWARD is reachable from HERE, which the host cannot test for us. Non-fatal.
    [void](Wait-ConstructVmReachable -Name $instName -TimeoutSeconds 600)

    Write-Step "Provisioning '$instName' over SSH from this PC"
    $provArgs = New-ConstructRemoteProvisionArgs -Name $instName -Endpoint $endpoint `
                    -ServiceUrl $svcUrl -ConfigBranch "vm-$instName" `
                    -Projects $chosenProjects -GitName $gitId.Name -GitEmail $gitId.Email `
                    -CloneCredB64 $chosenCloneCredB64 -VmToken $vmToken `
                    -PublicHost (Get-ConstructEndpointPublicHost -Endpoint $endpoint)
    $provArgs['AgentPassword'] = $chosenAgentPassword
    if ($restoreDir) { $provArgs['RestoreDir'] = $restoreDir }

    try {
        # Called DIRECTLY, not through Invoke-DeElevatedProvision: a remote install never
        # elevated in the first place, so there is nothing to step back down from.
        & $provisionScript @provArgs

        # ── Open it in VS Code, exactly like the local path ───────────────────
        $openLink = Get-RemoteOpenLink -VmHost $instName -WorkspaceRoot "/root/repos"
        if (Open-RemoteWorkspace -Link $openLink) {
            Show-Banner @(
                "Your Construct VM is ready on $publicHost.",
                "",
                "Opening it in VS Code (Remote-SSH) -- the control panel",
                "opens alongside."
            )
            Write-Note "If VS Code doesn't open, paste this link into a browser:  $openLink"
        } else {
            Show-Banner @(
                "Your Construct VM is ready on $publicHost.",
                "",
                "Open it in VS Code (Remote-SSH) -- the control panel opens alongside:",
                "",
                "  $openLink"
            )
            Write-Note "Tip: paste that link into a browser, or run:  start `"$openLink`""
        }
    } catch {
        Write-Host ""
        Write-Host "ERROR: install failed." -ForegroundColor Red
        Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
    } finally {
        Write-Host ""
        Wait-Exit
    }
    return
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

$HyperVmName = $VmName
# Derive the mshome DNS name and host alias ONCE from the VM name; used everywhere
# below instead of re-computing "$($HyperVmName.ToLowerInvariant()).mshome.net" each time.
# Every one of these comes from $script:VmIdentity (lib\AgentVm.Instances.ps1 via the
# adapter, resolved with the name rule above): guest hostname (ISO) = mshome DNS label,
# the mshome address, the SSH alias (the first DNS label -- the convention every shared
# lib helper derives from the host) and the saved-key name. The default VM keeps
# agent_vm_ed25519 byte-for-byte; any other VM gets an instance-scoped key so a second VM
# never overwrites the first VM's ~/.ssh key (docs/plans/modular-remote-architecture.md
# section 4.3). Identity args are passed to the provisioner ONLY for a non-default VM, so
# the default path's provisioner invocation (and thus the guest's config.env) is unchanged.
$VmGuestName = $script:VmIdentity.Name
$VmDnsName   = $script:VmIdentity.VmHost
$VmIsDefault = $script:VmIdentity.IsDefault
$VmAlias     = $script:VmIdentity.HostAlias
$VmKeyName   = $script:VmIdentity.KeyName
# WHICH INSTANCE this local run's per-VM state belongs to (B12) -- taken from the SAME
# resolved identity as every value above, so there is one answer and one line to re-point.
# The default instance resolves to the legacy top-level keys of .construct-settings.json;
# any other one to %LOCALAPPDATA%\The-Construct\instances\<name>.json. The REMOTE path
# never reaches here -- it returns inside the `if ($RemoteInstall)` block above, where the
# instance name is -InstanceName.
$VmInstanceName = $script:VmIdentity.Name
# The config-sync branch THIS run owns: an explicit -ConfigBranch wins, otherwise the
# SAME derivation Provision-AgentVM.ps1 applies ("agent-vm" -> "vm", anything else ->
# "vm-<alias>"). Every sync this script performs -- the PRE-WIPE tick below included --
# has to run on it: a tick that runs on the default 'vm' ref for a non-default VM reads
# THAT VM's store into the DEFAULT instance's branch and merges it into main
# (docs/config-sync.md, "Multiple instances").
$VmConfigBranch = if ($ConfigBranch) { $ConfigBranch } else { $script:VmIdentity.ConfigBranch }
# Version skew, checked HERE -- before the menu, the pre-wipe sync and the delete --
# rather than at the sync call with the VM already gone. A non-default branch that the
# installed library cannot name, or cannot be TOLD, must be a hard stop: falling back
# to 'vm' is precisely the cross-instance write this branch keying exists to prevent.
#
# EXPORT is exempt, and only export: it runs Provision-AgentVM.ps1 -Action export, which
# returns before the config repo is initialised or synced, and it never reaches the
# pre-wipe tick (that lives in the reinstall/redownload flow) -- so there is no ref for
# an older library to get wrong, and refusing a non-destructive config save would be
# pure loss. An UNBOUND -Action (the interactive menu) is gated: the choice is not known
# yet and the check has to happen before the delete.
$VmBranchNeeded = ($Action -ne 'export')
# The OTHER half of the pair: Provision-AgentVM.ps1 derives its own branch through
# AgentVm.Common.ps1's Get-ConstructConfigBranchName, so a scripts dir whose library
# lacks it would initialise this VM's store on 'vm' while everything here uses
# 'vm-<alias>'. A capability question, not one inferred from the value.
if ($VmBranchNeeded -and -not $VmIsDefault -and -not $ConfigBranch -and
    -not (Get-Command Get-ConstructConfigBranchName -ErrorAction SilentlyContinue)) {
    throw "This install's Construct library cannot derive a config-sync branch for the VM '$VmName' (Get-ConstructConfigBranchName is missing); update The Construct scripts, or pass -ConfigBranch explicitly, before running this action."
}
if ($VmBranchNeeded -and $VmConfigBranch -ne 'vm') {
    $vbSyncCmd = Get-Command Invoke-ConstructConfigSync -ErrorAction SilentlyContinue
    if ($vbSyncCmd -and -not $vbSyncCmd.Parameters.ContainsKey('VmBranch')) {
        throw "This install's Construct library does not support the config-sync branch '$VmConfigBranch' (Invoke-ConstructConfigSync has no -VmBranch), so the config sync would run on the default 'vm' branch instead; update The Construct scripts before running this action for the VM '$VmName'."
    }
}
# Both halves are the driver's: Test-ConstructDriverPrereqs is the cheap "this host
# can't drive the backend at all" short-circuit (locally: the Hyper-V cmdlets are
# absent), and Test-ConstructVmPresent is three-valued, so `-eq $true` behaves
# exactly like the previous Get-VM -ErrorAction SilentlyContinue: a VM in any state
# opens the menu, an unreadable backend falls through.
if (-not $SkipCreateVm -and (Test-ConstructDriverPrereqs) -and
    ((Test-ConstructVmPresent -Name $HyperVmName) -eq $true)) {

    $existingVmHandled = $true
    # The VM exists on THIS PC: record it before any action runs, so a VM created before
    # B11 (or by a script that predates the registry) is listed by the control panel from
    # its next reprovision/export onward. Writes nothing for a default-only install.
    $recordedInstancePath = Register-ConstructLocalVmInstance -Name $VmGuestName -ConfigBranch $ConfigBranch
    if ($recordedInstancePath) { Write-Note "Instance '$VmGuestName' recorded in $recordedInstancePath" }
    Show-TuiScreen -Title "The agent VM '$HyperVmName' is already installed on this host."

    if ($PSBoundParameters.ContainsKey('Action')) {
        # The control panel or install.ps1 one-liner pre-selects the action; skip the
        # interactive menu.
        $choice = switch ($Action) {
            'reprovision' { 0 }
            'reinstall'   { 1 }
            'redownload'  { 2 }
            'export'      { 3 }
            'add-config'  { 4 }
        }
        Write-Note "Action selected by the control panel: $Action"
    } else {
        $choice = Show-Menu -Title "What would you like to do?" -Options @(
            "Reprovision      re-run provisioning on the existing VM (keeps all data)",
            "Reinstall        DELETE the VM and its disk, then build + install fresh (reuse downloaded ISOs)",
            "Redownload       DELETE the VM, re-download the latest Ubuntu ISO, rebuild + install fresh",
            "Export config    save the VM's current agent config + auth to this host (no changes to the VM)",
            "Add config       import project configs from a remote repo or local directory",
            "Remove instance  forget this VM on this PC (the Hyper-V VM itself is kept)",
            "Quit             make no changes and exit"
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
            $reprovProjDir = if (Get-Command Get-ConstructConfigProjectsDir -ErrorAction SilentlyContinue) {
                Get-ConstructConfigProjectsDir -ScriptsDir $PSScriptRoot
            } else { Join-Path $PSScriptRoot 'projects' }
            $reprovCloneCredB64 = Resolve-GitCloneCredential -ProjectsDir $reprovProjDir -Names $reprovProjects
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
        Write-Step "Reprovisioning the existing VM"
        # Reprovision keeps the existing password; only honour an explicit
        # -AgentPassword passed on the command line (this path has no prompt).
        # -Auto: the finally below owns the pause, so the provisioner stays quiet.
        $reprovArgs = @{ Projects = $reprovProjects; Auto = $true }
        if (-not $VmIsDefault) { $reprovArgs['VmHost'] = $VmDnsName; $reprovArgs['HostAlias'] = $VmAlias; $reprovArgs['LocalKeyName'] = $VmKeyName }
        # Pass both git values (even if empty) so the provisioner doesn't re-prompt.
        $reprovArgs['GitUserName'] = $reprovGit.Name
        $reprovArgs['GitEmail']    = $reprovGit.Email
        $reprovArgs['ClaudePartialStreaming'] = $ClaudePartialStreaming
        $reprovArgs['MicPassthrough'] = $MicPassthrough
        $reprovArgs['OpenCodeBackgroundWatcher'] = $OpenCodeBackgroundWatcher
        $reprovArgs['T3Code'] = $T3Code
        $reprovArgs['T3CodeChannel'] = $T3CodeChannel
        $reprovArgs['T3CodeLimitResume'] = $T3CodeLimitResume
        # Only when explicitly chosen: empty leaves the provisioner's own alias
        # derivation in charge, so the default path splats exactly what it always did.
        if ($ConfigBranch) { $reprovArgs['ConfigBranch'] = $ConfigBranch }
        if ($PSBoundParameters.ContainsKey('AgentPassword')) { $reprovArgs['AgentPassword'] = $AgentPassword }
        if ($reprovCloneCredB64) { $reprovArgs['GitCloneCredentialsB64'] = $reprovCloneCredB64 }
        if ($PSBoundParameters.ContainsKey('AutoResolve')) { $reprovArgs['AutoResolve'] = $AutoResolve }
        try {
            $reprovCmd = Get-Command -Name $provisionScript -CommandType ExternalScript -ErrorAction Stop
            if (-not $reprovCmd.Parameters.ContainsKey('T3CodeChannel')) {
                $reprovArgs.Remove('T3CodeChannel')
            }
            if (-not $reprovCmd.Parameters.ContainsKey('T3CodeLimitResume')) {
                $reprovArgs.Remove('T3CodeLimitResume')
            }
            if (-not $reprovCmd.Parameters.ContainsKey('OpenCodeBackgroundWatcher')) {
                $reprovArgs.Remove('OpenCodeBackgroundWatcher')
            }
        } catch {
            $reprovArgs.Remove('T3CodeChannel')
            $reprovArgs.Remove('T3CodeLimitResume')
            $reprovArgs.Remove('OpenCodeBackgroundWatcher')
        }
        try {
            Invoke-DeElevatedProvision -ScriptPath $provisionScript -ProvisionParams $reprovArgs
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
                    # Config-sync v2 (spec section 9 step 1): run a sync tick BEFORE
                    # the tarball export so the host config repo captures the latest
                    # VM-side profile edits. The existing export still runs afterwards
                    # to capture non-profile data (auth, memory, skills). On Conflict
                    # or Blocked, stop the reinstall so the user can resolve first.
                    if ((Get-Command Test-ConstructGitAvailable -ErrorAction SilentlyContinue) -and
                        (Test-ConstructGitAvailable) -and
                        (Get-Command Invoke-ConstructConfigSync -ErrorAction SilentlyContinue)) {
                        $syncConfigDir = Get-ConstructConfigDir
                        $syncVmHost = $VmDnsName
                        $syncArgs = @{ ConfigDir = $syncConfigDir; VmHost = $syncVmHost }
                        # THIS VM's branch, never the default one: without it the tick
                        # would read a non-default VM's store into refs/heads/vm and
                        # merge it into main. Passed only when non-default, so the
                        # default instance's call stays argument-identical; the
                        # capability was already gated (fail-closed) above, before
                        # anything destructive could run.
                        if ($VmConfigBranch -ne 'vm') { $syncArgs['VmBranch'] = $VmConfigBranch }
                        if ($PSBoundParameters.ContainsKey('AutoResolve')) { $syncArgs['AutoResolve'] = $AutoResolve }
                        $syncResult = Invoke-ConstructConfigSync @syncArgs
                        if ($syncResult.Conflict -or $syncResult.Blocked) {
                            Write-Host ""
                            Write-Host "Config sync detected a conflict that must be resolved before reinstalling." -ForegroundColor Red
                            if ($syncResult.Reason) { Write-Host "    $($syncResult.Reason)" -ForegroundColor Red }
                            Write-Host "    Resolve the conflict in the config repo ($syncConfigDir), commit, and re-run." -ForegroundColor Yellow
                            Write-Host ""; Wait-Exit
                            return
                        }
                        # A degraded pre-wipe tick (VM unreachable, lock busy, skipped
                        # profiles) means VM-side edits may NOT have reached the host
                        # repo -- after the wipe, the tar backup is their only copy
                        # (the Provision restore backstop folds it back in). Say so
                        # NOW, while the user can still abort.
                        foreach ($w in @($syncResult.Warnings)) {
                            if ($w) { Write-Warning "Pre-reinstall config sync: $w" }
                        }
                    }

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
        # Point of no return: every abort path (unsaved-work scan, sync conflict,
        # failed-save cancel, typed confirmation) is behind us and the VM is about
        # to be deleted. Any VS Code window still attached to it over Remote-SSH
        # would only degrade into reconnect-error popups during the rebuild, so ask
        # those windows -- and only those -- to close now (graceful WM_CLOSE; the
        # install chain reopens VS Code onto the fresh VM at the end).
        $closedWindows = Close-VmVsCodeWindow -VmHost $VmDnsName
        if ($closedWindows -gt 0) {
            # WM_CLOSE is queued, not confirmed -- a window with a modal dialog up
            # may legitimately stay open, so say "asked", not "closed".
            Write-Note "Asked $closedWindows VS Code window(s) attached to $HyperVmName to close."
        }

        Show-TuiScreen -Title "Removing the existing VM" -Body @(
            "Powering off '$HyperVmName' and deleting its virtual disk..."
        )
        Remove-ConstructVm -Name $HyperVmName
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
    elseif ($choice -eq 4) {
        # ── Add project config (config-sync v2 / spec section 11 / D14) ────────
        # Import profiles from -ConfigRepo (git clone) or -ConfigDir (local dir),
        # then route: VM exists -> additive reprovision; no VM -> full install.
        # When triggered from the interactive menu (no -Action), -ConfigRepo and
        # -ConfigDir are not bound yet -- prompt interactively.
        try {
            # (1) Resolve the config store/repo.
            if (Get-Command Initialize-ConstructConfigStore -ErrorAction SilentlyContinue) {
                $addConfigDir = Initialize-ConstructConfigStore -ScriptsDir $PSScriptRoot
            } else {
                $addConfigDir = Get-ConstructConfigDir
            }
            if ((Get-Command Test-ConstructGitAvailable -ErrorAction SilentlyContinue) -and
                (Test-ConstructGitAvailable) -and
                (Get-Command Initialize-ConstructConfigRepo -ErrorAction SilentlyContinue)) {
                Initialize-ConstructConfigRepo -ConfigDir $addConfigDir | Out-Null
            }

            # (2) Import profiles.
            $importArgs = @{ ConfigDir = $addConfigDir }
            if ($PSBoundParameters.ContainsKey('ConfigRepo')) {
                $importArgs['SourceRepo'] = $ConfigRepo
            } elseif ($PSBoundParameters.ContainsKey('ConfigDir')) {
                $importArgs['SourceDir'] = $ConfigDir
            } else {
                # Interactive menu path: prompt for a source.
                $addSrc = Invoke-TuiInput -ScreenTitle "Add project config" -Body @(
                    "Enter a git repo URL to import from, or a local directory path."
                ) -Prompt "Config source (URL or path)"
                if ([string]::IsNullOrWhiteSpace($addSrc)) {
                    Write-Note "No source given. No changes made."
                    Write-Host ""; Wait-Exit
                    return
                }
                if (Test-Path -LiteralPath $addSrc) {
                    $importArgs['SourceDir'] = $addSrc
                } else {
                    $importArgs['SourceRepo'] = $addSrc
                }
            }
            # Honour -ImportConfigs: comma-separated names to cherry-pick.
            if ($PSBoundParameters.ContainsKey('ImportConfigs') -and $ImportConfigs) {
                $importArgs['Names'] = @($ImportConfigs -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            }
            if (-not (Get-Command Import-ConstructConfigs -ErrorAction SilentlyContinue)) {
                throw "Import-ConstructConfigs is not available. Update your Construct installation."
            }
            $importResult = Import-ConstructConfigs @importArgs
            $importedNames = @()
            if ($importResult -and $importResult.Imported) { $importedNames = @($importResult.Imported) }
            if ($importedNames.Count -eq 0) {
                Write-Note "No profiles were imported. No changes made."
                Write-Host ""; Wait-Exit
                return
            }
            Write-Ok "Imported project profile(s): $($importedNames -join ', ')"

            # (3) PROJECTS union per D14: VM reachable -> current PROJECTS from the
            # VM union imported names; else imported names union -Projects param.
            $addProjects = @()
            $vmReachable = Test-VmReachable -VmName $HyperVmName
            if ($vmReachable -and (Get-Command Get-ConstructVmProjects -ErrorAction SilentlyContinue)) {
                $vmProjects = Get-ConstructVmProjects -VmHost $VmDnsName
                if ($vmProjects) { $addProjects += @($vmProjects) }
            }
            $addProjects += $importedNames
            if ($PSBoundParameters.ContainsKey('Projects') -and $Projects) {
                $addProjects += @($Projects -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            }
            # Deduplicate preserving order: current VM projects first, then new imports.
            $seen = @{}
            $addProjectsUniq = @($addProjects | Where-Object {
                $k = $_.ToLower()
                if ($seen.ContainsKey($k)) { $false } else { $seen[$k] = $true; $true }
            })
            $addProjectsStr = $addProjectsUniq -join ','
            Write-Ok "Projects for provision: $addProjectsStr"

            # (4) Route: VM exists -> reprovision with the unioned selection
            # (additive: checkout-projects skips existing clones already).
            $provisionScript = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
            if (-not (Test-Path -LiteralPath $provisionScript)) { throw "Provision-AgentVM.ps1 not found in $PSScriptRoot." }
            # Git identity (resolved silently -- add-config doesn't change it).
            $acGiParams = @{ Dir = $PSScriptRoot; NoPrompt = $true }
            if ($PSBoundParameters.ContainsKey('GitUserName')) { $acGiParams['Name']  = $GitUserName }
            if ($PSBoundParameters.ContainsKey('GitEmail'))    { $acGiParams['Email'] = $GitEmail }
            $acGitId = Resolve-GitIdentity @acGiParams

            # Clone credentials for any new repos.
            $acCloneCredB64 = ""
            if (Get-Command Resolve-GitCloneCredential -ErrorAction SilentlyContinue) {
                $acProjDir = if (Get-Command Get-ConstructConfigProjectsDir -ErrorAction SilentlyContinue) {
                    Get-ConstructConfigProjectsDir -ScriptsDir $PSScriptRoot
                } else { Join-Path $PSScriptRoot 'projects' }
                $acCloneCredB64 = Resolve-GitCloneCredential -ProjectsDir $acProjDir -Names $addProjectsStr
            }

            Write-Step "Reprovisioning the VM with the new config"
            $acReprovArgs = @{
                Projects  = $addProjectsStr
                Auto      = $true
                GitUserName = $acGitId.Name
                GitEmail    = $acGitId.Email
                ClaudePartialStreaming = $ClaudePartialStreaming
                MicPassthrough        = $MicPassthrough
                OpenCodeBackgroundWatcher = $OpenCodeBackgroundWatcher
                T3Code                = $T3Code
                T3CodeChannel         = $T3CodeChannel
                T3CodeLimitResume     = $T3CodeLimitResume
            }
            if (-not $VmIsDefault) { $acReprovArgs['VmHost'] = $VmDnsName; $acReprovArgs['HostAlias'] = $VmAlias; $acReprovArgs['LocalKeyName'] = $VmKeyName }
            # Explicit config-sync branch only (empty = the provisioner derives it).
            if ($ConfigBranch) { $acReprovArgs['ConfigBranch'] = $ConfigBranch }
            if ($acCloneCredB64) { $acReprovArgs['GitCloneCredentialsB64'] = $acCloneCredB64 }
            if ($PSBoundParameters.ContainsKey('AutoResolve')) { $acReprovArgs['AutoResolve'] = $AutoResolve }
            try {
                $acProvCmd = Get-Command -Name $provisionScript -CommandType ExternalScript -ErrorAction Stop
                if (-not $acProvCmd.Parameters.ContainsKey('T3CodeChannel')) {
                    $acReprovArgs.Remove('T3CodeChannel')
                }
                if (-not $acProvCmd.Parameters.ContainsKey('T3CodeLimitResume')) {
                    $acReprovArgs.Remove('T3CodeLimitResume')
                }
                if (-not $acProvCmd.Parameters.ContainsKey('OpenCodeBackgroundWatcher')) {
                    $acReprovArgs.Remove('OpenCodeBackgroundWatcher')
                }
            } catch {
                $acReprovArgs.Remove('T3CodeChannel')
                $acReprovArgs.Remove('T3CodeLimitResume')
                $acReprovArgs.Remove('OpenCodeBackgroundWatcher')
            }
            Invoke-DeElevatedProvision -ScriptPath $provisionScript -ProvisionParams $acReprovArgs
        } catch {
            Write-Host ""
            Write-Host "ERROR: add-config failed." -ForegroundColor Red
            Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
        } finally {
            Write-Host ""
            Wait-Exit
        }
        return
    }
    elseif ($choice -eq 5) {
        # Remove instance: forget this VM on this PC. The Hyper-V VM is NOT deleted --
        # Reinstall is the action that does that -- so this is safe to offer here even
        # though the local flow runs elevated. It edits per-user files, so it says which
        # account's profile it is working in; on a PC where UAC switches to a different
        # administrator, run it unelevated instead (Auto-Install.ps1 -Action remove-instance).
        Write-Note "Working in the profile of $env:USERNAME ($HOME)."
        Invoke-ConstructRemoveInstanceAction -Name $VmGuestName -Interactive
        return
    }
    elseif ($choice -eq 6) {
        Write-Note "No changes made."
        Write-Host ""; Wait-Exit
        return
    }
}

# ── add-config without an existing VM: full fresh-install path ─────────────
# When -Action add-config is given but no VM exists (Hyper-V not present or no
# VM named Agent-VM), import first, then fall through to the normal fresh-install
# flow with the imported profiles as -Projects.
if ($PSBoundParameters.ContainsKey('Action') -and $Action -eq 'add-config' -and -not $existingVmHandled) {
    try {
        if (Get-Command Initialize-ConstructConfigStore -ErrorAction SilentlyContinue) {
            $addConfigDir = Initialize-ConstructConfigStore -ScriptsDir $PSScriptRoot
        } else {
            $addConfigDir = Get-ConstructConfigDir
        }
        if ((Get-Command Test-ConstructGitAvailable -ErrorAction SilentlyContinue) -and
            (Test-ConstructGitAvailable) -and
            (Get-Command Initialize-ConstructConfigRepo -ErrorAction SilentlyContinue)) {
            Initialize-ConstructConfigRepo -ConfigDir $addConfigDir | Out-Null
        }

        $importArgs = @{ ConfigDir = $addConfigDir }
        if ($PSBoundParameters.ContainsKey('ConfigRepo')) {
            $importArgs['SourceRepo'] = $ConfigRepo
        } elseif ($PSBoundParameters.ContainsKey('ConfigDir')) {
            $importArgs['SourceDir'] = $ConfigDir
        } else {
            throw "-Action add-config requires -ConfigRepo or -ConfigDir."
        }
        if ($PSBoundParameters.ContainsKey('ImportConfigs') -and $ImportConfigs) {
            $importArgs['Names'] = @($ImportConfigs -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
        if (-not (Get-Command Import-ConstructConfigs -ErrorAction SilentlyContinue)) {
            throw "Import-ConstructConfigs is not available. Update your Construct installation."
        }
        $importResult = Import-ConstructConfigs @importArgs
        $importedNames = @()
        if ($importResult -and $importResult.Imported) { $importedNames = @($importResult.Imported) }
        if ($importedNames.Count -gt 0) {
            Write-Ok "Imported project profile(s): $($importedNames -join ', ')"
            # Union imported names with -Projects if supplied, then fall through to
            # the fresh-install path which reads $chosenProjects.
            $existingProjects = @()
            if ($PSBoundParameters.ContainsKey('Projects') -and $Projects) {
                $existingProjects = @($Projects -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            }
            $union = @($existingProjects + $importedNames)
            $seen = @{}
            $union = @($union | Where-Object {
                $k = $_.ToLower()
                if ($seen.ContainsKey($k)) { $false } else { $seen[$k] = $true; $true }
            })
            $Projects = $union -join ','
            # Update $PSBoundParameters so the downstream check at
            # $PSBoundParameters.ContainsKey('Projects') passes -- otherwise
            # Select-Projects is called and overwrites $chosenProjects, losing
            # the imported names. Modifying the $Projects variable alone does
            # NOT update $PSBoundParameters.
            $PSBoundParameters['Projects'] = $Projects
        } else {
            Write-Note "No profiles were imported."
        }
    } catch {
        Write-Host ""
        Write-Host "ERROR: add-config import failed." -ForegroundColor Red
        Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
        Write-Host ""; Wait-Exit
        exit 1
    }
    # Fall through to the fresh-install flow below with the imported $Projects.
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
# Automatic checkpoints: an EXPLICIT -AutomaticCheckpoints wins; otherwise fall back to
# the control panel's saved preference (.construct-settings.json) so a hand-run install
# honours the toggle instead of silently reverting to the parameter default. This also
# covers an OLDER control-panel extension driving a NEWER Auto-Install: it passes no
# argument at all, and without this the saved "on" would be lost.
$effectiveAutoCheckpoints = $AutomaticCheckpoints
if (-not $PSBoundParameters.ContainsKey('AutomaticCheckpoints')) {
    try {
        # THIS VM's saved preference ($VmInstanceName is the one place that answers "which
        # instance"), so the default VM reads the legacy top-level key and any other VM
        # reads its own state file.
        $savedSettings = if (Get-Command Read-ConstructInstanceState -ErrorAction SilentlyContinue) {
            Read-ConstructInstanceState -Name $VmInstanceName -Dir $PSScriptRoot
        } else {
            Read-ConstructSettings -Dir $PSScriptRoot
        }
        if ($savedSettings -and $null -ne $savedSettings.vmAutoCheckpoints) {
            # NOT [bool]: every non-empty PowerShell string is truthy, so a hand-edited
            # settings file holding the STRING "false" would coerce to $true and silently
            # enable checkpoints. Compare the rendered value instead -- $true renders
            # "True", $false renders "False", and both JSON strings compare as written.
            $savedText = "$($savedSettings.vmAutoCheckpoints)".Trim().ToLowerInvariant()
            $effectiveAutoCheckpoints = if ($savedText -in @("true", "1")) { "true" } else { "false" }
            Write-Note "Automatic checkpoints: $effectiveAutoCheckpoints (from the saved control-panel setting)"
        }
    } catch { }
}

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
    #
    # But when a restore is in play AND its backup already carries stored git
    # credentials, those get reused for the clone (Provision-AgentVM.ps1 falls back
    # to them), so the prompt is redundant -- skip it. It's the stray prompt that
    # otherwise interrupts the unattended control-panel reinstall. With no stored
    # credentials available (e.g. a clean-wipe reinstall), still prompt so private
    # repos can be cloned during provisioning.
    if (Get-Command Resolve-GitCloneCredential -ErrorAction SilentlyContinue) {
        $freshProjDir = if (Get-Command Get-ConstructConfigProjectsDir -ErrorAction SilentlyContinue) {
            Get-ConstructConfigProjectsDir -ScriptsDir $PSScriptRoot
        } else { Join-Path $PSScriptRoot 'projects' }
        $ccParams = @{ ProjectsDir = $freshProjDir; Names = $chosenProjects }
        if ($restoreDir -and (Get-Command Test-BackupHasGitCredentials -ErrorAction SilentlyContinue) `
                        -and (Test-BackupHasGitCredentials -BackupDir $restoreDir)) {
            $ccParams['NoPrompt'] = $true
            Write-Note "Reusing the saved git credentials from the restore for cloning -- skipping the credential prompt."
        }
        $chosenCloneCredB64 = Resolve-GitCloneCredential @ccParams
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
    Ensure-ConstructDriverPrereqs
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
        "VM_USER=$VmUser" "VM_PASS=$VmPass" "VM_HOST=$VmGuestName" "SOURCE_ID=$SourceId" `
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
# Create-AgentVM.ps1 creates the VM and waits for SSH; with -Auto it returns
# without calling Provision (Auto-Install owns the provisioning call below,
# via Invoke-DeElevatedProvision -- currently inline, see the kill switch).
# The provision-related params ride along in $createArgs for standalone
# Create-AgentVM runs (no -Auto) where it makes its own provision call.
$createArgs = @{
    MemoryGB      = $chosenMemGB
    DiskSizeGB    = $chosenDiskGB
    Projects      = $chosenProjects
    AgentPassword = $chosenAgentPassword
    GitUserName   = $chosenGitName
    GitEmail      = $chosenGitEmail
    ClaudePartialStreaming = $ClaudePartialStreaming
    MicPassthrough = $MicPassthrough
    OpenCodeBackgroundWatcher = $OpenCodeBackgroundWatcher
    T3Code        = $T3Code
    T3CodeChannel = $T3CodeChannel
    AutomaticCheckpoints = $effectiveAutoCheckpoints
    # -Auto: Create-AgentVM skips its own Provision call and this script's
    # try/finally owns the final pause.
    Auto          = $true
}
# Version-skew guard: a partially-updated scripts dir can pair THIS script with an
# older Create-AgentVM.ps1 that has no -AutomaticCheckpoints parameter. Splatting it
# there is a parameter-binding failure -- and by this point the old VM is already
# DELETED, so the rebuild would simply break. Drop the argument instead (the old
# script's own default stands) and say so loudly, rather than fail the rebuild.
try {
    $createCmd = Get-Command -Name $createScript -CommandType ExternalScript -ErrorAction Stop
    if (-not $createCmd.Parameters.ContainsKey('AutomaticCheckpoints')) {
        $createArgs.Remove('AutomaticCheckpoints')
        Write-Warning "Create-AgentVM.ps1 in this folder is older than Auto-Install.ps1 and doesn't support -AutomaticCheckpoints."
        Write-Host "    The VM will be created with Hyper-V's automatic-checkpoint default (ON). Update Construct, then" -ForegroundColor Yellow
        Write-Host "    turn them off from the control panel (Settings -> VM resources) or run Set-AgentVmCheckpoints.ps1." -ForegroundColor Yellow
    }
    if (-not $createCmd.Parameters.ContainsKey('T3CodeChannel')) {
        $createArgs.Remove('T3CodeChannel')
    }
    if (-not $createCmd.Parameters.ContainsKey('OpenCodeBackgroundWatcher')) {
        $createArgs.Remove('OpenCodeBackgroundWatcher')
    }
    # Forward the VM name only when the target script understands it (skew guard).
    if ($createCmd.Parameters.ContainsKey('VmName')) {
        $createArgs['VmName'] = $HyperVmName
    }
    if (-not $VmIsDefault -and $createCmd.Parameters.ContainsKey('LocalKeyName')) {
        $createArgs['LocalKeyName'] = $VmKeyName
    }
    # An explicit config-sync branch rides down to Provision through Create-AgentVM.
    # The pre-destructive guard above already refused an install whose Create-AgentVM
    # can't carry it, so this only ever adds a parameter the target declares.
    if ($ConfigBranch -and $createCmd.Parameters.ContainsKey('ConfigBranch')) {
        $createArgs['ConfigBranch'] = $ConfigBranch
    }
    # Hand Create-AgentVM the ISO built/reused for THIS VM instead of letting it pick
    # "the newest *autoinstall*.iso", which may belong to another instance.
    if ($createCmd.Parameters.ContainsKey('AutoinstallIso') -and $OutputIso -and (Test-Path -LiteralPath $OutputIso)) {
        $createArgs['AutoinstallIso'] = $OutputIso
    }
} catch {
    # Fail SAFE, not open. We are already past Remove-AgentVm here, so passing an argument
    # the target might reject risks a binding failure with the old VM gone -- a broken
    # rebuild. Dropping it only costs Hyper-V's default (checkpoints on), which the
    # control panel can fix afterwards.
    $createArgs.Remove('AutomaticCheckpoints')
    $createArgs.Remove('T3CodeChannel')
    $createArgs.Remove('OpenCodeBackgroundWatcher')
    Write-Warning "Could not check Create-AgentVM.ps1's parameters ($($_.Exception.Message))."
    Write-Host "    Creating the VM without -AutomaticCheckpoints; set it afterwards from the control panel" -ForegroundColor Yellow
    Write-Host "    (Settings -> VM resources) or with Set-AgentVmCheckpoints.ps1." -ForegroundColor Yellow
}
# A custom VM name must reach Create-AgentVM.ps1; continuing without it would build
# "Agent-VM" and provision an address that never exists (the catch above swallows).
if (-not $VmIsDefault -and -not $createArgs.ContainsKey('VmName')) {
    throw "Create-AgentVM.ps1 does not accept -VmName; cannot create a VM named '$HyperVmName'. Update The Construct."
}
if ($restoreDir)         { $createArgs['RestoreDir']             = $restoreDir }
if ($chosenCloneCredB64) { $createArgs['GitCloneCredentialsB64'] = $chosenCloneCredB64 }
if ($PSBoundParameters.ContainsKey('Repo') -or $PSBoundParameters.ContainsKey('Ref')) {
    $createArgs['Repo'] = $Repo; $createArgs['Ref'] = $Ref
}
try {
    & $createScript @createArgs

    # ── Elevated host-side finalization (needs admin) ────────────────────────
    # Add the user to Hyper-V Administrators so the non-elevated control-panel
    # extension can read VM power state without a UAC prompt. (Driver contract:
    # the host-access half of Ensure-ConstructDriverPrereqs.)
    Ensure-ConstructDriverPrereqs -Scope HostAccess

    # ── Provisioning ─────────────────────────────────────────────────────────
    # Goes through Invoke-DeElevatedProvision, whose de-elevation is currently
    # DISABLED (kill switch in AgentVm.Common.ps1) — it runs Provision inline in
    # this console. Auto-Install builds provArgs directly (Create-AgentVM no
    # longer chains into Provision when -Auto).
    $provisionScript = Join-Path $PSScriptRoot "Provision-AgentVM.ps1"
    if (-not (Test-Path -LiteralPath $provisionScript)) {
        throw "Provision-AgentVM.ps1 not found in $PSScriptRoot."
    }
    $provArgs = @{
        Projects  = $chosenProjects
        AgentPassword = $chosenAgentPassword
        GitUserName   = $chosenGitName
        GitEmail      = $chosenGitEmail
        ClaudePartialStreaming = $ClaudePartialStreaming
        MicPassthrough        = $MicPassthrough
        OpenCodeBackgroundWatcher = $OpenCodeBackgroundWatcher
        T3Code                = $T3Code
        T3CodeChannel         = $T3CodeChannel
        Auto      = $true
    }
    if (-not $VmIsDefault) { $provArgs['VmHost'] = $VmDnsName; $provArgs['HostAlias'] = $VmAlias; $provArgs['LocalKeyName'] = $VmKeyName }
    # Explicit config-sync branch only; empty means "derive from -HostAlias" (today's
    # behaviour, and the guard above already refused an install that can't honour it).
    if ($ConfigBranch) { $provArgs['ConfigBranch'] = $ConfigBranch }
    if ($restoreDir)         { $provArgs['RestoreDir']             = $restoreDir }
    if ($chosenCloneCredB64) { $provArgs['GitCloneCredentialsB64'] = $chosenCloneCredB64 }
    if ($PSBoundParameters.ContainsKey('Repo') -or $PSBoundParameters.ContainsKey('Ref')) {
        $provArgs['Repo'] = $Repo; $provArgs['Ref'] = $Ref
    }
    if ($PSBoundParameters.ContainsKey('AutoResolve')) { $provArgs['AutoResolve'] = $AutoResolve }
    # Same version-skew guard as above: an older Provision-AgentVM.ps1 may lack
    # -T3CodeChannel; splatting it would fail parameter binding.
    try {
        $provCmd = Get-Command -Name $provisionScript -CommandType ExternalScript -ErrorAction Stop
        if (-not $provCmd.Parameters.ContainsKey('T3CodeChannel')) {
            $provArgs.Remove('T3CodeChannel')
        }
        if (-not $provCmd.Parameters.ContainsKey('OpenCodeBackgroundWatcher')) {
            $provArgs.Remove('OpenCodeBackgroundWatcher')
        }
    } catch {
        $provArgs.Remove('T3CodeChannel')
        $provArgs.Remove('OpenCodeBackgroundWatcher')
    }
    Invoke-DeElevatedProvision -ScriptPath $provisionScript -ProvisionParams $provArgs

    # ── Post-provision host setup ────────────────────────────────────────────
    # The install path reboots the VM at the very end of provisioning, but the
    # reboot is backgrounded on the VM -- the OLD boot's sshd keeps answering
    # for several seconds, so "the port is up" is NOT proof the restart
    # happened, and opening VS Code on it lands in a connection error. Require
    # actual restart proof: Provision exports whether it issued a reboot and
    # the pre-reboot boot id; Wait-VmSshReady probes over SSH until the boot id
    # CHANGES (or, without a baseline, uptime shows a fresh boot). No reboot
    # issued -> the VM never went down, open immediately. On timeout open
    # anyway -- Remote-SSH retries on its own, and the deep link is printed too.
    if (Get-Command Wait-VmSshReady -ErrorAction SilentlyContinue) {
        if ($global:ConstructVmRebootIssued -eq $false) {
            Write-Ok "No final reboot was needed -- the VM is already up"
        } else {
            Write-Step "Waiting for the VM to finish its final reboot"
            # $VmDnsName (FQDN), not the short $VmHost: the short name can
            # stop resolving after the reboot while the switch DNS still
            # serves the FQDN -- and ssh resolves the alias to the FQDN too.
            $sshWaitArgs = @{
                VmHost         = $VmDnsName
                SshTarget      = $VmAlias
                BaselineBootId = "$global:ConstructVmPreRebootBootId"
            }
            if (Wait-VmSshReady @sshWaitArgs) {
                Write-Ok "VM restarted and is back on SSH"
            } else {
                Write-Warning "The VM didn't confirm its restart within 5 minutes; opening VS Code anyway (it retries the connection itself)."
            }
        }
    }
    $openLink = Get-RemoteOpenLink -VmHost $VmDnsName -WorkspaceRoot "/root/repos"
    if (Open-RemoteWorkspace -Link $openLink) {
        Show-Banner @(
            "Your Construct VM is ready.",
            "",
            "Opening it in VS Code (Remote-SSH) -- the control panel",
            "opens alongside."
        )
        Write-Note "If VS Code doesn't open, paste this link into a browser:  $openLink"
    } else {
        Show-Banner @(
            "Your Construct VM is ready.",
            "",
            "Open it in VS Code (Remote-SSH) -- the control panel opens alongside:",
            "",
            "  $openLink"
        )
        Write-Note "Tip: paste that link into a browser, or run:  start `"$openLink`""
    }
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
