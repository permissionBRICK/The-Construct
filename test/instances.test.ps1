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
# An unknown backend is REPORTED but kept VERBATIM. Rewriting it to 'hyperv-local'
# (which this reader used to do) promoted every typo to destructive local Hyper-V access.
ok "unknown backend is kept as written, never promoted" ($reg3.Instances['good-vm'].Backend -ceq 'martian')
ok "invalid port falls back to 22"           ($reg3.Instances['good-vm'].SshPort -eq 22)
ok "dangling defaultInstance -> agent-vm"    ($reg3.Default -eq 'agent-vm')
ok "agent-vm synthesized alongside"          ($reg3.Instances.ContainsKey('agent-vm'))
ok "problem: invalid name reported"          (@($reg3.Problems | Where-Object { $_ -match 'Work_VM' }).Count -ge 1)
ok "problem: unknown backend reported"       (@($reg3.Problems | Where-Object { $_ -match 'martian' }).Count -ge 1)
ok "problem: invalid sshPort reported"       (@($reg3.Problems | Where-Object { $_ -match 'sshPort' }).Count -ge 1)
ok "problem: dangling default reported"      (@($reg3.Problems | Where-Object { $_ -match 'ghost' }).Count -ge 1)
ok "problem: remote without sshHost reported" (@($reg3.Problems | Where-Object { $_ -match 'no sshHost' }).Count -ge 1)
# ...and SKIPPED, not left actionable with the derived <name>.mshome.net address (a
# remote endpoint only ever comes from the host service).
ok "remote without sshHost is skipped"       (-not $reg3.Instances.ContainsKey('remote-vm'))

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
# The version must be a JSON NUMBER: a QUOTED "1" is a foreign schema. This reader used
# to compare the two operands as STRINGS and load the file, while instances.js refused
# it outright -- the same bytes then selected work-vm here and agent-vm there, which is
# exactly what the shared-reader contract forbids. Mirrored in
# extension/test/instances.test.js; change them together.
$f3d = New-RegistryFile '{ "version": "1", "instances": { "work-vm": { "backend": "hyperv-local" } } }'
$reg4c = Read-ConstructInstances -Path $f3d
ok "quoted version: a string '1' is NOT version 1" (-not $reg4c.Instances.ContainsKey('work-vm'))
ok "quoted version: only the default remains"    ($reg4c.Instances.Keys.Count -eq 1)
ok "quoted version: the default is byte-identical" (Test-ConstructDefaultInstance (Get-ConstructInstance -Registry $reg4c))
ok "quoted version: reported as a problem"       (@($reg4c.Problems | Where-Object { $_ -match 'version' }).Count -ge 1)
ok "quoted version: resolving the named instance falls back to the default" (
    Test-ConstructDefaultInstance (Get-ConstructInstance -Registry $reg4c -Name 'work-vm'))
# ...and the numeric spellings JSON considers the same number ARE version 1.
ok "numeric version: 1.0 is version 1" (
    (Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1.0, "instances": { "work-vm": { "backend": "hyperv-local" } } }')).Instances.ContainsKey('work-vm'))
ok "boolean version: true is NOT version 1" (
    -not (Read-ConstructInstances -Path (New-RegistryFile '{ "version": true, "instances": { "work-vm": { "backend": "hyperv-local" } } }')).Instances.ContainsKey('work-vm'))

# ── (g2) JS/PS NORMALIZATION PARITY MATRIX ──────────────────────────────────
# Both readers must normalize the SAME malformed input to the SAME instance and the
# SAME problems. extension/test/instances.test.js runs this identical matrix; the two
# lists are kept in step by hand, so change them together.
Write-Host ""
Write-Host "=== normalization parity (mirrored in extension/test/instances.test.js) ===" -ForegroundColor Cyan
# 'martian-remote' is an UNKNOWN backend on purpose: it is neither local (so the
# canonical-identity rule does not apply) nor remote (so the endpoint/vmName rules do
# not), which leaves exactly the field-by-field TYPE normalisation on show. The uppercase
# spelling of a KNOWN backend has its own fixture below, because a spelling the driver
# lookup resolves to the remote driver is held to that backend's rules.
$mx = New-RegistryFile @'
{ "version": 1,
  "instances": {
    "typed-vm": { "backend": "martian-remote", "sshHost": 123, "sshPort": "2201",
                  "hostAlias": true, "keyName": 42, "configBranch": ["x"], "owner": 7,
                  "service": { "url": "https://x", "auth": "TOKEN" } },
    "svc-vm":   { "service": "not-an-object" },
    "port-vm":  { "sshPort": "+2201" }
  } }
'@
$mxReg = Read-ConstructInstances -Path $mx
$tv = $mxReg.Instances['typed-vm']
ok "parity: unknown backend kept verbatim"               ($tv.Backend -ceq 'martian-remote')
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
ok "parity: unknown backend reported"       (@($mxReg.Problems | Where-Object { $_ -match 'martian-remote' }).Count -ge 1)
ok "parity: uppercase auth reported"        (@($mxReg.Problems | Where-Object { $_ -match 'service auth' }).Count -ge 1)
ok "parity: scalar service reported"        (@($mxReg.Problems | Where-Object { $_ -match "'service' must be an object" }).Count -ge 1)
ok "parity: bad port reported"              (@($mxReg.Problems | Where-Object { $_ -match 'invalid sshPort' }).Count -ge 1)

# A CASE-VARIANT of a known backend id does not load AT ALL, for either backend. Every
# enum comparison in both readers is case-sensitive (so 'HYPERV-REMOTE' is "unknown" to
# them), while the driver lookup trims and lowercases (so it hands back the REAL driver --
# the remote one here, the local one with hostLifecycle for 'HYPERV-LOCAL'). The two
# readings disagree about what the entry IS, so nothing acts on it under either.
foreach ($c in @(@{ label = 'remote'; id = 'HYPERV-REMOTE' },
                 @{ label = 'local';  id = 'HYPERV-LOCAL' },
                 @{ label = 'mixed';  id = 'Hyperv-Remote' })) {
    $cased = Read-ConstructInstances -Path (New-RegistryFile (
        '{"version":1,"instances":{"cased-vm":{"backend":"' + $c.id + '","sshHost":"buildbox.local","sshPort":2201}}}'))
    ok "parity: a case-variant backend id ($($c.label)) does not load" (-not $cased.Instances.ContainsKey('cased-vm'))
    ok "parity: ...reported as a spelling the two lookups read differently ($($c.label))" (
        @($cased.Problems | Where-Object { $_ -cmatch [regex]::Escape($c.id) -and $_ -match 'case-sensitive' -and $_ -match 'skipped' }).Count -ge 1)
    ok "parity: ...naming the canonical spelling ($($c.label))" (
        @($cased.Problems | Where-Object { $_ -match [regex]::Escape("is not spelled '$($c.id.ToLowerInvariant())'") }).Count -ge 1)
}
# ...while a genuinely unknown id IS kept: the driver lookup finds nothing for it under
# any casing, so the unknown-driver fallback is what acts on it.
ok "parity: an unknown backend still loads and is merely reported" (
    (Read-ConstructInstances -Path (New-RegistryFile `
        '{"version":1,"instances":{"cased-vm":{"backend":"proxmox","sshHost":"buildbox.local"}}}')).Instances.ContainsKey('cased-vm'))

# ── (g2b) KEY-CASING PARITY (mirrored in extension/test/instances.test.js) ──
# JSON property lookup is case-SENSITIVE in JavaScript and USED TO BE case-INSENSITIVE
# here (PSObject.Properties[$name]), so ONE registry's bytes aimed the two readers at
# DIFFERENT VMs: { "VERSION":1, "DEFAULTINSTANCE":"x", "INSTANCES":{...} } was ignored by
# the extension (agent-vm) and loaded here -- with 'x' as the DEFAULT instance -- while a
# wrong-cased 'BACKEND'/'SSHHOST' inside an entry turned a derived hyperv-local instance
# into a remote one on this side only. Both readers now do an ORDINAL, exact-case lookup
# for every top-level and nested schema field, so a wrong-cased key is simply ABSENT:
# never a value, and never a "must be a string" problem either.
# These strings are the EXACT bytes extension/test/instances.test.js feeds inst.load() --
# change the two lists together. (Deliberately no fixture spelling the SAME key twice in
# two casings: ConvertFrom-Json itself refuses such a document on PowerShell 6+, so this
# reader degrades to "not valid JSON" + the default instance -- fail-closed, not a target
# disagreement, and not fixable in a 5.1-compatible way.)
Write-Host ""
Write-Host "=== key-casing parity (mirrored in extension/test/instances.test.js) ===" -ForegroundColor Cyan
$caseFixtures = [ordered]@{
    'upper-top'    = '{"VERSION":1,"DEFAULTINSTANCE":"work-vm","INSTANCES":{"work-vm":{"backend":"hyperv-local"}}}'
    'mixed-top'    = '{"Version":1,"DefaultInstance":"work-vm","Instances":{"work-vm":{"backend":"hyperv-local"}}}'
    'upper-nested' = '{"version":1,"instances":{"work-vm":{"BACKEND":"hyperv-remote","SSHHOST":"buildbox.local",' +
                     '"SSHPORT":2201,"HOSTALIAS":"boxy","KEYNAME":"custom_key","CONFIGBRANCH":"branch-x",' +
                     '"SCRIPTSDIR":"C:/tools","OWNER":"someone","SERVICE":{"url":"https://x"}}}}'
    'mixed-nested' = '{"version":1,"instances":{"work-vm":{"Backend":"hyperv-remote","SshHost":"buildbox.local",' +
                     '"VmName":"BuildBox","SshPort":"2201"}}}'
    'upper-badtype' = '{"version":1,"instances":{"work-vm":{"SSHHOST":123,"KEYNAME":42}}}'
}
# Wrong-cased TOP-LEVEL keys: no version, no instances, no defaultInstance -- i.e. the
# zero-change default, silently.
foreach ($k in @('upper-top', 'mixed-top')) {
    $cr = Read-ConstructInstances -Path (New-RegistryFile $caseFixtures[$k])
    ok "casing ($k): the wrong-cased 'instances' bag is not read" (-not $cr.Instances.ContainsKey('work-vm'))
    ok "casing ($k): only the default instance loads"             ($cr.Instances.Keys.Count -eq 1)
    ok "casing ($k): the wrong-cased defaultInstance pointer is ignored" ($cr.Default -eq 'agent-vm')
    ok "casing ($k): the default is byte-identical to today" (Test-ConstructDefaultInstance (Get-ConstructInstance -Registry $cr))
    ok "casing ($k): silent -- an absent key is not a malformed file" (@($cr.Problems).Count -eq 0)
}
# Wrong-cased ENTRY fields: the entry loads, but every field is DERIVED (which is also
# what makes it a canonical hyperv-local instance rather than a skipped one).
foreach ($k in @('upper-nested', 'mixed-nested', 'upper-badtype')) {
    $cr = Read-ConstructInstances -Path (New-RegistryFile $caseFixtures[$k])
    ok "casing ($k): the entry itself still loads (its NAME is exact)" ($cr.Instances.ContainsKey('work-vm'))
    if (-not $cr.Instances.ContainsKey('work-vm')) { continue }
    $ce = $cr.Instances['work-vm']
    ok "casing ($k): 'BACKEND' ignored -> derived hyperv-local" ($ce.Backend -ceq 'hyperv-local')
    ok "casing ($k): 'SSHHOST' ignored -> derived host"         ($ce.VmHost -ceq 'work-vm.mshome.net')
    ok "casing ($k): 'SSHPORT' ignored -> 22"                   ($ce.SshPort -eq 22)
    ok "casing ($k): 'HOSTALIAS' ignored -> derived"            ($ce.HostAlias -ceq 'work-vm')
    ok "casing ($k): 'VmName' ignored -> derived"               ($ce.VmName -ceq 'work-vm')
    ok "casing ($k): 'KEYNAME' ignored -> derived"              ($ce.KeyName -ceq 'construct_work-vm_ed25519')
    ok "casing ($k): 'CONFIGBRANCH' ignored -> derived branch"  ($ce.ConfigBranch -ceq 'vm-work-vm')
    ok "casing ($k): 'SCRIPTSDIR' ignored -> null"              ($null -eq $ce.ScriptsDir)
    ok "casing ($k): 'OWNER' ignored -> null"                   ($null -eq $ce.Owner)
    ok "casing ($k): 'SERVICE' ignored -> null"                 ($null -eq $ce.Service)
    ok "casing ($k): a wrong-cased entry is a DEFAULT-behaving local instance" (
        @(Get-ConstructLocalIdentityProblem -Instance $ce).Count -eq 0)
    ok "casing ($k): no problems (not even a type complaint)"   (@($cr.Problems).Count -eq 0)
}

# ── (g3) IDENTITY-FIELD FORMAT RULES (mirrored in extension/test/instances.test.js) ──
# A field of the right TYPE can still be unusable -- or hostile -- once it reaches a
# PowerShell command line, an ssh argv, a key path or a git ref. Such an entry is
# SKIPPED WHOLE (never partially used) and reported. Both readers must skip the SAME
# entries, or the panel and the scripts would disagree about which VMs exist.
Write-Host ""
Write-Host "=== identity-field format rules (mirrored in extension/test/instances.test.js) ===" -ForegroundColor Cyan
$badIdentity = @(
    @{ label = 'vmHost-injection';  json = '{ "sshHost": "-x; Start-Process calc; #" }'; field = 'sshHost' },
    @{ label = 'vmHost-space';      json = '{ "sshHost": "buildbox local" }';            field = 'sshHost' },
    @{ label = 'vmHost-empty-label';json = '{ "sshHost": "buildbox..local" }';           field = 'sshHost' },
    # An EMBEDDED newline is the JS/PS parity trap: .NET's `$` matches just before a
    # final newline where JavaScript's does not, so the rules anchor with \A..\z.
    @{ label = 'vmHost-newline';    json = '{ "sshHost": "buildbox\nlocal" }';           field = 'sshHost' },
    @{ label = 'alias-newline';     json = '{ "hostAlias": "work\nvm" }';                field = 'hostAlias' },
    @{ label = 'branch-newline';    json = '{ "configBranch": "vm\nwork" }';             field = 'configBranch' },
    @{ label = 'alias-path';        json = '{ "hostAlias": "../../etc/passwd" }';        field = 'hostAlias' },
    @{ label = 'alias-space';       json = '{ "hostAlias": "work vm" }';                 field = 'hostAlias' },
    @{ label = 'key-path';          json = '{ "keyName": "..\\\\..\\\\id_rsa" }';        field = 'keyName' },
    @{ label = 'key-slash';         json = '{ "keyName": "sub/dir_ed25519" }';           field = 'keyName' },
    # keyName is a WINDOWS FILE NAME (~\.ssh\<KeyName>), not just a token: Win32 strips a
    # trailing dot (so this would write over the DEFAULT instance's key file), and a device
    # stem is not a creatable file at all -- provisioning would fail with the VM already
    # built. HostAlias keeps the plain token rule; an ssh alias is not a path.
    @{ label = 'key-trailing-dot';  json = '{ "keyName": "agent_vm_ed25519." }';         field = 'keyName' },
    @{ label = 'key-device-con';    json = '{ "keyName": "CON" }';                       field = 'keyName' },
    @{ label = 'key-device-lower';  json = '{ "keyName": "con" }';                       field = 'keyName' },
    @{ label = 'key-device-nul';    json = '{ "keyName": "NUL" }';                       field = 'keyName' },
    @{ label = 'key-device-com1';   json = '{ "keyName": "COM1" }';                      field = 'keyName' },
    @{ label = 'key-device-lpt9';   json = '{ "keyName": "lpt9" }';                      field = 'keyName' },
    @{ label = 'key-device-ext';    json = '{ "keyName": "CON.txt" }';                   field = 'keyName' },
    @{ label = 'key-device-ext2';   json = '{ "keyName": "con.key.txt" }';               field = 'keyName' },
    @{ label = 'vmname-dot';        json = '{ "vmName": "work.vm" }';                    field = 'vmName' },
    @{ label = 'vmname-space';      json = '{ "vmName": "Work VM" }';                    field = 'vmName' },
    @{ label = 'vmname-dash-start'; json = '{ "vmName": "-work" }';                      field = 'vmName' },
    @{ label = 'branch-reserved';   json = '{ "configBranch": "main" }';                 field = 'configBranch' },
    @{ label = 'branch-dotdot';     json = '{ "configBranch": "vm..x" }';                field = 'configBranch' },
    @{ label = 'branch-lock';       json = '{ "configBranch": "vm-x.lock" }';            field = 'configBranch' },
    @{ label = 'branch-case-hijack';json = '{ "configBranch": "VM" }';                   field = 'configBranch' }
)
foreach ($c in $badIdentity) {
    $r = Read-ConstructInstances -Path (New-RegistryFile ('{ "version": 1, "instances": { "bad-vm": ' + $c.json + ' } }'))
    ok "identity($($c.label)): the instance is SKIPPED"        (-not $r.Instances.ContainsKey('bad-vm'))
    ok "identity($($c.label)): the problem names '$($c.field)'" (@($r.Problems | Where-Object { $_ -match [regex]::Escape("`"$($c.field)`"") }).Count -ge 1)
    ok "identity($($c.label)): the problem says skipped"        (@($r.Problems | Where-Object { $_ -match 'skipped' }).Count -ge 1)
}
$skipReg = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "bad-vm": { "sshHost": "no spaces allowed" }, "good-vm": {} } }')
ok "identity: a valid sibling still loads"          ($skipReg.Instances.ContainsKey('good-vm'))
ok "identity: the default instance is still synthesized" (Test-ConstructDefaultInstance $skipReg.Instances['agent-vm'])
$brokenDefault = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "agent-vm": { "sshHost": "-oProxyCommand=calc" } } }')
ok "identity: a broken agent-vm entry falls back to the synthesized default" (Test-ConstructDefaultInstance $brokenDefault.Instances['agent-vm'])
# The shapes that MUST keep working. A free-form ENDPOINT belongs to a non-local backend
# (a hyperv-local instance's identity is pinned to its name -- see the canonical-identity
# block below), so the host/alias/key cases are stated on a remote entry.
foreach ($good in @(
    '{ "backend": "hyperv-remote", "sshHost": "buildbox.example.local" }',
    '{ "backend": "hyperv-remote", "sshHost": "10.0.0.7" }',
    '{ "backend": "hyperv-remote", "sshHost": "host" }',
    '{ "backend": "hyperv-remote", "sshHost": "fe80::1" }',
    '{ "backend": "hyperv-remote", "sshHost": "2001:db8::8a2e:370:7334" }',
    '{ "backend": "hyperv-remote", "sshHost": "buildbox.local", "keyName": "construct_work-vm_ed25519" }',
    '{ "backend": "hyperv-remote", "sshHost": "buildbox.local", "hostAlias": "work-vm.local" }',
    '{ "keyName": "construct_work-vm_ed25519" }',
    '{ "vmName": "Work-VM" }', '{ "configBranch": "vm-work" }', '{ "configBranch": "feature.x_1" }',
    '{ "backend": "hyperv-remote", "sshHost": " buildbox.local\n" }')) {   # both readers TRIM a string field first
    $r = Read-ConstructInstances -Path (New-RegistryFile ('{ "version": 1, "instances": { "work-vm": ' + $good + ' } }'))
    ok "identity: $good is accepted" ($r.Instances.ContainsKey('work-vm'))
}
# IPv6: a character-class regex would wave through '::::', '1::2::3' and friends, so the
# rule shape-filters and then PARSES ([System.Net.IPAddress]::TryParse). The identical
# matrix runs in extension/test/instances.test.js (net.isIP there) -- the two readers
# must agree address for address, or the panel and the scripts would disagree about
# which instances exist.
$ipv6Matrix = @(
    @{ v = '::';                      want = $true },
    @{ v = '::1';                     want = $true },
    @{ v = 'fe80::1';                 want = $true },
    @{ v = '2001:db8::8a2e:370:7334'; want = $true },
    @{ v = '1:2:3:4:5:6:7:8';         want = $true },
    @{ v = '0:0:0:0:0:0:0:0';         want = $true },
    @{ v = '1::';                     want = $true },
    @{ v = '::2';                     want = $true },
    @{ v = '::ffff:10.0.0.1';         want = $true },
    @{ v = '::::';                    want = $false },
    @{ v = '1::2::3';                 want = $false },
    @{ v = '1:2:3:4:5:6:7:8:9';       want = $false },
    @{ v = '1.2.3:4';                 want = $false },
    @{ v = '....:';                   want = $false },
    @{ v = ':::';                     want = $false },
    @{ v = ':1';                      want = $false },
    @{ v = '1:';                      want = $false },
    @{ v = '12345::1';                want = $false },
    @{ v = '::ffff:999.1.1.1';        want = $false },
    @{ v = '::ffff:1.2.3.004';        want = $false },
    # Rejected by SHAPE on both sides, precisely because the two parsers disagree about
    # them: Node accepts the zone id, .NET accepts the brackets.
    @{ v = 'fe80::1%eth0';            want = $false },
    @{ v = '[::1]';                   want = $false }
)
foreach ($c in $ipv6Matrix) {
    ok "ipv6: '$($c.v)' -> $($c.want)"               ((Test-ConstructInstanceIpv6 -Value $c.v) -eq $c.want)
    ok "ipv6: '$($c.v)' as an endpoint -> $($c.want)" ((Test-ConstructInstanceHostEndpoint $c.v) -eq $c.want)
}
foreach ($bad in @('::::', '1::2::3', '1:2:3:4:5:6:7:8:9', '1.2.3:4', '....:', 'fe80::1%eth0')) {
    $r = Read-ConstructInstances -Path (New-RegistryFile ('{ "version": 1, "instances": { "bad-vm": { "sshHost": "' + $bad + '" } } }'))
    ok "ipv6: a bogus literal ($bad) skips the instance" (-not $r.Instances.ContainsKey('bad-vm'))
    ok "ipv6: ...and is reported ($bad)" (@($r.Problems | Where-Object { $_ -match 'is not a host name or IP address' }).Count -ge 1)
}

# BOTH host spellings are validated, not only the one that wins normalisation:
# Resolve-ConstructInstanceDefaults prefers sshHost, so an invalid vmHost would
# otherwise sit unnoticed in the file for whatever reads that field next.
$dualHost = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-remote", "sshHost": "good.local", "vmHost": "-x; calc" } } }')
ok "identity: an invalid LOSING vmHost still skips the instance" (-not $dualHost.Instances.ContainsKey('work-vm'))
ok "identity: ...and names the field that is wrong"              (@($dualHost.Problems | Where-Object { $_ -match '"vmHost"' }).Count -ge 1)
$dualHost2 = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-remote", "sshHost": "-x; calc", "vmHost": "good.local" } } }')
ok "identity: an invalid WINNING sshHost skips it too" (-not $dualHost2.Instances.ContainsKey('work-vm'))
ok "identity: ...reported once, not twice" (
    (@($dualHost2.Problems | Where-Object { $_ -match 'is not a host name or IP address' }).Count) -eq 1)
$dualOk = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-remote", "sshHost": "good.local", "vmHost": "other.local" } } }')
ok "identity: two VALID spellings still load, sshHost winning" ($dualOk.Instances['work-vm'].VmHost -eq 'good.local')

# The key-file rule is STRICTER than the alias rule, and only for KeyName.
foreach ($v in @('CON', 'con', 'NUL', 'COM1', 'lpt9', 'CON.txt', 'con.key.txt', 'agent_vm_ed25519.')) {
    ok "keyfile: '$v' is refused as a key file name" (-not (Test-ConstructInstanceKeyFileName $v))
    ok "keyfile: '$v' is still a fine ssh alias"     (Test-ConstructInstanceToken $v)
    $r = Read-ConstructInstances -Path (New-RegistryFile ('{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-remote", "sshHost": "buildbox.local", "hostAlias": "' + $v + '" } } }'))
    ok "keyfile: ...so hostAlias '$v' still loads"   ($r.Instances.ContainsKey('work-vm'))
}
foreach ($v in @('construct_work-vm_ed25519', 'agent_vm_ed25519', 'com10_key', 'console_key', 'a', 'nul_key', 'con-1')) {
    ok "keyfile: '$v' is accepted" (Test-ConstructInstanceKeyFileName $v)
}
ok "keyfile: the DEFAULT key name is unaffected" (Test-ConstructInstanceKeyFileName (New-ConstructDefaultInstance).KeyName)
ok "keyfile: a derived key name is unaffected" (
    Test-ConstructInstanceKeyFileName (Resolve-ConstructInstanceDefaults -Name 'work-vm' -Entry $null).KeyName)

ok "identity: every derived default passes its own rules" (
    @(Get-ConstructInstanceIdentityProblem -Instance (Resolve-ConstructInstanceDefaults -Name 'work-vm' -Entry $null)).Count -eq 0)
ok "identity: today's literals pass" (
    @(Get-ConstructInstanceIdentityProblem -Instance (New-ConstructDefaultInstance)).Count -eq 0)

# ── (g4) UNKNOWN BACKENDS ARE NEVER PROMOTED (mirrored in extension/test/instances.test.js) ──
# Coercing an unrecognised backend to 'hyperv-local' handed every typo destructive LOCAL
# Hyper-V access: the driver dispatch would report hostLifecycle=true and let a rebuild
# run against a local VM that merely shares the name. The string is kept verbatim so the
# unknown-driver fallback can refuse it -- on BOTH sides of the contract.
Write-Host ""
Write-Host "=== unknown backends are kept verbatim (mirrored in extension/test/instances.test.js) ===" -ForegroundColor Cyan
# A GENUINELY unknown backend is kept verbatim: the driver dispatch degrades on it
# correctly (the unknown-driver fallback), which is what refuses the destructive actions.
# (A case-variant of a KNOWN id -- 'HYPERV-REMOTE' -- is NOT in this list: the driver
# lookup lowercases, so that one really does get a driver, and it is refused whole
# instead. See the case-variant fixtures in the parity matrix above.)
foreach ($b in @('proxmox', 'hyperv-remtoe', 'HYPERV-PROXMOX')) {
    $r = Read-ConstructInstances -Path (New-RegistryFile ('{ "version": 1, "instances": { "work-vm": { "backend": "' + $b + '", "sshHost": "buildbox.local" } } }'))
    ok "backend($b): the entry survives"                 ($r.Instances.ContainsKey('work-vm'))
    ok "backend($b): it is NOT rewritten to hyperv-local" ($r.Instances['work-vm'].Backend -ceq $b)
    ok "backend($b): it is not a local backend"           (-not (Test-ConstructLocalBackend $b))
    ok "backend($b): reported as unknown"                 (@($r.Problems | Where-Object { $_ -match [regex]::Escape($b) }).Count -ge 1)
}
# A PRESENT BUT UNUSABLE backend is NOT "no backend": deriving hyperv-local from it would
# hand destructive local Hyper-V access to a value the file never actually stated. Only an
# ABSENT (or JSON-null) backend derives the default, so these entries never load -- both
# in the otherwise-CANONICAL shape and with a foreign host.
$unusableBackends = @(
    @{ label = '42';           json = '42' },
    @{ label = 'true';         json = 'true' },
    @{ label = 'empty string'; json = '""' },
    @{ label = 'whitespace';   json = '"   "' },
    @{ label = 'an array';     json = '["hyperv-local"]' },
    @{ label = 'an object';    json = '{ "id": "hyperv-local" }' }
)
foreach ($u in $unusableBackends) {
    foreach ($shape in @(@{ label = 'canonical'; extra = '' },
                         @{ label = 'with a foreign host'; extra = ', "sshHost": "buildbox.local"' })) {
        $r = Read-ConstructInstances -Path (New-RegistryFile (
            '{ "version": 1, "instances": { "work-vm": { "backend": ' + $u.json + $shape.extra + ' } } }'))
        ok "backend($($u.label), $($shape.label)): the entry is SKIPPED" (-not $r.Instances.ContainsKey('work-vm'))
        ok "backend($($u.label), $($shape.label)): the problem names 'backend' and says skipped" (
            @($r.Problems | Where-Object { $_ -match '"backend"' -and $_ -match 'skipped' }).Count -ge 1)
        ok "backend($($u.label), $($shape.label)): not reported as a plain type problem" (
            @($r.Problems | Where-Object { $_ -match "'backend' must be a string" }).Count -eq 0)
        ok "backend($($u.label), $($shape.label)): what remains is the default instance" (
            Test-ConstructDefaultInstance (Get-ConstructInstance -Name 'work-vm' -Registry $r))
    }
}
# A JSON null (like an absent key) IS "omitted" -- that stays the zero-change default.
$nullBackend = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "backend": null } } }')
ok "backend(null): omitted-equivalent, derives hyperv-local" ($nullBackend.Instances['work-vm'].Backend -ceq 'hyperv-local')
ok "backend(null): and reports nothing"                      (@($nullBackend.Problems).Count -eq 0)
# A SPELLING THE TWO LOOKUPS READ DIFFERENTLY: every enum comparison in both readers is
# case-SENSITIVE ('unknown'), but getDriver() in the extension trims + lowercases before
# the lookup and WOULD return the LOCAL driver -- so an otherwise CANONICAL entry would
# drive destructive local actions. Neither reading is safe, so it does not load.
foreach ($b in @('HYPERV-LOCAL', 'Hyperv-Local', '  hyperv-LOCAL  ')) {
    ok "backend('$b'): the driver lookup WOULD read it as local" (Test-ConstructLocalBackend $b)
    $r = Read-ConstructInstances -Path (New-RegistryFile ('{ "version": 1, "instances": { "work-vm": { "backend": "' + $b + '" } } }'))
    ok "backend('$b'): the entry is SKIPPED" (-not $r.Instances.ContainsKey('work-vm'))
    ok "backend('$b'): the problem names 'backend'" (
        @($r.Problems | Where-Object { $_ -match '"backend"' -and $_ -match 'skipped' }).Count -ge 1)
    ok "backend('$b'): what remains is the default instance" (
        Test-ConstructDefaultInstance (Get-ConstructInstance -Name 'work-vm' -Registry $r))
}
# ...while the EXACT value (and one that only needed trimming, which both readers do to
# every string field) is the ordinary local backend.
foreach ($b in @('hyperv-local', ' hyperv-local ')) {
    $r = Read-ConstructInstances -Path (New-RegistryFile ('{ "version": 1, "instances": { "work-vm": { "backend": "' + $b + '" } } }'))
    ok "backend('$b'): loads as the local backend" (
        $r.Instances.ContainsKey('work-vm') -and $r.Instances['work-vm'].Backend -ceq 'hyperv-local')
    ok "backend('$b'): with no problems" (@($r.Problems).Count -eq 0)
}

# ── (g5) THE CANONICAL IDENTITY of a hyperv-local instance (mirrored in the JS suite) ──
# reinstall/redownload emit ONLY -VmName; Auto-Install.ps1 derives the guest host, the
# alias and the key from it. A local entry that states anything else would rebuild a
# DIFFERENT VM than it dials -- and be unable to reach the one it rebuilt.
Write-Host ""
Write-Host "=== canonical local identity (mirrored in extension/test/instances.test.js) ===" -ForegroundColor Cyan
$nonCanonical = @(
    # The headline case: 'work-vm' pointed at the DEFAULT VM -- a reinstall would delete
    # and recreate Agent-VM under the guise of rebuilding work-vm.
    @{ label = 'vmName of another VM'; json = '{ "vmName": "Agent-VM" }';                 field = 'vmName' },
    @{ label = 'a foreign sshHost';    json = '{ "sshHost": "buildbox.local" }';          field = 'sshHost' },
    # The legacy alias convention: the registry's alias is the BARE instance name.
    @{ label = 'the legacy construct- alias'; json = '{ "hostAlias": "construct-work-vm" }'; field = 'hostAlias' },
    @{ label = 'a custom key file';    json = '{ "keyName": "custom_key" }';              field = 'keyName' },
    @{ label = 'a non-standard port';  json = '{ "sshPort": 2201 }';                      field = 'sshPort' }
)
foreach ($c in $nonCanonical) {
    $r = Read-ConstructInstances -Path (New-RegistryFile ('{ "version": 1, "instances": { "work-vm": ' + $c.json + ' } }'))
    ok "canonical($($c.label)): the instance is SKIPPED" (-not $r.Instances.ContainsKey('work-vm'))
    ok "canonical($($c.label)): the problem names '$($c.field)'" (
        @($r.Problems | Where-Object { $_ -match [regex]::Escape("`"$($c.field)`"") -and $_ -match 'skipped' }).Count -ge 1)
    ok "canonical($($c.label)): the default instance is what remains" (
        Test-ConstructDefaultInstance (Get-ConstructInstance -Name 'work-vm' -Registry $r))
}
ok "canonical: a deviating agent-vm entry degrades to the synthesized default" (
    Test-ConstructDefaultInstance (Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "agent-vm": { "vmName": "Other-VM" } } }')).Instances['agent-vm'])
# Hyper-V VM names are case-insensitive, so only the LOWERCASED name must match.
ok "canonical: a differently-cased vmName is fine" (
    (Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "vmName": "Work-VM" } } }')).Instances.ContainsKey('work-vm'))
# The -ConfigBranch override must keep working: it is the one field the launched scripts
# can be TOLD, so an explicit branch is not a deviation.
$branchOverride = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "configBranch": "vm-team" } } }')
ok "canonical: an explicit configBranch is still allowed" ($branchOverride.Instances.ContainsKey('work-vm'))
ok "canonical: ...and is preserved for the -ConfigBranch threading" ($branchOverride.Instances['work-vm'].ConfigBranch -ceq 'vm-team')
# The positive control: a canonical entry loads clean, with the derived identity.
$canon = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-local" } } }')
ok "canonical: a canonical entry loads with no problems" (@($canon.Problems).Count -eq 0)
ok "canonical: ...with the derived VM name"   ($canon.Instances['work-vm'].VmName -ceq 'work-vm')
ok "canonical: ...the derived host"           ($canon.Instances['work-vm'].VmHost -ceq 'work-vm.mshome.net')
ok "canonical: ...the derived alias"          ($canon.Instances['work-vm'].HostAlias -ceq 'work-vm')
ok "canonical: ...the derived key"            ($canon.Instances['work-vm'].KeyName -ceq 'construct_work-vm_ed25519')
ok "canonical: ...and port 22"                ($canon.Instances['work-vm'].SshPort -eq 22)

# ── (g5b) The REMOTE backend's own identity rules (mirrored in the JS suite) ─
# Mirrored assertion-for-assertion in extension/test/instances.test.js (same fixtures,
# same order); change the two together.
#   VmName = Name  -- the host service addresses the VM by that name, and so does a
#     rebuild (-InstanceName). An entry keyed 'alias-vm' with vmName 'service-vm' had
#     Start and the power state acting on service-vm while Reinstall DELETED and
#     recreated alias-vm.
#   SshHost stated -- a remote endpoint is whatever the service allocated. An entry that
#     omits it used to load with the DERIVED '<name>.mshome.net:22', i.e. an actionable
#     instance pointing at an unrelated machine on this PC's own network.
Write-Host ""
Write-Host "=== canonical remote identity (mirrored in extension/test/instances.test.js) ===" -ForegroundColor Cyan
$remoteOkJson = '{ "backend": "hyperv-remote", "vmName": "work-vm", "sshHost": "buildbox.example.local", "sshPort": 2201 }'
$remoteGood = Read-ConstructInstances -Path (New-RegistryFile ('{ "version": 1, "instances": { "work-vm": ' + $remoteOkJson + ' } }'))
ok "remote: a canonical remote entry loads with no problems" (@($remoteGood.Problems).Count -eq 0)
ok "remote: ...as itself" ($remoteGood.Instances['work-vm'].VmHost -ceq 'buildbox.example.local')
# An omitted vmName DERIVES the instance name, so it satisfies the rule by construction.
ok "remote: an omitted vmName derives the instance name and is fine" (
    (Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-remote", "sshHost": "buildbox.local" } } }')).Instances.ContainsKey('work-vm'))
$nonCanonicalRemote = @(
    @{ label = 'a service VM name of its own'
       json  = '{ "backend": "hyperv-remote", "vmName": "service-vm", "sshHost": "buildbox.example.local", "sshPort": 2201 }'
       field = 'vmName' },
    # Compared EXACTLY: the value goes into a URL path and into a -InstanceName argument,
    # and nothing may assume the service folds case.
    @{ label = 'a differently-cased vmName'
       json  = '{ "backend": "hyperv-remote", "vmName": "Work-VM", "sshHost": "buildbox.example.local", "sshPort": 2201 }'
       field = 'vmName' },
    @{ label = 'no sshHost at all'; json = '{ "backend": "hyperv-remote", "sshPort": 2201 }'; field = 'sshHost' },
    @{ label = 'an empty sshHost';  json = '{ "backend": "hyperv-remote", "sshHost": "   ", "sshPort": 2201 }'; field = 'sshHost' },
    # The canonical spelling is 'sshHost' -- everything that writes the registry writes it.
    @{ label = 'the endpoint under the vmHost alias only'
       json  = '{ "backend": "hyperv-remote", "vmHost": "buildbox.local" }'; field = 'sshHost' }
)
foreach ($c in $nonCanonicalRemote) {
    $r = Read-ConstructInstances -Path (New-RegistryFile ('{ "version": 1, "instances": { "work-vm": ' + $c.json + ' } }'))
    ok "remote($($c.label)): the entry is ABSENT, not merely warned about" (-not $r.Instances.ContainsKey('work-vm'))
    ok "remote($($c.label)): the problem names '$($c.field)' and says skipped" (
        @($r.Problems | Where-Object { $_ -match [regex]::Escape("`"$($c.field)`"") -and $_ -match 'skipped' }).Count -ge 1)
    ok "remote($($c.label)): resolve falls back to the default instance" (
        Test-ConstructDefaultInstance (Get-ConstructInstance -Name 'work-vm' -Registry $r))
}
# The derived mshome endpoint is exactly what must NOT survive a missing sshHost.
$remoteNoHost = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-remote" } } }')
ok "remote: no entry is left holding the derived <name>.mshome.net address" (
    (-not $remoteNoHost.Instances.ContainsKey('work-vm')) -and
    @($remoteNoHost.Instances.Keys | Where-Object { $remoteNoHost.Instances[$_].VmHost -eq 'work-vm.mshome.net' }).Count -eq 0)
ok "remote: the missing endpoint is reported in the words the JS reader uses" (
    @($remoteNoHost.Problems | Where-Object { $_ -match 'no sshHost' }).Count -ge 1)
# The WRITE side refuses the same shapes where they are created (Get-ConstructInstanceEntryProblem,
# which is what Add-ConstructInstance and Auto-Install.ps1's pre-create check both ask).
$remoteOkEntry = @{ backend = 'hyperv-remote'; vmName = 'work-vm'; sshHost = 'buildbox.example.local'; sshPort = 2201 }
ok "remote: the entry check accepts the canonical entry" (
    @(Get-ConstructInstanceEntryProblem -Name 'work-vm' -Entry $remoteOkEntry).Count -eq 0)
$remoteSplit = @{} + $remoteOkEntry; $remoteSplit['vmName'] = 'service-vm'
ok "remote: the entry check refuses a service VM name of its own" (
    @(Get-ConstructInstanceEntryProblem -Name 'work-vm' -Entry $remoteSplit).Count -gt 0)
$remoteNoEp = @{ backend = 'hyperv-remote'; sshPort = 2201 }
ok "remote: the entry check refuses a missing endpoint" (
    @(Get-ConstructInstanceEntryProblem -Name 'work-vm' -Entry $remoteNoEp).Count -gt 0)
# The rules are the remote backend's ALONE: a local instance's vmName is its own
# (case-insensitive) rule and needs no sshHost at all.
ok "remote: the rules do not touch a hyperv-local entry" (
    (Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-local", "vmName": "Work-VM" } } }')).Instances.ContainsKey('work-vm'))

# ── (g6) Cross-entry identity COLLISIONS (mirrored in the JS suite) ──────────
# Two names for one machine: a rebuild of one would delete the other's VM, and a
# reprovision would overwrite its key file.
Write-Host ""
Write-Host "=== identity collisions (mirrored in extension/test/instances.test.js) ===" -ForegroundColor Cyan
$collisions = @(
    @{ label = "the default VM's name";  json = '"vmName": "agent-vm"';                field = 'vmName' },
    @{ label = "the default VM's host";  json = '"sshHost": "agent-vm.mshome.net"';    field = 'sshHost' },
    @{ label = "the default VM's alias"; json = '"hostAlias": "agent-vm"';             field = 'hostAlias' },
    @{ label = "the default VM's key";   json = '"keyName": "agent_vm_ed25519"';       field = 'keyName' }
)
foreach ($c in $collisions) {
    $r = Read-ConstructInstances -Path (New-RegistryFile (
        '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-remote", "sshHost": "buildbox.local", ' + $c.json + ' } } }'))
    ok "collision($($c.label)): the entry is skipped" (-not $r.Instances.ContainsKey('work-vm'))
    ok "collision($($c.label)): the problem names $($c.field)" (
        @($r.Problems | Where-Object { $_ -match [regex]::Escape($c.field) -and $_ -match 'skipped' }).Count -ge 1)
    ok "collision($($c.label)): the default instance survives untouched" (
        Test-ConstructDefaultInstance $r.Instances['agent-vm'])
}
$shared = Read-ConstructInstances -Path (New-RegistryFile @'
{ "version": 1, "instances": {
    "a-vm": { "backend": "hyperv-remote", "sshHost": "buildbox.local" },
    "b-vm": { "backend": "hyperv-remote", "sshHost": "BuildBox.local" } } }
'@)
ok "collision: two entries sharing a host drop BOTH" (
    -not $shared.Instances.ContainsKey('a-vm') -and -not $shared.Instances.ContainsKey('b-vm'))
ok "collision: ...reported once, naming both" (
    (@($shared.Problems | Where-Object { $_ -match 'share the same' }).Count -eq 1) -and
    (@($shared.Problems | Where-Object { $_ -match 'a-vm' -and $_ -match 'b-vm' }).Count -ge 1))
$sharedKey = Read-ConstructInstances -Path (New-RegistryFile @'
{ "version": 1, "instances": {
    "a-vm": { "backend": "hyperv-remote", "sshHost": "one.local", "keyName": "Shared_Key" },
    "b-vm": { "backend": "hyperv-remote", "sshHost": "two.local", "keyName": "shared_key" } } }
'@)
ok "collision: the comparison is case-insensitive (one NTFS file, one DNS name)" (
    -not $sharedKey.Instances.ContainsKey('a-vm'))
$noClash = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "a-vm": {}, "b-vm": {}, "agent-vm": {} } }')
ok "collision: two canonical local instances never collide" (@($noClash.Problems).Count -eq 0)

# ── The endpoint identity is (sshHost, sshPort), not the host alone ──────────
# Several hyperv-remote VMs legitimately live on ONE service host and are told apart by
# the SSH forward the service allocated them (one port per VM out of a configured range).
# Keyed on the host alone, every VM on a shared host collided and the "drop BOTH" rule
# then lost the whole registry. Mirrored in extension/test/instances.test.js.
Write-Host ""
Write-Host "=== endpoint uniqueness: (sshHost, sshPort) (mirrored in the JS suite) ===" -ForegroundColor Cyan
$sameHost = Read-ConstructInstances -Path (New-RegistryFile @'
{ "version": 1, "instances": {
    "a-vm": { "backend": "hyperv-remote", "sshHost": "buildbox.example.local", "sshPort": 2201 },
    "b-vm": { "backend": "hyperv-remote", "sshHost": "BuildBox.Example.local", "sshPort": 2202 } } }
'@)
ok "endpoint: two remote VMs on ONE service host, different ports, both load" (
    $sameHost.Instances.ContainsKey('a-vm') -and $sameHost.Instances.ContainsKey('b-vm'))
ok "endpoint: ...with nothing reported" (@($sameHost.Problems).Count -eq 0)
$samePort = Read-ConstructInstances -Path (New-RegistryFile @'
{ "version": 1, "instances": {
    "a-vm": { "backend": "hyperv-remote", "sshHost": "buildbox.example.local", "sshPort": 2201 },
    "b-vm": { "backend": "hyperv-remote", "sshHost": "BuildBox.Example.local", "sshPort": 2201 } } }
'@)
ok "endpoint: the SAME host and port is still one machine -- both dropped" (
    -not $samePort.Instances.ContainsKey('a-vm') -and -not $samePort.Instances.ContainsKey('b-vm'))
ok "endpoint: ...reported once, naming both and the host:port" (
    (@($samePort.Problems | Where-Object { $_ -match 'share the same sshHost/sshPort' }).Count -eq 1) -and
    (@($samePort.Problems | Where-Object { $_ -match 'a-vm' -and $_ -match 'b-vm' -and $_ -match 'buildbox\.example\.local:2201' }).Count -ge 1))
$portShapes = Read-ConstructInstances -Path (New-RegistryFile @'
{ "version": 1, "instances": {
    "a-vm": { "backend": "hyperv-remote", "sshHost": "one.local", "sshPort": 2201 },
    "b-vm": { "backend": "hyperv-remote", "sshHost": "one.local", "sshPort": "2201" } } }
'@)
ok "endpoint: the port comparison is numeric, so '2201' and 2201 are one endpoint" (
    -not $portShapes.Instances.ContainsKey('a-vm'))
# A local instance's port is canonically 22 and its host derives from its own name, so
# local entries still cannot share an endpoint -- but a remote VM reached through a
# FORWARD on the same machine is a different endpoint and must load.
$localAndRemote = Read-ConstructInstances -Path (New-RegistryFile (
    '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-remote", "sshHost": "agent-vm.mshome.net", "sshPort": 2201 } } }'))
ok "endpoint: a remote VM forwarded on the default VM's host (other port) loads" (
    $localAndRemote.Instances.ContainsKey('work-vm'))
ok "endpoint: ...and nothing is reported" (@($localAndRemote.Problems).Count -eq 0)
ok "endpoint: the default instance survives it untouched" (
    Test-ConstructDefaultInstance $localAndRemote.Instances['agent-vm'])
$defaultEndpoint = Read-ConstructInstances -Path (New-RegistryFile (
    '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-remote", "sshHost": "agent-vm.mshome.net", "sshPort": 22 } } }'))
ok "endpoint: ...while the default instance's OWN host:port is still reserved" (
    -not $defaultEndpoint.Instances.ContainsKey('work-vm'))
$sharedHostKey = Read-ConstructInstances -Path (New-RegistryFile @'
{ "version": 1, "instances": {
    "a-vm": { "backend": "hyperv-remote", "sshHost": "one.local", "sshPort": 2201, "keyName": "shared_key" },
    "b-vm": { "backend": "hyperv-remote", "sshHost": "one.local", "sshPort": 2202, "keyName": "Shared_Key" } } }
'@)
ok "endpoint: a shared host does not excuse a shared key file" (
    -not $sharedHostKey.Instances.ContainsKey('a-vm') -and -not $sharedHostKey.Instances.ContainsKey('b-vm'))

# -ExcludeLabel: the ONE caller-side filter, for the question Auto-Install.ps1 has to ask
# BEFORE the host service has allocated a VM's SSH forward. It drops the named identity
# from the check and NOTHING else -- it is not a relaxation of the rule set (the reader
# never passes it, and the JS twin has no such parameter).
$epPair = New-Object System.Collections.Hashtable ([System.StringComparer]::Ordinal)
$epPair['a-vm'] = Resolve-ConstructInstanceDefaults -Name 'a-vm' -Entry (ConvertTo-ConstructInstanceEntryObject -Entry @{
    backend = 'hyperv-remote'; vmName = 'a-vm'; sshHost = 'buildbox.example.local'; sshPort = 2201 })
$epPair['b-vm'] = Resolve-ConstructInstanceDefaults -Name 'b-vm' -Entry (ConvertTo-ConstructInstanceEntryObject -Entry @{
    backend = 'hyperv-remote'; vmName = 'b-vm'; sshHost = 'buildbox.example.local'; sshPort = 2201 })
ok "exclude: the endpoint collision is reported by default" (
    @((Get-ConstructInstanceCollision -Instances $epPair).Problems | Where-Object { $_ -match 'sshHost/sshPort' }).Count -eq 1)
ok "exclude: ...and is the ONLY problem those two entries have" (
    @((Get-ConstructInstanceCollision -Instances $epPair).Problems).Count -eq 1)
ok "exclude: -ExcludeLabel 'sshHost/sshPort' drops exactly that one" (
    @((Get-ConstructInstanceCollision -Instances $epPair -ExcludeLabel @('sshHost/sshPort')).Problems).Count -eq 0)
$keyPair = New-Object System.Collections.Hashtable ([System.StringComparer]::Ordinal)
$keyPair['a-vm'] = Resolve-ConstructInstanceDefaults -Name 'a-vm' -Entry (ConvertTo-ConstructInstanceEntryObject -Entry @{
    backend = 'hyperv-remote'; vmName = 'a-vm'; sshHost = 'one.local'; sshPort = 2201; keyName = 'shared_key' })
$keyPair['b-vm'] = Resolve-ConstructInstanceDefaults -Name 'b-vm' -Entry (ConvertTo-ConstructInstanceEntryObject -Entry @{
    backend = 'hyperv-remote'; vmName = 'b-vm'; sshHost = 'two.local'; sshPort = 2202; keyName = 'shared_key' })
ok "exclude: ...and leaves every OTHER identity rule in force" (
    @((Get-ConstructInstanceCollision -Instances $keyPair -ExcludeLabel @('sshHost/sshPort')).Problems | Where-Object { $_ -match 'keyName' }).Count -eq 1)
ok "exclude: an unknown label excludes nothing (a typo must not widen the check)" (
    @((Get-ConstructInstanceCollision -Instances $epPair -ExcludeLabel @('sshhost/sshport')).Problems).Count -eq 1)

# ── (g7) configBranch is a cross-entry identity (mirrored in the JS suite) ────
# The branch IS that instance's store inside the ONE host config repo
# (docs/config-sync.md, "Multiple instances"): two entries on one branch share their VM
# snapshots, deletion history, merge base and write-backs, so one VM's tick merges -- or
# deletes -- the other VM's configuration.
Write-Host ""
Write-Host "=== configBranch uniqueness (mirrored in extension/test/instances.test.js) ===" -ForegroundColor Cyan
$branchClaimJson = '{ "version": 1, "instances": { "work-vm": { "backend": "hyperv-remote", "sshHost": "buildbox.local", "configBranch": "vm" } } }'
$branchClaim = Read-ConstructInstances -Path (New-RegistryFile $branchClaimJson)
ok "branch: a non-default entry may NOT claim the default instance's 'vm'" (
    -not $branchClaim.Instances.ContainsKey('work-vm'))
ok "branch: ...and the problem names configBranch and the default instance" (
    @($branchClaim.Problems | Where-Object { $_ -match 'configBranch' -and $_ -match 'agent-vm' -and $_ -match 'skipped' }).Count -ge 1)
ok "branch: the default instance survives that entry untouched" (
    Test-ConstructDefaultInstance $branchClaim.Instances['agent-vm'])
$sharedBranch = Read-ConstructInstances -Path (New-RegistryFile @'
{ "version": 1, "instances": {
    "a-vm": { "backend": "hyperv-remote", "sshHost": "one.local", "configBranch": "vm-team" },
    "b-vm": { "backend": "hyperv-remote", "sshHost": "two.local", "configBranch": "VM-Team" } } }
'@)
ok "branch: two entries sharing one branch drop BOTH (case-insensitively -- Windows loose refs)" (
    -not $sharedBranch.Instances.ContainsKey('a-vm') -and -not $sharedBranch.Instances.ContainsKey('b-vm'))
ok "branch: ...reported once, naming both and the field" (
    (@($sharedBranch.Problems | Where-Object { $_ -match 'share the same configBranch' }).Count -eq 1) -and
    (@($sharedBranch.Problems | Where-Object { $_ -match 'a-vm' -and $_ -match 'b-vm' }).Count -ge 1))
$branchOk = Read-ConstructInstances -Path (New-RegistryFile '{ "version": 1, "instances": { "work-vm": { "configBranch": "vm-team" } } }')
ok "branch: a distinct explicit override is still allowed" ($branchOk.Instances.ContainsKey('work-vm'))

# ── (g8) Object.prototype names are NOT registry entries (JS parity) ──────────
# The JS reader's byName is a prototype-free map and every membership test is an
# OWN-property test, because a plain {} made byName['constructor'] truthy for EVERY
# registry -- so a file that merely POINTS at such a name resolved to Object's
# constructor FUNCTION over there while this reader (an ordinal Hashtable +
# ContainsKey) correctly reported "no entry". Same fixtures as the JS suite; the two
# must agree on every one of them.
Write-Host ""
Write-Host "=== Object.prototype name parity (mirrored in extension/test/instances.test.js) ===" -ForegroundColor Cyan
foreach ($pn in @('constructor', '__proto__', 'toString', 'hasOwnProperty', 'valueOf', 'isPrototypeOf')) {
    # (a) as defaultInstance, with NO entry of that name.
    $asDefaultJson = '{ "version": 1, "defaultInstance": "' + $pn + '", "instances": {} }'
    $asDefault = Read-ConstructInstances -Path (New-RegistryFile $asDefaultJson)
    ok "proto($pn): defaultInstance falls back to agent-vm" ($asDefault.Default -ceq 'agent-vm')
    ok "proto($pn): ...and it is reported" (
        @($asDefault.Problems | Where-Object { $_ -match 'has no entry' -or $_ -match 'not a valid instance name' }).Count -ge 1)
    ok "proto($pn): the resolved instance is the synthesized default" (
        Test-ConstructDefaultInstance (Get-ConstructInstance -Registry $asDefault))
    # (b) asked for BY NAME (the -Name lookup the scripts use).
    ok "proto($pn): Get-ConstructInstance -Name falls back to the default instance" (
        Test-ConstructDefaultInstance (Get-ConstructInstance -Name $pn -Registry $asDefault))
    ok "proto($pn): the instance table has no such key" (-not $asDefault.Instances.ContainsKey($pn))
    # (c) as an INSTANCE NAME in the file: the name rule decides, identically on both sides.
    $asEntryJson = '{ "version": 1, "instances": { "' + $pn + '": { "backend": "hyperv-remote", "sshHost": "buildbox.local" } } }'
    $asEntry = Read-ConstructInstances -Path (New-RegistryFile $asEntryJson)
    if (Test-ConstructInstanceName $pn) {
        # 'constructor' is a perfectly good instance name -- it must load like any other.
        ok "proto($pn): a real instance of that name LOADS" ($asEntry.Instances.ContainsKey($pn))
        ok "proto($pn): ...and resolves to itself, not to a type member" (
            (Get-ConstructInstance -Name $pn -Registry $asEntry).VmHost -ceq 'buildbox.local')
        $asBothJson = '{ "version": 1, "defaultInstance": "' + $pn + '", "instances": { "' + $pn + '": { "backend": "hyperv-remote", "sshHost": "buildbox.local" } } }'
        $asBoth = Read-ConstructInstances -Path (New-RegistryFile $asBothJson)
        ok "proto($pn): ...and it can be the defaultInstance" ($asBoth.Default -ceq $pn)
    } else {
        ok "proto($pn): an invalid name is skipped with a problem" (
            (-not $asEntry.Instances.ContainsKey($pn)) -and
            (@($asEntry.Problems | Where-Object { $_ -match 'is invalid' -and $_ -match 'skipped' }).Count -ge 1))
    }
}

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
    $pf = New-RegistryFile ('{"version":1,"instances":{"p-vm":{"backend":"hyperv-remote","sshHost":"p-vm.example.local","sshPort":' + $pc.lit + '}}}')
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
# A custom endpoint belongs to a non-local backend: a hyperv-local instance's identity is
# pinned to its name, so writing one with a foreign host would produce a file the reader
# (rightly) refuses on the way back in.
$regW.Instances['work-vm'] = Resolve-ConstructInstanceDefaults -Name 'work-vm' -Entry ([pscustomobject]@{ backend = 'hyperv-remote'; sshPort = 2201; sshHost = 'buildbox.local' })
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
