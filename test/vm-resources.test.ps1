#Requires -Version 5.1
<#
    Set-AgentVmResources.ps1 + the local driver's in-place resize (Set-ConstructVmMemory /
    Set-ConstructVmCpuCount), driven against stubs -- no Hyper-V needed.

    The script's flow lives in Invoke-ConstructVmResourceRestart, which only ever calls
    DRIVER functions, so it is extracted from the script's AST (the same technique
    test/remote-cpu.test.ps1 uses for Set-ConstructVmCpuCount) and run here with
    scripted VM states. What is pinned:

      * nothing is changed unless the VM was observed OFF (timeout -> throw, no writes)
      * a running VM gets ONE graceful stop request, never a power cut
      * a saved/paused VM is resumed first so the guest shuts down cleanly
      * a refused stop request (no integration service) is a note, not a failure
      * the memory setter is Off-only, static, 2 MB-aligned, and read back

    Run: pwsh -NoProfile -File test/vm-resources.test.ps1
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$repoRoot = Resolve-Path (Join-Path $here '..')
$scriptPath = Join-Path $repoRoot 'Set-AgentVmResources.ps1'
$driverPath = Join-Path $repoRoot 'drivers/hyperv-local/HyperVLocal.Driver.ps1'

$script:passed = 0
$script:failed = 0
function ok([string]$Name, $Condition, [string]$Detail = '') {
    if ($Condition) { $script:passed++; Write-Host "  PASS  $Name" -ForegroundColor Green }
    else { $script:failed++; Write-Host "  FAIL  $Name $(if ($Detail) { "  << $Detail" })" -ForegroundColor Red }
}
function Throws([scriptblock]$Action, [string]$Pattern) {
    try { & $Action | Out-Null } catch { return ($_.Exception.Message -match $Pattern) }
    return $false
}
function FunctionText([string]$Path, [string]$Name) {
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$errors)
    if ($errors.Count) { throw "parse errors in $Path" }
    $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name }, $true)
    if (-not $fn) { throw "function $Name not found in $Path" }
    return $fn.Extent.Text
}

# ── (a) Parser check ────────────────────────────────────────────────────────
Write-Host "=== Parser ===" -ForegroundColor Cyan
foreach ($p in @($scriptPath, $driverPath)) {
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($p, [ref]$null, [ref]$errors)
    ok "parse: $(Split-Path -Leaf $p) has zero errors" ($errors.Count -eq 0)
}

# ── (b) Set-ConstructVmMemory (driver) ──────────────────────────────────────
Write-Host "=== Set-ConstructVmMemory ===" -ForegroundColor Cyan
. ([scriptblock]::Create((FunctionText $driverPath 'Set-ConstructVmMemory')))
$script:vmState = 'Off'; $script:memWrites = @(); $script:memReadback = $null
function Get-VM { param($Name, $ErrorAction) [pscustomobject]@{ Name = $Name; State = $script:vmState } }
function Set-VMMemory { param($VM, $DynamicMemoryEnabled, $StartupBytes, $ErrorAction)
    $script:memWrites += [pscustomobject]@{ Dynamic = $DynamicMemoryEnabled; Bytes = $StartupBytes }
    if ($null -eq $script:memReadback) { $script:memReadback = $StartupBytes }
}
function Get-VMMemory { param($VM, $ErrorAction) [pscustomobject]@{ Startup = $script:memReadback } }

foreach ($st in @('Running', 'Saved', 'Paused')) {
    $script:vmState = $st; $script:memWrites = @()
    ok "memory: a $st VM is refused" (Throws { Set-ConstructVmMemory -Name t -MemoryGB 8 } 'powered-off')
    ok "memory: ...and nothing was written ($st)" ($script:memWrites.Count -eq 0)
}
$script:vmState = 'Off'; $script:memWrites = @(); $script:memReadback = $null
Set-ConstructVmMemory -Name t -MemoryGB 16
ok "memory: 16 GB -> Set-VMMemory once, static, 16 GiB" (
    $script:memWrites.Count -eq 1 -and $script:memWrites[0].Dynamic -eq $false -and $script:memWrites[0].Bytes -eq 16GB)
$script:memWrites = @(); $script:memReadback = $null
Set-ConstructVmMemory -Name t -MemoryGB 6.5
ok "memory: a fractional size is honoured (6.5 GB)" ($script:memWrites[0].Bytes -eq [int64](6.5 * 1GB))
$script:memWrites = @(); $script:memReadback = $null
Set-ConstructVmMemory -Name t -MemoryGB 0.500001   # a hair over 512 MiB
ok "memory: rounded DOWN to Hyper-V's 2 MB granularity" ($script:memWrites[0].Bytes -eq 512MB)
ok "memory: zero / negative refused before any lookup" (
    (Throws { Set-ConstructVmMemory -Name t -MemoryGB 0 } 'greater than zero') -and
    (Throws { Set-ConstructVmMemory -Name t -MemoryGB -3 } 'greater than zero'))
ok "memory: below 32 MB refused" (Throws { Set-ConstructVmMemory -Name t -MemoryGB 0.01 } 'at least 32 MB')
$script:memWrites = @(); $script:memReadback = 4GB   # a write Hyper-V silently ignores
ok "memory: a readback mismatch is reported" (Throws { Set-ConstructVmMemory -Name t -MemoryGB 8 } 'did not apply')

# ── (c) Invoke-ConstructVmResourceRestart (script flow) against driver stubs ─
Write-Host "=== Invoke-ConstructVmResourceRestart ===" -ForegroundColor Cyan
. ([scriptblock]::Create((FunctionText $scriptPath 'Wait-ConstructVmStateIs')))
. ([scriptblock]::Create((FunctionText $scriptPath 'Invoke-ConstructVmResourceRestart')))
function Write-Step($msg) { }
function Write-Ok($msg) { }
function Write-Note($msg) { $script:notes += $msg }
function Start-Sleep { param($Seconds, $Milliseconds) }   # the poll loop must not really wait

# The stubbed VM: `states` is the sequence Get-ConstructVmState reports (the last one
# repeats); every driver call is recorded in order.
function Reset-Vm([string[]]$States, [switch]$StopThrows) {
    $script:states = $States; $script:stateIdx = 0; $script:calls = @(); $script:notes = @()
    $script:stopThrows = [bool]$StopThrows
}
function Get-ConstructVmState { param($Name)
    $i = [math]::Min($script:stateIdx, $script:states.Count - 1); $script:stateIdx++
    $script:calls += "state=$($script:states[$i])"
    return $script:states[$i]
}
function Stop-ConstructVm { param($Name, [switch]$TurnOff, [switch]$Force)
    $script:calls += "stop$(if ($TurnOff) { ':turnoff' })$(if ($Force) { ':force' })"
    if ($script:stopThrows) { throw 'The virtual machine is already in the specified state.' }
}
function Start-ConstructVm { param($Name) $script:calls += 'start' }
function Set-ConstructVmMemory { param($Name, $MemoryGB) $script:calls += "mem=$MemoryGB" }
function Set-ConstructVmCpuCount { param($Name, $ProcessorCount) $script:calls += "cpu=$ProcessorCount" }
function Writes { @($script:calls | Where-Object { $_ -notlike 'state=*' }) }

Reset-Vm @('running', 'running', 'unknown', 'off')
$r = Invoke-ConstructVmResourceRestart -Name t -MemoryGB 16 -CpuCount 8 -ShutdownTimeoutSec 30 -PollSec 0
ok "running: stop once -> wait for off -> memory, cpu -> start (in that order)" (
    ((Writes) -join ',') -eq 'stop,mem=16,cpu=8,start') ((Writes) -join ',')
ok "running: reports what it did" ($r.WasOff -eq $false -and $r.Started -eq $true -and ($r.Applied -join ',') -eq '16 GB RAM,8 vCPU')
ok "running: the stop is graceful (no -TurnOff / -Force)" (-not (($script:calls) -match ':turnoff|:force'))

Reset-Vm @('off')
Invoke-ConstructVmResourceRestart -Name t -CpuCount 4 -ShutdownTimeoutSec 30 -PollSec 0 | Out-Null
ok "off: no stop request, cpu only, then start" (((Writes) -join ',') -eq 'cpu=4,start') ((Writes) -join ',')

Reset-Vm @('off')
Invoke-ConstructVmResourceRestart -Name t -MemoryGB 12 -ShutdownTimeoutSec 30 -PollSec 0 | Out-Null
ok "off: memory only" (((Writes) -join ',') -eq 'mem=12,start')

Reset-Vm @('saved', 'saved', 'running', 'running', 'off')
Invoke-ConstructVmResourceRestart -Name t -MemoryGB 8 -ShutdownTimeoutSec 30 -PollSec 0 | Out-Null
ok "saved: resumed first, then ONE graceful stop, then the change, then start" (
    ((Writes) -join ',') -eq 'start,stop,mem=8,start') ((Writes) -join ',')

Reset-Vm @('paused', 'running', 'running', 'off')
Invoke-ConstructVmResourceRestart -Name t -CpuCount 2 -ShutdownTimeoutSec 30 -PollSec 0 | Out-Null
ok "paused: same as saved" (((Writes) -join ',') -eq 'start,stop,cpu=2,start')

Reset-Vm @('running', 'running', 'off') -StopThrows
Invoke-ConstructVmResourceRestart -Name t -CpuCount 2 -ShutdownTimeoutSec 30 -PollSec 0 | Out-Null
ok "a refused stop request (no integration service / already stopping) is a note, and the wait decides" (
    ((Writes) -join ',') -eq 'stop,cpu=2,start' -and ($script:notes -join ' ') -match 'not accepted')

Reset-Vm @('running')
ok "timeout: a guest that never powers off -> throw, NOTHING changed" (
    (Throws { Invoke-ConstructVmResourceRestart -Name t -MemoryGB 16 -CpuCount 8 -ShutdownTimeoutSec 0 -PollSec 0 } 'did not power off') -and
    ((Writes) -join ',') -eq 'stop')

Reset-Vm @('saved')
ok "timeout: a VM that never resumes -> throw, nothing changed" (
    (Throws { Invoke-ConstructVmResourceRestart -Name t -MemoryGB 16 -ShutdownTimeoutSec 0 -PollSec 0 } 'did not resume') -and
    ((Writes) -join ',') -eq 'start')

Reset-Vm @('absent')
ok "absent VM -> throw before anything" ((Throws { Invoke-ConstructVmResourceRestart -Name t -MemoryGB 16 -PollSec 0 } 'not found') -and (Writes).Count -eq 0)
Reset-Vm @('unknown')
ok "unreadable state -> throw before anything" ((Throws { Invoke-ConstructVmResourceRestart -Name t -MemoryGB 16 -PollSec 0 } 'could not be read') -and (Writes).Count -eq 0)

Reset-Vm @('off')
ok "nothing to apply -> throw" ((Throws { Invoke-ConstructVmResourceRestart -Name t -PollSec 0 } 'Nothing to apply') -and (Writes).Count -eq 0)
ok "a CPU count above 64 -> throw" ((Throws { Invoke-ConstructVmResourceRestart -Name t -CpuCount 65 -PollSec 0 } 'between 1 and 64') -and (Writes).Count -eq 0)

Reset-Vm @('off')
$r = Invoke-ConstructVmResourceRestart -Name t -CpuCount 4 -StartAfter $false -PollSec 0
ok "-StartAfter false leaves the VM off" (((Writes) -join ',') -eq 'cpu=4' -and $r.Started -eq $false)

# ── (d) Script source invariants (the parts only Windows + UAC can run) ─────
Write-Host "=== Source invariants ===" -ForegroundColor Cyan
$src = Get-Content -Raw $scriptPath
ok "script: capability-gated on the driver's Resources flag" (
    $src -match 'Get-ConstructDriverCapabilities' -and $src -match 'if \(-not \$caps\.Resources\)')
ok "script: never forces a power cut (no -TurnOff / -Force on the stop)" (
    $src -notmatch 'Stop-ConstructVm[^\r\n]*-(TurnOff|Force)')
ok "script: reports 'running' to the panel BEFORE the flow (so the panel's SSH poweroff waits for the elevated console)" (
    $src.IndexOf('Write-ResourcesResult "running"') -gt 0 -and
    $src.IndexOf('Write-ResourcesResult "running"') -lt $src.IndexOf('$outcome = Invoke-ConstructVmResourceRestart'))
ok "script: the result file reports fail/ok from the failure flag, in finally" (
    $src -match 'Write-ResourcesResult \$\(if \(\$failed\) \{ "fail" \} else \{ "ok" \}\)')
ok "script: the result file is written temp+rename" (
    $src -match 'Set-Content[^\r\n]*\$tmp' -and $src -match 'Move-Item[^\r\n]*\$tmp')
ok "script: a refused -InstanceName also reports fail (before the elevation step)" (
    $src.IndexOf('Write-ResourcesResult "fail"') -gt 0 -and
    $src.IndexOf('Write-ResourcesResult "fail"') -lt $src.IndexOf('Self-elevate to Administrator'))
ok "script: the common-lib / driver load is inside the guarded block" (
    $src.IndexOf('try {') -lt $src.IndexOf('Required helper not found'))
ok "script: -FromPanel skips the pause only on success" ($src -match 'if \(\(-not \$FromPanel\) -or \$failed\)')
ok "script: disk size is out of scope (no -VmDiskGB parameter)" ($src -notmatch '\$VmDiskGB')

$driverSrc = Get-Content -Raw $driverPath
ok "driver: hyperv-local declares Resources = true" ($driverSrc -match 'Resources\s*=\s*\$true')
ok "driver: hyperv-remote declares Resources = false" (
    (Get-Content -Raw (Join-Path $repoRoot 'drivers/hyperv-remote/HyperVRemote.Driver.ps1')) -match 'Resources\s*=\s*\$false')

Write-Host ""
Write-Host "  vm-resources tests — $script:passed passed, $script:failed failed" -ForegroundColor $(if ($script:failed) { 'Red' } else { 'Green' })
if ($script:failed) { exit 1 }
exit 0
