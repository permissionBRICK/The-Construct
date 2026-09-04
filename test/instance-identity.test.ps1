#Requires -Version 5.1
<#
    Unit tests for the B1 instance-identity parameterisation.  Run:

        pwsh -NoProfile -File test/instance-identity.test.ps1

    Self-contained, no Hyper-V: covers parser checks, param metadata,
    the SSH-config block writer, and any new pure helpers.
#>
$ErrorActionPreference = "Stop"

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

# ── (a) Parser check: every touched .ps1 must parse with zero errors ────────
Write-Host ""
Write-Host "=== Parser checks ===" -ForegroundColor Cyan
$touchedScripts = @(
    "Create-AgentVM.ps1",
    "Auto-Install.ps1",
    "Provision-AgentVM.ps1",
    "Set-AgentVmCheckpoints.ps1",
    "Get-AgentUsage.ps1",
    "Update-T3Code.ps1",
    "lib/AgentVm.Common.ps1",
    "lib/AgentVm.Instances.ps1",
    "lib/AgentVm.InstanceTarget.ps1"
)
foreach ($rel in $touchedScripts) {
    $full = Join-Path $repoRoot $rel
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($full, [ref]$null, [ref]$errors)
    ok "parse: $rel has zero errors" ($errors.Count -eq 0)
    if ($errors.Count -gt 0) {
        foreach ($e in $errors) { Write-Host "    ERROR: $($e.Message) (line $($e.Extent.StartLineNumber))" -ForegroundColor Red }
    }
}

# ── (b) Param metadata: each script exposes the new params with exact defaults ─
Write-Host ""
Write-Host "=== Param metadata ===" -ForegroundColor Cyan

function Get-ScriptParam {
    param([string]$ScriptPath, [string]$ParamName)
    $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$null, [ref]$errors)
    $paramBlock = $ast.ParamBlock
    if (-not $paramBlock) { return $null }
    foreach ($p in $paramBlock.Parameters) {
        if ($p.Name.VariablePath.UserPath -eq $ParamName) { return $p }
    }
    return $null
}

function Get-ParamDefaultValue {
    param([System.Management.Automation.Language.ParameterAst]$Param)
    if (-not $Param -or -not $Param.DefaultValue) { return $null }
    return $Param.DefaultValue.Extent.Text
}

# Create-AgentVM.ps1: [string]$VmName = "Agent-VM"
$p = Get-ScriptParam (Join-Path $repoRoot "Create-AgentVM.ps1") "VmName"
ok "Create-AgentVM has -VmName param" ($null -ne $p)
ok "Create-AgentVM -VmName default is 'Agent-VM'" ((Get-ParamDefaultValue $p) -eq '"Agent-VM"')

# Auto-Install.ps1: [string]$VmName = "Agent-VM"
$p = Get-ScriptParam (Join-Path $repoRoot "Auto-Install.ps1") "VmName"
ok "Auto-Install has -VmName param" ($null -ne $p)
ok "Auto-Install -VmName default is 'Agent-VM'" ((Get-ParamDefaultValue $p) -eq '"Agent-VM"')

# Provision-AgentVM.ps1: [int]$SshPort = 22
$p = Get-ScriptParam (Join-Path $repoRoot "Provision-AgentVM.ps1") "SshPort"
ok "Provision-AgentVM has -SshPort param" ($null -ne $p)
ok "Provision-AgentVM -SshPort default is 22" ((Get-ParamDefaultValue $p) -eq '22')

# Get-AgentUsage.ps1: [int]$SshPort = 22
$p = Get-ScriptParam (Join-Path $repoRoot "Get-AgentUsage.ps1") "SshPort"
ok "Get-AgentUsage has -SshPort param" ($null -ne $p)
ok "Get-AgentUsage -SshPort default is 22" ((Get-ParamDefaultValue $p) -eq '22')

# Update-T3Code.ps1: optional -VmHost, -HostAlias, -SshPort
$p = Get-ScriptParam (Join-Path $repoRoot "Update-T3Code.ps1") "VmHost"
ok "Update-T3Code has -VmHost param" ($null -ne $p)
$p = Get-ScriptParam (Join-Path $repoRoot "Update-T3Code.ps1") "HostAlias"
ok "Update-T3Code has -HostAlias param" ($null -ne $p)
$p = Get-ScriptParam (Join-Path $repoRoot "Update-T3Code.ps1") "SshPort"
ok "Update-T3Code has -SshPort param" ($null -ne $p)
ok "Update-T3Code -SshPort default is 0 (omit when default)" ((Get-ParamDefaultValue $p) -eq '0')

# lib/AgentVm.Common.ps1: Invoke-ConstructVmSsh has -KeyPath and -SshPort
# Parse the file and find the function's param block.
$libPath = Join-Path $repoRoot "lib/AgentVm.Common.ps1"
$libErrors = $null
$libAst = [System.Management.Automation.Language.Parser]::ParseFile($libPath, [ref]$null, [ref]$libErrors)
$fnAst = $libAst.FindAll({ param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Invoke-ConstructVmSsh' }, $true) | Select-Object -First 1
ok "lib has Invoke-ConstructVmSsh function" ($null -ne $fnAst)
if ($fnAst) {
    $fnParams = $fnAst.Body.ParamBlock.Parameters
    $kpParam = $fnParams | Where-Object { $_.Name.VariablePath.UserPath -eq 'KeyPath' }
    $spParam = $fnParams | Where-Object { $_.Name.VariablePath.UserPath -eq 'SshPort' }
    ok "Invoke-ConstructVmSsh has -KeyPath param" ($null -ne $kpParam)
    ok "Invoke-ConstructVmSsh has -SshPort param" ($null -ne $spParam)
    ok "Invoke-ConstructVmSsh -SshPort default is 22" (($spParam.DefaultValue.Extent.Text) -eq '22')
}

# ── (c) SSH-config writer unit test ─────────────────────────────────────────
Write-Host ""
Write-Host "=== SSH-config writer ===" -ForegroundColor Cyan

# We need to test Set-HostSshConfig from Provision-AgentVM.ps1.  It's a function
# defined inside the script file, so we extract it via the AST and dot-source
# the definition into a controlled scope, stubbing out the dependencies it uses
# (Protect-SshFile, Write-Ok, Write-Warning, ssh, ssh-keygen).

$provPath = Join-Path $repoRoot "Provision-AgentVM.ps1"
$provErrors = $null
$provAst = [System.Management.Automation.Language.Parser]::ParseFile($provPath, [ref]$null, [ref]$provErrors)

# Extract the text of Set-HostSshConfig
$setHostAst = $provAst.FindAll({
    param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Set-HostSshConfig'
}, $true) | Select-Object -First 1
# Also need Protect-SshFile
$protectAst = $provAst.FindAll({
    param($a) $a -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $a.Name -eq 'Protect-SshFile'
}, $true) | Select-Object -First 1

if ($setHostAst -and $protectAst) {
    # To test Set-HostSshConfig we need to redirect $HOME. Since $HOME is read-only
    # in pwsh 7, we rewrite the extracted function text to replace references to
    # $HOME with a temp-dir variable that we control, then dot-source it.
    $protectText = $protectAst.Extent.Text
    $setHostText = $setHostAst.Extent.Text

    function Invoke-SetHostSshConfigTest {
        param(
            [string]$TempDir,
            [string]$TestHostAlias,
            [string]$TestVmHost,
            [string]$TestRemoteUser,
            [string]$TestLocalKeyName,
            [int]$TestSshPort,
            [string]$ExistingConfig   # optional: pre-populate ssh config content
        )

        $sshDir = Join-Path $TempDir ".ssh"
        New-Item -ItemType Directory -Path $sshDir -Force | Out-Null
        if ($ExistingConfig) {
            [System.IO.File]::WriteAllText((Join-Path $sshDir "config"), ($ExistingConfig -replace "`r`n", "`n") + "`n")
        }

        # Rewrite the function body to use $TestHome instead of $HOME.
        $rewrittenSetHost = ($script:setHostText) -replace '\$HOME\b', "`$TestHome" -replace '\$sshDir\b', "`$TestSshDir"
        $rewrittenProtect = $script:protectText

        # Build a self-contained script block that defines stubs + the rewritten
        # function, calls it, and returns the config file content.
        $sb = [scriptblock]::Create(@"
            `$ErrorActionPreference = "Stop"
            function Protect-SshFile { param(`$Path) }
            function Write-Ok   { param(`$msg) }
            function Write-Warning { param(`$msg) }
            function ssh         { }
            function ssh-keygen  { }

            `$HostAlias    = '$TestHostAlias'
            `$VmHost       = '$TestVmHost'
            `$RemoteUser   = '$TestRemoteUser'
            `$LocalKeyName = '$TestLocalKeyName'
            `$SshPort      = $TestSshPort
            `$TestHome     = '$($TempDir -replace "'","''")'
            `$TestSshDir   = '$($sshDir -replace "'","''")'
            `$script:SshPortArgs = if (`$SshPort -ne 22) { @("-p", "`$SshPort") } else { @() }

            $rewrittenSetHost

            `$keyText = "-----BEGIN OPENSSH PRIVATE KEY-----`nfakekey`n-----END OPENSSH PRIVATE KEY-----"
            Set-HostSshConfig -PrivateKeyText `$keyText

            `$cfgPath = Join-Path `$TestSshDir "config"
            if (Test-Path -LiteralPath `$cfgPath) {
                return (Get-Content -LiteralPath `$cfgPath -Raw)
            }
            return ""
"@)
        return (& $sb)
    }

    # Test 1: Default alias + port 22 -> byte-identical to pre-change output.
    $tmpDir1 = Join-Path ([System.IO.Path]::GetTempPath()) "construct-test-ssh-$(Get-Random)"
    New-Item -ItemType Directory -Path $tmpDir1 -Force | Out-Null
    try {
        $result1 = Invoke-SetHostSshConfigTest -TempDir $tmpDir1 -TestHostAlias "agent-vm" `
            -TestVmHost "agent-vm.mshome.net" -TestRemoteUser "root" `
            -TestLocalKeyName "agent_vm_ed25519" -TestSshPort 22
        # The expected block for default values (no Port line):
        $expectedDefault = @"
Host agent-vm
    HostName agent-vm.mshome.net
    User root
    IdentityFile $(Join-Path $tmpDir1 '.ssh' 'agent_vm_ed25519')
    IdentitiesOnly yes
"@
        # Normalize line endings for comparison.
        $norm1 = ($result1 -replace "`r`n", "`n").Trim()
        $normE = ($expectedDefault -replace "`r`n", "`n").Trim()
        ok "ssh-config: default alias + port 22 matches expected block" ($norm1 -eq $normE)
        ok "ssh-config: no Port line when port is 22" ($result1 -notmatch '(?m)^\s*Port\s')
    } finally {
        Remove-Item -LiteralPath $tmpDir1 -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Test 2: Custom alias + non-22 port -> Port line present.
    $tmpDir2 = Join-Path ([System.IO.Path]::GetTempPath()) "construct-test-ssh-$(Get-Random)"
    New-Item -ItemType Directory -Path $tmpDir2 -Force | Out-Null
    try {
        $result2 = Invoke-SetHostSshConfigTest -TempDir $tmpDir2 -TestHostAlias "myvm" `
            -TestVmHost "myvm.example.net" -TestRemoteUser "root" `
            -TestLocalKeyName "agent_vm_ed25519" -TestSshPort 2222
        ok "ssh-config: custom alias present" ($result2 -match '(?m)^Host myvm\s*$')
        ok "ssh-config: HostName set" ($result2 -match '(?m)^\s*HostName myvm\.example\.net\s*$')
        ok "ssh-config: Port 2222 present" ($result2 -match '(?m)^\s*Port 2222\s*$')
    } finally {
        Remove-Item -LiteralPath $tmpDir2 -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Test 3: Replace existing block + keep unrelated blocks.
    $tmpDir3 = Join-Path ([System.IO.Path]::GetTempPath()) "construct-test-ssh-$(Get-Random)"
    New-Item -ItemType Directory -Path $tmpDir3 -Force | Out-Null
    try {
        $preCfg = @"
Host github.com
    User git
    IdentityFile ~/.ssh/id_ed25519

Host myvm
    HostName old-host.example.net
    User agent
    IdentityFile ~/.ssh/old_key
    Port 9999

Host other-server
    HostName other.example.net
"@
        $result3 = Invoke-SetHostSshConfigTest -TempDir $tmpDir3 -TestHostAlias "myvm" `
            -TestVmHost "myvm.new.net" -TestRemoteUser "root" `
            -TestLocalKeyName "agent_vm_ed25519" -TestSshPort 2222 `
            -ExistingConfig $preCfg

        # The unrelated blocks must survive untouched.
        ok "ssh-config: unrelated 'github.com' block kept" ($result3 -match '(?m)^Host github\.com')
        ok "ssh-config: unrelated 'other-server' block kept" ($result3 -match '(?m)^Host other-server')
        # The old block should be replaced.
        ok "ssh-config: old HostName replaced" ($result3 -notmatch 'old-host\.example\.net')
        ok "ssh-config: new HostName present" ($result3 -match '(?m)^\s*HostName myvm\.new\.net')
        ok "ssh-config: updated Port line" ($result3 -match '(?m)^\s*Port 2222')
        # Old port gone.
        ok "ssh-config: old Port 9999 gone" ($result3 -notmatch '9999')
    } finally {
        Remove-Item -LiteralPath $tmpDir3 -Recurse -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Host "  SKIP  Could not extract Set-HostSshConfig or Protect-SshFile from the AST" -ForegroundColor Yellow
}

# ── (d) Pure helper unit tests ──────────────────────────────────────────────
Write-Host ""
Write-Host "=== Pure helper tests ===" -ForegroundColor Cyan

# Invoke-ConstructVmSsh port-args construction: verify that when SshPort != 22,
# the "-p" flag appears in the args.  We test this by looking at the function body
# AST for the pattern.  Since we can't call ssh from this test host, we verify the
# parameter wiring via the AST.
if ($fnAst) {
    $fnBody = $fnAst.Extent.Text
    ok "Invoke-ConstructVmSsh: builds portArgs for non-22" ($fnBody -match 'portArgs.*-p')
    ok "Invoke-ConstructVmSsh: splats portArgs into ssh call" ($fnBody -match '@portArgs')
}

# Verify the env prefix in Provision-AgentVM.ps1 includes the CONTRACT vars.
$provContent = Get-Content -LiteralPath (Join-Path $repoRoot "Provision-AgentVM.ps1") -Raw
ok "envPrefix includes CONSTRUCT_EXTERNAL_HOST" ($provContent -match "CONSTRUCT_EXTERNAL_HOST=")
ok "envPrefix includes CONSTRUCT_EXTERNAL_SSH_PORT" ($provContent -match "CONSTRUCT_EXTERNAL_SSH_PORT=")

# Get-ExternalEnvSuffix (Provision-AgentVM.ps1): the external identity is appended to
# provision.sh's env prefix ONLY when it carries information. Extract the pure helpers
# from the script's AST and exercise every case without a VM.
Write-Host ""
Write-Host "=== External env suffix (zero-change + reset semantics) ===" -ForegroundColor Cyan
$provPath = Join-Path $repoRoot "Provision-AgentVM.ps1"
$provTokens = $null; $provErrors = $null
$provAst = [System.Management.Automation.Language.Parser]::ParseFile($provPath, [ref]$provTokens, [ref]$provErrors)
foreach ($fnName in @('ConvertTo-PosixSingleQuoted', 'Get-ExternalEnvSuffix', 'ConvertFrom-SavedExternalIdentity')) {
    $fn = $provAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fnName }, $true) | Select-Object -First 1
    ok "Provision-AgentVM defines $fnName" ($null -ne $fn)
    if ($fn) { Invoke-Expression $fn.Extent.Text }
}
if (Get-Command Get-ExternalEnvSuffix -ErrorAction SilentlyContinue) {
    $legacy = " CONSTRUCT_EXTERNAL_HOST='agent-vm.mshome.net' CONSTRUCT_EXTERNAL_SSH_PORT='22'"
    ok "suffix: default endpoint, nothing bound -> empty" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 22 -ExplicitlyBound $false) -eq "")
    ok "suffix: legacy explicit default on a stock guest -> empty (byte-identical)" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 22 -ExplicitlyBound $true -SavedHost "" -SavedPort "") -eq "")
    ok "suffix: legacy explicit default, guest saved port 22 only -> empty" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 22 -ExplicitlyBound $true -SavedHost "" -SavedPort "22") -eq "")
    ok "suffix: explicit default RESETS a saved custom host" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 22 -ExplicitlyBound $true -SavedHost "old.example.net" -SavedPort "") -eq $legacy)
    ok "suffix: explicit default RESETS a saved custom port" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 22 -ExplicitlyBound $true -SavedHost "" -SavedPort "2201") -eq $legacy)
    ok "suffix: non-default host always sent, even when not 'bound'" `
        ((Get-ExternalEnvSuffix -VmHost 'work-vm.mshome.net' -SshPort 22 -ExplicitlyBound $false) -eq " CONSTRUCT_EXTERNAL_HOST='work-vm.mshome.net' CONSTRUCT_EXTERNAL_SSH_PORT='22'")
    ok "suffix: non-default port always sent" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 2201 -ExplicitlyBound $true) -eq " CONSTRUCT_EXTERNAL_HOST='agent-vm.mshome.net' CONSTRUCT_EXTERNAL_SSH_PORT='2201'")
    # The remote read returns ONE scalar with both lines (Invoke-Ssh uses Out-String).
    $stock = ConvertFrom-SavedExternalIdentity -Raw "H=`nP=`n"
    ok "saved-identity parse: stock guest scalar -> empty host and port" ($stock.Host -eq "" -and $stock.Port -eq "")
    ok "suffix: legacy explicit default with the stock-guest scalar -> empty (integration)" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 22 -ExplicitlyBound $true -SavedHost $stock.Host -SavedPort $stock.Port) -eq "")
    $crlf = ConvertFrom-SavedExternalIdentity -Raw "H=`r`nP=`r`n"
    ok "saved-identity parse: CRLF stock scalar -> empty" ($crlf.Host -eq "" -and $crlf.Port -eq "")
    $custom = ConvertFrom-SavedExternalIdentity -Raw "H='old.example.net'`nP=2201`n"
    ok "saved-identity parse: quoted host + port decoded" ($custom.Host -eq "old.example.net" -and $custom.Port -eq "2201")
    ok "suffix: explicit default with the custom scalar -> reset suffix" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 22 -ExplicitlyBound $true -SavedHost $custom.Host -SavedPort $custom.Port) -eq $legacy)
    $portOnly = ConvertFrom-SavedExternalIdentity -Raw "H=`nP=22`n"
    ok "suffix: explicit default, guest saved port 22 via scalar -> empty" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 22 -ExplicitlyBound $true -SavedHost $portOnly.Host -SavedPort $portOnly.Port) -eq "")
    ok "suffix: apostrophe in host is POSIX-quoted" `
        ((Get-ExternalEnvSuffix -VmHost "o'brien-vm.mshome.net" -SshPort 22 -ExplicitlyBound $true) -eq " CONSTRUCT_EXTERNAL_HOST='o'\''brien-vm.mshome.net' CONSTRUCT_EXTERNAL_SSH_PORT='22'")

    # ── -PublicHost: the per-VM web host name (plan section 4.12) ───────────
    # On a host service that renders one, the guest's external identity is the VM's OWN
    # name (T3 SANs, T3CODE_PUBLIC_BASE_URL, printed URLs) while SSH keeps going to the
    # endpoint. Empty -- every local install -- changes nothing at all.
    ok "suffix: -PublicHost empty leaves the default path byte-identical" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 22 -ExplicitlyBound $false -PublicHost "") -eq "")
    ok "suffix: -PublicHost replaces the host half, keeping the ssh PORT" `
        ((Get-ExternalEnvSuffix -VmHost 'buildbox.example.local' -SshPort 2201 -ExplicitlyBound $true -PublicHost 'work-vm.vpn.example') -eq " CONSTRUCT_EXTERNAL_HOST='work-vm.vpn.example' CONSTRUCT_EXTERNAL_SSH_PORT='2201'")
    ok "suffix: -PublicHost is sent even when nothing was bound (it is never the default)" `
        ((Get-ExternalEnvSuffix -VmHost 'buildbox.example.local' -SshPort 2201 -ExplicitlyBound $false -PublicHost 'work-vm.vpn.example') -eq " CONSTRUCT_EXTERNAL_HOST='work-vm.vpn.example' CONSTRUCT_EXTERNAL_SSH_PORT='2201'")
    ok "suffix: -PublicHost is POSIX-quoted like every other crossing value" `
        ((Get-ExternalEnvSuffix -VmHost 'buildbox.example.local' -SshPort 2201 -ExplicitlyBound $true -PublicHost "o'brien.vpn.example") -eq " CONSTRUCT_EXTERNAL_HOST='o'\''brien.vpn.example' CONSTRUCT_EXTERNAL_SSH_PORT='2201'")
    # A -PublicHost that IS the default identity is still judged by the rules above, so
    # it can never turn the zero-change path into a non-default one.
    ok "suffix: a -PublicHost equal to the default identity keeps the legacy silence" `
        ((Get-ExternalEnvSuffix -VmHost 'agent-vm.mshome.net' -SshPort 22 -ExplicitlyBound $false -PublicHost 'agent-vm.mshome.net') -eq "")
}

# ── The guest's host-forward status file (plan section 4.12) ────────────────
# /etc/construct/host-forwards is how the guest tells the provisioner which public
# addresses the host service allocated. A local VM has no such file, and the parsers
# then answer "nothing" -- which is what keeps the OpenCode URL and the summary text
# byte-identical on a local install.
Write-Host ""
Write-Host "=== Host-forward status file ===" -ForegroundColor Cyan
foreach ($fnName in @('ConvertFrom-HostForwardStatus', 'Get-HostForwardUrl', 'Test-HostForwardDenied',
                      'Get-T3ForwardUrl', 'Get-OpenCodeServerPlan')) {
    $fn = $provAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fnName }, $true) | Select-Object -First 1
    ok "Provision-AgentVM defines $fnName" ($null -ne $fn)
    if ($fn) { Invoke-Expression $fn.Extent.Text }
}
if (Get-Command ConvertFrom-HostForwardStatus -ErrorAction SilentlyContinue) {
    $hf = ConvertFrom-HostForwardStatus -Raw "OPENCODE=work-vm.vpn.example:2301`nT3=work-vm.vpn.example:2302`n"
    ok "forwards: both keys are read" ($hf['OPENCODE'] -eq 'work-vm.vpn.example:2301' -and $hf['T3'] -eq 'work-vm.vpn.example:2302')
    ok "forwards: the OpenCode URL is http on the forwarded authority" `
        ((Get-HostForwardUrl -Forwards $hf -Key 'OPENCODE') -eq 'http://work-vm.vpn.example:2301')
    ok "forwards: T3's URL is https (its listener is the TLS proxy)" `
        ((Get-HostForwardUrl -Forwards $hf -Key 'T3' -Scheme 'https') -eq 'https://work-vm.vpn.example:2302')
    ok "forwards: nothing is denied here" (-not (Test-HostForwardDenied -Forwards $hf -Key 'OPENCODE'))

    # An IPv6 authority keeps the brackets the guest CLI put there -- one bracketing
    # place, exactly like docs/expose.md's host rule.
    $hf6 = ConvertFrom-HostForwardStatus -Raw "OPENCODE=[fe80::1]:2301`n"
    ok "forwards: an IPv6 authority is passed through bracketed" `
        ((Get-HostForwardUrl -Forwards $hf6 -Key 'OPENCODE') -eq 'http://[fe80::1]:2301')

    $denied = ConvertFrom-HostForwardStatus -Raw "OPENCODE=denied`n"
    ok "forwards: 'denied' is not a URL" ((Get-HostForwardUrl -Forwards $denied -Key 'OPENCODE') -eq "")
    ok "forwards: ...and is reported as a refusal, not as 'no forward'" (Test-HostForwardDenied -Forwards $denied -Key 'OPENCODE')

    $failed = ConvertFrom-HostForwardStatus -Raw "OPENCODE=error`n"
    ok "forwards: 'error' is not a URL either" ((Get-HostForwardUrl -Forwards $failed -Key 'OPENCODE') -eq "")
    ok "forwards: ...and is NOT a refusal (the user is not the reason)" (-not (Test-HostForwardDenied -Forwards $failed -Key 'OPENCODE'))

    # The LOCAL path: no file, so the raw read is empty and every answer is 'nothing'.
    $none = ConvertFrom-HostForwardStatus -Raw ""
    ok "forwards: an absent file parses to no keys" ($none.Keys.Count -eq 0)
    ok "forwards: ...so there is no URL" ((Get-HostForwardUrl -Forwards $none -Key 'OPENCODE') -eq "")
    ok "forwards: ...and nothing was denied" (-not (Test-HostForwardDenied -Forwards $none -Key 'OPENCODE'))

    $noisy = ConvertFrom-HostForwardStatus -Raw "# a comment`n`nOPENCODE=work-vm.vpn.example:2301`nnot a line`n"
    ok "forwards: comments and junk lines are ignored" ($noisy.Keys.Count -eq 1 -and $noisy['OPENCODE'] -eq 'work-vm.vpn.example:2301')
}

# ── The OpenCode server entry: three answers, one decision ──────────────────
# A remote VM sits on the host service's internal switch, so http://<VmHost>:<port> is a
# DEAD link there -- it may only ever be used on the local/default path. The branch is a
# pure function precisely so the registration and the summary cannot drift apart.
Write-Host ""
Write-Host "=== The OpenCode server entry ===" -ForegroundColor Cyan
if (Get-Command Get-OpenCodeServerPlan -ErrorAction SilentlyContinue) {
    $direct = 'http://agent-vm.mshome.net:4096'

    # LOCAL / DEFAULT PATH: the direct URL, verbatim, whatever the (absent) status says.
    $planLocal = Get-OpenCodeServerPlan -Forwards @{} -ServiceManaged $false -DirectUrl $direct
    ok "opencode: a local VM registers the direct URL" ($planLocal.Action -eq 'register' -and $planLocal.Url -eq $direct)
    ok "opencode: ...and says it came from the direct path" ($planLocal.Reason -eq 'direct')
    $planLocalNoisy = Get-OpenCodeServerPlan -Forwards (ConvertFrom-HostForwardStatus -Raw "OPENCODE=denied`n") -ServiceManaged $false -DirectUrl $direct
    ok "opencode: a local VM is never affected by a stale status file" (
        $planLocalNoisy.Action -eq 'register' -and $planLocalNoisy.Url -eq $direct)

    # SERVICE-MANAGED: the forwarded URL, or NO entry at all.
    $fwd = ConvertFrom-HostForwardStatus -Raw "OPENCODE=work-vm.vpn.example:2301`n"
    $planFwd = Get-OpenCodeServerPlan -Forwards $fwd -ServiceManaged $true -DirectUrl $direct
    ok "opencode: a service-managed VM registers the FORWARDED URL" (
        $planFwd.Action -eq 'register' -and $planFwd.Url -eq 'http://work-vm.vpn.example:2301')

    $planDenied = Get-OpenCodeServerPlan -Forwards (ConvertFrom-HostForwardStatus -Raw "OPENCODE=denied`n") -ServiceManaged $true -DirectUrl $direct
    ok "opencode: a refusal omits the entry" ($planDenied.Action -eq 'omit')
    ok "opencode: ...and is reported as the owner's policy" ($planDenied.Reason -eq 'denied')

    # THE DEAD-LINK CASE: a failed request, or a status file that could not be read, must
    # NOT fall back to the VM's own port -- that entry could never connect.
    $planError = Get-OpenCodeServerPlan -Forwards (ConvertFrom-HostForwardStatus -Raw "OPENCODE=error`n") -ServiceManaged $true -DirectUrl $direct
    ok "opencode: a failed forward omits the entry (never the dead direct URL)" (
        $planError.Action -eq 'omit' -and $planError.Url -eq "")
    ok "opencode: ...and is NOT reported as a refusal" ($planError.Reason -eq 'missing')

    $planAbsent = Get-OpenCodeServerPlan -Forwards @{} -ServiceManaged $true -DirectUrl $direct
    ok "opencode: an absent/unreadable status file omits the entry too" (
        $planAbsent.Action -eq 'omit' -and $planAbsent.Reason -eq 'missing')
}

# ── The forwarded T3 URL: stated by the guest, never guessed ───────────────
# The allocated forward ALONE is ambiguous: provision.sh requests it for the TLS port
# whenever HTTPS is wanted, BEFORE setup-t3-https.sh runs, and every failure path in that
# script clears the HTTPS status while the forward stays. So the guest records T3_URL --
# the origin T3 was really told to advertise -- and this side only validates it.
if (Get-Command Get-T3ForwardUrl -ErrorAction SilentlyContinue) {
    $t3Https = ConvertFrom-HostForwardStatus -Raw "T3=work-vm.vpn.example:2302`nT3_URL=https://work-vm.vpn.example:2302`n"
    ok "t3: an HTTPS VM's forwarded URL is reported as https" (
        (Get-T3ForwardUrl -Forwards $t3Https) -eq 'https://work-vm.vpn.example:2302')

    # EXPLICIT T3CODE_HTTPS=false: the PLAIN listener is forwarded and really serves it.
    $t3Plain = ConvertFrom-HostForwardStatus -Raw "T3=work-vm.vpn.example:2305`nT3_URL=http://work-vm.vpn.example:2305`n"
    ok "t3: a deliberately plain-HTTP VM's forwarded URL is reported as http" (
        (Get-T3ForwardUrl -Forwards $t3Plain) -eq 'http://work-vm.vpn.example:2305')

    # HTTPS REQUESTED BUT THE SETUP FAILED: the forward exists (for the TLS port) and the
    # guest advertises nothing on it. The two cases above differ ONLY in T3_URL, which is
    # exactly why the scheme may not be inferred from the forward.
    $t3Failed = ConvertFrom-HostForwardStatus -Raw "T3=work-vm.vpn.example:2302`n"
    ok "t3: a forward whose HTTPS setup failed yields NO url" ((Get-T3ForwardUrl -Forwards $t3Failed) -eq "")
    ok "t3: ...while the forward itself is still visible as allocated" (
        (Get-HostForwardUrl -Forwards $t3Failed -Key 'T3') -ne "")
    ok "t3: ...and it is not a refusal" (-not (Test-HostForwardDenied -Forwards $t3Failed -Key 'T3'))

    ok "t3: a denied forward yields no url either" (
        (Get-T3ForwardUrl -Forwards (ConvertFrom-HostForwardStatus -Raw "T3=denied`n")) -eq "")
    ok "t3: an absent status file yields no url" ((Get-T3ForwardUrl -Forwards @{}) -eq "")

    # T3_URL crosses the SSH boundary and is printed, so anything that is not an http(s)
    # origin with a port is refused rather than echoed.
    foreach ($bad in @('javascript:alert(1)', 'https://work-vm.vpn.example', 'http://x y:2302', "https://a:2302`nX")) {
        ok "t3: a T3_URL that is not an origin is refused ('$($bad -replace "`n", '\n')')" (
            (Get-T3ForwardUrl -Forwards @{ 'T3_URL' = $bad }) -eq "")
    }
    ok "t3: an IPv6 origin is accepted (one bracket pair, as the guest wrote it)" (
        (Get-T3ForwardUrl -Forwards @{ 'T3_URL' = 'https://[2001:db8::1]:2302' }) -eq 'https://[2001:db8::1]:2302')
}

# The summary must not hide any of the three answers behind the CA-import block, which
# only runs when the TLS proxy came up.
ok "t3: the forwarded-URL summary is gated on the service, not on HTTPS readiness" (
    $provContent -match '(?ms)^if \(\$ServiceUrl\) \{\r?\n\s+\$t3ForwardUrl = Get-T3ForwardUrl')
ok "t3: ...and the URL is the guest's, not one this side assembles" (
    $provContent -notmatch 'Get-T3ForwardScheme')
ok "t3: an allocated forward with no origin is reported rather than skipped" (
    $provContent -match 'a host forward exists, but the VM is not serving on it')
ok "opencode: both call sites go through the one plan function" (
    ([regex]::Matches($provContent, 'Get-OpenCodeServerPlan -Forwards')).Count -ge 2)

# ── Remote host service params (batch B7) ───────────────────────────────────
# The zero-change bar for the remote work: all three new provisioner parameters and all
# four new installer parameters default to something INERT, so an install that never
# names a remote host behaves -- and splats -- exactly as before.
Write-Host ""
Write-Host "=== Remote host service params (B7) ===" -ForegroundColor Cyan
# -PublicHost (plan section 4.12) joins them: inert while empty, which is every local
# install and every remote host without a PublicHostPattern.
foreach ($pn in @('ServiceUrl', 'InstanceName', 'VmTokenB64', 'PublicHost')) {
    $rp = Get-ScriptParam (Join-Path $repoRoot "Provision-AgentVM.ps1") $pn
    ok "Provision-AgentVM has -$pn param" ($null -ne $rp)
    ok "Provision-AgentVM -$pn defaults to EMPTY (inert)" ((Get-ParamDefaultValue $rp) -eq '""')
}
$abp = Get-ScriptParam (Join-Path $repoRoot "Auto-Install.ps1") "Backend"
ok "Auto-Install has -Backend param" ($null -ne $abp)
ok "Auto-Install -Backend defaults to hyperv-local (today's path)" ((Get-ParamDefaultValue $abp) -eq '"hyperv-local"')
$asu = Get-ScriptParam (Join-Path $repoRoot "Auto-Install.ps1") "ServiceUrl"
ok "Auto-Install has -ServiceUrl param" ($null -ne $asu)
ok "Auto-Install -ServiceUrl defaults to EMPTY" ((Get-ParamDefaultValue $asu) -eq '""')
$asa = Get-ScriptParam (Join-Path $repoRoot "Auto-Install.ps1") "ServiceAuth"
ok "Auto-Install has -ServiceAuth param" ($null -ne $asa)
ok "Auto-Install -ServiceAuth defaults to negotiate" ((Get-ParamDefaultValue $asa) -eq '"negotiate"')
$ain = Get-ScriptParam (Join-Path $repoRoot "Auto-Install.ps1") "InstanceName"
ok "Auto-Install has -InstanceName param" ($null -ne $ain)
ok "Auto-Install -InstanceName defaults to EMPTY" ((Get-ParamDefaultValue $ain) -eq '""')
$acpu = Get-ScriptParam (Join-Path $repoRoot "Auto-Install.ps1") "VmCpuCount"
ok "Auto-Install has -VmCpuCount param" ($null -ne $acpu)
ok "Auto-Install -VmCpuCount defaults to 0 (= the script's own default)" ((Get-ParamDefaultValue $acpu) -eq '0')

# The env prefix carries the guest-side contract names (bin/provision.sh reads these).
ok "envPrefix includes CONSTRUCT_SERVICE_URL" ($provContent -match "CONSTRUCT_SERVICE_URL=")
ok "envPrefix includes CONSTRUCT_INSTANCE_NAME" ($provContent -match "CONSTRUCT_INSTANCE_NAME=")
ok "envPrefix includes CONSTRUCT_VM_TOKEN_B64" ($provContent -match "CONSTRUCT_VM_TOKEN_B64=")
ok "envPrefix appends the service suffix after the external one" ($provContent -match '\$externalEnv \+ \$serviceEnv')

# Get-ServiceEnvSuffix: pure, so every case is testable without a VM. Extracted from the
# script's AST exactly like Get-ExternalEnvSuffix above.
$fnSvc = $provAst.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-ServiceEnvSuffix' }, $true) | Select-Object -First 1
ok "Provision-AgentVM defines Get-ServiceEnvSuffix" ($null -ne $fnSvc)
if ($fnSvc) {
    Invoke-Expression $fnSvc.Extent.Text
    # THE ZERO-CHANGE CASE: nothing set -> nothing appended, so a local VM's remote
    # command line, provisioning log and config.env are byte-identical to before.
    ok "service suffix: nothing set -> empty (zero-change)" `
        ((Get-ServiceEnvSuffix -ServiceUrl "" -InstanceName "" -VmTokenB64 "") -eq "")
    ok "service suffix: whitespace-only is treated as unset" `
        ((Get-ServiceEnvSuffix -ServiceUrl "   " -InstanceName "" -VmTokenB64 "") -eq "")
    ok "service suffix: URL only" `
        ((Get-ServiceEnvSuffix -ServiceUrl "https://buildbox:7462" -InstanceName "" -VmTokenB64 "") -eq " CONSTRUCT_SERVICE_URL='https://buildbox:7462'")
    ok "service suffix: all three, in the contract order" `
        ((Get-ServiceEnvSuffix -ServiceUrl "https://b:7462" -InstanceName "work-vm" -VmTokenB64 "dG9rZW4=") -eq
         " CONSTRUCT_SERVICE_URL='https://b:7462' CONSTRUCT_INSTANCE_NAME='work-vm' CONSTRUCT_VM_TOKEN_B64='dG9rZW4='")
    ok "service suffix: a token can be sent without a name" `
        ((Get-ServiceEnvSuffix -ServiceUrl "https://b:7462" -InstanceName "" -VmTokenB64 "abc") -eq
         " CONSTRUCT_SERVICE_URL='https://b:7462' CONSTRUCT_VM_TOKEN_B64='abc'")
    # Every value crosses a shell boundary, so every value is POSIX-quoted -- a URL with
    # an apostrophe (or anything else) can neither break nor extend the command.
    ok "service suffix: an apostrophe is POSIX-quoted, not interpolated" `
        ((Get-ServiceEnvSuffix -ServiceUrl "https://o'brien:7462" -InstanceName "" -VmTokenB64 "") -eq
         " CONSTRUCT_SERVICE_URL='https://o'\''brien:7462'")
    ok "service suffix: a hostile instance name cannot inject a command" `
        ((Get-ServiceEnvSuffix -ServiceUrl "" -InstanceName "x'; rm -rf /; '" -VmTokenB64 "") -eq
         " CONSTRUCT_INSTANCE_NAME='x'\''; rm -rf /; '\'''")
}

# ── Name-only targeting: -InstanceName on the four launched scripts (B11) ───
Write-Host ""
Write-Host "=== Name-only targeting (-InstanceName) ===" -ForegroundColor Cyan

# Every script the control panel and the T3 Desktop updater launch must ACCEPT the name.
foreach ($rel in @("Provision-AgentVM.ps1", "Update-T3Code.ps1", "Set-AgentVmCheckpoints.ps1",
                   "Get-AgentUsage.ps1", "Create-AgentVM.ps1", "Auto-Install.ps1")) {
    $p = Get-ScriptParam (Join-Path $repoRoot $rel) "InstanceName"
    ok "$rel has -InstanceName" ($null -ne $p)
    ok "$rel -InstanceName defaults to empty (nothing is resolved unless asked)" (
        (Get-ParamDefaultValue $p) -eq '""')
}

# ...and must resolve it through the ONE adapter, never with a second copy of the rules.
foreach ($rel in @("Provision-AgentVM.ps1", "Update-T3Code.ps1", "Set-AgentVmCheckpoints.ps1",
                   "Get-AgentUsage.ps1")) {
    $txt = [System.IO.File]::ReadAllText((Join-Path $repoRoot $rel))
    ok "$rel resolves it with Resolve-ConstructVmTarget" ($txt -match 'Resolve-ConstructVmTarget')
    ok "$rel loads lib\AgentVm.InstanceTarget.ps1 for it" ($txt -match 'AgentVm\.InstanceTarget\.ps1')
    ok "$rel only resolves when a name was given" ($txt -match '(?m)^if \(\$InstanceName\) \{')
}
foreach ($rel in @("Create-AgentVM.ps1", "Auto-Install.ps1")) {
    $txt = [System.IO.File]::ReadAllText((Join-Path $repoRoot $rel))
    ok "$rel derives the VM name with Get-ConstructLocalVmIdentity" ($txt -match 'Get-ConstructLocalVmIdentity')
}

# THE CAPABILITY MARKER the control panel probes for (extension/src/lifecycle.js
# INSTANCE_TARGET_LIB): a file, because the PARAMETER name predates this meaning.
ok "the marker file exists" (Test-Path -LiteralPath (Join-Path $repoRoot "lib/AgentVm.InstanceTarget.ps1"))
$lifecycleJs = [System.IO.File]::ReadAllText((Join-Path $repoRoot "extension/src/lifecycle.js"))
ok "the panel probes for that exact file name" ($lifecycleJs -match 'AgentVm\.InstanceTarget\.ps1')

# The GUEST still learns the instance name only for a SERVICE-MANAGED VM: a local
# instance's env prefix (and therefore its config.env) is unchanged.
$provTxt = [System.IO.File]::ReadAllText((Join-Path $repoRoot "Provision-AgentVM.ps1"))
ok "guest: the name is gated on -ServiceUrl before it reaches the env prefix" (
    $provTxt -match '\$guestInstanceName = ""\s*\r?\n\s*if \(\$ServiceUrl\) \{ \$guestInstanceName = \$InstanceName \}')
ok "guest: ...and that is what Get-ServiceEnvSuffix is handed" (
    $provTxt -match 'Get-ServiceEnvSuffix -ServiceUrl \$ServiceUrl -InstanceName \$guestInstanceName')

# THE HOST SERVICE IS PART OF THE TARGET: a name-targeted reprovision of a remote
# instance has to reach the service the REGISTRY names, not whatever the guest was last
# told -- and an explicit -ServiceUrl that disagrees is a conflict like any other.
ok "service: -ServiceUrl is conflict-checked with the rest" (
    $provTxt -match "foreach \(\`$tp in @\('VmHost', 'HostAlias', 'SshPort', 'LocalKeyName', 'ConfigBranch', 'ServiceUrl', 'PublicHost'\)\)")
ok "service: ...and is taken from the entry when the caller did not bind it" (
    $provTxt -match "if \(-not \`$PSBoundParameters\.ContainsKey\('ServiceUrl'\)\)\s*\{\s*\`$ServiceUrl\s*=\s*\[string\]\`$instanceTarget\.ServiceUrl \}")

# An endpoint resolved BY NAME is still an endpoint the caller stated, so the guest
# records it exactly as it would from -VmHost.
ok "endpoint: a resolved non-default endpoint counts as explicitly stated" (
    $provTxt -match '\$script:InstanceEndpointStated = \$true')
ok "endpoint: ...and feeds the same `$explicitEndpoint gate" (
    $provTxt -match '\$explicitEndpoint = \[bool\]\(\$PSBoundParameters\.ContainsKey\(''VmHost''\) -or \$PSBoundParameters\.ContainsKey\(''SshPort''\) -or \$script:InstanceEndpointStated\)')

# ── Local registry writing from the installers (B11) ───────────────────────
Write-Host ""
Write-Host "=== Local instance recording ===" -ForegroundColor Cyan
$createTxt = [System.IO.File]::ReadAllText((Join-Path $repoRoot "Create-AgentVM.ps1"))
ok "Create-AgentVM records the VM through the shared writer" ($createTxt -match 'Register-ConstructLocalVm ')
ok "Create-AgentVM records it after the install media is detached" (
    $createTxt.IndexOf('Detach-ConstructInstallMedia -Name $VmName') -lt $createTxt.LastIndexOf('Register-ThisVmInstance'))
ok "Create-AgentVM also records on the reprovision branch" (
    ([regex]::Matches($createTxt, 'Register-ThisVmInstance')).Count -ge 3)
$autoTxt = [System.IO.File]::ReadAllText((Join-Path $repoRoot "Auto-Install.ps1"))
ok "Auto-Install records an existing local VM through the shared writer" (
    $autoTxt -match 'Register-ConstructLocalVmInstance -Name \$VmGuestName')
ok "Auto-Install never hand-rolls the entry JSON" ($autoTxt -notmatch '"backend"\s*:\s*"hyperv-local"')

# ── The adapter both installers derive through (lib\AgentVm.InstanceTarget.ps1) ──
# THE TWO WAYS IN ARE NOT CASE-EQUIVALENT, and that asymmetry is the whole point:
# -VmName is a Hyper-V DISPLAY name (case-insensitive, lowercased to get the instance
# name), while -InstanceName IS the instance name and is held to the one rule AS
# SUPPLIED. An installer that lowercased the latter would happily create a VM under a
# spelling extension/src/instances.js, Resolve-ConstructInstanceTarget and the panel's
# "Register this VM" box all refuse -- an instance nothing could name afterwards.
Write-Host ""
Write-Host "=== The identity adapter ===" -ForegroundColor Cyan
. (Join-Path $repoRoot "lib/AgentVm.InstanceTarget.ps1")

function Get-IdentityError([scriptblock]$Script) {
    try { & $Script | Out-Null; return "" } catch { return [string]$_.Exception.Message }
}

$idDefault = Get-ConstructLocalVmIdentity -VmName 'Agent-VM'
ok "adapter: -VmName keeps the DISPLAY case Hyper-V was given" ($idDefault.VmName -ceq 'Agent-VM')
ok "adapter: ...and lowercases it for the instance name"       ($idDefault.Name -ceq 'agent-vm')
ok "adapter: ...deriving today's literals for the default VM" (
    $idDefault.VmHost -ceq 'agent-vm.mshome.net' -and $idDefault.HostAlias -ceq 'agent-vm' -and
    $idDefault.KeyName -ceq 'agent_vm_ed25519' -and $idDefault.ConfigBranch -ceq 'vm' -and
    [int]$idDefault.SshPort -eq 22 -and $idDefault.IsDefault -eq $true)
$idNamed = Get-ConstructLocalVmIdentity -VmName 'Work-VM'
ok "adapter: a display-cased NON-default VM is accepted"  ($idNamed.Name -ceq 'work-vm')
ok "adapter: ...and its display name is returned as given" ($idNamed.VmName -ceq 'Work-VM')
ok "adapter: ...with the instance-scoped derivations"      (
    $idNamed.VmHost -ceq 'work-vm.mshome.net' -and $idNamed.HostAlias -ceq 'work-vm' -and
    $idNamed.KeyName -ceq 'construct_work-vm_ed25519' -and $idNamed.ConfigBranch -ceq 'vm-work-vm' -and
    $idNamed.IsDefault -eq $false)
$idByName = Get-ConstructLocalVmIdentity -Name 'work-vm' -ParameterLabel 'InstanceName'
ok "adapter: -InstanceName derives the same identity" (
    $idByName.VmName -ceq 'work-vm' -and $idByName.KeyName -ceq $idNamed.KeyName -and
    $idByName.ConfigBranch -ceq $idNamed.ConfigBranch)

# THE ASYMMETRY, pinned in both directions.
$upperInstance = Get-IdentityError { Get-ConstructLocalVmIdentity -Name 'Work-VM' -ParameterLabel 'InstanceName' }
ok "adapter: an UPPERCASE -InstanceName is REFUSED (the registry could never name it)" ($upperInstance -ne "")
ok "adapter: ...named as -InstanceName, not -VmName" ($upperInstance -like "*-InstanceName 'Work-VM'*")
ok "adapter: ...with the ONE rule, and a lowercase example" (
    $upperInstance -like "*$(Get-ConstructInstanceNameRule)*" -and $upperInstance -like "*e.g. 'work-vm'*")
ok "adapter: ...and the very same spelling IS accepted as a -VmName" (
    (Get-IdentityError { Get-ConstructLocalVmIdentity -VmName 'Work-VM' }) -eq "")
# The registry reader agrees with the strict half -- that is what "could never name it" means.
ok "adapter: the registry reader refuses that instance name too" (
    -not (& { . (Join-Path $repoRoot "lib/AgentVm.Instances.ps1"); Test-ConstructInstanceName 'Work-VM' }))

foreach ($bad in @('Work VM', 'work.vm', 'work-', '-work', '')) {
    ok "adapter: '$bad' is refused as a -VmName" (
        (Get-IdentityError { Get-ConstructLocalVmIdentity -VmName $bad }) -ne "")
}
$reserved = Get-IdentityError { Get-ConstructLocalVmIdentity -VmName 'Construct-Work' }
ok "adapter: the reserved prefix is refused case-insensitively" ($reserved -ne "")
ok "adapter: ...and is reported as its own problem" ($reserved -like "*reserved*prefix*")
ok "adapter: exactly one of -Name / -VmName is required" (
    (Get-IdentityError { Get-ConstructLocalVmIdentity }) -ne "" -and
    (Get-IdentityError { Get-ConstructLocalVmIdentity -Name 'work-vm' -VmName 'Work-VM' }) -ne "")
ok "adapter: the rule and the pattern come from the registry library" (
    (Get-ConstructInstanceNameRule) -eq (& { . (Join-Path $repoRoot "lib/AgentVm.Instances.ps1"); $script:ConstructInstanceNameRule }) -and
    (Get-ConstructInstanceNamePattern) -eq (& { . (Join-Path $repoRoot "lib/AgentVm.Instances.ps1"); $script:ConstructInstanceNameRe }))

# ── ONE NAME RULE, ONE DERIVATION: the installers may hold no copy ──────────
# The guard that fails the moment either installer re-states the rule or a formula
# instead of asking lib\AgentVm.Instances.ps1 (through the adapter) for it. Comments and
# doc blocks are stripped first: they DESCRIBE the rule on purpose, and describing it is
# not the problem -- executing a second copy of it is.
Write-Host ""
Write-Host "=== One name rule, one derivation ===" -ForegroundColor Cyan

function Get-ScriptCode {
    param([string]$Path)
    $txt = [System.IO.File]::ReadAllText($Path)
    # <# .. #> blocks, then whole-line # comments (the same comment-stripping the
    # extension's scriptSupportsParam probe uses).
    $txt = [regex]::Replace($txt, '(?s)<#.*?#>', '')
    return [regex]::Replace($txt, '(?m)^[ \t]*#.*$', '')
}

# Auto-Install.ps1's REMOTE half derives a remote VM's identity from the name the SERVICE
# knows it by -- a different contract (docs/remote-host.md), owned elsewhere, and not
# what this guard is about. It is one contiguous region, so the local guard runs on the
# code outside it; the split itself is asserted, so the guard cannot silently start
# covering nothing.
$autoFull = Get-ScriptCode (Join-Path $repoRoot "Auto-Install.ps1")
$remoteBanner = 'REMOTE HOST INSTALL'
$remoteEnd    = '# -- Handle an already-installed VM'
$autoRaw      = [System.IO.File]::ReadAllText((Join-Path $repoRoot "Auto-Install.ps1"))
$remoteStartIx = $autoRaw.IndexOf($remoteBanner)
$remoteEndIx   = $autoRaw.IndexOf('Handle an already-installed VM')
ok "guard: Auto-Install's remote region is found and is one contiguous block" (
    $remoteStartIx -gt 0 -and $remoteEndIx -gt $remoteStartIx)
$autoLocalRaw = $autoRaw.Substring(0, $remoteStartIx) + $autoRaw.Substring($remoteEndIx)
$autoLocalCode = [regex]::Replace([regex]::Replace($autoLocalRaw, '(?s)<#.*?#>', ''), '(?m)^[ \t]*#.*$', '')

$installerCode = @{
    "Create-AgentVM.ps1"            = Get-ScriptCode (Join-Path $repoRoot "Create-AgentVM.ps1")
    "Auto-Install.ps1 (local half)" = $autoLocalCode
}
foreach ($rel in $installerCode.Keys) {
    $code = $installerCode[$rel]
    ok "$rel does not restate the name REGEX" ($code -notmatch '\[a-z0-9\]\(\[a-z0-9-\]\{0,61\}')
    ok "$rel does not restate the name RULE text" ($code -notmatch '1-63 lowercase letters')
    ok "$rel does not build '<name>.mshome.net' itself" ($code -notmatch '\.mshome\.net')
    ok "$rel does not build a key file name itself" ($code -notmatch "agent_vm_ed25519" -and $code -notmatch 'construct_\$')
    ok "$rel does not build 'vm-<alias>' itself" ($code -notmatch '"vm-\$')
    ok "$rel asks the library for the derived identity" ($code -match 'Get-ConstructLocalVmIdentity|Get-ConstructDerivedVmIdentity')
}
$autoCode = Get-ScriptCode (Join-Path $repoRoot "Auto-Install.ps1")

# Create-AgentVM's local identity comes from ONE call, and every derived value below it
# reads that object rather than re-deriving.
$createCode = $installerCode["Create-AgentVM.ps1"]
ok "Create-AgentVM derives its identity exactly once" (
    @([regex]::Matches($createCode, '\$script:VmIdentity\s*=\s*Get-ConstructLocalVmIdentity')).Count -eq 1)
ok "Create-AgentVM reads the guest name off it"  ($createCode -match '\$script:VmGuestName\s*=\s*\$script:VmIdentity\.Name')
ok "Create-AgentVM reads 'is this the default' off it" ($createCode -match '\$script:VmIsDefault\s*=\s*\$script:VmIdentity\.IsDefault')
ok "Create-AgentVM reads the key name off it"    ($createCode -match '\$LocalKeyName\s*=\s*\$script:VmIdentity\.KeyName')
ok "Auto-Install derives its identity exactly once" (
    @([regex]::Matches($autoCode, '\$script:VmIdentity\s*=\s*Get-ConstructLocalVmIdentity')).Count -eq 1)
foreach ($pair in @(
    @{ Var = 'VmGuestName'; Field = 'Name' },
    @{ Var = 'VmDnsName';   Field = 'VmHost' },
    @{ Var = 'VmIsDefault'; Field = 'IsDefault' },
    @{ Var = 'VmAlias';     Field = 'HostAlias' },
    @{ Var = 'VmKeyName';   Field = 'KeyName' }
)) {
    ok "Auto-Install reads `$$($pair.Var) off the derived identity" (
        $autoCode -match ("\`$$($pair.Var)\s*=\s*\`$script:VmIdentity\.$($pair.Field)"))
}
ok "Auto-Install takes the config-sync branch from it (explicit -ConfigBranch still wins)" (
    $autoCode -match '\$VmConfigBranch = if \(\$ConfigBranch\) \{ \$ConfigBranch \} else \{ \$script:VmIdentity\.ConfigBranch \}')
ok "Auto-Install sources the name rule instead of stating it" (
    $autoCode -match '\$script:ConstructVmNameRe\s*=\s*Get-ConstructInstanceNamePattern' -and
    $autoCode -match '\$script:ConstructVmNameRule\s*=\s*Get-ConstructInstanceNameRule')
# ...and the derived identity the rest of the installer's local helpers use comes from
# the same library through one loader.
ok "Auto-Install's local helpers share one derivation loader" (
    @([regex]::Matches($autoCode, 'Get-ConstructDerivedVmIdentity -VmName')).Count -ge 3)

# ── ZERO-CHANGE: a default-only install writes nothing AND prints nothing ───
# The registration step is the ONE thing B11 adds to the default local path, so the
# fixture is its complete captured output. Pre-B11 that step did not exist, so the
# fixture it is diffed against is the empty transcript -- anything at all on ANY stream
# (Write-Note, Write-Warning, a stray object) is a visible change to an install that must
# look exactly like it always did.
Write-Host ""
Write-Host "=== Zero-change: the default local path ===" -ForegroundColor Cyan

$zcRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("construct-b11-zerochange-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $zcRoot -Force | Out-Null
$savedLocalAppData = $env:LOCALAPPDATA
try {
    $env:LOCALAPPDATA = Join-Path $zcRoot "appdata"

    # The installers' own registration functions, lifted from their ASTs and run against
    # a real (empty) profile -- not a re-implementation of them.
    function Get-FunctionText([string]$ScriptPath, [string]$Name) {
        $errs = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($ScriptPath, [ref]$null, [ref]$errs)
        $fn = $ast.FindAll({
            param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name
        }, $true) | Select-Object -First 1
        if ($fn) { return [string]$fn.Extent.Text }
        return ""
    }

    # Auto-Install's wrapper: the existing-VM path's registration, with -ScriptsDir
    # pointed at this checkout (which IS a scripts dir).
    $autoFnText = Get-FunctionText (Join-Path $repoRoot "Auto-Install.ps1") 'Register-ConstructLocalVmInstance'
    ok "zero-change: Auto-Install's registration function was found" ($autoFnText -ne "")
    Invoke-Expression $autoFnText
    $autoTranscript = & {
        Register-ConstructLocalVmInstance -Name 'agent-vm' -ScriptsDir $repoRoot
    } *>&1 | Out-String
    ok "zero-change: the DEFAULT install's registration prints nothing at all" ($autoTranscript -eq "")
    ok "zero-change: ...and returns nothing to print"  ($null -eq (Register-ConstructLocalVmInstance -Name 'agent-vm' -ScriptsDir $repoRoot))

    # Create-AgentVM's wrapper, which reads $script:VmGuestName / $ConfigBranch from its
    # caller's scope and prints through Write-Note / Write-Warning.
    $createFnText = Get-FunctionText (Join-Path $repoRoot "Create-AgentVM.ps1") 'Register-ThisVmInstance'
    ok "zero-change: Create-AgentVM's registration function was found" ($createFnText -ne "")
    $createTranscript = & {
        function Write-Note($msg) { Write-Host "    $msg" }
        $script:VmGuestName = 'agent-vm'
        $ConfigBranch = ""
        Invoke-Expression $createFnText
        Register-ThisVmInstance -ScriptsDir $repoRoot
    } *>&1 | Out-String
    ok "zero-change: Create-AgentVM's registration prints nothing either" ($createTranscript -eq "")

    $zcRegistry = Join-Path (Join-Path $env:LOCALAPPDATA "The-Construct") "instances.json"
    ok "zero-change: no instances.json was created" (-not (Test-Path -LiteralPath $zcRegistry))
    ok "zero-change: not even its directory"        (-not (Test-Path -LiteralPath (Split-Path -Parent $zcRegistry)))

    # The CONTROL: a non-default VM does write, and does say so -- proving the assertions
    # above are about the default path and not about a helper that never runs.
    $namedTranscript = & {
        Register-ConstructLocalVmInstance -Name 'build-vm' -ScriptsDir $repoRoot
    } *>&1 | Out-String
    ok "control: a NAMED VM does write the registry" (Test-Path -LiteralPath $zcRegistry)
    ok "control: ...and the path is what the caller reports" ($namedTranscript.Trim() -eq $zcRegistry)
    ok "control: ...and the default is written alongside it" (
        ([System.IO.File]::ReadAllText($zcRegistry) -match '"agent-vm"') -and
        ([System.IO.File]::ReadAllText($zcRegistry) -match '"build-vm"'))
    # ...and once the file exists, the default IS materialised (no longer the only VM).
    ok "control: the default is recorded once a registry exists" (
        (Register-ConstructLocalVmInstance -Name 'agent-vm' -ScriptsDir $repoRoot) -eq $zcRegistry)
} finally {
    $env:LOCALAPPDATA = $savedLocalAppData
    Remove-Item -LiteralPath $zcRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "  $($script:pass) passed, $($script:fail) failed" -ForegroundColor $(if ($script:fail -gt 0) { "Red" } else { "Green" })
Write-Host "==============================" -ForegroundColor Cyan

if ($script:fail -gt 0) { exit 1 }
exit 0
