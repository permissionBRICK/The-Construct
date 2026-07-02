#Requires -Version 5.1
<#
    Unit tests for the config-sync v2 engine in lib/AgentVm.Common.ps1: canonical
    JSON serializer cross-check with the JS engine (node), Test-ConstructProfile
    agreement with validateProfile, a real-git sync tick test, and a parse check.
    No Pester dependency. Run:

        pwsh -NoProfile -File test/config-sync.test.ps1

    Covers the canonical serializer (byte-match with node), the strict validator,
    the sync engine (real git, injectable VM store), and the import path. The
    winget/SSH/elevation/VS Code paths are Windows-only and need manual
    validation.
#>
$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

# Stub out Write-Step/Write-Ok/Write-Note that the lib expects from its callers.
function Write-Step($msg) { }
function Write-Ok($msg)   { }
function Write-Note($msg)  { }

. (Join-Path $here "..\lib\AgentVm.Common.ps1")

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

# ── Parse check ──────────────────────────────────────────────────────────────
$errs = $null; $toks = $null
$libPath = Join-Path $repoRoot "lib/AgentVm.Common.ps1"
$ast = [System.Management.Automation.Language.Parser]::ParseFile($libPath, [ref]$toks, [ref]$errs)
ok "parse: lib/AgentVm.Common.ps1 has no parse errors" ($errs.Count -eq 0)
if ($errs.Count -gt 0) {
    foreach ($e in $errs) { Write-Host "    $e" -ForegroundColor Red }
}

# ── ASCII-only string-literal check (WinPS 5.1 safety) ──────────────────────
$strs = $ast.FindAll({ param($n)
    $n -is [System.Management.Automation.Language.StringConstantExpressionAst] -or
    $n -is [System.Management.Automation.Language.ExpandableStringExpressionAst] }, $true)
$bad = @($strs | Where-Object { $_.Extent.Text -match '[^\x00-\x7F]' })
ok "ascii: no non-ASCII inside string literals (WinPS 5.1-safe)" ($bad.Count -eq 0)
if ($bad.Count -gt 0) {
    $bad | Select-Object -First 3 | ForEach-Object {
        Write-Host ("    line {0}: {1}" -f $_.Extent.StartLineNumber, $_.Extent.Text) -ForegroundColor DarkYellow
    }
}

# ── Canonical JSON serializer cross-check ────────────────────────────────────
# Spawn node with extension/src/projects.js and assert byte equality.
$projJs = Join-Path $repoRoot "extension/src/projects.js"
$nodeAvailable = $null -ne (Get-Command node -ErrorAction SilentlyContinue)

# Helper: run node, write canonical JSON to a temp file, read it back as raw bytes.
# This avoids PowerShell's native-command output pipeline, which splits lines and
# drops trailing newlines -- fatal for a byte-match comparison.
function Get-JsCanonical($name, $inputJson) {
    $tmpOut = Join-Path ([System.IO.Path]::GetTempPath()) ("js-canon-" + [guid]::NewGuid().ToString("N") + ".txt")
    $jsCode = "const p = require('$($projJs -replace '\\','/')'); const fs = require('fs'); const o = JSON.parse(fs.readFileSync('/dev/stdin','utf8')); const r = p.canonicalProfileJson('$name', o); if (r !== null) fs.writeFileSync('$tmpOut', r, 'utf8'); else fs.writeFileSync('$tmpOut', '', 'utf8');"
    $inputJson | & node -e $jsCode 2>$null
    try {
        return [System.IO.File]::ReadAllText($tmpOut, [System.Text.Encoding]::UTF8)
    } finally {
        Remove-Item -LiteralPath $tmpOut -Force -ErrorAction SilentlyContinue
    }
}

if (-not $nodeAvailable) {
    Write-Host "  SKIP  canonical JSON cross-check (node not available)" -ForegroundColor Yellow
} else {
    # Fixture 1: minimal profile.
    $fixture1Json = '{"name":"minimal"}'
    $jsResult1 = Get-JsCanonical "minimal" $fixture1Json
    $psObj1 = $fixture1Json | ConvertFrom-Json
    $psResult1 = ConvertTo-ConstructCanonicalJson -Name "minimal" -Object $psObj1
    ok "canonical: minimal profile byte-matches JS" ($psResult1 -ceq $jsResult1)
    if ($psResult1 -cne $jsResult1) {
        Write-Host "    PS length=$($psResult1.Length) JS length=$($jsResult1.Length)" -ForegroundColor DarkYellow
        Write-Host "    PS: $($psResult1.Substring(0, [Math]::Min(200, $psResult1.Length)))" -ForegroundColor DarkYellow
        Write-Host "    JS: $($jsResult1.Substring(0, [Math]::Min(200, $jsResult1.Length)))" -ForegroundColor DarkYellow
    }

    # Fixture 2: full profile with all MCP shapes.
    $fixture2Json = @'
{
  "name": "full-test",
  "repos": [
    {"url": "https://github.com/foo/bar", "directory": "bar"},
    {"url": "git@github.com:x/y.git"}
  ],
  "sdks": {"node": "20", "python": ["3.11", "3.12"]},
  "mcp": [
    "filesystem",
    {
      "name": "custom-server", "type": "stdio", "command": "npx",
      "args": ["-y", "@example/mcp"],
      "env": {"API_KEY": "secret"},
      "agents": ["claude", "codex"], "enabled": true
    },
    {
      "name": "web-api", "type": "http", "url": "https://api.example.com",
      "headers": {"X-Token": "abc"},
      "bearerTokenEnvVar": "MY_TOKEN", "enabled": false
    }
  ],
  "hostPackages": ["jq", "curl"],
  "provisionCommands": ["echo hello"],
  "tests": {"lint": "npm run lint", "unit": "npm test"}
}
'@
    $jsResult2 = Get-JsCanonical "full-test" $fixture2Json
    $psObj2 = $fixture2Json | ConvertFrom-Json
    $psResult2 = ConvertTo-ConstructCanonicalJson -Name "full-test" -Object $psObj2
    ok "canonical: full profile with all MCP shapes byte-matches JS" ($psResult2 -ceq $jsResult2)
    if ($psResult2 -cne $jsResult2) {
        Write-Host "    PS length=$($psResult2.Length) JS length=$($jsResult2.Length)" -ForegroundColor DarkYellow
        # Find first difference
        $minLen = [Math]::Min($psResult2.Length, $jsResult2.Length)
        for ($di = 0; $di -lt $minLen; $di++) {
            if ($psResult2[$di] -cne $jsResult2[$di]) {
                Write-Host ("    First diff at index {0}: PS='{1}' JS='{2}'" -f $di, $psResult2[$di], $jsResult2[$di]) -ForegroundColor DarkYellow
                $ctxStart = [Math]::Max(0, $di - 20)
                $ctxLen = [Math]::Min(40, $psResult2.Length - $ctxStart)
                Write-Host "    PS context: ...$($psResult2.Substring($ctxStart, $ctxLen))..." -ForegroundColor DarkYellow
                $ctxLen2 = [Math]::Min(40, $jsResult2.Length - $ctxStart)
                Write-Host "    JS context: ...$($jsResult2.Substring($ctxStart, $ctxLen2))..." -ForegroundColor DarkYellow
                break
            }
        }
    }

    # Fixture 3: unicode + control chars + quotes/backslashes.
    $fixture3Json = @'
{
  "name": "special-chars",
  "repos": [{"url": "https://example.com/\"repo\""}],
  "sdks": {},
  "mcp": [],
  "hostPackages": [],
  "provisionCommands": ["echo \"hello\tworld\""],
  "tests": {}
}
'@
    $jsResult3 = Get-JsCanonical "special-chars" $fixture3Json
    $psObj3 = $fixture3Json | ConvertFrom-Json
    $psResult3 = ConvertTo-ConstructCanonicalJson -Name "special-chars" -Object $psObj3
    ok "canonical: special chars (quotes, backslashes) byte-matches JS" ($psResult3 -ceq $jsResult3)
    if ($psResult3 -cne $jsResult3) {
        Write-Host "    PS length=$($psResult3.Length) JS length=$($jsResult3.Length)" -ForegroundColor DarkYellow
    }

    # Fixture 4: messy key order + unknown keys (should be sanitised out).
    $fixture4Json = @'
{
  "tests": {"z": "last", "a": "first"},
  "unknown_key": "should be dropped",
  "name": "messy",
  "hostPackages": ["pkg1"],
  "repos": [{"url": "https://example.com/r", "unknown_repo_key": "drop"}],
  "mcp": [],
  "sdks": {"beta": "2", "alpha": "1"},
  "provisionCommands": []
}
'@
    $jsResult4 = Get-JsCanonical "messy" $fixture4Json
    $psObj4 = $fixture4Json | ConvertFrom-Json
    $psResult4 = ConvertTo-ConstructCanonicalJson -Name "messy" -Object $psObj4
    ok "canonical: messy key order + unknown keys byte-matches JS" ($psResult4 -ceq $jsResult4)
    if ($psResult4 -cne $jsResult4) {
        Write-Host "    PS length=$($psResult4.Length) JS length=$($jsResult4.Length)" -ForegroundColor DarkYellow
        Write-Host "    PS: $psResult4" -ForegroundColor DarkYellow
        Write-Host "    JS: $jsResult4" -ForegroundColor DarkYellow
    }

    # Fixture 5: empty name -> null.
    $psResultNull = ConvertTo-ConstructCanonicalJson -Name "" -Object ([pscustomobject]@{name=""})
    ok "canonical: empty name returns null" ($null -eq $psResultNull)

    # Fixture 6: unicode (non-ASCII) passthrough.
    $fixture6Json = '{"name":"unicode-test","repos":[],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":["echo \"' + [char]0xe9 + [char]0xfc + [char]0xf1 + '\""],"tests":{}}'
    $jsResult6 = Get-JsCanonical "unicode-test" $fixture6Json
    $psObj6 = $fixture6Json | ConvertFrom-Json
    $psResult6 = ConvertTo-ConstructCanonicalJson -Name "unicode-test" -Object $psObj6
    ok "canonical: unicode passthrough byte-matches JS" ($psResult6 -ceq $jsResult6)

    # Fixture 7: MCP with inferred type (no explicit type key).
    $fixture7Json = '{"name":"infer-type","mcp":[{"name":"s1","command":"foo"},{"name":"s2","url":"http://bar"}]}'
    $jsResult7 = Get-JsCanonical "infer-type" $fixture7Json
    $psObj7 = $fixture7Json | ConvertFrom-Json
    $psResult7 = ConvertTo-ConstructCanonicalJson -Name "infer-type" -Object $psObj7
    ok "canonical: MCP inferred type byte-matches JS" ($psResult7 -ceq $jsResult7)
}

# ── Test-ConstructProfile agreement with validateProfile ─────────────────────
if (-not $nodeAvailable) {
    Write-Host "  SKIP  validator cross-check (node not available)" -ForegroundColor Yellow
} else {
    # Helper to get JS validation result via a temp file to avoid pipeline issues.
    function Get-JsValidation($name, $json) {
        $tmpIn  = Join-Path ([System.IO.Path]::GetTempPath()) ("js-val-in-" + [guid]::NewGuid().ToString("N") + ".json")
        $tmpOut = Join-Path ([System.IO.Path]::GetTempPath()) ("js-val-out-" + [guid]::NewGuid().ToString("N") + ".json")
        [System.IO.File]::WriteAllText($tmpIn, $json, (New-Object System.Text.UTF8Encoding $false))
        $js = "const p = require('$($projJs -replace '\\','/')'); const fs = require('fs'); const o = JSON.parse(fs.readFileSync('$tmpIn','utf8')); const r = p.validateProfile('$name', o); fs.writeFileSync('$tmpOut', JSON.stringify(r), 'utf8');"
        & node -e $js 2>$null
        try {
            $resultJson = [System.IO.File]::ReadAllText($tmpOut, [System.Text.Encoding]::UTF8)
            return ($resultJson | ConvertFrom-Json)
        } finally {
            Remove-Item -LiteralPath $tmpIn, $tmpOut -Force -ErrorAction SilentlyContinue
        }
    }

    # Valid profile.
    $vJson = '{"name":"valid","repos":[{"url":"https://github.com/a/b"}],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":[],"tests":{}}'
    $vObj = $vJson | ConvertFrom-Json
    $psV = Test-ConstructProfile -Name "valid" -Object $vObj
    $jsV = Get-JsValidation "valid" $vJson
    ok "validate: valid profile agrees (both ok)" ($psV.Ok -eq $jsV.ok)

    # Invalid: missing name.
    $invJson = '{"repos":[]}'
    $invObj = $invJson | ConvertFrom-Json
    $psInv = Test-ConstructProfile -Name "test" -Object $invObj
    $jsInv = Get-JsValidation "test" $invJson
    ok "validate: missing name agrees (both not ok)" ($psInv.Ok -eq $false -and $jsInv.ok -eq $false)

    # Invalid: name mismatch.
    $mmJson = '{"name":"wrong","repos":[]}'
    $mmObj = $mmJson | ConvertFrom-Json
    $psMm = Test-ConstructProfile -Name "expected" -Object $mmObj
    $jsMm = Get-JsValidation "expected" $mmJson
    ok "validate: name mismatch agrees (both not ok)" ($psMm.Ok -eq $false -and $jsMm.ok -eq $false)

    # Invalid: unknown key.
    $ukJson = '{"name":"uk","badkey":"x"}'
    $ukObj = $ukJson | ConvertFrom-Json
    $psUk = Test-ConstructProfile -Name "uk" -Object $ukObj
    $jsUk = Get-JsValidation "uk" $ukJson
    ok "validate: unknown key agrees (both not ok)" ($psUk.Ok -eq $false -and $jsUk.ok -eq $false)

    # Invalid: repos not an array.
    $rnJson = '{"name":"rn","repos":"not-array"}'
    $rnObj = $rnJson | ConvertFrom-Json
    $psRn = Test-ConstructProfile -Name "rn" -Object $rnObj
    $jsRn = Get-JsValidation "rn" $rnJson
    ok "validate: repos not array agrees" ($psRn.Ok -eq $false -and $jsRn.ok -eq $false)

    # Valid: profile with only name.
    $onJson = '{"name":"onlyname"}'
    $onObj = $onJson | ConvertFrom-Json
    $psOn = Test-ConstructProfile -Name "onlyname" -Object $onObj
    $jsOn = Get-JsValidation "onlyname" $onJson
    ok "validate: name-only profile agrees (both ok)" ($psOn.Ok -eq $jsOn.ok)

    # Invalid: MCP bad type.
    $mtJson = '{"name":"mt","mcp":[{"name":"s","type":"badtype"}]}'
    $mtObj = $mtJson | ConvertFrom-Json
    $psMt = Test-ConstructProfile -Name "mt" -Object $mtObj
    $jsMt = Get-JsValidation "mt" $mtJson
    ok "validate: MCP bad type agrees" ($psMt.Ok -eq $false -and $jsMt.ok -eq $false)

    # Invalid: MCP no command or url.
    $mcJson = '{"name":"mc","mcp":[{"name":"s"}]}'
    $mcObj = $mcJson | ConvertFrom-Json
    $psMc = Test-ConstructProfile -Name "mc" -Object $mcObj
    $jsMc = Get-JsValidation "mc" $mcJson
    ok "validate: MCP no command/url agrees" ($psMc.Ok -eq $false -and $jsMc.ok -eq $false)
}

# ── Real-git sync tick test ──────────────────────────────────────────────────
# Uses a temp config dir with a real git repo. The VM store is simulated by an
# injectable SshInvoker that reads/writes to a local temp directory.
$tmpBase = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-test-" + [guid]::NewGuid().ToString("N"))
$configDir = Join-Path $tmpBase "config"
$vmStoreDir = Join-Path $tmpBase "vmstore"
New-Item -ItemType Directory -Path (Join-Path $configDir "projects") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $configDir "manifest") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $configDir "bases") -Force | Out-Null
New-Item -ItemType Directory -Path $vmStoreDir -Force | Out-Null

# Mock SSH invoker: runs the given bash script against $vmStoreDir instead of
# a real VM. Rewrites /opt/construct/projects to the local vmStoreDir.
# Store the VM store dir path in script scope so scriptblocks can access it.
$script:testVmStoreDir = $vmStoreDir

$sshRead = {
    param([string]$script)
    $rewritten = $script -replace '/opt/construct/projects', $script:testVmStoreDir
    $tmpScript = Join-Path ([System.IO.Path]::GetTempPath()) ("ssh-mock-" + [guid]::NewGuid().ToString("N") + ".sh")
    [System.IO.File]::WriteAllText($tmpScript, $rewritten, (New-Object System.Text.UTF8Encoding $false))
    try {
        $out = & bash $tmpScript 2>$null
        $code = $LASTEXITCODE
        $outStr = if ($null -ne $out) { ($out -join "`n") } else { "" }
        return [pscustomobject]@{ Code = $code; Output = $outStr }
    } finally {
        Remove-Item -LiteralPath $tmpScript -Force -ErrorAction SilentlyContinue
    }
}

$sshWrite = {
    param([string]$script)
    $rewritten = $script -replace '/opt/construct/projects', $script:testVmStoreDir
    $tmpScript = Join-Path ([System.IO.Path]::GetTempPath()) ("ssh-mock-w-" + [guid]::NewGuid().ToString("N") + ".sh")
    [System.IO.File]::WriteAllText($tmpScript, $rewritten, (New-Object System.Text.UTF8Encoding $false))
    try {
        $out = & bash $tmpScript 2>$null
        $code = $LASTEXITCODE
        $outStr = if ($null -ne $out) { ($out -join "`n") } else { "" }
        return [pscustomobject]@{ Code = $code; Output = $outStr }
    } finally {
        Remove-Item -LiteralPath $tmpScript -Force -ErrorAction SilentlyContinue
    }
}

try {
    # Init the config repo.
    $initOk = Initialize-ConstructConfigRepo -ConfigDir $configDir
    ok "sync: repo initialised" $initOk

    # Verify main and vm branches exist.
    $branches = & git -C $configDir branch 2>$null
    $hasBranches = ("$branches" -match 'main') -and ("$branches" -match 'vm')
    ok "sync: main + vm branches exist" $hasBranches

    # Repo hardening (regression: the config repo inherits the host's global git
    # config; commit.gpgsign=true with no key made every headless commit fail and
    # left phantom "unresolved merges"). Initialize-ConstructConfigRepo must pin
    # signing off and LF locally.
    $gpgLocal = (& git -C $configDir config --local commit.gpgsign 2>$null)
    ok "harden: commit.gpgsign=false locally" ("$gpgLocal".Trim() -eq "false")
    $crlfLocal = (& git -C $configDir config --local core.autocrlf 2>$null)
    ok "harden: core.autocrlf=false locally" ("$crlfLocal".Trim() -eq "false")
    & git -C $configDir config --local core.hooksPath 2>$null | Out-Null
    ok "harden: core.hooksPath emptied locally" ($LASTEXITCODE -eq 0)
    ok "harden: .gitattributes pins LF" (Test-Path -LiteralPath (Join-Path $configDir ".gitattributes"))
    # Bookkeeping files (.gitattributes + .migrated) are ignored so they neither
    # clutter status nor trip git's untracked-overwrite merge guard.
    $excText = ""
    $excPath = Join-Path (Join-Path (Join-Path $configDir ".git") "info") "exclude"
    if (Test-Path -LiteralPath $excPath) { $excText = [System.IO.File]::ReadAllText($excPath) }
    ok "harden: .git/info/exclude ignores .gitattributes" ($excText -match '(?m)^\.gitattributes$')
    ok "harden: .git/info/exclude ignores .migrated" ($excText -match '(?m)^\.migrated$')
    [System.IO.File]::WriteAllText((Join-Path $configDir ".migrated"), "1")
    $porcelain = @(& git -C $configDir status --porcelain 2>$null | Where-Object { $_ -match '\.migrated|\.gitattributes' })
    ok "harden: git status hides the ignored bookkeeping files" ($porcelain.Count -eq 0)
    # A manual commit (no engine $gitArgs) must survive an inherited failing hook,
    # because the empty hooksPath is persisted repo-locally by the hardening step.
    # Run in a throwaway repo so it can't perturb the scenario repo below.
    $hookRepo = Join-Path $tmpBase "hookrepo"
    New-Item -ItemType Directory -Path (Join-Path $hookRepo "projects") -Force | Out-Null
    $ghDir = Join-Path $tmpBase "globalhooks"
    New-Item -ItemType Directory -Path $ghDir -Force | Out-Null
    $hardenEnc = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText((Join-Path $ghDir "pre-commit"), "#!/bin/sh`nexit 1`n", $hardenEnc)
    if ($IsLinux -or $IsMacOS) { & chmod +x (Join-Path $ghDir "pre-commit") 2>$null | Out-Null }
    $null = Initialize-ConstructConfigRepo -ConfigDir $hookRepo
    & git -C $hookRepo config --local core.hooksPath $ghDir 2>$null | Out-Null   # simulate the inherited hook
    Set-ConstructConfigRepoHardening -ConfigDir $hookRepo                         # re-harden empties it
    [System.IO.File]::WriteAllText((Join-Path $hookRepo "projects/hooktest.json"),
        (ConvertTo-ConstructCanonicalJson -Name "hooktest" -Object ([pscustomobject]@{ name = "hooktest" })), $hardenEnc)
    & git -C $hookRepo @("-c","user.name=u","-c","user.email=u@u") add -A 2>$null | Out-Null
    & git -C $hookRepo @("-c","user.name=u","-c","user.email=u@u") commit -m "manual" 2>$null | Out-Null
    ok "harden: manual commit survives an inherited failing hook" ($LASTEXITCODE -eq 0)

    # ── Scenario 1: Host adds a profile, VM store empty -> seed ──────────────
    $profile1 = @'
{
  "name": "proj-a",
  "repos": [{"url": "https://github.com/test/proj-a"}],
  "sdks": {},
  "mcp": [],
  "hostPackages": [],
  "provisionCommands": [],
  "tests": {}
}
'@
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText((Join-Path $configDir "projects/proj-a.json"), $profile1, $utf8NoBom)

    $r1 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" -SeedOnly `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "sync: seed-only ran" $r1.Ran
    ok "sync: seed-only seeded" $r1.Seeded

    # Check that the profile landed in the VM store.
    $vmFile = Join-Path $vmStoreDir "proj-a.json"
    ok "sync: seeded profile exists in VM store" (Test-Path -LiteralPath $vmFile)

    # ── Scenario 2: VM edits a profile, host is unchanged -> merge ───────────
    # Edit the VM copy.
    $profile1Edited = $profile1 -replace '"tests": {}', '"tests": {"unit": "npm test"}'
    [System.IO.File]::WriteAllText($vmFile, $profile1Edited, $utf8NoBom)

    $r2 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "sync: merge ran" $r2.Ran
    ok "sync: merge ok" $r2.Ok
    ok "sync: merge merged" $r2.Merged

    # Host file should now have the VM edit.
    $hostContent = [System.IO.File]::ReadAllText((Join-Path $configDir "projects/proj-a.json"), [System.Text.Encoding]::UTF8)
    ok "sync: host file has the VM edit" ($hostContent -match "npm test")

    # ── Scenario 3: Host edits, VM unchanged -> write-back ───────────────────
    $profile1HostEdit = $hostContent -replace '"hostPackages": \[\]', '"hostPackages": ["curl"]'
    [System.IO.File]::WriteAllText((Join-Path $configDir "projects/proj-a.json"), $profile1HostEdit, $utf8NoBom)

    $r3 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "sync: host-edit ran" $r3.Ran
    ok "sync: host-edit ok" $r3.Ok

    # VM store should now have the host edit.
    $vmContent = [System.IO.File]::ReadAllText($vmFile, [System.Text.Encoding]::UTF8)
    ok "sync: VM store has the host edit" ($vmContent -match "curl")

    # ── Scenario 3b: clean pending merge is auto-committed ───────────────────
    # Recreate the state a user hit in the panel: MERGE_HEAD exists, but there
    # are no unmerged files left. The sync tick should commit it with Construct's
    # per-command identity instead of requiring VS Code/Git UI or global git config.
    $hostPendingContent = ConvertTo-ConstructCanonicalJson -Name "host-pending" -Object ([pscustomobject]@{
        name = "host-pending"
        repos = @([pscustomobject]@{ url = "https://github.com/test/host-pending" })
        provisionCommands = @("npm ci")
    })
    [System.IO.File]::WriteAllText((Join-Path $configDir "projects/host-pending.json"), $hostPendingContent, $utf8NoBom)
    & git -C $configDir @("-c","user.name=T","-c","user.email=t@t") add -A 2>$null | Out-Null
    & git -C $configDir @("-c","user.name=T","-c","user.email=t@t") commit -m "host pending clean" 2>$null | Out-Null

    & git -C $configDir checkout vm 2>$null | Out-Null
    $vmPendingContent = ConvertTo-ConstructCanonicalJson -Name "vm-pending" -Object ([pscustomobject]@{
        name = "vm-pending"
        repos = @([pscustomobject]@{ url = "https://github.com/test/vm-pending" })
        sdks = [pscustomobject]@{ node = "22" }
    })
    [System.IO.File]::WriteAllText((Join-Path $configDir "projects/vm-pending.json"), $vmPendingContent, $utf8NoBom)
    & git -C $configDir @("-c","user.name=T","-c","user.email=t@t") add -A 2>$null | Out-Null
    & git -C $configDir @("-c","user.name=T","-c","user.email=t@t") commit -m "vm pending clean" 2>$null | Out-Null
    & git -C $configDir checkout main 2>$null | Out-Null
    & git -C $configDir merge --no-ff --no-commit vm 2>$null | Out-Null
    ok "sync: pending clean merge has MERGE_HEAD" (Test-Path -LiteralPath (Join-Path $configDir ".git/MERGE_HEAD"))
    $pendingUnmerged = @(& git -C $configDir diff --name-only --diff-filter=U 2>$null)
    ok "sync: pending clean merge has no unmerged files" ($pendingUnmerged.Count -eq 0)

    [System.IO.File]::WriteAllText((Join-Path $vmStoreDir "vm-pending.json"), $vmPendingContent, $utf8NoBom)
    # Enforce commit signing with no working key — the exact host state that used to
    # leave the merge stuck. The recovery commit must still succeed (hermetically).
    & git -C $configDir config commit.gpgsign true 2>$null | Out-Null
    & git -C $configDir config gpg.program /bin/false 2>$null | Out-Null
    $r3b = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "sync: pending clean merge recovered (under enforced signing)" $r3b.Ok
    ok "sync: pending clean merge committed (under enforced signing)" (-not (Test-Path -LiteralPath (Join-Path $configDir ".git/MERGE_HEAD")))
    $hostAfterPending = [System.IO.File]::ReadAllText((Join-Path $configDir "projects/host-pending.json"), [System.Text.Encoding]::UTF8)
    $vmAfterPendingContent = [System.IO.File]::ReadAllText((Join-Path $configDir "projects/vm-pending.json"), [System.Text.Encoding]::UTF8)
    ok "sync: pending clean merge kept host side" ($hostAfterPending -match "npm ci")
    ok "sync: pending clean merge kept VM side" ($vmAfterPendingContent -match '"node": "22"')
    $mainAfterPending = & git -C $configDir rev-parse main 2>$null
    $vmAfterPending = & git -C $configDir rev-parse vm 2>$null
    ok "sync: pending clean merge advanced vm ref" ("$mainAfterPending".Trim() -eq "$vmAfterPending".Trim())

    # ── Scenario 4: Reserved name in VM store is skipped ─────────────────────
    [System.IO.File]::WriteAllText((Join-Path $vmStoreDir "default.json"), '{"name":"default"}', $utf8NoBom)

    $r4 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "sync: reserved name in VM store produces a warning" (@($r4.Warnings | Where-Object { $_ -match "Reserved" }).Count -gt 0)

    # Clean up reserved name file.
    Remove-Item -LiteralPath (Join-Path $vmStoreDir "default.json") -Force -ErrorAction SilentlyContinue

    # ── Scenario 5: Invalid VM file is skipped ───────────────────────────────
    [System.IO.File]::WriteAllText((Join-Path $vmStoreDir "bad.json"), '{"name":"wrong-name"}', $utf8NoBom)

    $r5 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "sync: invalid VM file produces a warning" (@($r5.Warnings | Where-Object { $_ -match "Invalid|bad" }).Count -gt 0)
    ok "sync: invalid VM file listed in SkippedInvalid" ($r5.SkippedInvalid.Count -gt 0)

    Remove-Item -LiteralPath (Join-Path $vmStoreDir "bad.json") -Force -ErrorAction SilentlyContinue

    # ── Scenario 6: VM file is canonicalized on commit (fix 5) ──────────────
    # Write a non-canonical (4-space-indent) valid profile to the VM store.
    $nonCanonProfile = @'
{
    "name": "proj-a",
    "repos": [{"url": "https://github.com/test/proj-a"}],
    "sdks": {},
    "mcp": [],
    "hostPackages": ["curl"],
    "provisionCommands": [],
    "tests": {"unit": "npm test"}
}
'@
    [System.IO.File]::WriteAllText($vmFile, $nonCanonProfile, $utf8NoBom)

    $r6 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "sync: non-canonical VM file tick ok" $r6.Ok

    # After the tick, the host file should be in canonical form (2-space indent).
    $hostAfter6 = [System.IO.File]::ReadAllText((Join-Path $configDir "projects/proj-a.json"), [System.Text.Encoding]::UTF8)
    $hostObj6 = $hostAfter6 | ConvertFrom-Json
    $expectedCanon6 = ConvertTo-ConstructCanonicalJson -Name "proj-a" -Object $hostObj6
    ok "sync: host file is canonical after merging non-canonical VM edit" ($hostAfter6 -ceq $expectedCanon6)

    # ── Scenario 7: Invalid VM file excluded from write-back (fix 2) ────────
    # Put an invalid file in the VM store; it should NOT be deleted by write-back.
    $invalidVmFile = Join-Path $vmStoreDir "wip.json"
    [System.IO.File]::WriteAllText($invalidVmFile, '{"name":"wrong-name-wip"}', $utf8NoBom)

    $r7 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "sync: invalid VM file still exists after tick (not deleted by write-back)" (Test-Path -LiteralPath $invalidVmFile)
    ok "sync: invalid VM file skipped in SkippedInvalid" ($r7.SkippedInvalid.Count -gt 0)

    Remove-Item -LiteralPath $invalidVmFile -Force -ErrorAction SilentlyContinue

    # ── Scenario 8: Reserved name in config/projects excluded from commit (fix 10) ──
    $reservedFile = Join-Path $configDir "projects/default.json"
    [System.IO.File]::WriteAllText($reservedFile, '{"name":"default"}', $utf8NoBom)

    $r8 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    # default.json should NOT appear in git ls-tree main.
    $lsTree8 = & git -C $configDir ls-tree --name-only main -- projects/ 2>$null
    $hasDefault = @($lsTree8 | Where-Object { $_ -match 'default\.json' }).Count -gt 0
    ok "sync: reserved default.json NOT tracked in main" (-not $hasDefault)
    ok "sync: reserved name produces a warning" (@($r8.Warnings | Where-Object { $_ -match "Reserved.*default" }).Count -gt 0)

    Remove-Item -LiteralPath $reservedFile -Force -ErrorAction SilentlyContinue

    # ── Scenario 9: NOSTORE marker -> fresh-VM seed (fix 1) ─────────────────
    # Simulate a VM wipe: delete the VM store dir entirely, but the vm branch
    # has profiles from previous ticks.
    Remove-Item -LiteralPath $vmStoreDir -Recurse -Force -ErrorAction SilentlyContinue
    # vmStoreDir no longer exists -> NOSTORE marker -> fresh-VM seed path.

    $r9 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "sync: fresh-VM seed path triggered when store dir absent" $r9.Seeded
    ok "sync: fresh-VM seed path ok" $r9.Ok

    # VM store dir should now exist with the seeded profile.
    $vmFile9 = Join-Path $vmStoreDir "proj-a.json"
    ok "sync: seeded profile after VM wipe" (Test-Path -LiteralPath $vmFile9)

    # ── Scenario 10: a previously-SYNCED profile that becomes INVALID on the VM
    #    must NOT delete the host copy (skip-invalid is not a deletion). Mirrors
    #    the JS regression; catches the vm-branch mass-delete bug. ───────────────
    # proj-a is currently synced on host + VM store (re-seeded by scenario 9).
    ok "stale-invalid: proj-a present on host before corruption" (Test-Path -LiteralPath (Join-Path $configDir "projects/proj-a.json"))
    # The agent half-writes proj-a.json on the VM, corrupting it (repos as a string).
    [System.IO.File]::WriteAllText($vmFile9, '{"name":"proj-a","repos":"corrupt"}', $utf8NoBom)

    $r10 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "stale-invalid: tick ok" $r10.Ok
    ok "stale-invalid: proj-a listed in SkippedInvalid" (@($r10.SkippedInvalid | Where-Object { $_.Name -eq "proj-a" }).Count -gt 0)
    # The corrupt VM edit must be a skip, NOT a deletion: host keeps the last valid copy.
    ok "stale-invalid: host proj-a PRESERVED (not deleted)" (Test-Path -LiteralPath (Join-Path $configDir "projects/proj-a.json"))
    $lsTree10 = & git -C $configDir ls-tree --name-only main -- projects/ 2>$null
    ok "stale-invalid: proj-a still tracked on main" (@($lsTree10 | Where-Object { $_ -match 'proj-a\.json' }).Count -gt 0)

    # After the agent fixes the file, the new valid edit should sync through.
    $projAFixed = '{"name":"proj-a","repos":[{"url":"https://github.com/test/proj-a"}],"sdks":{},"mcp":[],"hostPackages":["wget"],"provisionCommands":[],"tests":{}}'
    [System.IO.File]::WriteAllText($vmFile9, $projAFixed, $utf8NoBom)
    $r11 = Invoke-ConstructConfigSync -ConfigDir $configDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "stale-invalid: recovery tick ok" $r11.Ok
    $hostAfterFix = [System.IO.File]::ReadAllText((Join-Path $configDir "projects/proj-a.json"), [System.Text.Encoding]::UTF8)
    ok "stale-invalid: fixed edit synced to host" ($hostAfterFix -match "wget")

} finally {
    Remove-Item -LiteralPath $tmpBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Import-ConstructConfigs test ─────────────────────────────────────────────
$impBase = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-imp-" + [guid]::NewGuid().ToString("N"))
$impConfigDir = Join-Path $impBase "config"
$impSrcDir    = Join-Path $impBase "source"
New-Item -ItemType Directory -Path (Join-Path $impConfigDir "projects") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $impConfigDir "manifest") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $impConfigDir "bases") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $impSrcDir "projects") -Force | Out-Null

try {
    # Init config repo.
    $null = Initialize-ConstructConfigRepo -ConfigDir $impConfigDir

    # Create source profiles.
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    $srcProfile = '{"name":"imported","repos":[{"url":"https://github.com/test/imported"}],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":[],"tests":{}}'
    [System.IO.File]::WriteAllText((Join-Path $impSrcDir "projects/imported.json"), $srcProfile, $utf8NoBom)

    # Also add a reserved name and a .sample file that should be skipped.
    [System.IO.File]::WriteAllText((Join-Path $impSrcDir "projects/default.json"), '{"name":"default"}', $utf8NoBom)
    [System.IO.File]::WriteAllText((Join-Path $impSrcDir "projects/example.sample.json"), '{}', $utf8NoBom)

    $impResult = Import-ConstructConfigs -ConfigDir $impConfigDir -SourceDir $impSrcDir
    ok "import: imported profile from source dir" ($impResult.Imported -contains "imported")
    ok "import: skipped reserved name 'default'" ($impResult.Imported -notcontains "default")

    # Check the file exists.
    $impFile = Join-Path $impConfigDir "projects/imported.json"
    ok "import: file written to config dir" (Test-Path -LiteralPath $impFile)

    # Check it is canonical.
    $impContent = [System.IO.File]::ReadAllText($impFile, [System.Text.Encoding]::UTF8)
    $impObj = $impContent | ConvertFrom-Json
    $expectedCanonical = ConvertTo-ConstructCanonicalJson -Name "imported" -Object $impObj
    ok "import: written file is canonical" ($impContent -ceq $expectedCanonical)

    # CLI collision: importing the same name again should throw.
    $threw = $false
    try {
        $null = Import-ConstructConfigs -ConfigDir $impConfigDir -SourceDir $impSrcDir
    } catch {
        $threw = $true
        ok "import: collision throws with a clear message" ($_.Exception.Message -match "Name collision")
    }
    ok "import: collision throws" $threw

    # D16 discovery: top-level *.json when no projects/ subdir.
    $impSrcFlat = Join-Path $impBase "source-flat"
    New-Item -ItemType Directory -Path $impSrcFlat -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $impSrcFlat "flat-profile.json"),
        '{"name":"flat-profile","repos":[],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":[],"tests":{}}', $utf8NoBom)

    $impFlat = Import-ConstructConfigs -ConfigDir $impConfigDir -SourceDir $impSrcFlat
    ok "import: discovers top-level *.json when no projects/ subdir" ($impFlat.Imported -contains "flat-profile")

    # ── Staging clone must FAIL CLOSED on a fetch failure (external review) ──────
    # A refresh of an EXISTING clone whose remote fetch fails must NOT silently
    # return the stale clone (which would import out-of-date profiles).
    $savedLA2 = $env:LOCALAPPDATA
    $stagingBase = Join-Path $impBase "staging-home"
    $srcRepo     = Join-Path $impBase "src-remote"
    try {
        $env:LOCALAPPDATA = $stagingBase
        # Build a real source repo with one commit on main.
        New-Item -ItemType Directory -Path (Join-Path $srcRepo "projects") -Force | Out-Null
        [System.IO.File]::WriteAllText((Join-Path $srcRepo "projects/x.json"),
            '{"name":"x","repos":[],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":[],"tests":{}}', $utf8NoBom)
        & git -C $srcRepo init -q 2>$null | Out-Null
        & git -C $srcRepo symbolic-ref HEAD refs/heads/main 2>$null | Out-Null
        & git -C $srcRepo -c user.name=t -c user.email=t@t add -A 2>$null | Out-Null
        & git -C $srcRepo -c user.name=t -c user.email=t@t commit -q -m init 2>$null | Out-Null

        # First refresh clones successfully.
        $cloneDir = Update-ConstructStagingClone -SourceRepo $srcRepo
        ok "staging: initial clone succeeds" (Test-Path -LiteralPath (Join-Path $cloneDir ".git"))

        # Break the clone's origin so the next fetch fails.
        & git -C $cloneDir remote set-url origin (Join-Path $impBase "does-not-exist-repo") 2>$null | Out-Null

        # A refresh WITHOUT -NoFetch must now throw (fail closed), not return stale content.
        $fetchThrew = $false
        try { $null = Update-ConstructStagingClone -SourceRepo $srcRepo }
        catch { $fetchThrew = $true }
        ok "staging: refresh throws when fetch fails (fail closed)" $fetchThrew

        # -NoFetch is the sanctioned way to reuse the existing clone without a fetch.
        $noFetchOk = $false
        try { $d = Update-ConstructStagingClone -SourceRepo $srcRepo -NoFetch; $noFetchOk = (Test-Path -LiteralPath (Join-Path $d ".git")) }
        catch { $noFetchOk = $false }
        ok "staging: -NoFetch reuses the existing clone without fetching" $noFetchOk
    } finally {
        if ($null -ne $savedLA2) { $env:LOCALAPPDATA = $savedLA2 } else { Remove-Item Env:\LOCALAPPDATA -ErrorAction SilentlyContinue }
    }

} finally {
    Remove-Item -LiteralPath $impBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Get-ConstructConfigDir ───────────────────────────────────────────────────
$savedLA = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = "/tmp/test-la"
    $cd = Get-ConstructConfigDir
    ok "configdir: uses LOCALAPPDATA" ($cd -eq (Join-Path "/tmp/test-la/The-Construct" "config"))

    $env:LOCALAPPDATA = ""
    $savedTemp = $env:TEMP
    $env:TEMP = "/tmp/test-temp"
    $cd2 = Get-ConstructConfigDir
    ok "configdir: falls back to TEMP" ($cd2 -eq (Join-Path "/tmp/test-temp/The-Construct" "config"))
    $env:TEMP = $savedTemp
} finally {
    if ($null -ne $savedLA) { $env:LOCALAPPDATA = $savedLA }
    else { Remove-Item Env:\LOCALAPPDATA -ErrorAction SilentlyContinue }
}

# ── Test-ConstructGitAvailable ───────────────────────────────────────────────
ok "git-available: true on this box" (Test-ConstructGitAvailable)

# ── Ensure-ConstructGit -AutoMode on a box that has git ──────────────────────
ok "ensure-git: returns true when git is present" (Ensure-ConstructGit -AutoMode)

# ── ConvertTo-ConstructJsonString edge cases ─────────────────────────────────
ok "jsonstr: empty string" ((ConvertTo-ConstructJsonString -Value "") -ceq '""')
ok "jsonstr: null" ((ConvertTo-ConstructJsonString -Value $null) -ceq '""')
ok "jsonstr: double-quote" ((ConvertTo-ConstructJsonString -Value '"') -ceq '"\""')
ok "jsonstr: backslash" ((ConvertTo-ConstructJsonString -Value '\') -ceq '"\\"')
ok "jsonstr: tab" ((ConvertTo-ConstructJsonString -Value "`t") -ceq '"\t"')
ok "jsonstr: newline" ((ConvertTo-ConstructJsonString -Value "`n") -ceq '"\n"')
# Control char 0x01 -> .
$ctrl1 = [char]1
$expected01 = '"' + "\u0001" + '"'
ok "jsonstr: control char 0x01" ((ConvertTo-ConstructJsonString -Value "$ctrl1") -ceq $expected01)


# ── Case-sensitive comparisons in sanitize/validate (fix 6) ──────────────
if ($nodeAvailable) {
    # MCP type "STDIO" (uppercase) should be rejected by both engines.
    $csTypeJson = '{"name":"cstype","mcp":[{"name":"s","type":"STDIO","command":"foo"}]}'
    $csTypeObj = $csTypeJson | ConvertFrom-Json
    $psCsType = Test-ConstructProfile -Name "cstype" -Object $csTypeObj
    $jsCsType = Get-JsValidation "cstype" $csTypeJson
    ok "case-sensitive: MCP type STDIO rejected by PS" (-not $psCsType.Ok)
    ok "case-sensitive: MCP type STDIO rejected by JS" (-not $jsCsType.ok)
    ok "case-sensitive: MCP type STDIO agrees" ($psCsType.Ok -eq $jsCsType.ok)

    # Legacy enum "FILESYSTEM" (uppercase) should be rejected.
    $csEnumJson = '{"name":"csenum","mcp":["FILESYSTEM"]}'
    $csEnumObj = $csEnumJson | ConvertFrom-Json
    $psCsEnum = Test-ConstructProfile -Name "csenum" -Object $csEnumObj
    $jsCsEnum = Get-JsValidation "csenum" $csEnumJson
    ok "case-sensitive: legacy enum FILESYSTEM rejected by PS" (-not $psCsEnum.Ok)
    ok "case-sensitive: legacy enum FILESYSTEM agrees" ($psCsEnum.Ok -eq $jsCsEnum.ok)

    # Agents "CLAUDE" (uppercase) should be rejected.
    $csAgentJson = '{"name":"csagent","mcp":[{"name":"s","type":"stdio","command":"foo","agents":["CLAUDE"]}]}'
    $csAgentObj = $csAgentJson | ConvertFrom-Json
    $psCsAgent = Test-ConstructProfile -Name "csagent" -Object $csAgentObj
    $jsCsAgent = Get-JsValidation "csagent" $csAgentJson
    ok "case-sensitive: agents CLAUDE rejected by PS" (-not $psCsAgent.Ok)
    ok "case-sensitive: agents CLAUDE agrees" ($psCsAgent.Ok -eq $jsCsAgent.ok)

    # Sanitize: STDIO type is not recognized, but command:"foo" allows type
    # inference -> the entry is kept with inferred type "stdio" (matching JS).
    $sanStdio = Invoke-ConstructSanitizeProfile -Name "san" -Object ($csTypeJson | ConvertFrom-Json)
    ok "case-sensitive: sanitize infers stdio from command despite STDIO type" ($sanStdio.mcp.Count -eq 1 -and $sanStdio.mcp[0].type -ceq "stdio")

    # Sanitize: FILESYSTEM legacy enum stripped.
    $sanEnum = Invoke-ConstructSanitizeProfile -Name "san" -Object ($csEnumJson | ConvertFrom-Json)
    ok "case-sensitive: sanitize strips FILESYSTEM legacy enum" ($sanEnum.mcp.Count -eq 0)
}

# ── Integer key ordering in free-form maps (fix 7) ──────────────────────
if ($nodeAvailable) {
    # sdks with integer-like keys should sort numeric-first.
    $intKeyJson = '{"name":"intkeys","sdks":{"2":"a","10":"b","1":"c","zz":"d"},"tests":{"3":"x","1":"y","alpha":"z"}}'
    $jsIntKey = Get-JsCanonical "intkeys" $intKeyJson
    $psIntKeyObj = $intKeyJson | ConvertFrom-Json
    $psIntKey = ConvertTo-ConstructCanonicalJson -Name "intkeys" -Object $psIntKeyObj
    ok "int-key-order: sdks+tests with integer keys byte-matches JS" ($psIntKey -ceq $jsIntKey)
    if ($psIntKey -cne $jsIntKey) {
        Write-Host "    PS: $psIntKey" -ForegroundColor DarkYellow
        Write-Host "    JS: $jsIntKey" -ForegroundColor DarkYellow
    }
}

# ── Import manifest provenance fields (fix 9) ───────────────────────────
$impManifBase = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-manif-" + [guid]::NewGuid().ToString("N"))
$impManifConfigDir = Join-Path $impManifBase "config"
$impManifSrcDir    = Join-Path $impManifBase "source"
New-Item -ItemType Directory -Path (Join-Path $impManifConfigDir "projects") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $impManifConfigDir "manifest") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $impManifConfigDir "bases") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $impManifSrcDir "projects") -Force | Out-Null

try {
    $null = Initialize-ConstructConfigRepo -ConfigDir $impManifConfigDir

    # Create a git repo for the source to get provenance fields.
    $utf8NoBom2 = New-Object System.Text.UTF8Encoding $false
    $manifSrcProfile = '{"name":"provtest","repos":[],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":[],"tests":{}}'
    [System.IO.File]::WriteAllText((Join-Path $impManifSrcDir "projects/provtest.json"), $manifSrcProfile, $utf8NoBom2)
    & git -C $impManifSrcDir init 2>$null | Out-Null
    & git -C $impManifSrcDir -c user.name=Test -c user.email=test@test add -A 2>$null | Out-Null
    & git -C $impManifSrcDir -c user.name=Test -c user.email=test@test commit -m "init" 2>$null | Out-Null
    & git -C $impManifSrcDir branch -M main 2>$null | Out-Null

    $impManifResult = Import-ConstructConfigs -ConfigDir $impManifConfigDir -SourceDir $impManifSrcDir -SourceRepo "file://$impManifSrcDir"
    ok "manifest: imported provtest" ($impManifResult.Imported -contains "provtest")

    $manifPath = Join-Path $impManifConfigDir "manifest/provtest.json"
    if (Test-Path -LiteralPath $manifPath) {
        $manifContent = Get-Content -LiteralPath $manifPath -Raw | ConvertFrom-Json
        ok "manifest: has remoteUrl" ($null -ne $manifContent.remoteUrl)
        ok "manifest: has ref" ($manifContent.PSObject.Properties.Name -contains 'ref')
        ok "manifest: has baseCommit" ($manifContent.PSObject.Properties.Name -contains 'baseCommit')
        ok "manifest: has baseBlobSha" ($manifContent.PSObject.Properties.Name -contains 'baseBlobSha')
        ok "manifest: has pathInRemote" ($manifContent.PSObject.Properties.Name -contains 'pathInRemote')
        ok "manifest: has importedAs" ($manifContent.PSObject.Properties.Name -contains 'importedAs')
    } else {
        ok "manifest: provtest.json exists" $false
    }
} finally {
    Remove-Item -LiteralPath $impManifBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Empty sdks array is VALID (cross-engine agreement) ─────────────────
# {"name":"e","sdks":{"a":[]}} must be valid in BOTH engines: JS validateProfile
# uses Array.isArray(v) && v.every(str) (vacuously true for []) and
# project.schema.json has no minItems. The PS validator diverging here wedged
# the tick's post-merge gate on files the JS side happily commits.
$emptySdksJson = '{"name":"e","sdks":{"a":[]}}'
$emptySdksObj = $emptySdksJson | ConvertFrom-Json
$psEmptySdks = Test-ConstructProfile -Name "e" -Object $emptySdksObj
ok "empty-sdks: PS validator accepts an empty sdks array" $psEmptySdks.Ok
if (-not $psEmptySdks.Ok) {
    Write-Host "    errors: $($psEmptySdks.Errors -join '; ')" -ForegroundColor DarkYellow
}
if ($nodeAvailable) {
    $jsEmptySdks = Get-JsValidation "e" $emptySdksJson
    ok "empty-sdks: JS validator accepts it too" ($jsEmptySdks.ok -eq $true)
    ok "empty-sdks: cross-engine verdicts agree" ($psEmptySdks.Ok -eq $jsEmptySdks.ok)
    # And the canonical forms still byte-match (sanitize drops the empty array
    # identically in both engines).
    $jsEmptyCanon = Get-JsCanonical "e" $emptySdksJson
    $psEmptyCanon = ConvertTo-ConstructCanonicalJson -Name "e" -Object ($emptySdksJson | ConvertFrom-Json)
    ok "empty-sdks: canonical form byte-matches JS" ($psEmptyCanon -ceq $jsEmptyCanon)
}

# The tick must NOT skip this exact fixture when it arrives from the VM store.
$esBase = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-esdk-" + [guid]::NewGuid().ToString("N"))
$esConfigDir = Join-Path $esBase "config"
$esVmStoreDir = Join-Path $esBase "vmstore"
New-Item -ItemType Directory -Path (Join-Path $esConfigDir "projects") -Force | Out-Null
New-Item -ItemType Directory -Path $esVmStoreDir -Force | Out-Null
$script:testVmStoreDir = $esVmStoreDir
try {
    $null = Initialize-ConstructConfigRepo -ConfigDir $esConfigDir
    $utf8Es = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText((Join-Path $esVmStoreDir "e.json"), $emptySdksJson, $utf8Es)

    $rEs = Invoke-ConstructConfigSync -ConfigDir $esConfigDir -VmHost "dummy" `
        -SshReadInvoker $sshRead -SshWriteInvoker $sshWrite
    ok "empty-sdks: tick ok" $rEs.Ok
    ok "empty-sdks: tick does NOT skip the profile" (@($rEs.SkippedInvalid | Where-Object { "$_" -match '\be\b' }).Count -eq 0 -and @($rEs.SkippedInvalid).Count -eq 0)
    ok "empty-sdks: profile merged onto the host" (Test-Path -LiteralPath (Join-Path $esConfigDir "projects/e.json"))
} finally {
    Remove-Item -LiteralPath $esBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Test-ConstructRenameTarget (rename-target validation, D17/D1/D5) ────
$rtBase = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-rt-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path (Join-Path $rtBase "projects") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $rtBase "manifest") -Force | Out-Null
try {
    $utf8Rt = New-Object System.Text.UTF8Encoding $false

    ok "rename-target: fresh name is ok" (Test-ConstructRenameTarget -ConfigDir $rtBase -NewName "fresh").Ok
    ok "rename-target: reserved 'default' refused" (-not (Test-ConstructRenameTarget -ConfigDir $rtBase -NewName "default").Ok)
    ok "rename-target: reserved 'DEFAULT' refused (case-insensitive)" (-not (Test-ConstructRenameTarget -ConfigDir $rtBase -NewName "DEFAULT").Ok)
    ok "rename-target: reserved 'project.schema' refused" (-not (Test-ConstructRenameTarget -ConfigDir $rtBase -NewName "project.schema").Ok)
    ok "rename-target: empty name refused" (-not (Test-ConstructRenameTarget -ConfigDir $rtBase -NewName "  ").Ok)
    ok "rename-target: filename-unsafe name refused" (-not (Test-ConstructRenameTarget -ConfigDir $rtBase -NewName "a/b").Ok)

    # Existing profile without provenance: refused (no silent overwrite).
    [System.IO.File]::WriteAllText((Join-Path $rtBase "projects/taken.json"), '{"name":"taken"}', $utf8Rt)
    $rtTaken = Test-ConstructRenameTarget -ConfigDir $rtBase -NewName "taken" -RemoteUrl "https://r/x" -PathInRemote "projects/orig.json"
    ok "rename-target: existing profile refused" (-not $rtTaken.Ok)
    ok "rename-target: refusal names the collision" ($rtTaken.Reason -match "taken")

    # Existing profile WITH a same-provenance manifest: allowed (it is an update).
    $rtManif = '{"remoteUrl":"https://r/x","ref":"main","pathInRemote":"projects/orig.json","importedAs":"taken","baseCommit":"","baseBlobSha":""}'
    [System.IO.File]::WriteAllText((Join-Path $rtBase "manifest/taken.json"), $rtManif, $utf8Rt)
    ok "rename-target: same-provenance update allowed" (Test-ConstructRenameTarget -ConfigDir $rtBase -NewName "taken" -RemoteUrl "https://r/x" -PathInRemote "projects/orig.json").Ok
    ok "rename-target: different remote refused" (-not (Test-ConstructRenameTarget -ConfigDir $rtBase -NewName "taken" -RemoteUrl "https://r/OTHER" -PathInRemote "projects/orig.json").Ok)
    ok "rename-target: different pathInRemote refused" (-not (Test-ConstructRenameTarget -ConfigDir $rtBase -NewName "taken" -RemoteUrl "https://r/x" -PathInRemote "projects/other.json").Ok)
} finally {
    Remove-Item -LiteralPath $rtBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Import-ConstructConfigAs (validated rename import) ──────────────────
$raBase = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-ra-" + [guid]::NewGuid().ToString("N"))
$raConfigDir = Join-Path $raBase "config"
$raSrcDir = Join-Path $raBase "src"
New-Item -ItemType Directory -Path (Join-Path $raConfigDir "projects") -Force | Out-Null
New-Item -ItemType Directory -Path $raSrcDir -Force | Out-Null
try {
    $null = Initialize-ConstructConfigRepo -ConfigDir $raConfigDir
    $utf8Ra = New-Object System.Text.UTF8Encoding $false
    $raSrcFile = Join-Path $raSrcDir "orig.json"
    [System.IO.File]::WriteAllText($raSrcFile, '{"name":"orig","repos":[],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":[],"tests":{}}', $utf8Ra)

    # Happy path: import orig.json as 'orig-2'.
    $raOk = Import-ConstructConfigAs -ConfigDir $raConfigDir -SourceFile $raSrcFile -NewName "orig-2" `
        -RemoteUrl "https://r/repo" -PathInRemote "projects/orig.json"
    ok "rename-import: succeeds under the new name" $raOk.Ok
    $raDest = Join-Path $raConfigDir "projects/orig-2.json"
    ok "rename-import: file written under new name" (Test-Path -LiteralPath $raDest)
    if (Test-Path -LiteralPath $raDest) {
        $raObj = Get-Content -LiteralPath $raDest -Raw | ConvertFrom-Json
        ok "rename-import: name field rewritten" ($raObj.name -ceq "orig-2")
    }
    $raManifPath = Join-Path $raConfigDir "manifest/orig-2.json"
    ok "rename-import: manifest written" (Test-Path -LiteralPath $raManifPath)
    if (Test-Path -LiteralPath $raManifPath) {
        $raManif = Get-Content -LiteralPath $raManifPath -Raw | ConvertFrom-Json
        ok "rename-import: manifest keeps original pathInRemote + importedAs" ($raManif.pathInRemote -ceq "projects/orig.json" -and $raManif.importedAs -ceq "orig-2")
    }
    ok "rename-import: base written" (Test-Path -LiteralPath (Join-Path $raConfigDir "bases/orig-2.json"))

    # Reserved target: refused, projects/default.json NOT written (D1/D5).
    $raRes = Import-ConstructConfigAs -ConfigDir $raConfigDir -SourceFile $raSrcFile -NewName "default" `
        -RemoteUrl "https://r/repo" -PathInRemote "projects/orig.json"
    ok "rename-import: reserved target refused" (-not $raRes.Ok)
    ok "rename-import: default.json NOT written" (-not (Test-Path -LiteralPath (Join-Path $raConfigDir "projects/default.json")))
    $raLsTree = & git -C $raConfigDir ls-tree --name-only main -- projects/ 2>$null
    ok "rename-import: default.json NOT committed to main" (@($raLsTree | Where-Object { $_ -match 'default\.json' }).Count -eq 0)

    # Existing target with different provenance: refused, content untouched.
    $raBefore = [System.IO.File]::ReadAllText($raDest, [System.Text.Encoding]::UTF8)
    $raClash = Import-ConstructConfigAs -ConfigDir $raConfigDir -SourceFile $raSrcFile -NewName "orig-2" `
        -RemoteUrl "https://r/OTHER" -PathInRemote "projects/orig.json"
    ok "rename-import: existing target refused (no silent overwrite)" (-not $raClash.Ok)
    $raAfter = [System.IO.File]::ReadAllText($raDest, [System.Text.Encoding]::UTF8)
    ok "rename-import: existing content untouched" ($raBefore -ceq $raAfter)

    # Same-provenance target: allowed (update of our own earlier import).
    $raUpd = Import-ConstructConfigAs -ConfigDir $raConfigDir -SourceFile $raSrcFile -NewName "orig-2" `
        -RemoteUrl "https://r/repo" -PathInRemote "projects/orig.json"
    ok "rename-import: same-provenance update allowed" $raUpd.Ok
} finally {
    Remove-Item -LiteralPath $raBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Per-candidate import: one collision must not abort the rest ─────────
# Mirrors the interactive link-row flow: clone once, then one
# Import-ConstructConfigs call per candidate; a collision on a non-final
# candidate is caught (rename prompt in the TUI), later candidates still
# import, and the remote is registered regardless.
$pcBase = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-pc-" + [guid]::NewGuid().ToString("N"))
$pcConfigDir = Join-Path $pcBase "config"
$pcSrcRepo = Join-Path $pcBase "srcrepo"
New-Item -ItemType Directory -Path (Join-Path $pcConfigDir "projects") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $pcSrcRepo "projects") -Force | Out-Null
$savedPcLA = $env:LOCALAPPDATA
try {
    # Point the staging cache at the temp dir so the test never touches the
    # real cache.
    $env:LOCALAPPDATA = Join-Path $pcBase "localappdata"
    New-Item -ItemType Directory -Path $env:LOCALAPPDATA -Force | Out-Null

    $null = Initialize-ConstructConfigRepo -ConfigDir $pcConfigDir
    $utf8Pc = New-Object System.Text.UTF8Encoding $false
    foreach ($pn in @("aa", "bb", "cc")) {
        [System.IO.File]::WriteAllText((Join-Path $pcSrcRepo "projects/$pn.json"),
            ('{"name":"' + $pn + '","repos":[],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":[],"tests":{}}'), $utf8Pc)
    }
    & git -C $pcSrcRepo init 2>$null | Out-Null
    & git -C $pcSrcRepo -c user.name=Test -c user.email=test@test add -A 2>$null | Out-Null
    & git -C $pcSrcRepo -c user.name=Test -c user.email=test@test commit -m "init" 2>$null | Out-Null

    # Pre-existing local profile 'bb' (no provenance) => collision mid-list.
    [System.IO.File]::WriteAllText((Join-Path $pcConfigDir "projects/bb.json"),
        '{"name":"bb","repos":[],"sdks":{},"mcp":[],"hostPackages":["local"],"provisionCommands":[],"tests":{}}', $utf8Pc)

    $pcUrl = "file://$pcSrcRepo"
    $pcCloneDir = Update-ConstructStagingClone -SourceRepo $pcUrl
    $pcCands = @(Get-ConstructImportCandidates -SourceDir $pcCloneDir)
    ok "per-candidate: discovery finds all three candidates" ($pcCands.Count -eq 3)

    $pcImported = @()
    $pcCollisions = @()
    foreach ($cand in $pcCands) {
        try {
            $pcRes = Import-ConstructConfigs -ConfigDir $pcConfigDir -SourceRepo $pcUrl -Names @($cand.BaseName) -NoFetch
            $pcImported += @($pcRes.Imported)
        } catch {
            if ($_.Exception.Message -match "Name collision") { $pcCollisions += $cand.BaseName }
        }
    }
    Register-ConstructConfigRemote -ConfigDir $pcConfigDir -RemoteUrl $pcUrl

    ok "per-candidate: collision detected for bb only" ($pcCollisions.Count -eq 1 -and $pcCollisions[0] -eq "bb")
    ok "per-candidate: aa imported despite bb collision" ($pcImported -contains "aa")
    ok "per-candidate: cc imported AFTER the bb collision" ($pcImported -contains "cc")
    ok "per-candidate: local bb untouched" (([System.IO.File]::ReadAllText((Join-Path $pcConfigDir "projects/bb.json"), [System.Text.Encoding]::UTF8)) -match "local")

    # Each successful import got its own commit.
    $pcLog = & git -C $pcConfigDir log --oneline main 2>$null
    ok "per-candidate: aa import committed" (@($pcLog | Where-Object { $_ -match "import: aa" }).Count -eq 1)
    ok "per-candidate: cc import committed" (@($pcLog | Where-Object { $_ -match "import: cc" }).Count -eq 1)

    # The linked remote is registered even though a candidate collided.
    $pcRemotesFile = Join-Path $pcConfigDir "manifest/remotes.json"
    $pcRemotes = @()
    if (Test-Path -LiteralPath $pcRemotesFile) {
        $pcRemotes = @((Get-Content -LiteralPath $pcRemotesFile -Raw | ConvertFrom-Json))
    }
    ok "per-candidate: remote registered in manifest/remotes.json" (@($pcRemotes | Where-Object { $_.url -eq $pcUrl }).Count -eq 1)
    # Registration is idempotent.
    Register-ConstructConfigRemote -ConfigDir $pcConfigDir -RemoteUrl $pcUrl
    $pcRemotes2 = @((Get-Content -LiteralPath $pcRemotesFile -Raw | ConvertFrom-Json))
    ok "per-candidate: remote registration idempotent" ($pcRemotes2.Count -eq $pcRemotes.Count)
} finally {
    if ($null -ne $savedPcLA) { $env:LOCALAPPDATA = $savedPcLA }
    else { Remove-Item Env:\LOCALAPPDATA -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $pcBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Shell-injection escaping in Write-ConstructVmStore (finding 5/11) ────────
# Write-ConstructVmStore must escape profile names containing bash metacharacters
# (backtick, $(), single quotes) so they are treated as literals in the generated
# bash script. We capture the generated script via a mock SshInvoker and verify
# that dangerous names do NOT execute embedded commands.
$injBase = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-inj-" + [guid]::NewGuid().ToString("N"))
$injVmStoreDir = Join-Path $injBase "vmstore"
New-Item -ItemType Directory -Path $injVmStoreDir -Force | Out-Null
$script:testVmStoreDir = $injVmStoreDir

try {
    # Names that would cause shell injection if unescaped.
    $dangerousNames = @(
        'test$(whoami)',
        'test`id`',
        "o'brien"
    )

    foreach ($dName in $dangerousNames) {
        $marker = Join-Path $injVmStoreDir "INJECTION_MARKER_$([guid]::NewGuid().ToString('N'))"
        $testName = $dName
        if ($dName -match '^\$') {
            $testName = $dName  # keep literal
        }

        $ops = @(
            [pscustomobject]@{
                Name = $dName
                Action = "write"
                Content = '{"name":"test"}'
                Expect = $null
                ExpectAbsent = $true
            }
        )

        # Capture the generated bash script.
        $capturedScript = $null
        $captureSshWrite = {
            param([string]$script)
            $script:capturedBashScript = $script
            $rewritten = $script -replace '/opt/construct/projects', $script:testVmStoreDir
            $tmpScript = Join-Path ([System.IO.Path]::GetTempPath()) ("ssh-inj-" + [guid]::NewGuid().ToString("N") + ".sh")
            [System.IO.File]::WriteAllText($tmpScript, $rewritten, (New-Object System.Text.UTF8Encoding $false))
            try {
                $out = & bash $tmpScript 2>$null
                $code = $LASTEXITCODE
                $outStr = if ($null -ne $out) { ($out -join "`n") } else { "" }
                return [pscustomobject]@{ Code = $code; Output = $outStr }
            } finally {
                Remove-Item -LiteralPath $tmpScript -Force -ErrorAction SilentlyContinue
            }
        }

        $script:capturedBashScript = $null
        $writeResult = Write-ConstructVmStore -VmHost "dummy" -Ops $ops -SshInvoker $captureSshWrite

        # The generated bash should NOT contain the raw dangerous name in
        # double-quoted context. Verify the file path uses single-quote wrapping.
        $safeName = $dName -replace "'", "'\\''"
        $bashScript = $script:capturedBashScript

        # Verify the bash script uses single-quote wrapping for the name.
        $hasDoubleQuotedName = $bashScript -match "`"`\`$store/$([regex]::Escape($dName))\.json`""
        ok "injection: name '$dName' is NOT in a bash double-quoted path" (-not $hasDoubleQuotedName)

        # The operation should have completed (wrote the file).
        ok "injection: name '$dName' write-back completed" ($null -ne $writeResult -and $writeResult.Done.Count -gt 0)
    }

    # Verify that a name with a single quote round-trips correctly through
    # Write-ConstructVmStore (the file should exist with the quoted name).
    $sqOps = @([pscustomobject]@{
        Name = "o'brien"
        Action = "write"
        Content = '{"name":"obrien"}'
        Expect = $null
        ExpectAbsent = $true
    })
    # Clean vm store first.
    Get-ChildItem -LiteralPath $injVmStoreDir -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    $sqResult = Write-ConstructVmStore -VmHost "dummy" -Ops $sqOps -SshInvoker $sshWrite
    $sqFile = Join-Path $injVmStoreDir "o'brien.json"
    ok "injection: single-quote name file created on disk" (Test-Path -LiteralPath $sqFile)

    # Backtick name should NOT trigger command execution.
    Get-ChildItem -LiteralPath $injVmStoreDir -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    $btMarker = Join-Path $injVmStoreDir "PWNED"
    $btOps = @([pscustomobject]@{
        Name = 'x`touch ' + $btMarker + '`'
        Action = "write"
        Content = '{"name":"test"}'
        Expect = $null
        ExpectAbsent = $true
    })
    $null = Write-ConstructVmStore -VmHost "dummy" -Ops $btOps -SshInvoker $sshWrite
    ok "injection: backtick command NOT executed (no marker file)" (-not (Test-Path -LiteralPath $btMarker))

} finally {
    Remove-Item -LiteralPath $injBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ── base64 portability in Read/Write-ConstructVmStore (finding 7) ───────────
# Verify the generated bash scripts use 'base64 | tr -d' instead of 'base64 -w0'.
$portBase = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-port-" + [guid]::NewGuid().ToString("N"))
$portVmStoreDir = Join-Path $portBase "vmstore"
New-Item -ItemType Directory -Path $portVmStoreDir -Force | Out-Null
$script:testVmStoreDir = $portVmStoreDir
try {
    # Seed a file so Read-ConstructVmStore has something to read.
    $utf8Port = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText((Join-Path $portVmStoreDir "port.json"), '{"name":"port"}', $utf8Port)

    # Capture the read script.
    $script:capturedReadScript = $null
    $captureReadInvoker = {
        param([string]$script)
        $script:capturedReadScript = $script
        $rewritten = $script -replace '/opt/construct/projects', $script:testVmStoreDir
        $tmpScript = Join-Path ([System.IO.Path]::GetTempPath()) ("ssh-port-r-" + [guid]::NewGuid().ToString("N") + ".sh")
        [System.IO.File]::WriteAllText($tmpScript, $rewritten, (New-Object System.Text.UTF8Encoding $false))
        try {
            $out = & bash $tmpScript 2>$null
            $code = $LASTEXITCODE
            $outStr = if ($null -ne $out) { ($out -join "`n") } else { "" }
            return [pscustomobject]@{ Code = $code; Output = $outStr }
        } finally {
            Remove-Item -LiteralPath $tmpScript -Force -ErrorAction SilentlyContinue
        }
    }

    $null = Read-ConstructVmStore -VmHost "dummy" -SshInvoker $captureReadInvoker
    ok "portability: Read-ConstructVmStore does NOT use 'base64 -w0'" ($script:capturedReadScript -notmatch 'base64 -w0')
    ok "portability: Read-ConstructVmStore uses 'base64 | tr'" ($script:capturedReadScript -match "base64.*\| tr -d")

    # Capture the write script.
    $script:capturedWriteScript = $null
    $captureWriteInvoker = {
        param([string]$script)
        $script:capturedWriteScript = $script
        $rewritten = $script -replace '/opt/construct/projects', $script:testVmStoreDir
        $tmpScript = Join-Path ([System.IO.Path]::GetTempPath()) ("ssh-port-w-" + [guid]::NewGuid().ToString("N") + ".sh")
        [System.IO.File]::WriteAllText($tmpScript, $rewritten, (New-Object System.Text.UTF8Encoding $false))
        try {
            $out = & bash $tmpScript 2>$null
            $code = $LASTEXITCODE
            $outStr = if ($null -ne $out) { ($out -join "`n") } else { "" }
            return [pscustomobject]@{ Code = $code; Output = $outStr }
        } finally {
            Remove-Item -LiteralPath $tmpScript -Force -ErrorAction SilentlyContinue
        }
    }

    $writeOps = @([pscustomobject]@{
        Name = "port"
        Action = "write"
        Content = '{"name":"port","updated":true}'
        Expect = $null
        ExpectAbsent = $false
    })
    $null = Write-ConstructVmStore -VmHost "dummy" -Ops $writeOps -SshInvoker $captureWriteInvoker
    ok "portability: Write-ConstructVmStore does NOT use 'base64 -w0'" ($script:capturedWriteScript -notmatch 'base64 -w0')
} finally {
    Remove-Item -LiteralPath $portBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Path traversal in Push-ConstructConfigUpstream (finding 10) ─────────────
# A malicious PathInRemote like "../../etc/passwd" must be blocked.
$ptBase = Join-Path ([System.IO.Path]::GetTempPath()) ("cs-pt-" + [guid]::NewGuid().ToString("N"))
$ptConfigDir = Join-Path $ptBase "config"
$ptSrcRepo = Join-Path $ptBase "srcrepo"
New-Item -ItemType Directory -Path (Join-Path $ptConfigDir "projects") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ptConfigDir "manifest") -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $ptConfigDir "bases") -Force | Out-Null
$savedPtLA = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = Join-Path $ptBase "localappdata"
    New-Item -ItemType Directory -Path $env:LOCALAPPDATA -Force | Out-Null

    $utf8Pt = New-Object System.Text.UTF8Encoding $false

    # Create a source repo for the staging clone.
    New-Item -ItemType Directory -Path (Join-Path $ptSrcRepo "projects") -Force | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $ptSrcRepo "projects/safe.json"),
        '{"name":"safe","repos":[],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":[],"tests":{}}', $utf8Pt)
    & git -C $ptSrcRepo init 2>$null | Out-Null
    & git -C $ptSrcRepo -c user.name=Test -c user.email=test@test add -A 2>$null | Out-Null
    & git -C $ptSrcRepo -c user.name=Test -c user.email=test@test commit -m "init" 2>$null | Out-Null
    & git -C $ptSrcRepo branch -M main 2>$null | Out-Null

    $null = Initialize-ConstructConfigRepo -ConfigDir $ptConfigDir

    # Import so the staging clone exists.
    $ptUrl = "file://$ptSrcRepo"
    $null = Import-ConstructConfigs -ConfigDir $ptConfigDir -SourceRepo $ptUrl

    # Now create a profile with a legitimate PathInRemote.
    [System.IO.File]::WriteAllText((Join-Path $ptConfigDir "projects/safe.json"),
        '{"name":"safe","repos":[],"sdks":{},"mcp":[],"hostPackages":[],"provisionCommands":[],"tests":{}}', $utf8Pt)

    # Create manifest with a MALICIOUS PathInRemote that escapes the clone dir.
    $ptManifest = @{
        remoteUrl = $ptUrl
        ref = "main"
        pathInRemote = "../../ESCAPED.json"
        importedAs = "safe"
        baseCommit = ""
        baseBlobSha = ""
    }
    [System.IO.File]::WriteAllText((Join-Path $ptConfigDir "manifest/safe.json"),
        ($ptManifest | ConvertTo-Json -Depth 5), $utf8Pt)

    $ptThrew = $false
    $ptErrMsg = ""
    try {
        $null = Push-ConstructConfigUpstream -ConfigDir $ptConfigDir -RemoteUrl $ptUrl
    } catch {
        $ptThrew = $true
        $ptErrMsg = $_.Exception.Message
    }
    ok "path-traversal: Push-ConstructConfigUpstream throws on escaping PathInRemote" $ptThrew
    ok "path-traversal: error message mentions traversal" ($ptErrMsg -match "Path traversal blocked")

    # A safe PathInRemote should still work (no throw).
    $ptManifestSafe = @{
        remoteUrl = $ptUrl
        ref = "main"
        pathInRemote = "projects/safe.json"
        importedAs = "safe"
        baseCommit = ""
        baseBlobSha = ""
    }
    [System.IO.File]::WriteAllText((Join-Path $ptConfigDir "manifest/safe.json"),
        ($ptManifestSafe | ConvertTo-Json -Depth 5), $utf8Pt)

    $ptSafeThrew = $false
    try {
        $null = Push-ConstructConfigUpstream -ConfigDir $ptConfigDir -RemoteUrl $ptUrl
    } catch {
        $ptSafeThrew = $true
    }
    ok "path-traversal: safe PathInRemote does NOT throw" (-not $ptSafeThrew)

} finally {
    if ($null -ne $savedPtLA) { $env:LOCALAPPDATA = $savedPtLA }
    else { Remove-Item Env:\LOCALAPPDATA -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $ptBase -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host ("  config-sync unit tests - {0}/{1} passed" -f $script:pass, ($script:pass + $script:fail))
Write-Host ""
if ($script:fail -gt 0) { exit 1 }
exit 0
