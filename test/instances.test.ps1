#Requires -Version 5.1
<#
    Unit tests for the B3 instance registry (lib/AgentVm.Instances.ps1). Run:

        pwsh -NoProfile -File test/instances.test.ps1

    Self-contained: no Hyper-V, no network, no %LOCALAPPDATA% dependency (every
    registry read/write is pointed at a temp file with -Path).
#>
$ErrorActionPreference = "Stop"

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here
$libPath  = Join-Path $repoRoot "lib/AgentVm.Instances.ps1"

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

# ── (a) Parser check ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Parser check ===" -ForegroundColor Cyan
$errors = $null
$null = [System.Management.Automation.Language.Parser]::ParseFile($libPath, [ref]$null, [ref]$errors)
ok "parse: lib/AgentVm.Instances.ps1 has zero errors" ($errors.Count -eq 0)
foreach ($e in $errors) { Write-Host "    ERROR: $($e.Message) (line $($e.Extent.StartLineNumber))" -ForegroundColor Red }
if ($errors.Count -gt 0) { exit 1 }

. $libPath

$tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("construct-instances-test-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmpRoot -Force | Out-Null
function New-RegistryFile([string]$Text) {
    $p = Join-Path $tmpRoot ("reg-" + [Guid]::NewGuid().ToString("N") + ".json")
    [System.IO.File]::WriteAllText($p, $Text)
    return $p
}

try {

# ── (b) Path resolution ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Registry path ===" -ForegroundColor Cyan
$p = Get-ConstructInstancesPath
ok "path: ends with The-Construct\instances.json" ($p -match '[\\/]The-Construct[\\/]instances\.json$')
ok "path: sits beside the config dir (same parent)" (
    (Split-Path -Parent $p) -eq (Split-Path -Parent (Join-Path (Split-Path -Parent $p) "config")))

# ── (c) The synthesized default -- the ZERO-CHANGE bar ──────────────────────
Write-Host ""
Write-Host "=== Default instance synthesis (zero-change path) ===" -ForegroundColor Cyan
$missing = Join-Path $tmpRoot "does-not-exist.json"
$reg = Read-ConstructInstances -Path $missing
ok "missing file: does not throw and reports Exists=false" ($reg.Exists -eq $false)
ok "missing file: no problems reported (absence is normal)" (@($reg.Problems).Count -eq 0)
ok "missing file: nothing was written" (-not (Test-Path -LiteralPath $missing))
ok "missing file: exactly one instance" ($reg.Instances.Keys.Count -eq 1)
ok "missing file: default is agent-vm" ($reg.Default -eq 'agent-vm')

$d = Get-ConstructInstance -Registry $reg
ok "default: Name        = agent-vm"             ($d.Name -eq 'agent-vm')
ok "default: Backend     = hyperv-local"         ($d.Backend -eq 'hyperv-local')
ok "default: VmName      = Agent-VM"             ($d.VmName -eq 'Agent-VM')
ok "default: VmHost      = agent-vm.mshome.net"  ($d.VmHost -eq 'agent-vm.mshome.net')
ok "default: SshPort     = 22"                   ($d.SshPort -eq 22)
ok "default: HostAlias   = agent-vm"             ($d.HostAlias -eq 'agent-vm')
ok "default: KeyName     = agent_vm_ed25519"     ($d.KeyName -eq 'agent_vm_ed25519')
ok "default: ConfigBranch= vm"                   ($d.ConfigBranch -eq 'vm')
ok "default: ScriptsDir  = null (newest install)" ($null -eq $d.ScriptsDir)
ok "default: passes Test-ConstructDefaultInstance" (Test-ConstructDefaultInstance $d)
ok "null instance also counts as the default"     (Test-ConstructDefaultInstance $null)

# ── (d) Name validation ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Instance names ===" -ForegroundColor Cyan
ok "name: agent-vm ok"        (Test-ConstructInstanceName 'agent-vm')
ok "name: a ok"               (Test-ConstructInstanceName 'a')
ok "name: 9lives ok"          (Test-ConstructInstanceName '9lives')
ok "name: 40 chars ok"        (Test-ConstructInstanceName ('a' * 40))
ok "name: 41 chars rejected"  (-not (Test-ConstructInstanceName ('a' * 41)))
ok "name: uppercase rejected" (-not (Test-ConstructInstanceName 'Work-VM'))
ok "name: underscore rejected" (-not (Test-ConstructInstanceName 'work_vm'))
ok "name: leading dash rejected" (-not (Test-ConstructInstanceName '-work'))
ok "name: dot rejected"       (-not (Test-ConstructInstanceName 'work.vm'))
ok "name: slash rejected"     (-not (Test-ConstructInstanceName 'work/vm'))
ok "name: empty rejected"     (-not (Test-ConstructInstanceName ''))

# ── (e) Derivation for a NON-default instance ───────────────────────────────
Write-Host ""
Write-Host "=== Derived defaults (non-default instance) ===" -ForegroundColor Cyan
$w = Resolve-ConstructInstanceDefaults -Name 'work-vm' -Entry $null
ok "derived: Backend      = hyperv-local"            ($w.Backend -eq 'hyperv-local')
ok "derived: VmName       = work-vm"                 ($w.VmName -eq 'work-vm')
ok "derived: VmHost       = work-vm.mshome.net"      ($w.VmHost -eq 'work-vm.mshome.net')
ok "derived: SshPort      = 22"                      ($w.SshPort -eq 22)
ok "derived: HostAlias    = work-vm (BARE name)"     ($w.HostAlias -eq 'work-vm')
ok "derived: KeyName      = construct_work-vm_ed25519" ($w.KeyName -eq 'construct_work-vm_ed25519')
ok "derived: ConfigBranch = vm-work-vm (never vm/x)" ($w.ConfigBranch -eq 'vm-work-vm')
ok "derived: ConfigBranch has no slash"              ($w.ConfigBranch -notmatch '/')
ok "derived: is NOT the default instance"            (-not (Test-ConstructDefaultInstance $w))

# Explicit fields win over the derivation.
$entry = [pscustomobject]@{
    backend = 'hyperv-remote'; vmName = 'BuildBox-3'
    sshHost = 'buildbox.example.local'; sshPort = 2201
    hostAlias = 'custom-alias'; keyName = 'custom_key'; configBranch = 'branch-x'
    scriptsDir = 'C:\tools\construct'
    service = [pscustomobject]@{ url = 'https://buildbox:7462'; auth = 'negotiate' }
    owner = 'DOMAIN\christoph'
}
$r = Resolve-ConstructInstanceDefaults -Name 'work-vm' -Entry $entry
ok "explicit: backend honoured"      ($r.Backend -eq 'hyperv-remote')
ok "explicit: vmName honoured"       ($r.VmName -eq 'BuildBox-3')
ok "explicit: sshHost -> VmHost"     ($r.VmHost -eq 'buildbox.example.local')
ok "explicit: sshPort honoured"      ($r.SshPort -eq 2201)
ok "explicit: hostAlias honoured"    ($r.HostAlias -eq 'custom-alias')
ok "explicit: keyName honoured"      ($r.KeyName -eq 'custom_key')
ok "explicit: configBranch honoured" ($r.ConfigBranch -eq 'branch-x')
ok "explicit: scriptsDir honoured"   ($r.ScriptsDir -eq 'C:\tools\construct')
ok "explicit: service url honoured"  ($r.Service.Url -eq 'https://buildbox:7462')
ok "explicit: service auth honoured" ($r.Service.Auth -eq 'negotiate')
ok "explicit: owner honoured"        ($r.Owner -eq 'DOMAIN\christoph')

# An explicitly-spelled-out agent-vm with today's values is still THE DEFAULT.
$spelled = Resolve-ConstructInstanceDefaults -Name 'agent-vm' -Entry ([pscustomobject]@{
    backend = 'hyperv-local'; vmName = 'Agent-VM'; sshHost = 'agent-vm.mshome.net'; sshPort = 22
})
ok "spelled-out agent-vm still counts as the default" (Test-ConstructDefaultInstance $spelled)
# ...but changing ANY targeting field makes it non-default.
$moved = Resolve-ConstructInstanceDefaults -Name 'agent-vm' -Entry ([pscustomobject]@{ sshPort = 2222 })
ok "agent-vm on a different port is NOT the default" (-not (Test-ConstructDefaultInstance $moved))

# ── (f) Parsing a real registry ─────────────────────────────────────────────
Write-Host ""
Write-Host "=== Registry parsing ===" -ForegroundColor Cyan
$file = New-RegistryFile @'
{
  "version": 1,
  "defaultInstance": "work-vm",
  "instances": {
    "agent-vm": { "backend": "hyperv-local", "vmName": "Agent-VM", "sshHost": "agent-vm.mshome.net", "sshPort": 22 },
    "work-vm":  { "backend": "hyperv-remote", "vmName": "work-vm", "sshHost": "buildbox.example.local", "sshPort": 2201,
                  "service": { "url": "https://buildbox.example.local:7462", "auth": "negotiate" },
                  "owner": "DOMAIN\\christoph" }
  }
}
'@
$reg2 = Read-ConstructInstances -Path $file
ok "parse: two instances"            ($reg2.Instances.Keys.Count -eq 2)
ok "parse: no problems"              (@($reg2.Problems).Count -eq 0)
ok "parse: Exists = true"            ($reg2.Exists -eq $true)
ok "parse: defaultInstance honoured" ($reg2.Default -eq 'work-vm')
$wv = Get-ConstructInstance -Name 'work-vm' -Registry $reg2
ok "parse: work-vm host"    ($wv.VmHost -eq 'buildbox.example.local')
ok "parse: work-vm port"    ($wv.SshPort -eq 2201)
ok "parse: work-vm alias derived as the bare name" ($wv.HostAlias -eq 'work-vm')
ok "parse: work-vm key derived"    ($wv.KeyName -eq 'construct_work-vm_ed25519')
ok "parse: work-vm branch derived" ($wv.ConfigBranch -eq 'vm-work-vm')
ok "parse: unknown name falls back to the registry default" ((Get-ConstructInstance -Name 'nope' -Registry $reg2).Name -eq 'work-vm')
ok "parse: empty name falls back to the registry default"   ((Get-ConstructInstance -Name '' -Registry $reg2).Name -eq 'work-vm')

# ── (g) Malformed input NEVER throws and always yields the default ──────────
Write-Host ""
Write-Host "=== Malformed registries degrade to the default ===" -ForegroundColor Cyan
$bad = @(
    @{ label = 'not JSON';        text = 'this is not json {' },
    @{ label = 'a JSON array';    text = '[1,2,3]' },
    @{ label = 'empty file';      text = '' },
    @{ label = 'instances not an object'; text = '{"version":1,"instances":"nope"}' }
)
foreach ($case in $bad) {
    $f = New-RegistryFile $case.text
    $threw = $false
    $rr = $null
    try { $rr = Read-ConstructInstances -Path $f } catch { $threw = $true }
    ok "malformed ($($case.label)): does not throw" (-not $threw)
    if (-not $threw) {
        ok "malformed ($($case.label)): default instance available" ((Get-ConstructInstance -Registry $rr).Name -eq 'agent-vm')
        ok "malformed ($($case.label)): default is byte-identical to today" (Test-ConstructDefaultInstance (Get-ConstructInstance -Registry $rr))
        if ($case.label -ne 'empty file') {
            ok "malformed ($($case.label)): reports a problem" (@($rr.Problems).Count -ge 1)
        }
    }
}

# Invalid entries are skipped with a problem; the good ones survive.
$f2 = New-RegistryFile @'
{ "version": 1, "defaultInstance": "ghost",
  "instances": {
    "Work_VM": { "backend": "hyperv-local" },
    "good-vm": { "backend": "martian", "sshPort": 99999 },
    "remote-vm": { "backend": "hyperv-remote" }
  } }
'@
$reg3 = Read-ConstructInstances -Path $f2
ok "invalid name is skipped"                 (-not $reg3.Instances.ContainsKey('Work_VM'))
ok "valid sibling survives"                  ($reg3.Instances.ContainsKey('good-vm'))
ok "unknown backend falls back"              ($reg3.Instances['good-vm'].Backend -eq 'hyperv-local')
ok "invalid port falls back to 22"           ($reg3.Instances['good-vm'].SshPort -eq 22)
ok "dangling defaultInstance -> agent-vm"    ($reg3.Default -eq 'agent-vm')
ok "agent-vm synthesized alongside"          ($reg3.Instances.ContainsKey('agent-vm'))
ok "problem: invalid name reported"          (@($reg3.Problems | Where-Object { $_ -match 'Work_VM' }).Count -ge 1)
ok "problem: unknown backend reported"       (@($reg3.Problems | Where-Object { $_ -match 'martian' }).Count -ge 1)
ok "problem: invalid sshPort reported"       (@($reg3.Problems | Where-Object { $_ -match 'sshPort' }).Count -ge 1)
ok "problem: dangling default reported"      (@($reg3.Problems | Where-Object { $_ -match 'ghost' }).Count -ge 1)
ok "problem: remote without sshHost reported" (@($reg3.Problems | Where-Object { $_ -match 'no sshHost' }).Count -ge 1)

# A foreign schema version is REFUSED, not partially read: a later version may redefine
# what a field MEANS, so acting on a misread entry could target the wrong machine.
$f3 = New-RegistryFile '{ "version": 99, "instances": { "later-vm": { "backend": "hyperv-local" } } }'
$reg4 = Read-ConstructInstances -Path $f3
ok "future version: entries are NOT consumed"    (-not $reg4.Instances.ContainsKey('later-vm'))
ok "future version: only the default remains"    ($reg4.Instances.Keys.Count -eq 1)
ok "future version: the default is byte-identical" (Test-ConstructDefaultInstance (Get-ConstructInstance -Registry $reg4))
ok "future version: reported as a problem"       (@($reg4.Problems | Where-Object { $_ -match 'version' }).Count -ge 1)
$f3b = New-RegistryFile '{ "version": 2, "defaultInstance": "later-vm", "instances": { "later-vm": {} } }'
$reg4b = Read-ConstructInstances -Path $f3b
ok "version 2: defaultInstance pointer ignored too" ($reg4b.Default -eq 'agent-vm')
# An ABSENT version is still read as v1 (hand-written files routinely omit it).
$f3c = New-RegistryFile '{ "instances": { "later-vm": { "backend": "hyperv-local" } } }'
ok "absent version: still read as v1" ((Read-ConstructInstances -Path $f3c).Instances.ContainsKey('later-vm'))

# ── (g2) JS/PS NORMALIZATION PARITY MATRIX ──────────────────────────────────
# Both readers must normalize the SAME malformed input to the SAME instance and the
# SAME problems. extension/test/instances.test.js runs this identical matrix; the two
# lists are kept in step by hand, so change them together.
Write-Host ""
Write-Host "=== normalization parity (mirrored in extension/test/instances.test.js) ===" -ForegroundColor Cyan
$mx = New-RegistryFile @'
{ "version": 1,
  "instances": {
    "typed-vm": { "backend": "HYPERV-REMOTE", "sshHost": 123, "sshPort": "2201",
                  "hostAlias": true, "keyName": 42, "configBranch": ["x"], "owner": 7,
                  "service": { "url": "https://x", "auth": "TOKEN" } },
    "svc-vm":   { "service": "not-an-object" },
    "port-vm":  { "sshPort": "+2201" }
  } }
'@
$mxReg = Read-ConstructInstances -Path $mx
$tv = $mxReg.Instances['typed-vm']
ok "parity: uppercase backend rejected (case-sensitive)" ($tv.Backend -eq 'hyperv-local')
ok "parity: numeric sshHost NOT stringified"             ($tv.VmHost -eq 'typed-vm.mshome.net')
ok "parity: digit-string sshPort accepted"               ($tv.SshPort -eq 2201)
ok "parity: boolean hostAlias -> derived bare name"      ($tv.HostAlias -eq 'typed-vm')
ok "parity: numeric keyName -> derived"                  ($tv.KeyName -eq 'construct_typed-vm_ed25519')
ok "parity: array configBranch -> derived"               ($tv.ConfigBranch -eq 'vm-typed-vm')
ok "parity: numeric owner -> null"                       ($null -eq $tv.Owner)
ok "parity: uppercase service auth -> negotiate"         ($tv.Service.Auth -eq 'negotiate')
ok "parity: scalar service ignored"                      ($null -eq $mxReg.Instances['svc-vm'].Service)
ok "parity: '+2201' rejected -> 22"                      ($mxReg.Instances['port-vm'].SshPort -eq 22)
foreach ($f in @('sshHost', 'hostAlias', 'keyName', 'configBranch', 'owner')) {
    ok "parity: type problem reported for '$f'" (@($mxReg.Problems | Where-Object { $_ -match "'$f' must be a string" }).Count -ge 1)
}
ok "parity: uppercase backend reported"     (@($mxReg.Problems | Where-Object { $_ -match 'HYPERV-REMOTE' }).Count -ge 1)
ok "parity: uppercase auth reported"        (@($mxReg.Problems | Where-Object { $_ -match 'service auth' }).Count -ge 1)
ok "parity: scalar service reported"        (@($mxReg.Problems | Where-Object { $_ -match "'service' must be an object" }).Count -ge 1)
ok "parity: bad port reported"              (@($mxReg.Problems | Where-Object { $_ -match 'invalid sshPort' }).Count -ge 1)

# Port boundaries + numbers no Int32 can hold. A huge sshPort must be REPORTED and fall
# back to 22, never crash the reader: [int]999999999999 throws "Value was either too
# large or too small for an Int32", which would escape Read-ConstructInstances and break
# the "never throws" contract the whole zero-change path rests on.
$portCases = @(
    @{ lit = '999999999999';  want = 22;    problem = $true },
    @{ lit = '-999999999999'; want = 22;    problem = $true },
    @{ lit = '1e20';          want = 22;    problem = $true },
    @{ lit = '1';             want = 1;     problem = $false },
    @{ lit = '65535';         want = 65535; problem = $false },
    @{ lit = '0';             want = 22;    problem = $true },
    @{ lit = '65536';         want = 22;    problem = $true },
    @{ lit = '2201.5';        want = 22;    problem = $true },
    @{ lit = 'true';          want = 22;    problem = $true },
    @{ lit = '"2201"';        want = 2201;  problem = $false },
    @{ lit = '"+2201"';       want = 22;    problem = $true },
    @{ lit = '" 2201 "';      want = 2201;  problem = $false },
    @{ lit = '"0"';           want = 22;    problem = $true },
    @{ lit = '"99999"';       want = 22;    problem = $true }
)
foreach ($pc in $portCases) {
    $pf = New-RegistryFile ('{"version":1,"instances":{"p-vm":{"sshPort":' + $pc.lit + '}}}')
    $threw = $false
    $pr = $null
    try { $pr = Read-ConstructInstances -Path $pf } catch { $threw = $true }
    ok "port $($pc.lit): does not throw" (-not $threw)
    if (-not $threw) {
        ok "port $($pc.lit): normalizes to $($pc.want)" ($pr.Instances['p-vm'].SshPort -eq $pc.want)
        $reported = @($pr.Problems | Where-Object { $_ -match 'invalid sshPort' }).Count -ge 1
        ok "port $($pc.lit): problem reported = $($pc.problem)" ($reported -eq $pc.problem)
    }
}

# Test-ConstructDefaultInstance is case-SENSITIVE (-ceq here, === in JS): an explicitly
# cased field is a DIFFERENT instance, and the two readers must agree or one side would
# emit target arguments the other omits.
ok "case: vmName 'agent-vm' is NOT the default (Agent-VM is)" (
    -not (Test-ConstructDefaultInstance (Resolve-ConstructInstanceDefaults -Name 'agent-vm' -Entry ([pscustomobject]@{ vmName = 'agent-vm' }))))
ok "case: vmHost 'AGENT-VM.mshome.net' is NOT the default" (
    -not (Test-ConstructDefaultInstance (Resolve-ConstructInstanceDefaults -Name 'agent-vm' -Entry ([pscustomobject]@{ sshHost = 'AGENT-VM.mshome.net' }))))
ok "case: keyName 'Agent_VM_ed25519' is NOT the default" (
    -not (Test-ConstructDefaultInstance (Resolve-ConstructInstanceDefaults -Name 'agent-vm' -Entry ([pscustomobject]@{ keyName = 'Agent_VM_ed25519' }))))

# Scalar top levels are malformed files, not empty registries -- they MUST report.
foreach ($scalar in @('0', 'false', 'null', '"hello"', '123')) {
    $sf = New-RegistryFile $scalar
    $sr = Read-ConstructInstances -Path $sf
    ok "parity: top-level $scalar reports a problem" (@($sr.Problems).Count -ge 1)
    ok "parity: top-level $scalar yields the default" (Test-ConstructDefaultInstance (Get-ConstructInstance -Registry $sr))
}

# ── (h) Atomic save round-trip ──────────────────────────────────────────────
Write-Host ""
Write-Host "=== Save (atomic) ===" -ForegroundColor Cyan
$out = Join-Path $tmpRoot "sub/dir/instances.json"
$regW = Read-ConstructInstances -Path $out
$regW.Instances['work-vm'] = Resolve-ConstructInstanceDefaults -Name 'work-vm' -Entry ([pscustomobject]@{ sshPort = 2201; sshHost = 'buildbox.local' })
Save-ConstructInstances -Registry $regW -Path $out | Out-Null
ok "save: creates the containing directory" (Test-Path -LiteralPath $out)
ok "save: leaves no temp file behind" (@(Get-ChildItem -LiteralPath (Split-Path -Parent $out) -Filter "*.tmp.*" -ErrorAction SilentlyContinue).Count -eq 0)
$back = Read-ConstructInstances -Path $out
ok "round-trip: both instances present" ($back.Instances.Keys.Count -eq 2)
ok "round-trip: work-vm port preserved" ($back.Instances['work-vm'].SshPort -eq 2201)
ok "round-trip: work-vm host preserved" ($back.Instances['work-vm'].VmHost -eq 'buildbox.local')
ok "round-trip: default instance unchanged" (Test-ConstructDefaultInstance $back.Instances['agent-vm'])
ok "round-trip: no problems" (@($back.Problems).Count -eq 0)
$txt = [System.IO.File]::ReadAllText($out)
ok "save: writes schema version 1" ($txt -match '"version"\s*:\s*1')
ok "save: no UTF-8 BOM" (-not $txt.StartsWith([char]0xFEFF))

} finally {
    Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "instance-registry tests -- $script:pass/$($script:pass + $script:fail) passed" -ForegroundColor $(if ($script:fail) { "Red" } else { "Green" })
if ($script:fail -gt 0) { exit 1 }
exit 0
