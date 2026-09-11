#Requires -Version 5.1
<#
.SYNOPSIS
    Resize the Construct agent VM's RAM and/or vCPU count by restarting it:
    shut the VM down, change the virtual hardware while it is off, start it again.

.DESCRIPTION
    Memory and processor count are virtual hardware, which Hyper-V only lets you
    change on a powered-off VM. Until now the control panel's "VM resources" settings
    were applied only when the VM was REBUILT (Reinstall / Redownload). This script is
    the cheap path for the two values that don't need a rebuild:

        1. shut the VM down gracefully (a saved or paused VM is resumed first so the
           guest can shut down cleanly; a VM that is already off skips this step),
        2. apply the new memory size and/or vCPU count through the hypervisor driver,
        3. start the VM again.

    The disk size is deliberately NOT handled here -- growing a VHDX also needs the
    guest's partition and filesystem grown, which stays a Reinstall / Redownload job.

    Nothing is changed unless the VM is observed OFF: a guest that ignores the
    shutdown request (no integration services, a hung shutdown) makes the script fail
    after -ShutdownTimeoutSec with the VM left as it was. It never forces a power cut.

    Requires elevation (Hyper-V cmdlets); self-elevates when run directly.

.PARAMETER VmMemoryGB
    The new RAM size in GB (fractional values are allowed, e.g. 6.5). 0 = leave the
    memory as it is. At least one of -VmMemoryGB / -VmCpuCount must be given.

.PARAMETER VmCpuCount
    The new number of virtual processors (1-64, and at most the host's logical
    processor count). 0 = leave the count as it is.

.PARAMETER VmName
    The Hyper-V VM to change. Defaults to the Construct VM, "Agent-VM".

.PARAMETER InstanceName
    Name-only targeting (plan section 4.12): the Construct instance to change, resolved
    through the client-side registry -- -VmName and -Backend then come from its entry.
    An explicit -VmName/-Backend that disagrees with the entry is an error.

.PARAMETER Backend
    Hypervisor backend the VM lives on. "hyperv-local" (the default) is the local
    Hyper-V path; resizing is a capability-gated feature of the driver contract
    (docs/drivers.md). A remote instance is resized by its host service instead.

.PARAMETER ShutdownTimeoutSec
    How long to wait for the guest to power off before giving up (default 180).

.PARAMETER StartAfter
    "true" (default) to start the VM again once the change is applied; "false" to
    leave it off.

.PARAMETER FromPanel
    Set by the control panel: skip the end-of-run "Press Enter" pause on SUCCESS
    (the panel is the feedback). Failures always pause so the error stays readable.

.EXAMPLE
    .\Set-AgentVmResources.ps1 -VmMemoryGB 16 -VmCpuCount 8
    Restart the default VM with 16 GB of RAM and 8 vCPUs.
#>
[CmdletBinding()]
param(
    [double]$VmMemoryGB = 0,
    [int]$VmCpuCount = 0,
    [string]$VmName = "Agent-VM",
    [string]$Backend = "hyperv-local",
    [int]$ShutdownTimeoutSec = 180,
    [ValidateSet("true", "false")]
    [string]$StartAfter = "true",
    # NAME-ONLY TARGETING (B11, plan section 4.12). Empty = today's behaviour exactly:
    # nothing is read, nothing is resolved, -VmName/-Backend stand as given.
    [string]$InstanceName = "",
    [switch]$FromPanel
)

# Report the outcome to the control panel through a result file (the mechanism
# Set-AgentVmCheckpoints.ps1 and Update-Construct.ps1 use). Written temp+rename so
# the polling panel can never read a half-written value. Best-effort.
function Write-ResourcesResult([string]$Value) {
    if (-not $env:CONSTRUCT_RESOURCES_RESULT) { return }
    try {
        $tmp = "$($env:CONSTRUCT_RESOURCES_RESULT).tmp"
        Set-Content -LiteralPath $tmp -Value $Value -Encoding ASCII -NoNewline
        Move-Item -LiteralPath $tmp -Destination $env:CONSTRUCT_RESOURCES_RESULT -Force
    } catch { }
}

# ── -InstanceName -> -VmName/-Backend, BEFORE the elevation below ────────────
# Resolved HERE, while this process is still the one the user started, and forwarded to
# the elevated copy as the RESOLVED values: the instance registry lives in %LOCALAPPDATA%,
# and where UAC switches to a different admin account the elevated process would read that
# account's profile instead -- and find no registry at all.
if ($InstanceName) {
    try {
        $instanceTargetLib = Join-Path $PSScriptRoot "lib\AgentVm.InstanceTarget.ps1"
        if (-not (Test-Path -LiteralPath $instanceTargetLib)) {
            throw "-InstanceName needs lib/AgentVm.InstanceTarget.ps1, which is missing from this install. Update The Construct, or pass -VmName instead."
        }
        . $instanceTargetLib
        $explicitTarget = @{}
        foreach ($tp in @('VmName', 'Backend')) {
            if ($PSBoundParameters.ContainsKey($tp)) { $explicitTarget[$tp] = $PSBoundParameters[$tp] }
        }
        $instanceTarget = Resolve-ConstructVmTarget -Name $InstanceName -Explicit $explicitTarget
        if (-not $PSBoundParameters.ContainsKey('VmName'))  { $VmName  = [string]$instanceTarget.VmName }
        if (-not $PSBoundParameters.ContainsKey('Backend')) { $Backend = [string]$instanceTarget.Backend }
        # The elevated relaunch forwards $PSBoundParameters verbatim, so hand it the
        # RESOLVED identity and drop the name -- the child must not resolve it again.
        [void]$PSBoundParameters.Remove('InstanceName')
        $PSBoundParameters['VmName']  = $VmName
        $PSBoundParameters['Backend'] = $Backend
    } catch {
        Write-Host ""
        Write-Host "  ERROR: $($_.Exception.Message)" -ForegroundColor Red
        Write-ResourcesResult "fail"
        Write-Host ""
        if (-not [Console]::IsInputRedirected) { [void](Read-Host "  Press Enter to exit") }
        exit 1
    }
}

# ── Self-elevate to Administrator ────────────────────────────────────────────
# Hyper-V's Set-VMMemory / Set-VMProcessor / Stop-VM / Start-VM need admin. Launched
# from the control panel this already runs elevated (the panel uses Start-Process
# -Verb RunAs), so this branch only fires for a direct run.
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Relaunching as Administrator..." -ForegroundColor Yellow
    # Canonical Windows argument quoting (CommandLineToArgvW rules) -- same helper as
    # Set-AgentVmCheckpoints.ps1, so a -VmName with a quote can't split into extra tokens.
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

function Wait-ConstructVmStateIs {
    <#
        Poll Get-ConstructVmState until it reports one of -States (or the timeout
        passes). Returns the last observed state; the caller decides what a miss means.
        `-OnTick` runs once per poll with the observed state -- the resize flow uses it
        to issue the graceful shutdown as soon as a resumed VM is running.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string[]]$States,
        [int]$TimeoutSec = 180,
        [int]$PollSec = 2,
        [scriptblock]$OnTick = $null
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $state = Get-ConstructVmState -Name $Name
    while ($true) {
        if ($States -contains $state) { return $state }
        if ($OnTick) { & $OnTick $state }
        if ((Get-Date) -ge $deadline) { return $state }
        Start-Sleep -Seconds $PollSec
        $state = Get-ConstructVmState -Name $Name
    }
}

function Invoke-ConstructVmResourceRestart {
    <#
        The whole flow, driver calls only (so test/vm-resources.test.ps1 can drive it
        against stubs):

            observe state -> (resume a saved/paused VM) -> graceful shutdown ->
            wait for Off -> Set-ConstructVmMemory / Set-ConstructVmCpuCount -> Start

        Returns a hashtable describing what happened. Throws on every failure, ALWAYS
        before any hardware change unless the VM was observed Off -- the caller can
        promise "nothing changed" for a shutdown that didn't happen.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [double]$MemoryGB = 0,
        [int]$CpuCount = 0,
        [int]$ShutdownTimeoutSec = 180,
        [int]$PollSec = 2,
        [bool]$StartAfter = $true
    )
    if ($MemoryGB -le 0 -and $CpuCount -le 0) {
        throw "Nothing to apply: give -VmMemoryGB and/or -VmCpuCount."
    }
    if ($CpuCount -lt 0 -or $CpuCount -gt 64) { throw "-VmCpuCount must be between 1 and 64." }

    Write-Step "Locating the VM"
    $state = Get-ConstructVmState -Name $Name
    switch ($state) {
        'absent'  { throw "Hyper-V VM '$Name' was not found. Nothing to change (install the VM first, or pass -VmName)." }
        'unknown' { throw "The power state of '$Name' could not be read (no Hyper-V access, or the VM is mid-transition). Nothing was changed." }
    }
    Write-Ok "Found '$Name' (state: $state)"

    # ── 1. Power off, gracefully ─────────────────────────────────────────────
    $wasOff = ($state -eq 'off')
    if (-not $wasOff) {
        Write-Step "Shutting the VM down"
        if ($state -in @('saved', 'paused')) {
            # Hardware can't change under a saved/paused VM, and discarding its saved
            # state would be a power cut. Resume it so the guest shuts down cleanly.
            Write-Note "The VM is $state -- resuming it so the guest can shut down cleanly."
            Start-ConstructVm -Name $Name
            $running = Wait-ConstructVmStateIs -Name $Name -States @('running') -TimeoutSec $ShutdownTimeoutSec -PollSec $PollSec
            if ($running -ne 'running') { throw "The VM did not resume within $ShutdownTimeoutSec s (state: $running). Nothing was changed." }
        }
        # Ask the guest to shut down. Stop-VM (without -Force/-TurnOff) waits for the
        # guest; it throws when the shutdown integration service is unavailable or the
        # VM is already on its way down (the panel sends `poweroff` over SSH first), so a
        # throw is a note, not a failure -- the wait below is what decides.
        $script:stopRequested = $false
        $requestStop = {
            param($observed)
            if ($observed -eq 'running' -and -not $script:stopRequested) {
                $script:stopRequested = $true
                try {
                    Stop-ConstructVm -Name $Name
                    Write-Ok "Guest shutdown requested"
                } catch {
                    Write-Note "Graceful shutdown request not accepted ($($_.Exception.Message.Trim())) -- waiting for the guest to power off on its own."
                }
            }
        }
        & $requestStop (Get-ConstructVmState -Name $Name)
        $final = Wait-ConstructVmStateIs -Name $Name -States @('off') -TimeoutSec $ShutdownTimeoutSec -PollSec $PollSec -OnTick $requestStop
        if ($final -ne 'off') {
            throw "The VM did not power off within $ShutdownTimeoutSec s (state: $final). Nothing was changed -- shut it down from inside the guest and run this again."
        }
        Write-Ok "VM is off"
    } else {
        Write-Note "The VM is already off -- no shutdown needed."
    }

    # ── 2. Apply the new size ────────────────────────────────────────────────
    Write-Step "Applying the new VM size"
    $applied = @()
    if ($MemoryGB -gt 0) {
        Set-ConstructVmMemory -Name $Name -MemoryGB $MemoryGB
        Write-Ok ("Memory: {0} GB" -f $MemoryGB)
        $applied += ("{0} GB RAM" -f $MemoryGB)
    }
    if ($CpuCount -gt 0) {
        Set-ConstructVmCpuCount -Name $Name -ProcessorCount $CpuCount
        Write-Ok "Processors: $CpuCount"
        $applied += "$CpuCount vCPU"
    }

    # ── 3. Start again ───────────────────────────────────────────────────────
    if ($StartAfter) {
        Write-Step "Starting the VM"
        Start-ConstructVm -Name $Name
        Write-Ok "Start requested"
    } else {
        Write-Note "-StartAfter false -- the VM stays off."
    }
    return @{ WasOff = $wasOff; Applied = $applied; Started = [bool]$StartAfter }
}

$failed = $false
try {
    # Shared helpers + the hypervisor driver, loaded INSIDE the guarded block so a
    # damaged/partial install fails through the same catch/finally as any other error
    # (the -FromPanel pause contract and the result file both depend on it).
    $commonLib = Join-Path $PSScriptRoot "lib\AgentVm.Common.ps1"
    if (-not (Test-Path -LiteralPath $commonLib)) { throw "Required helper not found: $commonLib" }
    . $commonLib

    $driverLoader = Join-Path $PSScriptRoot "drivers\Load-ConstructDriver.ps1"
    if (-not (Test-Path -LiteralPath $driverLoader)) { throw "Required helper not found: $driverLoader" }
    . $driverLoader -Backend $Backend
    $caps = Get-ConstructDriverCapabilities
    if (-not $caps.Resources) {
        throw "The '$Backend' backend does not resize VMs from this PC (a remote instance is resized by its host service from the control panel), so there is nothing to change here."
    }

    if ($VmMemoryGB -le 0 -and $VmCpuCount -le 0) {
        throw "Nothing to apply: give -VmMemoryGB and/or -VmCpuCount."
    }
    # Sanity-check against THIS host before touching the VM: Hyper-V refuses a vCPU
    # count above its logical processors, and a VM that no longer fits in RAM fails to
    # start AFTER the change -- both are better refused up front, with the numbers.
    $cs = $null
    try { $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop } catch { $cs = $null }
    if ($cs) {
        if ($VmCpuCount -gt [int]$cs.NumberOfLogicalProcessors) {
            throw ("-VmCpuCount {0} exceeds this host's {1} logical processors." -f $VmCpuCount, $cs.NumberOfLogicalProcessors)
        }
        $hostGB = [math]::Round(([double]$cs.TotalPhysicalMemory) / 1GB, 1)
        if ($VmMemoryGB -gt 0 -and $VmMemoryGB -ge $hostGB) {
            throw ("-VmMemoryGB {0} is not below this host's {1} GB of physical RAM." -f $VmMemoryGB, $hostGB)
        }
    }

    $wants = @()
    if ($VmMemoryGB -gt 0) { $wants += ("{0} GB RAM" -f $VmMemoryGB) }
    if ($VmCpuCount -gt 0) { $wants += "$VmCpuCount vCPU" }
    Write-Host ""
    Write-Host "  VM resources -> $($wants -join ', ')  ($VmName)" -ForegroundColor White

    # Tell the panel this console is up and ELEVATED before the guest is touched: it
    # sends the guest its `poweroff` over SSH only after seeing this, so a declined UAC
    # (which never reaches here) can't leave the VM shut down with nothing applied.
    Write-ResourcesResult "running"

    $outcome = Invoke-ConstructVmResourceRestart -Name $VmName -MemoryGB $VmMemoryGB -CpuCount $VmCpuCount `
        -ShutdownTimeoutSec $ShutdownTimeoutSec -StartAfter ($StartAfter -eq "true")

    Write-Host ""
    Write-Host ("  Done: {0} applied{1}." -f ($outcome.Applied -join ', '), $(if ($outcome.Started) { ", VM starting" } else { "" })) -ForegroundColor Green
} catch {
    $failed = $true
    Write-Host ""
    Write-Host "  FAILED: $($_.Exception.Message)" -ForegroundColor Red
} finally {
    # The panel polls this file and reports the outcome; a declined UAC never reaches
    # this code at all, which is exactly right (it times out and says so).
    Write-ResourcesResult $(if ($failed) { "fail" } else { "ok" })
    # Panel launches skip the pause on success (the panel reports the result); a
    # failure always pauses so the message can be read before the console closes.
    if ((-not $FromPanel) -or $failed) {
        Write-Host ""
        if (-not [Console]::IsInputRedirected) { [void](Read-Host "  Press Enter to exit") }
    }
}

if ($failed) { exit 1 }
exit 0
