#Requires -Version 5.1
<#
    Unit tests for the hyperv-remote driver (batch B7). Run:

        pwsh -NoProfile -File test/remote-driver.test.ps1

    Self-contained and network-free: the driver is loaded through the real loader and
    then its ONE dependency -- Invoke-ConstructApi / Wait-ConstructJob from
    lib/AgentVm.Remote.ps1 -- is SHADOWED by functions defined here, which record every
    call. So this pins the CONTRACT MAPPING (docs/drivers.md section 6) rather than the
    HTTP, which test/remote-client.test.ps1 covers.

    The point of the state assertions is one rule: `absent` comes from a 404 and nothing
    else. An unreachable service, a 401 or a 403 must read as "can't tell" -- the panel
    offers to CREATE a VM for `absent`.
#>
$ErrorActionPreference = "Stop"

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}
function Test-Throws([scriptblock]$Script) {
    try { & $Script | Out-Null; return $false } catch { return $true }
}
function Get-ThrowMessage([scriptblock]$Script) {
    try { & $Script | Out-Null; return "" } catch { return [string]$_.Exception.Message }
}

# Quiet progress helpers, defined BEFORE the driver so its guarded fallbacks stay out of
# the way -- exactly what the real host scripts do.
function Write-Step($msg) { }
function Write-Ok($msg)   { }
function Write-Note($msg) { }

$driverLoader = Join-Path $repoRoot "drivers/Load-ConstructDriver.ps1"

# ── (a) The loader ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Driver loader ===" -ForegroundColor Cyan
. (Join-Path $repoRoot "lib/AgentVm.Remote.ps1")
# Load the LOCAL backend first: that is what defines Import-ConstructDriver (the loader
# is a script, dot-sourced into this scope), and it also proves the zero-change call
# `. $loader -Backend "hyperv-local"` still works untouched. The remote driver is loaded
# below and overrides the contract functions.
. $driverLoader -Backend "hyperv-local"
ok "loader: the local driver is still loadable with no extra arguments" ((Get-ConstructDriverCapabilities).Backend -eq 'hyperv-local')
ok "loader: resolves hyperv-remote to its driver file" `
    ((Import-ConstructDriver -Backend 'hyperv-remote' -DriverRoot (Join-Path $repoRoot "drivers")) -like '*HyperVRemote.Driver.ps1')
ok "loader: still resolves hyperv-local (the zero-change default)" `
    ((Import-ConstructDriver -Backend '' -DriverRoot (Join-Path $repoRoot "drivers")) -like '*HyperVLocal.Driver.ps1')
$unknownMsg = Get-ThrowMessage { Import-ConstructDriver -Backend 'proxmox' -DriverRoot (Join-Path $repoRoot "drivers") }
ok "loader: an unknown backend throws" ($unknownMsg -ne "")
ok "loader: ...naming BOTH known backends" ($unknownMsg -match 'hyperv-local' -and $unknownMsg -match 'hyperv-remote')
# A -ServiceUrl handed to a backend that takes none is a caller bug, not a silent no-op.
ok "loader: -ServiceUrl on hyperv-local is refused" `
    (Test-Throws { . $driverLoader -Backend "hyperv-local" -ServiceUrl "https://x:7462" })

$SVC = "https://buildbox.example.local:7462"
$auth = New-ConstructApiAuth -Mode token -Token 'tok'
. $driverLoader -Backend "hyperv-remote" -ServiceUrl $SVC -Auth $auth
ok "loader: the context is applied by the loader" ((Get-ConstructDriverContext).ServiceUrl -eq $SVC)
ok "loader: ...and reports the auth MODE, never the credential" ((Get-ConstructDriverContext).AuthMode -eq 'token')

# ── (b) The API stub (shadows the library, in this scope) ───────────────────
$script:calls   = New-Object System.Collections.Generic.List[object]
$script:answers = @{}      # "METHOD /path" -> @{ Body = <obj>; Status = <int> }
$script:defaultAnswer = @{ Body = $null; Status = 500 }
$script:lastStatus = 0
$script:lastError = ""

function Invoke-ConstructApi {
    [CmdletBinding()]
    param([string]$BaseUrl, [string]$Method = 'GET', [string]$Path, $Body, $Auth, [string]$Pin, [string]$StoreDir, [int]$TimeoutSec = 100, [switch]$NoThrow)
    $script:calls.Add([pscustomobject]@{ BaseUrl = $BaseUrl; Method = $Method; Path = $Path; Body = $Body; Auth = $Auth })
    $key = "$Method $Path"
    $a = if ($script:answers.ContainsKey($key)) { $script:answers[$key] } else { $script:defaultAnswer }
    $script:lastStatus = [int]$a.Status
    $script:lastError = if ($a.ContainsKey('Error')) { [string]$a.Error } else { "" }
    if ($a.Status -ge 200 -and $a.Status -lt 300) { return $a.Body }
    if ($NoThrow) { return $null }
    throw "Construct host service refused $key (HTTP $($a.Status)): $($script:lastError)"
}
function Get-ConstructApiLastStatus { return [int]$script:lastStatus }
function Get-ConstructApiLastError  { return [string]$script:lastError }

$script:jobResult = $null
$script:jobIds    = New-Object System.Collections.Generic.List[string]
function Wait-ConstructJob {
    [CmdletBinding()]
    param([string]$BaseUrl, [string]$JobId, $Auth, [string]$Pin, [string]$StoreDir, [int]$PollSeconds = 2, [int]$TimeoutSeconds = 3600, [scriptblock]$OnProgress)
    $script:jobIds.Add($JobId)
    return $script:jobResult
}

function Reset-Api {
    $script:calls.Clear(); $script:answers = @{}; $script:jobIds.Clear()
    $script:defaultAnswer = @{ Body = $null; Status = 500 }
    $script:lastStatus = 0; $script:lastError = ""
}
function Answer([string]$Key, $Body, [int]$Status = 200, [string]$ErrorText = "") {
    $script:answers[$Key] = @{ Body = $Body; Status = $Status; Error = $ErrorText }
}

# ── (c) Capabilities ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Capabilities ===" -ForegroundColor Cyan
$caps = Get-ConstructDriverCapabilities
ok "caps: no checkpoints (Set-AgentVmCheckpoints drives the LOCAL Hyper-V)" ($caps.Checkpoints -eq $false)
ok "caps: no console" ($caps.Console -eq 'none')
ok "caps: suspend YES (the idle policy saves it; a start resumes it)" ($caps.Suspend -eq $true)
ok "caps: it names its backend" ($caps.Backend -eq 'hyperv-remote')
# The contract says the four checkpoint functions exist ONLY when Checkpoints is true.
# Asserted against the driver FILE, not the session: this test deliberately loaded the
# local driver first (for Import-ConstructDriver), and in production exactly one driver
# is ever loaded -- so "is it defined right now?" would be about the test, not the driver.
$remoteDriverAst = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot "drivers/hyperv-remote/HyperVRemote.Driver.ps1"), [ref]$null, [ref]$null)
$remoteDriverFns = @($remoteDriverAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true) | ForEach-Object { $_.Name })
foreach ($fn in @('Get-ConstructVmCheckpointInfo', 'Set-ConstructVmAutoCheckpointPolicy', 'Get-ConstructVmAutomaticCheckpoint', 'Remove-ConstructVmCheckpoint')) {
    ok "caps: the driver file defines no $fn (capability-gated, and Checkpoints is false)" ($remoteDriverFns -notcontains $fn)
}
# ...and it DOES define every function the portable contract requires.
foreach ($fn in @('Get-ConstructDriverCapabilities', 'Test-ConstructDriverPrereqs', 'Ensure-ConstructDriverPrereqs',
                  'New-ConstructVm', 'Remove-ConstructVm', 'Start-ConstructVm', 'Stop-ConstructVm', 'Save-ConstructVm',
                  'Get-ConstructVmState', 'Test-ConstructVmPresent', 'Get-ConstructVmEndpoint',
                  'Wait-ConstructVmReachable', 'Detach-ConstructInstallMedia')) {
    ok "contract: the driver implements $fn" ($remoteDriverFns -contains $fn)
}

# ── (d) State: only a 404 is 'absent' ───────────────────────────────────────
Write-Host ""
Write-Host "=== Get-ConstructVmState ===" -ForegroundColor Cyan
foreach ($pair in @(@('running', 'running'), @('off', 'off'), @('paused', 'paused'), @('saved', 'saved'), @('absent', 'absent'), @('starting', 'unknown'), @('', 'unknown'))) {
    Reset-Api
    Answer 'GET /vms/work-vm/state' ([pscustomobject]@{ state = $pair[0] }) 200
    ok "state: '$($pair[0])' -> $($pair[1])" ((Get-ConstructVmState -Name 'work-vm') -eq $pair[1])
}
Reset-Api
Answer 'GET /vms/work-vm/state' $null 404 'No VM named work-vm.'
ok "state: a 404 is the ONLY absent" ((Get-ConstructVmState -Name 'work-vm') -eq 'absent')
foreach ($status in @(401, 403, 500, 0)) {
    Reset-Api
    Answer 'GET /vms/work-vm/state' $null $status 'nope'
    ok "state: HTTP $status is 'can't tell', NOT absent" ((Get-ConstructVmState -Name 'work-vm') -eq 'unknown')
}
Reset-Api
Answer 'GET /vms/work-vm/state' ([pscustomobject]@{ state = 'RUNNING' }) 200
ok "state: the service's enum is compared case-insensitively" ((Get-ConstructVmState -Name 'work-vm') -eq 'running')
Reset-Api
Answer 'GET /vms/work-vm/state' ([pscustomobject]@{ state = 'running' }) 200
[void](Get-ConstructVmState -Name 'work-vm')
ok "state: it asks the VM's own route" ($script:calls[0].Path -eq '/vms/work-vm/state' -and $script:calls[0].Method -eq 'GET')
ok "state: ...on the configured service, with the configured credential" ($script:calls[0].BaseUrl -eq $SVC -and $script:calls[0].Auth['Mode'] -eq 'token')

# ── (e) Presence is THREE-valued ────────────────────────────────────────────
Write-Host ""
Write-Host "=== Test-ConstructVmPresent ===" -ForegroundColor Cyan
Reset-Api
Answer 'GET /vms/work-vm' ([pscustomobject]@{ name = 'work-vm' }) 200
ok "present: 200 -> `$true" ((Test-ConstructVmPresent -Name 'work-vm') -eq $true)
Reset-Api
Answer 'GET /vms/work-vm' $null 404
ok "present: 404 -> `$false" ((Test-ConstructVmPresent -Name 'work-vm') -eq $false)
Reset-Api
Answer 'GET /vms/work-vm' $null 403
ok "present: 403 (somebody else's VM) -> `$null, NOT `$false" ($null -eq (Test-ConstructVmPresent -Name 'work-vm'))
Reset-Api
Answer 'GET /vms/work-vm' $null 0
ok "present: unreachable -> `$null" ($null -eq (Test-ConstructVmPresent -Name 'work-vm'))

# ── (f) Endpoint ────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Get-ConstructVmEndpoint ===" -ForegroundColor Cyan
Reset-Api
Answer 'GET /vms/work-vm/endpoint' ([pscustomobject]@{ sshHost = 'buildbox.example.local'; sshPort = 2201 }) 200
$ep = Get-ConstructVmEndpoint -Name 'work-vm'
ok "endpoint: the service's host + allocated port" ($ep.SshHost -eq 'buildbox.example.local' -and $ep.SshPort -eq 2201)
ok "endpoint: the port is an int (it becomes -SshPort)" ($ep.SshPort -is [int])
Reset-Api
Answer 'GET /vms/work-vm/endpoint' $null 409 'no forward yet'
$msg409 = Get-ThrowMessage { Get-ConstructVmEndpoint -Name 'work-vm' }
ok "endpoint: a 409 explains that the forward isn't allocated yet" ($msg409 -match 'has not been allocated' -or $msg409 -match 'no client-reachable address')
Reset-Api
Answer 'GET /vms/work-vm/endpoint' $null 500 'boom'
ok "endpoint: any other failure throws" (Test-Throws { Get-ConstructVmEndpoint -Name 'work-vm' })

# ── (g) Create ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== New-ConstructVm ===" -ForegroundColor Cyan
Reset-Api
Answer 'POST /vms' ([pscustomobject]@{ jobId = 'job-1' }) 202
$script:jobResult = [pscustomobject]@{
    name = 'work-vm'
    endpoint = [pscustomobject]@{ sshHost = 'buildbox.example.local'; sshPort = 2201 }
    vmToken = 'ONE-TIME-SECRET'
}
$created = New-ConstructVm -Descriptor @{ Name = 'work-vm'; ProcessorCount = 4; MemoryGB = 8; DiskGB = 50; Nested = $true; AutomaticCheckpoints = $false }
ok "create: POSTs to /vms" ($script:calls[0].Method -eq 'POST' -and $script:calls[0].Path -eq '/vms')
ok "create: the descriptor becomes the service's request shape" `
    ($script:calls[0].Body['name'] -eq 'work-vm' -and $script:calls[0].Body['cpu'] -eq 4 -and
     $script:calls[0].Body['ramGb'] -eq 8 -and $script:calls[0].Body['diskGb'] -eq 50)
ok "create: opts carry the capability-shaped fields" `
    ($script:calls[0].Body['opts']['nested'] -eq $true -and $script:calls[0].Body['opts']['automaticCheckpoints'] -eq $false)
ok "create: HOST-side descriptor fields are NOT forwarded (the service owns them)" `
    (-not $script:calls[0].Body.ContainsKey('switchName') -and -not $script:calls[0].Body.ContainsKey('vhdPath'))
ok "create: the returned job is followed" ($script:jobIds.Count -eq 1 -and $script:jobIds[0] -eq 'job-1')
ok "create: it returns the endpoint (there is no name convention to rebuild it from)" `
    ($created.Endpoint.SshHost -eq 'buildbox.example.local' -and $created.Endpoint.SshPort -eq 2201)
ok "create: ...and the ONE-TIME VM token" ($created.VmToken -eq 'ONE-TIME-SECRET')
ok "create: ...and the name" ($created.Name -eq 'work-vm')

# MemoryBytes wins and rounds to the NEAREST GB: rounding 7.9 GB down to 7 would quietly
# hand the user less RAM than the local path would have given them.
Reset-Api
Answer 'POST /vms' ([pscustomobject]@{ jobId = 'job-2' }) 202
[void](New-ConstructVm -Descriptor @{ Name = 'work-vm'; ProcessorCount = 2; MemoryBytes = [long](7.9 * 1GB); DiskBytes = [long](50 * 1GB) })
ok "create: MemoryBytes rounds to the nearest GB" ($script:calls[0].Body['ramGb'] -eq 8)
ok "create: DiskBytes converts too" ($script:calls[0].Body['diskGb'] -eq 50)
ok "create: opts are omitted entirely when the descriptor states none" (-not $script:calls[0].Body.ContainsKey('opts'))

Reset-Api
ok "create: a descriptor with no name is refused before any call" `
    ((Test-Throws { New-ConstructVm -Descriptor @{ ProcessorCount = 1; MemoryGB = 4; DiskGB = 20 } }) -and $script:calls.Count -eq 0)
Reset-Api
ok "create: a descriptor with no CPU count is refused" `
    ((Test-Throws { New-ConstructVm -Descriptor @{ Name = 'x'; MemoryGB = 4; DiskGB = 20 } }) -and $script:calls.Count -eq 0)
Reset-Api
ok "create: a descriptor with no memory is refused" `
    ((Test-Throws { New-ConstructVm -Descriptor @{ Name = 'x'; ProcessorCount = 1; DiskGB = 20 } }) -and $script:calls.Count -eq 0)
Reset-Api
Answer 'POST /vms' ([pscustomobject]@{ }) 202
ok "create: a 202 with no job id is an error, not a silent success" (Test-Throws { New-ConstructVm -Descriptor @{ Name = 'x'; ProcessorCount = 1; MemoryGB = 4; DiskGB = 20 } })

# ── (h) Remove ──────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Remove-ConstructVm ===" -ForegroundColor Cyan
Reset-Api
Answer 'DELETE /vms/work-vm' ([pscustomobject]@{ jobId = 'job-del' }) 202
$script:jobResult = $null
Remove-ConstructVm -Name 'work-vm'
ok "remove: DELETEs the VM" ($script:calls[0].Method -eq 'DELETE' -and $script:calls[0].Path -eq '/vms/work-vm')
ok "remove: and follows the job" ($script:jobIds.Count -eq 1 -and $script:jobIds[0] -eq 'job-del')
Reset-Api
Answer 'DELETE /vms/gone' $null 404
Remove-ConstructVm -Name 'gone'
ok "remove: a missing VM is a NO-OP (the desired end state already holds)" ($script:jobIds.Count -eq 0)
Reset-Api
Answer 'DELETE /vms/x' $null 403
ok "remove: any other refusal throws" (Test-Throws { Remove-ConstructVm -Name 'x' })

# ── (i) Power ───────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Power ===" -ForegroundColor Cyan
foreach ($pair in @(@('Start-ConstructVm', 'start'), @('Stop-ConstructVm', 'stop'), @('Save-ConstructVm', 'save'))) {
    Reset-Api
    Answer 'POST /vms/work-vm/power' ([pscustomobject]@{ state = 'running' }) 200
    & $pair[0] -Name 'work-vm'
    ok "power: $($pair[0]) posts action '$($pair[1])'" `
        ($script:calls.Count -eq 1 -and $script:calls[0].Method -eq 'POST' -and
         $script:calls[0].Path -eq '/vms/work-vm/power' -and $script:calls[0].Body['action'] -eq $pair[1])
}
Reset-Api
Answer 'POST /vms/work-vm/power' ([pscustomobject]@{ state = 'off' }) 200
Stop-ConstructVm -Name 'work-vm' -TurnOff -Force
ok "power: -TurnOff/-Force are accepted for contract compatibility, and change nothing" ($script:calls[0].Body['action'] -eq 'stop')

# ── (j) Prerequisites + install media ───────────────────────────────────────
Write-Host ""
Write-Host "=== Prereqs and install media ===" -ForegroundColor Cyan
Reset-Api
Answer 'GET /whoami' ([pscustomobject]@{ name = 'DOMAIN\alice'; known = $true; role = 'user' }) 200
ok "prereqs: a working credential is `$true" ((Test-ConstructDriverPrereqs) -eq $true)
Reset-Api
Answer 'GET /whoami' $null 401
ok "prereqs: a refused credential is `$false, and never throws" ((Test-ConstructDriverPrereqs) -eq $false)
Reset-Api
Answer 'GET /whoami' $null 0
ok "prereqs: an unreachable service is `$false too" ((Test-ConstructDriverPrereqs) -eq $false)
Reset-Api
Answer 'GET /whoami' ([pscustomobject]@{ name = 'DOMAIN\bob'; known = $false }) 200
$notEnrolled = Get-ThrowMessage { Ensure-ConstructDriverPrereqs }
ok "prereqs: Ensure- distinguishes 'authenticated but not enrolled'" ($notEnrolled -match 'not enrolled')
ok "prereqs: ...and names who the service thinks we are" ($notEnrolled -match 'DOMAIN\\bob')
Reset-Api
Answer 'GET /whoami' ([pscustomobject]@{ name = 'a'; known = $true }) 200
ok "prereqs: Ensure- accepts an enrolled identity" (-not (Test-Throws { Ensure-ConstructDriverPrereqs }))

Reset-Api
Detach-ConstructInstallMedia -Name 'work-vm'
ok "media: Detach is a no-op -- the service already did it" ($script:calls.Count -eq 0)

# ── (k) An unconfigured driver refuses, with guidance ───────────────────────
Write-Host ""
Write-Host "=== Missing service context ===" -ForegroundColor Cyan
[void](Set-ConstructDriverContext -ServiceUrl $SVC -Auth $auth)   # (restored below)
$script:ConstructRemoteServiceUrl = ""
$noCtx = Get-ThrowMessage { Get-ConstructVmState -Name 'x' }
ok "context: an unconfigured driver refuses rather than guessing a host" ($noCtx -ne "")
ok "context: ...and names service.url as the fix" ($noCtx -match 'service\.url')
ok "context: Test-ConstructDriverPrereqs is `$false without a service, and never throws" ((Test-ConstructDriverPrereqs) -eq $false)
[void](Set-ConstructDriverContext -ServiceUrl $SVC -Auth $auth)
ok "context: it can be re-pointed at a service" ((Get-ConstructDriverContext).ServiceUrl -eq $SVC)

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "  $($script:pass) passed, $($script:fail) failed" -ForegroundColor $(if ($script:fail -gt 0) { "Red" } else { "Green" })
Write-Host "==============================" -ForegroundColor Cyan
if ($script:fail -gt 0) { exit 1 }
exit 0
