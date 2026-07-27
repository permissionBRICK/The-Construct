#Requires -Version 5.1
<#
.SYNOPSIS
    Turn Hyper-V automatic checkpoints on or off for the Construct agent VM, and
    (when turning them off) remove the automatic checkpoint Hyper-V already took.

.DESCRIPTION
    Automatic checkpoints are Client Hyper-V's default: every VM start snapshots the
    machine. For a disposable agent VM that only costs -- the .avhdx differencing
    disk grows with every write, disk I/O slows down, and the checkpoint has to be
    merged whenever it is deleted. Construct therefore CREATES VMs with them off
    (Create-AgentVM.ps1 -AutomaticCheckpoints) and lets the control panel flip the
    setting on an already-running VM through this script.

    Changing the policy does NOT remove a checkpoint Hyper-V has already taken, so
    when disabling, this script also deletes the existing AUTOMATIC checkpoints. It
    never deletes a checkpoint you made yourself WITHOUT ASKING: checkpoints are
    classified by Hyper-V's own IsAutomaticSnapshot flag (via WMI / the snapshot object),
    and only those are removed unattended. On a host that doesn't report the flag, a
    checkpoint that merely LOOKS automatic by name is shown separately and removed only
    if you type "yes" for that specific one -- so an approved deletion is always yours.

    Requires elevation (Hyper-V cmdlets); self-elevates when run directly.

.PARAMETER Enabled
    "true" to let Hyper-V take an automatic checkpoint at every VM start, "false"
    (the default, and Construct's default) to turn them off.

.PARAMETER VmName
    The Hyper-V VM to change. Defaults to the Construct VM, "Agent-VM".

.PARAMETER RemoveExisting
    "true" (default) to also delete existing automatic checkpoints when disabling;
    "false" to change the policy only and leave any existing checkpoint in place.

.PARAMETER FromPanel
    Set by the control panel: skip the end-of-run "Press Enter" pause on SUCCESS
    (the panel is the feedback). Failures always pause so the error stays readable.

.EXAMPLE
    .\Set-AgentVmCheckpoints.ps1 -Enabled false
    Disable automatic checkpoints and merge away the one Hyper-V already took.
#>
[CmdletBinding()]
param(
    [ValidateSet("true", "false")]
    [string]$Enabled = "false",
    [string]$VmName = "Agent-VM",
    [ValidateSet("true", "false")]
    [string]$RemoveExisting = "true",
    [switch]$FromPanel
)

# ── Self-elevate to Administrator ────────────────────────────────────────────
# Hyper-V's Set-VM / Remove-VMSnapshot need admin. Launched from the control panel
# this already runs elevated (the panel uses Start-Process -Verb RunAs), so this
# branch only fires for a direct run.
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Relaunching as Administrator..." -ForegroundColor Yellow
    # Canonical Windows argument quoting (CommandLineToArgvW rules): quote when needed,
    # double the run of backslashes before a quote or the closing quote, escape an
    # embedded quote as \". A naive "`"$value`"" wrapper would let a -VmName containing
    # a double quote split into extra parameter tokens in the elevated process.
    function Get-QuotedArg([string]$Value) {
        if ($Value -ne "" -and $Value -notmatch '[ \t\n\v"]') { return $Value }
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.Append('"')
        $bs = 0
        foreach ($ch in $Value.ToCharArray()) {
            if ($ch -eq '\') { $bs++; continue }
            if ($ch -eq '"') { [void]$sb.Append('\' * ($bs * 2 + 1)); [void]$sb.Append('"'); $bs = 0; continue }
            if ($bs -gt 0) { [void]$sb.Append('\' * $bs); $bs = 0 }
            [void]$sb.Append($ch)
        }
        [void]$sb.Append('\' * ($bs * 2))
        [void]$sb.Append('"')
        return $sb.ToString()
    }
    $argList = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Get-QuotedArg $PSCommandPath))
    foreach ($kv in $PSBoundParameters.GetEnumerator()) {
        if ($kv.Value -is [System.Management.Automation.SwitchParameter]) {
            if ($kv.Value.IsPresent) { $argList += "-$($kv.Key)" }
        } else {
            $argList += "-$($kv.Key)"; $argList += (Get-QuotedArg ([string]$kv.Value))
        }
    }
    # -Wait -PassThru so this process reports the ELEVATED run's outcome instead of
    # always exiting 0: a cancelled UAC prompt throws, and a failed child exits non-zero.
    try {
        $elevated = Start-Process powershell.exe -Verb RunAs -ArgumentList $argList -PassThru -Wait -ErrorAction Stop
    } catch {
        Write-Host "  Elevation was cancelled or failed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
    exit $elevated.ExitCode
}

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }

$wantEnabled = ($Enabled -eq "true")
$wantRemoval = ($RemoveExisting -eq "true")
$failed = $false

try {
    # Shared helpers: the automatic-checkpoint classifier. Loaded INSIDE the guarded
    # block so a damaged/partial install fails through the same catch/finally as any
    # other error -- a throw out here would skip the -FromPanel pause contract and the
    # result file, and the panel console would just vanish.
    $commonLib = Join-Path $PSScriptRoot "lib\AgentVm.Common.ps1"
    if (-not (Test-Path -LiteralPath $commonLib)) { throw "Required helper not found: $commonLib" }
    . $commonLib

    Write-Host ""
    Write-Host "  Automatic checkpoints -> $(if ($wantEnabled) { 'ON' } else { 'OFF' })  ($VmName)" -ForegroundColor White

    # ── 1. The VM must exist ─────────────────────────────────────────────────
    Write-Step "Locating the VM"
    $vm = $null
    try { $vm = Get-VM -Name $VmName -ErrorAction Stop } catch { $vm = $null }
    if (-not $vm) {
        throw "Hyper-V VM '$VmName' was not found. Nothing to change (install the VM first, or pass -VmName)."
    }
    Write-Ok "Found '$VmName' (state: $($vm.State))"

    # ── 2. Apply the policy ──────────────────────────────────────────────────
    # AutomaticCheckpointsEnabled is a VM-level policy, not virtual hardware, so it
    # can be set while the VM is running; it takes effect at the next start.
    Write-Step "Setting the automatic-checkpoint policy"
    # Property-probe rather than a hard reference: builds predating automatic
    # checkpoints (Server 2016 / pre-1709) expose neither the property nor the
    # Set-VM parameter, and there the feature simply doesn't exist.
    $curProp = $vm.PSObject.Properties['AutomaticCheckpointsEnabled']
    $current = if ($curProp -and $null -ne $curProp.Value) { [bool]$curProp.Value } else { $null }
    $setVmCmd = Get-Command Set-VM -ErrorAction SilentlyContinue
    $supported = ($setVmCmd -and $setVmCmd.Parameters.ContainsKey('AutomaticCheckpointsEnabled'))
    if ($null -ne $current -and $current -eq $wantEnabled) {
        Write-Note "Already $(if ($wantEnabled) { 'enabled' } else { 'disabled' }) -- no change needed."
    } elseif (-not $supported) {
        Write-Note "This Hyper-V version has no automatic checkpoints -- nothing to change."
    } else {
        Set-VM -Name $VmName -AutomaticCheckpointsEnabled $wantEnabled -ErrorAction Stop
        Write-Ok "Automatic checkpoints are now $(if ($wantEnabled) { 'ENABLED' } else { 'DISABLED' })"
    }

    # ── 3. Clean up the checkpoint Hyper-V already took ──────────────────────
    # Only when disabling: turning the policy off leaves any existing automatic
    # checkpoint on disk, and that .avhdx keeps growing until it is merged away.
    if ($wantEnabled) {
        Write-Note "Leaving existing checkpoints alone (enabling doesn't create one until the next VM start)."
    } elseif (-not $wantRemoval) {
        Write-Note "-RemoveExisting false -- policy changed, existing checkpoints left in place."
    } else {
        Write-Step "Removing existing automatic checkpoints"
        $found = Get-AgentVmAutomaticCheckpoint -VmName $VmName
        if (-not $found.Enumerated) {
            # The policy is changed, but we could not READ the checkpoint list -- so we
            # cannot claim the existing automatic checkpoint was cleaned up. Fail loudly
            # rather than printing "no checkpoints" over a checkpoint that is still there.
            throw "Automatic checkpoints are now disabled, but this VM's checkpoints could not be listed, so an existing automatic checkpoint may remain. Check Hyper-V Manager (or re-run this script) to remove it."
        }
        $certain  = @($found.Certain)
        $probable = @($found.Probable)

        if ($certain.Count -eq 0 -and $probable.Count -eq 0) {
            if (@($found.All).Count -gt 0) {
                Write-Note "$(@($found.All).Count) checkpoint(s) present, none of them automatic -- left untouched."
            } else {
                Write-Note "No checkpoints on this VM."
            }
        }

        # Positively identified (Hyper-V's own IsAutomaticSnapshot flag): remove.
        foreach ($snap in $certain) {
            Write-Note "Removing automatic checkpoint '$($snap.Name)'..."
            # Remove BY OBJECT, not by name: -Name would match every checkpoint
            # sharing that name, and Hyper-V allows duplicates.
            Remove-VMSnapshot -VMSnapshot $snap -Confirm:$false -ErrorAction Stop
            Write-Ok "Removed '$($snap.Name)'"
        }

        # Name-matched only (older Hyper-V builds expose no flag). Deleting a checkpoint
        # is irreversible, so never guess -- ask. ONE PROMPT PER CHECKPOINT, deliberately:
        # a single blanket "yes" over a list would take a deliberately-created checkpoint
        # that merely happens to be named like Hyper-V's along with the real one.
        $removedProbable = 0
        if ($probable.Count -gt 0) {
            Write-Host ""
            Write-Warning "Hyper-V on this host doesn't report which checkpoints are automatic."
            Write-Host "    The following match the name Hyper-V auto-generates (`"$VmName - (<timestamp>)`")," -ForegroundColor DarkGray
            Write-Host "    but a checkpoint you created yourself could be named that way too." -ForegroundColor DarkGray
            Write-Host "    Removing a checkpoint is IRREVERSIBLE -- each one is asked about separately." -ForegroundColor Yellow
            foreach ($snap in $probable) {
                Write-Host ""
                Write-Host "      $($snap.Name)   (created $($snap.CreationTime))" -ForegroundColor White
                $answer = Read-Host "      Remove this checkpoint? (type yes to remove, anything else to keep)"
                if ($answer -eq "yes") {
                    Remove-VMSnapshot -VMSnapshot $snap -Confirm:$false -ErrorAction Stop
                    Write-Ok "Removed '$($snap.Name)'"
                    $removedProbable++
                } else {
                    Write-Note "Kept '$($snap.Name)'."
                }
            }
            if ($removedProbable -lt $probable.Count) {
                Write-Note "Kept checkpoints can be removed from Hyper-V Manager if you want the disk space back."
            }
        }

        if ($certain.Count -gt 0 -or $removedProbable -gt 0) {
            # Removing a checkpoint queues a background merge of the .avhdx into the
            # base .vhdx; the disk space comes back when that finishes.
            Write-Note "Hyper-V merges the removed checkpoint's disk in the background -- this can take a few minutes."
        }
    }

    Write-Host ""
    Write-Host "  Done." -ForegroundColor Green
} catch {
    $failed = $true
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    # Report the outcome to the control panel, which polls this file (the same mechanism
    # Update-Construct.ps1 uses) and only records the setting as APPLIED on "ok" -- a
    # declined UAC never reaches this code at all, which is exactly right. Written
    # temp+rename so the panel can never read a half-written value. Best-effort.
    if ($env:CONSTRUCT_CHECKPOINT_RESULT) {
        try {
            $tmp = "$($env:CONSTRUCT_CHECKPOINT_RESULT).tmp"
            Set-Content -LiteralPath $tmp -Value $(if ($failed) { "fail" } else { "ok" }) -Encoding ASCII -NoNewline
            Move-Item -LiteralPath $tmp -Destination $env:CONSTRUCT_CHECKPOINT_RESULT -Force
        } catch { }
    }
    # Panel launches skip the pause on success (the panel reports the result); a
    # failure always pauses so the message can be read before the console closes.
    if ((-not $FromPanel) -or $failed) {
        Write-Host ""
        if (-not [Console]::IsInputRedirected) { [void](Read-Host "  Press Enter to exit") }
    }
}

if ($failed) { exit 1 }
exit 0
