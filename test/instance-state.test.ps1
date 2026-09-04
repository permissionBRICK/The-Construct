#Requires -Version 5.1
<#
    Unit tests for the B12 per-instance state store (lib/AgentVm.InstanceState.ps1) and
    for Set-ConstructProvisionedMarker's instance keying. Run:

        pwsh -NoProfile -File test/instance-state.test.ps1

    The REGRESSION BAR these tests exist for: an install with one local `agent-vm` and no
    registry must write exactly the files it wrote before this module existed -- the
    legacy top-level keys of .construct-settings.json, and NO instances\agent-vm.json.
    Everything else here is about the second VM: its own file, the install-wide/VM-scoped
    split, atomicity, and the path-safety guard on a name that becomes a file name.

    Cross-reader parity with extension/src/instancestate.js is asserted for real (node is
    available on the box this runs on): each side reads the other's file.
#>
$ErrorActionPreference = "Stop"

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here
$libPath  = Join-Path $repoRoot "lib/AgentVm.InstanceState.ps1"

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

# Parses cleanly (the check every shipped .ps1 gets).
$errors = $null
[System.Management.Automation.Language.Parser]::ParseFile($libPath, [ref]$null, [ref]$errors) | Out-Null
ok "parse: lib/AgentVm.InstanceState.ps1 has zero errors" ($errors.Count -eq 0)

. (Join-Path $repoRoot "lib/AgentVm.Common.ps1")
. $libPath

$root = Join-Path ([System.IO.Path]::GetTempPath()) ("construct-state-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $root -Force | Out-Null
$oldLocal = $env:LOCALAPPDATA
$env:LOCALAPPDATA = $root
try {
    $scriptsDir = Join-Path $root "scripts"
    New-Item -ItemType Directory -Path $scriptsDir -Force | Out-Null
    $legacy      = Join-Path $scriptsDir ".construct-settings.json"
    $instanceDir = Join-Path (Join-Path $root "The-Construct") "instances"

    # ── Path math ────────────────────────────────────────────────────────────
    ok "dir: instances\ sits beside instances.json" ((Get-ConstructInstanceStateDir) -eq $instanceDir)
    ok "path: the default instance has NO state file" ($null -eq (Get-ConstructInstanceStatePath -Name 'agent-vm'))
    ok "path: an absent name is the default store too" ($null -eq (Get-ConstructInstanceStatePath -Name ''))
    # CASE-SENSITIVE, like instances.isDefaultInstance and the registry: 'Agent-VM' is not
    # a valid instance name at all, so silently treating it as the default would have this
    # module and the registry disagree about which VM a caller meant.
    ok "path: 'agent-vm' is the default store" (Test-ConstructDefaultInstanceStore 'agent-vm')
    ok "path: 'Agent-VM' is NOT the default store (case-sensitive, like the registry)" (
        -not (Test-ConstructDefaultInstanceStore 'Agent-VM'))
    ok "path: ...and it is refused outright rather than given a file" (
        $null -eq (Get-ConstructInstanceStatePath -Name 'Agent-VM'))
    ok "path: a named instance gets instances\<name>.json" (
        (Get-ConstructInstanceStatePath -Name 'work-vm') -eq (Join-Path $instanceDir "work-vm.json"))

    # THE ONE NAME RULE (Test-ConstructInstanceName), asked of lib/AgentVm.Instances.ps1 --
    # not a second regex here. A lowercase DNS label cannot hold a separator or a dot, so
    # passing that rule is also what makes the name safe as a FILE NAME.
    ok "name rule: a traversal name resolves to no path" ($null -eq (Get-ConstructInstanceStatePath -Name '../evil'))
    ok "name rule: a separator name resolves to no path" ($null -eq (Get-ConstructInstanceStatePath -Name 'a\b'))
    ok "name rule: a dotted name resolves to no path" ($null -eq (Get-ConstructInstanceStatePath -Name 'a..b'))
    ok "name rule: an uppercase name resolves to no path" ($null -eq (Get-ConstructInstanceStatePath -Name 'Work-VM'))
    ok "name rule: an underscore name resolves to no path" ($null -eq (Get-ConstructInstanceStatePath -Name 'work_vm'))
    ok "name rule: a trailing hyphen resolves to no path" ($null -eq (Get-ConstructInstanceStatePath -Name 'work-'))
    ok "name rule: the RESERVED construct- prefix resolves to no path" (
        $null -eq (Get-ConstructInstanceStatePath -Name 'construct-work'))
    ok "name rule: a 64-character name is over the DNS label limit" (
        $null -eq (Get-ConstructInstanceStatePath -Name ('a' * 64)))
    ok "name rule: a 63-character name is accepted" (
        $null -ne (Get-ConstructInstanceStatePath -Name ('a' * 63)))
    ok "name rule: the verdict matches Test-ConstructInstanceName itself" ((@(
            'work-vm', 'a', 'Work-VM', 'work_vm', 'work-', 'construct-work', 'a..b'
        ) | Where-Object {
            $viaState = [bool](Test-ConstructInstanceStateName $_)
            $viaRule  = [bool](& { param($m, $n) . $m; Test-ConstructInstanceName $n } (Join-Path $repoRoot "lib/AgentVm.Instances.ps1") $_)
            $viaState -ne $viaRule
        }).Count -eq 0)
    $guardThrew = $false
    try { Save-ConstructInstanceState -Name '../evil' -Dir $scriptsDir -Values @{ micPassthrough = $true } }
    catch { $guardThrew = $true }
    ok "name rule: saving under an unusable name throws instead of writing" $guardThrew

    # ── ZERO-CHANGE: the default instance keeps the legacy file, and only it ──
    Save-ConstructInstanceState -Name 'agent-vm' -Dir $scriptsDir -Values @{ micPassthrough = $true; projects = @('web') }
    ok "default: writes the legacy .construct-settings.json" (Test-Path -LiteralPath $legacy)
    ok "default: creates NO instances\ directory" (-not (Test-Path -LiteralPath $instanceDir))
    $legacyObj = Read-ConstructSettings -Dir $scriptsDir
    ok "default: the keys land at the LEGACY TOP LEVEL" ($legacyObj.micPassthrough -eq $true)
    ok "default: read gives back what Read-ConstructSettings gives back" (
        (Read-ConstructInstanceState -Name 'agent-vm' -Dir $scriptsDir).micPassthrough -eq $true)
    # Byte-identical to writing through the legacy function directly.
    $viaState = Get-Content -LiteralPath $legacy -Raw
    Save-ConstructSettings -Dir $scriptsDir -Values @{ micPassthrough = $true; projects = @('web') }
    ok "default: the file is byte-identical to a direct Save-ConstructSettings" (
        $viaState -eq (Get-Content -LiteralPath $legacy -Raw))

    # Install-wide keys keep going to the legacy file for the default instance too.
    Save-ConstructInstanceState -Name 'agent-vm' -Dir $scriptsDir -Values @{ installedCommit = "abc1234" }
    ok "default: install-wide keys stay in the legacy file" (
        (Read-ConstructSettings -Dir $scriptsDir).installedCommit -eq "abc1234")

    # ── A named instance uses only its own file ──────────────────────────────
    Save-ConstructInstanceState -Name 'work-vm' -Dir $scriptsDir -Values @{ micPassthrough = $false; t3codeChannel = 'nightly' }
    $workFile = Join-Path $instanceDir "work-vm.json"
    ok "named: writes instances\work-vm.json" (Test-Path -LiteralPath $workFile)
    $workDoc = Get-Content -LiteralPath $workFile -Raw | ConvertFrom-Json
    ok "named: records the schema version" ($workDoc.version -eq 1)
    ok "named: records its own name" ($workDoc.instance -eq 'work-vm')
    ok "named: holds the VM-scoped keys" ($workDoc.t3codeChannel -eq 'nightly')
    ok "named: leaves the DEFAULT instance's keys alone" (
        (Read-ConstructSettings -Dir $scriptsDir).micPassthrough -eq $true)
    ok "named: read returns its own values" (
        (Read-ConstructInstanceState -Name 'work-vm' -Dir $scriptsDir).micPassthrough -eq $false)

    # Merge: a second save preserves untouched keys.
    Save-ConstructInstanceState -Name 'work-vm' -Dir $scriptsDir -Values @{ projects = @('api') }
    $merged = Read-ConstructInstanceState -Name 'work-vm' -Dir $scriptsDir
    ok "named: a later save MERGES (t3codeChannel survives)" ($merged.t3codeChannel -eq 'nightly')
    ok "named: ...and adds the new key" (@($merged.projects) -contains 'api')

    # PARITY with extension/src/instancestate.js: an install-wide-ONLY save writes the
    # install-wide file and creates NO per-instance file. A file holding nothing but
    # version/instance would be a phantom VM state, and the two writers must agree.
    $lonelyFile = Join-Path $instanceDir "lonely-vm.json"
    Save-ConstructInstanceState -Name 'lonely-vm' -Dir $scriptsDir -Values @{ installedCommit = "cafe123" }
    ok "install-wide only: the install-wide file is written" (
        (Read-ConstructSettings -Dir $scriptsDir).installedCommit -eq "cafe123")
    ok "install-wide only: NO per-instance file is created" (-not (Test-Path -LiteralPath $lonelyFile))
    ok "install-wide only: ...and the instance still reads as 'nothing saved'" (
        $null -eq (Read-ConstructInstanceState -Name 'lonely-vm' -Dir $scriptsDir))
    Save-ConstructInstanceState -Name 'lonely-vm' -Dir $scriptsDir -Values @{ smbShare = $true }
    ok "install-wide only: a later VM-scoped save DOES create it" (Test-Path -LiteralPath $lonelyFile)

    # Install-wide keys mixed into a per-instance save are SPLIT OFF.
    Save-ConstructInstanceState -Name 'work-vm' -Dir $scriptsDir -Values @{ installedCommit = "deadbee"; smbShare = $false }
    $workDoc2 = Get-Content -LiteralPath $workFile -Raw | ConvertFrom-Json
    ok "split: an install-wide key never lands in the per-instance file" (
        -not ($workDoc2.PSObject.Properties.Name -contains 'installedCommit'))
    ok "split: ...it lands in the install-wide file instead" (
        (Read-ConstructSettings -Dir $scriptsDir).installedCommit -eq "deadbee")
    ok "split: the VM-scoped key of the same save lands in the per-instance file" ($workDoc2.smbShare -eq $false)

    # A hand-edited install-wide key in a per-instance file is IGNORED on read.
    $hand = Join-Path $instanceDir "hand.json"
    Set-Content -LiteralPath $hand -Value '{"version":1,"instance":"hand","installedCommit":"ffffff0","micPassthrough":true}' -Encoding UTF8
    $handState = Read-ConstructInstanceState -Name 'hand' -Dir $scriptsDir
    ok "read: metadata is not returned as a setting" (-not ($handState.PSObject.Properties.Name -contains 'version'))
    ok "read: a hand-edited install-wide key is ignored" (
        -not ($handState.PSObject.Properties.Name -contains 'installedCommit'))
    ok "read: the VM-scoped keys beside it still read" ($handState.micPassthrough -eq $true)

    # ── Tolerance: missing / corrupt ─────────────────────────────────────────
    ok 'read: an instance with no file yields $null' ($null -eq (Read-ConstructInstanceState -Name 'never' -Dir $scriptsDir))
    $corrupt = Join-Path $instanceDir "corrupt.json"
    Set-Content -LiteralPath $corrupt -Value '{ not json' -Encoding UTF8
    ok 'read: a corrupt file yields $null (never throws)' ($null -eq (Read-ConstructInstanceState -Name 'corrupt' -Dir $scriptsDir))
    Set-Content -LiteralPath $corrupt -Value '{"version":1,"instance":"corrupt"}' -Encoding UTF8
    ok 'read: a file with only metadata yields $null' ($null -eq (Read-ConstructInstanceState -Name 'corrupt' -Dir $scriptsDir))
    # A JSON root that is not an OBJECT is "nothing saved" -- the same answer readJsonObject
    # gives in the JS twin. Without the check ConvertFrom-Json hands back an array and the
    # reader would surface PowerShell's own array metadata (Length, Count) as settings.
    foreach ($bad in @('[1,2]', '"a string"', '42', 'true', 'null')) {
        Set-Content -LiteralPath $corrupt -Value $bad -Encoding UTF8
        ok "read: a non-object JSON root ($bad) yields `$null, like the JS twin" (
            $null -eq (Read-ConstructInstanceState -Name 'corrupt' -Dir $scriptsDir))
    }
    # A corrupt file is REPLACED, not merged into.
    Save-ConstructInstanceState -Name 'corrupt' -Dir $scriptsDir -Values @{ smbShare = $true }
    ok "write: a corrupt file is replaced by a valid one" (
        (Read-ConstructInstanceState -Name 'corrupt' -Dir $scriptsDir).smbShare -eq $true)

    # ── Atomicity ────────────────────────────────────────────────────────────
    ok "atomic: no .tmp leftovers in instances\" (
        @(Get-ChildItem -LiteralPath $instanceDir -Filter "*.tmp.*" -ErrorAction SilentlyContinue).Count -eq 0)
    $raw = [System.IO.File]::ReadAllBytes($workFile)
    ok "atomic: BOM-less UTF-8" (-not ($raw.Length -ge 3 -and $raw[0] -eq 0xEF -and $raw[1] -eq 0xBB -and $raw[2] -eq 0xBF))
    ok "atomic: trailing newline" ((Get-Content -LiteralPath $workFile -Raw).EndsWith("`n"))

    # ── Set-ConstructProvisionedMarker keys by instance ──────────────────────
    $pvDir = Join-Path $root "pv"
    New-Item -ItemType Directory -Path $pvDir -Force | Out-Null
    Save-ConstructSettings -Dir $pvDir -Values @{ installedCommit = "aaaa111" }
    $shaDefault = Set-ConstructProvisionedMarker -Dir $pvDir -InstanceName 'agent-vm'
    ok "marker: the default instance records at the legacy top level" (
        (Read-ConstructSettings -Dir $pvDir).provisionedCommit -eq "aaaa111" -and $shaDefault -eq "aaaa111")
    ok "marker: ...and still creates no instances\agent-vm.json" (
        -not (Test-Path -LiteralPath (Join-Path $instanceDir "agent-vm.json")))
    Set-ConstructProvisionedMarker -Dir $pvDir -InstanceName 'work-vm' | Out-Null
    ok "marker: a named instance records in its own file" (
        (Read-ConstructInstanceState -Name 'work-vm' -Dir $pvDir).provisionedCommit -eq "aaaa111")
    ok "marker: ...and reads installedCommit from the INSTALL-WIDE file" (
        (Read-ConstructSettings -Dir $pvDir).installedCommit -eq "aaaa111")
    # An omitted -InstanceName is today's behaviour, unchanged.
    Save-ConstructSettings -Dir $pvDir -Values @{ installedCommit = "bbbb222" }
    Set-ConstructProvisionedMarker -Dir $pvDir | Out-Null
    ok "marker: an omitted -InstanceName still writes the legacy key" (
        (Read-ConstructSettings -Dir $pvDir).provisionedCommit -eq "bbbb222")
    ok "marker: ...and leaves the named instance's own marker alone" (
        (Read-ConstructInstanceState -Name 'work-vm' -Dir $pvDir).provisionedCommit -eq "aaaa111")

    # ── Cross-reader parity with extension/src/instancestate.js ──────────────
    $node = Get-Command node -ErrorAction SilentlyContinue
    if ($node) {
        $srcDir = (Join-Path $repoRoot "extension/src").Replace('\', '/')
        # 1. The JS reader sees what PowerShell wrote.
        $jsRead = & node -e @"
const s = require('$srcDir/instancestate.js');
const st = s.store('work-vm', process.argv[1], { LOCALAPPDATA: process.argv[2] });
const v = s.readState(st);
console.log(JSON.stringify([v.t3codeChannel, v.smbShare, v.installedCommit === undefined]));
"@ $scriptsDir $root
        ok "parity: the JS reader reads the PowerShell writer's file" ($jsRead -eq '["nightly",false,true]')
        # 2. PowerShell sees what the JS writer wrote.
        & node -e @"
const s = require('$srcDir/instancestate.js');
const st = s.store('js-vm', process.argv[1], { LOCALAPPDATA: process.argv[2] });
s.saveState(st, { micPassthrough: true, t3codeChannel: 'nightly' });
"@ $scriptsDir $root | Out-Null
        $fromJs = Read-ConstructInstanceState -Name 'js-vm' -Dir $scriptsDir
        ok "parity: the PowerShell reader reads the JS writer's file" (
            $fromJs.micPassthrough -eq $true -and $fromJs.t3codeChannel -eq 'nightly')
        # 3. The JS writer does not create a file for the default instance either.
        & node -e @"
const s = require('$srcDir/instancestate.js');
s.saveState(s.store('agent-vm', process.argv[1], { LOCALAPPDATA: process.argv[2] }), { smbShare: true });
"@ $scriptsDir $root | Out-Null
        ok "parity: the JS writer creates no instances\agent-vm.json either" (
            -not (Test-Path -LiteralPath (Join-Path $instanceDir "agent-vm.json")))
    } else {
        ok "parity: node is unavailable -- cross-reader checks skipped" $true
    }
} finally {
    $env:LOCALAPPDATA = $oldLocal
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host ("  instance-state tests -- {0}/{1} passed" -f $script:pass, ($script:pass + $script:fail))
Write-Host ""
if ($script:fail -gt 0) { exit 1 }
exit 0
