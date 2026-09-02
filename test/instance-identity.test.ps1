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
    "lib/AgentVm.Common.ps1"
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
}

# ── Remote host service params (batch B7) ───────────────────────────────────
# The zero-change bar for the remote work: all three new provisioner parameters and all
# four new installer parameters default to something INERT, so an install that never
# names a remote host behaves -- and splats -- exactly as before.
Write-Host ""
Write-Host "=== Remote host service params (B7) ===" -ForegroundColor Cyan
foreach ($pn in @('ServiceUrl', 'InstanceName', 'VmTokenB64')) {
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

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "  $($script:pass) passed, $($script:fail) failed" -ForegroundColor $(if ($script:fail -gt 0) { "Red" } else { "Green" })
Write-Host "==============================" -ForegroundColor Cyan

if ($script:fail -gt 0) { exit 1 }
exit 0
