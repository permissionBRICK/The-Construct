#Requires -Version 5.1
<#
    Tests for the constructd host installer. Run:

        pwsh -NoProfile -File service/tests/host-installer.test.ps1

    The installer touches Hyper-V, the certificate store, the firewall and the
    service control manager, so it cannot be executed here (and would be a poor
    idea on a dev box anyway). What CAN be checked without a Windows host is
    everything that goes wrong silently otherwise:

      (a) the scripts parse with zero errors under PS 5.1 syntax,
      (b) every parameter the docs and the README promise exists, with the
          documented default and the documented type,
      (c) both scripts declare SupportsShouldProcess, so -WhatIf is real,
      (d) the pure helpers (port-range and listen-URL parsing) behave,
      (e) the settings the installer writes match the option names the service
          binds -- a typo there produces a service that starts with defaults
          instead of failing, which is the worst possible outcome.

    The .NET test suite runs this file too (HostInstallerTests), so it is part of
    `dotnet test` on a machine that has pwsh.
#>
$ErrorActionPreference = "Stop"

$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$serviceDir = Split-Path -Parent $here
$repoRoot   = Split-Path -Parent $serviceDir

$installer   = Join-Path $serviceDir "host/Install-ConstructHost.ps1"
$uninstaller = Join-Path $serviceDir "host/Uninstall-ConstructHost.ps1"

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

# ── (a) Parser checks ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Parser checks ===" -ForegroundColor Cyan

foreach ($path in @($installer, $uninstaller)) {
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$errors)
    ok "parse: $(Split-Path -Leaf $path) has zero errors" ($errors.Count -eq 0)
    foreach ($e in $errors) {
        Write-Host "    ERROR: $($e.Message) (line $($e.Extent.StartLineNumber))" -ForegroundColor Red
    }
}

# PS 5.1 has no ternary, no ?? and no ?. -- a script that uses them parses on
# pwsh 7 and then fails on the host it is meant to run on.
foreach ($path in @($installer, $uninstaller)) {
    $text = Get-Content -Raw -LiteralPath $path
    $name = Split-Path -Leaf $path
    ok "$name has no null-coalescing operator" (-not ($text -match '\?\?'))
    ok "$name has no null-conditional access" (-not ($text -match '\$\w+\?\.'))
    ok "$name declares #Requires -Version 5.1" ($text -match '(?m)^#Requires -Version 5\.1')
}

# ── (b) Parameter metadata ───────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Parameters ===" -ForegroundColor Cyan

function Get-ScriptParameters($path) {
    <# Name -> @{ Type; Default; Mandatory } straight from the AST, so this reads
       what the script really declares rather than what a comment claims. #>
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
    $result = @{}
    foreach ($p in $ast.ParamBlock.Parameters) {
        $name = $p.Name.VariablePath.UserPath
        $default = $null
        if ($p.DefaultValue) { $default = $p.DefaultValue.Extent.Text }
        $mandatory = $false
        foreach ($attr in $p.Attributes) {
            if ($attr -is [System.Management.Automation.Language.AttributeAst] -and
                $attr.TypeName.Name -eq "Parameter") {
                foreach ($named in $attr.NamedArguments) {
                    if ($named.ArgumentName -eq "Mandatory") { $mandatory = $true }
                }
            }
        }
        # ValidatePattern, so a test can assert what a parameter will and will not accept.
        $pattern = $null
        foreach ($attr in $p.Attributes) {
            if ($attr -is [System.Management.Automation.Language.AttributeAst] -and
                $attr.TypeName.Name -eq "ValidatePattern" -and $attr.PositionalArguments.Count -gt 0) {
                $pattern = $attr.PositionalArguments[0].Value
            }
        }

        $result[$name] = @{
            Type            = $p.StaticType.Name
            Default         = $default
            Mandatory       = $mandatory
            ValidatePattern = $pattern
        }
    }
    return $result
}

$install = Get-ScriptParameters $installer

# The batch brief names these; the README documents them. Renaming one silently
# breaks every runbook that calls the installer.
$expectedInstall = @(
    "ScriptsDir", "PublishDir", "ListenUrl", "PublicHost", "DataDir",
    "SshPortRange", "AppPortRange", "CertThumbprint", "ServiceName",
    "SwitchName", "WslDistro", "ListenAddress",
    "IsoSourcePath", "IsoSourceUrl", "IsoSha256",
    "AdminUser", "AdminMaxVms", "SkipPrereqs", "SkipAclHardening",
    "SkipIsoBuild", "IsoBuildOnly", "RotateAdminToken",
    "KeepHostAwake", "SkipPowerSettings", "NoStart")

foreach ($name in $expectedInstall) {
    ok "installer has -$name" ($install.ContainsKey($name))
}

ok "installer -ScriptsDir is mandatory" ($install["ScriptsDir"].Mandatory)
ok "installer -PublishDir is mandatory" ($install["PublishDir"].Mandatory)
ok "installer -ListenUrl defaults to https://0.0.0.0:7462" ($install["ListenUrl"].Default -eq '"https://0.0.0.0:7462"')
ok "installer -DataDir defaults under ProgramData" ($install["DataDir"].Default -eq '"C:\ProgramData\Construct\service"')
ok "installer -SshPortRange defaults to 2201-2299" ($install["SshPortRange"].Default -eq '"2201-2299"')
ok "installer -AppPortRange defaults to 2300-2999" ($install["AppPortRange"].Default -eq '"2300-2999"')
ok "installer -SwitchName defaults to the Default Switch" ($install["SwitchName"].Default -eq '"Default Switch"')
ok "installer -WslDistro defaults to Ubuntu" ($install["WslDistro"].Default -eq '"Ubuntu"')
ok "installer -ListenAddress defaults to 0.0.0.0" ($install["ListenAddress"].Default -eq '"0.0.0.0"')
ok "installer -ServiceName defaults to constructd" ($install["ServiceName"].Default -eq '"constructd"')
ok "installer -PublicHost defaults to this machine" ($install["PublicHost"].Default -eq '$env:COMPUTERNAME')
ok "installer -SkipPrereqs is a switch" ($install["SkipPrereqs"].Type -eq "SwitchParameter")
ok "installer -NoStart is a switch" ($install["NoStart"].Type -eq "SwitchParameter")
ok "installer -AdminMaxVms is an int" ($install["AdminMaxVms"].Type -eq "Int32")
ok "installer -SkipAclHardening is a switch (hardening is the default)" (
    $install["SkipAclHardening"].Type -eq "SwitchParameter" -and $null -eq $install["SkipAclHardening"].Default)
ok "installer -SkipIsoBuild is opt-in (the ISO is built by default)" (
    $install["SkipIsoBuild"].Type -eq "SwitchParameter" -and $null -eq $install["SkipIsoBuild"].Default)
ok "installer -IsoBuildOnly is opt-in" (
    $install["IsoBuildOnly"].Type -eq "SwitchParameter" -and $null -eq $install["IsoBuildOnly"].Default)
ok "the installer no longer takes -ProvisionWslForService" (-not $install.ContainsKey("ProvisionWslForService"))
ok "installer -RotateAdminToken is opt-in" (
    $install["RotateAdminToken"].Type -eq "SwitchParameter" -and $null -eq $install["RotateAdminToken"].Default)
# Not a [bool] with a default: "not given" has to stay distinguishable from "given as false",
# because that is what decides between prompting and leaving the machine alone.
ok "installer -KeepHostAwake is an unbound switch (absent means 'ask, or leave it alone')" (
    $install["KeepHostAwake"].Type -eq "SwitchParameter" -and $null -eq $install["KeepHostAwake"].Default)
ok "installer -SkipPowerSettings is opt-in" (
    $install["SkipPowerSettings"].Type -eq "SwitchParameter" -and $null -eq $install["SkipPowerSettings"].Default)

$uninstall = Get-ScriptParameters $uninstaller
foreach ($name in @("ServiceName", "DataDir", "PublicHost", "SshPortRange", "AppPortRange",
                    "ListenAddress", "RemovePortProxies", "RemoveCertificate", "RemoveData")) {
    ok "uninstaller has -$name" ($uninstall.ContainsKey($name))
}
ok "uninstaller -RemoveData is a switch (opt-in)" ($uninstall["RemoveData"].Type -eq "SwitchParameter")
ok "uninstaller -RemoveData is NOT the default" ($null -eq $uninstall["RemoveData"].Default)
ok "uninstaller -ServiceName matches the installer's" ($uninstall["ServiceName"].Default -eq $install["ServiceName"].Default)
ok "uninstaller -DataDir matches the installer's" ($uninstall["DataDir"].Default -eq $install["DataDir"].Default)
ok "uninstaller port ranges match the installer's" (
    $uninstall["SshPortRange"].Default -eq $install["SshPortRange"].Default -and
    $uninstall["AppPortRange"].Default -eq $install["AppPortRange"].Default)

# ── (c) -WhatIf is real ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== ShouldProcess ===" -ForegroundColor Cyan

foreach ($path in @($installer, $uninstaller)) {
    $text = Get-Content -Raw -LiteralPath $path
    $name = Split-Path -Leaf $path
    ok "$name declares SupportsShouldProcess" ($text -match 'CmdletBinding\(SupportsShouldProcess')
    ok "$name guards its changes with ShouldProcess" (
        ([regex]::Matches($text, 'ShouldProcess\(')).Count -ge 4)
}

# Every helper function that calls ShouldProcess must be an advanced function --
# in a plain function $PSCmdlet is not its own, and -WhatIf would not reach it.
$installAst = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref]$null, [ref]$null)
$functions = $installAst.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)
foreach ($fn in $functions) {
    if ($fn.Body.Extent.Text -match 'ShouldProcess\(') {
        ok "installer function $($fn.Name) is an advanced function" (
            $fn.Body.ParamBlock -and
            ($fn.Body.ParamBlock.Attributes | Where-Object { $_.TypeName.Name -eq "CmdletBinding" }))
    }
}

# ── (d) Pure helpers ─────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Helpers ===" -ForegroundColor Cyan

# Dot-source just the helper definitions: the installer's body would try to
# elevate. Taking them from the AST keeps this honest -- it is the shipped code.
foreach ($fn in $functions) {
    if ($fn.Name -in @("Split-PortRange", "Get-ListenPort", "Get-ConstructAclPolicy",
                       "Get-ConstructTrustedSid", "Get-ConstructAncestorRiskMask",
                       "Get-ConstructWriteRiskMask", "Get-ConstructUnsafeAce", "Resolve-ConstructAceSid", "Sort-ConstructHardeningOrder", "Format-ConstructCommandOutput",
                       "ConvertTo-ConstructPayload", "New-ConstructRelaunchScript",
                       "Get-ConstructPowerSetting", "ConvertFrom-ConstructPowerQuery",
                       "ConvertFrom-ConstructActiveScheme", "Format-ConstructPowerTimeout",
                       "Get-ConstructPowerReport", "Set-ConstructPowerNever",
                       "Test-ConstructNonInteractiveArgument", "Test-ConstructPromptAllowed",
                       "Write-Ok", "Write-Note")) {
        . ([scriptblock]::Create($fn.Extent.Text))
    }
}

# The access-mask table the risk functions read; it is script-scope state in the
# installer, so the test has to bring it along.
$maskAssignment = $installAst.FindAll(
    { $args[0] -is [System.Management.Automation.Language.AssignmentStatementAst] -and
      $args[0].Left.Extent.Text -eq '$script:ConstructRight' }, $true)
. ([scriptblock]::Create($maskAssignment[0].Extent.Text))

$uninstallAst = [System.Management.Automation.Language.Parser]::ParseFile($uninstaller, [ref]$null, [ref]$null)
$uninstallFunctions = $uninstallAst.FindAll(
    { $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)
foreach ($fn in $uninstallFunctions) {
    if ($fn.Name -eq "ConvertFrom-PortProxyRow") {
        . ([scriptblock]::Create($fn.Extent.Text))
    }

    # The uninstaller carries its own copy of the elevation transport (two standalone
    # scripts; dot-sourcing the installer would run its body). Load them under distinct
    # names so the installer's versions above stay under test too, and so a divergence
    # between the two shows up as a failure rather than as one shadowing the other.
    if ($fn.Name -eq "ConvertTo-ConstructPayload") {
        $renamed = $fn.Extent.Text -replace 'function\s+ConvertTo-ConstructPayload', 'function ConvertTo-UninstallPayload'
        . ([scriptblock]::Create($renamed))
    }
    if ($fn.Name -eq "New-ConstructRelaunchScript") {
        $renamed = $fn.Extent.Text -replace 'function\s+New-ConstructRelaunchScript', 'function New-UninstallRelaunchScript'
        . ([scriptblock]::Create($renamed))
    }
}

$uninstallAstText = $uninstallAst.Extent.Text

$range = Split-PortRange -Range "2201-2299" -Name "-SshPortRange"
ok "Split-PortRange reads the start" ($range.Start -eq 2201)
ok "Split-PortRange reads the end" ($range.End -eq 2299)

$threw = $false
try { Split-PortRange -Range "2299-2201" -Name "-SshPortRange" } catch { $threw = $true }
ok "Split-PortRange rejects a reversed range" $threw

$threw = $false
try { Split-PortRange -Range "0-99999" -Name "-SshPortRange" } catch { $threw = $true }
ok "Split-PortRange rejects ports outside 1-65535" $threw

ok "Get-ListenPort reads the port from the URL" ((Get-ListenPort -Url "https://0.0.0.0:7462") -eq 7462)
ok "Get-ListenPort handles a host name" ((Get-ListenPort -Url "https://buildbox.example.local:9443") -eq 9443)

$threw = $false
try { Get-ListenPort -Url "not a url" } catch { $threw = $true }
ok "Get-ListenPort rejects a non-URL" $threw

# ── (d2) The ACL policy ──────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== ACL policy ===" -ForegroundColor Cyan

# Well-known SIDs, not names: the policy has to be right on a non-English host.
$codePolicy = @(Get-ConstructAclPolicy -Kind Code)
$dataPolicy = @(Get-ConstructAclPolicy -Kind Data)

# @(...) around every filter: a lone hashtable's own .Count is its key count, not 1.
ok "code paths grant LocalSystem full control" (
    @($codePolicy | Where-Object { $_.Sid -eq 'S-1-5-18' -and $_.Rights -eq 'FullControl' }).Count -eq 1)
ok "code paths grant Administrators full control" (
    @($codePolicy | Where-Object { $_.Sid -eq 'S-1-5-32-544' -and $_.Rights -eq 'FullControl' }).Count -eq 1)
ok "code paths let Users read and execute only" (
    @($codePolicy | Where-Object { $_.Sid -eq 'S-1-5-32-545' -and $_.Rights -eq 'ReadAndExecute' }).Count -eq 1)
ok "code paths grant nobody else anything" ($codePolicy.Count -eq 3)

# The data directory is the authorization database: users, token hashes, the audit
# trail. Users must not be able to read it, never mind write it.
ok "the data path is SYSTEM + Administrators only" (
    $dataPolicy.Count -eq 2 -and
    @($dataPolicy | Where-Object { $_.Sid -eq 'S-1-5-32-545' }).Count -eq 0)
ok "the data path grants LocalSystem full control" (
    @($dataPolicy | Where-Object { $_.Sid -eq 'S-1-5-18' -and $_.Rights -eq 'FullControl' }).Count -eq 1)
ok "no policy uses a localizable account name" (
    @(($codePolicy + $dataPolicy) | Where-Object { $_.Sid -notmatch '^S-1-' }).Count -eq 0)

$installAstText = Get-Content -Raw -LiteralPath $installer
ok "the installer hardens the publish directory" ($installAstText -match '@\{ Path = \$PublishDir;\s+Kind = ''Code'';\s+Name = "-PublishDir" \}')
ok "the installer hardens the scripts directory" ($installAstText -match '@\{ Path = \$ScriptsDir;\s+Kind = ''Code'';\s+Name = "-ScriptsDir" \}')
ok "the installer hardens one protected service root" ($installAstText -match '@\{ Path = \$serviceRoot; Kind = ''Data''; Name = "the service root" \}')
ok "the ISO catalog lives under the protected service root" ($installAstText -match '\$isoCacheDir = Join-Path \$DataDir "iso"')
ok "no LocalSystem WSL directory is created any more" (-not ($installAstText -match '\$wslRoot'))
ok "the installer refuses reparse points" ($installAstText -match 'ReparsePoint')
ok "the installer refuses paths under a user profile root" ($installAstText -match 'user profile root')
ok "the installer rejects overlapping port ranges" ($installAstText -match 'overlap')

# Hardening has to happen before anything is put in those directories, and long
# before the service can ever run from them.
$aclAt      = $installAstText.IndexOf('foreach ($entry in (Sort-ConstructHardeningOrder -Entries $hardening))')
$isoAt      = $installAstText.IndexOf("Building the autoinstall ISO (as you, via WSL)")
$registerAt = $installAstText.IndexOf("Registering the Windows service")
ok "paths are hardened before anything is built into them" ($aclAt -gt 0 -and $aclAt -lt $isoAt)
ok "paths are hardened before the service is registered" ($aclAt -gt 0 -and $aclAt -lt $registerAt)

# ── (d4) The ancestor / descendant ACL decision ──────────────────────────────
Write-Host ""
Write-Host "=== ACL risk decisions ===" -ForegroundColor Cyan

$ancestorMask = Get-ConstructAncestorRiskMask
$writeMask    = Get-ConstructWriteRiskMask

# Synthetic ACLs, because a machine without Windows ACLs cannot produce real ones --
# and these are exactly the shapes that matter.
$systemAce = @{ Sid = 'S-1-5-18';     Rights = 0x1F01FF; Type = 'Allow' }   # FullControl
$adminAce  = @{ Sid = 'S-1-5-32-544'; Rights = 0x1F01FF; Type = 'Allow' }
$usersRead = @{ Sid = 'S-1-5-32-545'; Rights = 0x0200A9; Type = 'Allow' }   # ReadAndExecute
# What C:\ProgramData grants Users on a stock Windows: create entries, but not
# delete or re-permission existing ones.
$usersCreate = @{ Sid = 'S-1-5-32-545'; Rights = (0x0200A9 -bor 0x000004); Type = 'Allow' }
$usersDeleteChild = @{ Sid = 'S-1-5-32-545'; Rights = (0x0200A9 -bor 0x000040); Type = 'Allow' }
$attackerFull = @{ Sid = 'S-1-5-21-1-2-3-1001'; Rights = 0x1F01FF; Type = 'Allow' }
$attackerWrite = @{ Sid = 'S-1-5-21-1-2-3-1001'; Rights = 0x000002; Type = 'Allow' }
$attackerDeny  = @{ Sid = 'S-1-5-21-1-2-3-1001'; Rights = 0x1F01FF; Type = 'Deny' }

ok "a stock ProgramData-style parent is accepted" (
    @(Get-ConstructUnsafeAce -Aces @($systemAce, $adminAce, $usersCreate) -RiskMask $ancestorMask).Count -eq 0)
ok "a parent where a user can delete children is refused" (
    @(Get-ConstructUnsafeAce -Aces @($systemAce, $adminAce, $usersDeleteChild) -RiskMask $ancestorMask).Count -eq 1)
ok "an attacker-writable parent is refused" (
    @(Get-ConstructUnsafeAce -Aces @($systemAce, $attackerFull) -RiskMask $ancestorMask).Count -eq 1)
ok "SYSTEM and Administrators are never flagged" (
    @(Get-ConstructUnsafeAce -Aces @($systemAce, $adminAce) -RiskMask $writeMask).Count -eq 0)
ok "TrustedInstaller is never flagged" (
    @(Get-ConstructUnsafeAce -Aces @(@{ Sid = 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'; Rights = 0x1F01FF; Type = 'Allow' }) -RiskMask $writeMask).Count -eq 0)
ok "a Deny ACE is not a risk" (
    @(Get-ConstructUnsafeAce -Aces @($attackerDeny) -RiskMask $writeMask).Count -eq 0)

# The pre-created child with its own DACL: read-only is fine, any write is not.
ok "a protected child that only grants read is accepted" (
    @(Get-ConstructUnsafeAce -Aces @($systemAce, $adminAce, $usersRead) -RiskMask $writeMask).Count -eq 0)
ok "a protected child with an explicit untrusted write ACE is refused" (
    @(Get-ConstructUnsafeAce -Aces @($systemAce, $adminAce, $attackerWrite) -RiskMask $writeMask).Count -eq 1)
ok "creating entries in a CHILD still counts as write" (
    @(Get-ConstructUnsafeAce -Aces @($usersCreate) -RiskMask $writeMask).Count -eq 1)
ok "an empty ACL is not a risk" (@(Get-ConstructUnsafeAce -Aces @() -RiskMask $writeMask).Count -eq 0)

# Field failure (German host, 2026-09-02): Get-Acl hands back NTAccount names and
# translating "APPLICATION PACKAGE AUTHORITY\ALLE ANWENDUNGSPAKETE" (present on every
# stock C:\Windows) throws IdentityNotMappedException. The SID resolver must never throw
# and must keep an unmappable identity in the list as an UNTRUSTED one.
if ($env:OS -eq 'Windows_NT') {
    # [SecurityIdentifier] cannot be constructed on Linux; the branch is exercised on Windows only.
    $realSid = New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')
    ok "resolver: a SecurityIdentifier is returned as its value" ((Resolve-ConstructAceSid -Identity $realSid) -eq 'S-1-5-32-544')
}
$unmappable = [pscustomobject]@{ Value = 'APPLICATION PACKAGE AUTHORITY\ALLE ANWENDUNGSPAKETE' }
$unmappable | Add-Member -MemberType ScriptMethod -Name Translate -Value { throw (New-Object System.Security.Principal.IdentityNotMappedException("no mapping")) }
$resolved = Resolve-ConstructAceSid -Identity $unmappable
ok "resolver: an unmappable name does not throw and keeps the raw name" ($resolved -eq 'APPLICATION PACKAGE AUTHORITY\ALLE ANWENDUNGSPAKETE')
ok "resolver: ...and that identity is NOT trusted (fails closed when it holds risky rights)" `
    (@(Get-ConstructUnsafeAce -Aces @(@{ Sid = $resolved; Rights = 0x1F01FF; Type = 'Allow'; InheritOnly = $false }) -RiskMask $ancestorMask).Count -eq 1)
$orphan = [pscustomobject]@{ Value = 'S-1-5-21-1-2-3-9999' }
$orphan | Add-Member -MemberType ScriptMethod -Name Translate -Value { throw (New-Object System.Security.Principal.IdentityNotMappedException("no mapping")) }
ok "resolver: an orphaned SID shown as a name resolves to that SID string" ((Resolve-ConstructAceSid -Identity $orphan) -eq 'S-1-5-21-1-2-3-9999')
$mappable = [pscustomobject]@{ Value = 'VORDEFINIERT\Administratoren' }
$mappable | Add-Member -MemberType ScriptMethod -Name Translate -Value { param($t) [pscustomobject]@{ Value = 'S-1-5-32-544' } }
ok "resolver: a mappable name is translated" ((Resolve-ConstructAceSid -Identity $mappable) -eq 'S-1-5-32-544')
ok "resolver: null identity yields an empty (untrusted) SID" ((Resolve-ConstructAceSid -Identity $null) -eq '')
# Field failure #2 (2026-09-02): -PublishDir C:\Construct\service\publish was hardened BEFORE
# -ScriptsDir C:\Construct, so its ancestors still carried the stock C:\ inheritance
# (Authenticated Users: Modify) and the trust check refused the layout the docs recommend.
$order = @(Sort-ConstructHardeningOrder -Entries @(
    @{ Path = 'C:\ProgramData\Construct'; Kind = 'Data'; Name = 'root' }
    @{ Path = 'C:\Construct\service\publish'; Kind = 'Code'; Name = '-PublishDir' }
    @{ Path = 'C:\Construct\'; Kind = 'Code'; Name = '-ScriptsDir' }
))
ok "hardening order: -ScriptsDir (the parent) is hardened before -PublishDir inside it" (($order | ForEach-Object { $_.Name }) -join ',' -eq '-ScriptsDir,root,-PublishDir')
$order2 = @(Sort-ConstructHardeningOrder -Entries @(
    @{ Path = 'D:\aaa\bbb'; Kind = 'Code'; Name = 'a' }
    @{ Path = 'D:\ccc\ddd'; Kind = 'Code'; Name = 'b' }
))
ok "hardening order: equal depth keeps the given order" (($order2 | ForEach-Object { $_.Name }) -join ',' -eq 'a,b')
ok "hardening order: empty list" (@(Sort-ConstructHardeningOrder -Entries @()).Count -eq 0)
$installBody = Get-Content -Raw -LiteralPath $installer
ok "the installer hardens through Sort-ConstructHardeningOrder, not a fixed child-first list" ($installBody -match 'foreach \(\$entry in \(Sort-ConstructHardeningOrder -Entries \$hardening\)\)')
$aceListText = ($functions | Where-Object Name -eq 'ConvertTo-ConstructAceList').Extent.Text
ok "ACE list asks the ACL for SID-keyed rules instead of translating names" ($aceListText -match 'GetAccessRules\(\$true, \$true, \[System\.Security\.Principal\.SecurityIdentifier\]\)')
ok "ACE list routes every identity through the resolver" ($aceListText -match 'Resolve-ConstructAceSid -Identity')
$installText = Get-Content -Raw -LiteralPath $installer
ok 'relative -ScriptsDir/-PublishDir/-DataDir are made absolute against $PWD before the elevation relaunch' `
    (($installText.IndexOf("GetUnresolvedProviderPathFromPSPath(`$value)") -gt 0) -and
     ($installText.IndexOf("GetUnresolvedProviderPathFromPSPath(`$value)") -lt $installText.IndexOf("Relaunching as Administrator")))
ok 'the trust check does not resolve paths with [System.IO.Path]::GetFullPath (process cwd, not $PWD)' `
    (-not (($functions | Where-Object Name -eq 'Assert-ConstructPathTrustworthy').Extent.Text -match 'GetFullPath'))

# Inherit-only (IO) ACEs grant nothing on the object carrying them. Stock Windows puts
# CREATOR OWNER:(OI)(CI)(IO)(F) on C:\ProgramData, so judging them would refuse the
# DEFAULT -DataDir on every clean host -- the zero-change path failing on a false alarm.
$creatorOwnerIo = @{ Sid = 'S-1-3-0'; Rights = 0x1F01FF; Type = 'Allow'; InheritOnly = $true }
ok "a stock ProgramData CREATOR OWNER (IO) ace does not refuse the default DataDir" (
    @(Get-ConstructUnsafeAce -Aces @($systemAce, $adminAce, $usersCreate, $creatorOwnerIo) -RiskMask $ancestorMask).Count -eq 0)
ok "the same ace WITHOUT inherit-only is still a risk" (
    @(Get-ConstructUnsafeAce -Aces @(@{ Sid = 'S-1-3-0'; Rights = 0x1F01FF; Type = 'Allow'; InheritOnly = $false }) -RiskMask $ancestorMask).Count -eq 1)
ok "an inherit-only attacker ace on a hardened child is not a risk either" (
    @(Get-ConstructUnsafeAce -Aces @(@{ Sid = 'S-1-5-21-1-2-3-1001'; Rights = 0x1F01FF; Type = 'Allow'; InheritOnly = $true }) -RiskMask $writeMask).Count -eq 0)
ok "the ace list carries the inherit-only flag through from a real ACL" (
    $installAstText -match 'InheritOnly')

$installAst2 = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref]$null, [ref]$null)
ok "the ancestor chain is walked, not just the leaf" ($installAstText -match 'while \(\$current\)')
ok "existing descendants are re-permissioned" ($installAstText -match 'Get-ChildItem -LiteralPath \$Path -Recurse -Force')
ok "the hardened tree is verified after the fact" ($installAstText -match 'still grants write access to')

# ── (d3) The uninstaller's portproxy row parser ──────────────────────────────
Write-Host ""
Write-Host "=== Portproxy row parsing ===" -ForegroundColor Cyan

$row = ConvertFrom-PortProxyRow -Line "0.0.0.0         2201        172.20.144.5    22"
ok "a rule row parses" ($null -ne $row -and $row.ListenPort -eq 2201 -and $row.ConnectPort -eq 22)
ok "the listen address is read" ($row.ListenAddress -eq "0.0.0.0")

# The one that would otherwise leave every Construct rule behind.
$wild = ConvertFrom-PortProxyRow -Line "*   2201  172.20.144.5   22"
ok "the wildcard listen address normalizes to 0.0.0.0" ($null -ne $wild -and $wild.ListenAddress -eq "0.0.0.0")

ok "the English header is not a rule" (
    $null -eq (ConvertFrom-PortProxyRow -Line "Address         Port        Address         Port"))
ok "the German header is not a rule" (
    $null -eq (ConvertFrom-PortProxyRow -Line "Adresse         Port        Adresse         Port"))
ok "the separator is not a rule" (
    $null -eq (ConvertFrom-PortProxyRow -Line "--------------- ----------  --------------- ----------"))
ok "a blank line is not a rule" ($null -eq (ConvertFrom-PortProxyRow -Line ""))
ok "an IPv6 row is skipped" (
    $null -eq (ConvertFrom-PortProxyRow -Line "::              2201        fe80::1         22"))
ok "an out-of-range port is not a rule" (
    $null -eq (ConvertFrom-PortProxyRow -Line "0.0.0.0         70000       172.20.144.5    22"))
ok "a CRLF row still parses" (
    $null -ne (ConvertFrom-PortProxyRow -Line "0.0.0.0   2201  172.20.144.5   22`r"))

$uninstallText = Get-Content -Raw -LiteralPath $uninstaller
ok "the uninstaller checks netsh's show exit code" ($uninstallText -match 'could not list the port-proxy rules')
ok "the uninstaller checks netsh's delete exit code" ($uninstallText -match 'Could not delete the rule on')

# ── (d5) Value transport into the elevated / LocalSystem contexts ────────────
Write-Host ""
Write-Host "=== Injection-proof value transport ===" -ForegroundColor Cyan

# Everything nasty a parameter or a distro name could carry. If any of it can reach
# a generated script as source, it runs elevated or as LocalSystem.
$nasty = @(
    "Ub untu",                                   # a space: must stay ONE argument
    "it's",                                      # an apostrophe: ends a single-quoted literal
    'say "hi"',                                  # double quotes
    "a; Remove-Item C:\ -Recurse",               # a statement separator
    "b`n Remove-Item C:\ -Recurse",              # a newline
    "c`t`0d",                                    # control characters
    'e$(Get-Process)',                           # a subexpression
    'f`ng',                                      # a backtick escape
    "h\\server\share"                            # backslashes
)

foreach ($value in $nasty) {
    $payload = ConvertTo-ConstructPayload -Values @{ WslDistro = $value; Flag = [switch]$true }

    # The value survives the trip byte for byte...
    $round = ConvertFrom-Json $payload
    ok "payload round-trips $([regex]::Escape($value).Substring(0, [Math]::Min(18, $value.Length)))..." (
        $round.WslDistro -eq $value)

    # ...and never appears as SOURCE in the script that runs in the privileged context.
    # The relaunch script carries the payload, but only as inert base64.
    $relaunchScript = New-ConstructRelaunchScript `
        -ScriptPath "C:\Construct\service\host\Install-ConstructHost.ps1" -PayloadJson $payload

    ok "no generated script contains that value" (-not $relaunchScript.Contains($value))

    # And it really is still there, recoverable byte for byte from the base64 the
    # elevated copy decodes -- inertness that lost the value would be no good.
    $b64 = [regex]::Match($relaunchScript, "FromBase64String\('([A-Za-z0-9+/=]*)'\)").Groups[1].Value
    $decoded = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)))
    ok "the elevated copy decodes that value unchanged" ($decoded.WslDistro -eq $value)
}

ok "a switch is serialized as a boolean so it splats back" (
    (ConvertFrom-Json (ConvertTo-ConstructPayload -Values @{ NoStart = [switch]$true })).NoStart -eq $true)

$relaunchScript = New-ConstructRelaunchScript `
    -ScriptPath "C:\Construct\service\host\Install-ConstructHost.ps1" `
    -PayloadJson (ConvertTo-ConstructPayload -Values @{ ScriptsDir = "C:\Construct" })
ok "the elevation script splats the serialized parameters" ($relaunchScript -match '@bound')
ok "the elevation script keeps the window open on failure" ($relaunchScript -match 'Read-Host')

# THE elevation-boundary rule: the parameters must be baked into the encoded command,
# which is fixed once Start-Process has been called. A file would have to live in the
# UNELEVATED caller's own writable %TEMP%, where a process running as the same user can
# swap it while the UAC prompt is up -- choosing the ScriptsDir that then executes as
# LocalSystem, or adding -SkipAclHardening. A GUID name stops guessing, not watching.
ok "the elevation script carries its parameters inline as base64" (
    $relaunchScript -match "FromBase64String\('[A-Za-z0-9+/=]*'\)")
ok "the elevation script reads NO file for its parameters" (
    -not ($relaunchScript -match 'Get-Content') -and -not ($relaunchScript -match 'LiteralPath'))
ok "the installer never stages elevation parameters in the user's temp directory" (
    -not ($installAstText -match 'GetTempPath'))
# Nothing crosses a privilege boundary through a file any more: with the LocalSystem
# task runner gone, the installer writes no temp file at all.
ok "the installer stages nothing in any temp directory" (
    -not ($installAstText -match 'GetTempPath') -and -not ($installAstText -match 'SystemRoot\) "Temp'))

# A path of ours with an apostrophe still cannot end a literal.
$relaunchQuoted = New-ConstructRelaunchScript -ScriptPath "C:\Temp\it's\Install-ConstructHost.ps1" `
    -PayloadJson (ConvertTo-ConstructPayload -Values @{ ScriptsDir = "C:\Construct" })
ok "our own paths are escaped as PowerShell literals" ($relaunchQuoted -match "it''s")

# And the distro name is constrained before it ever gets that far.
ok "-WslDistro is constrained by a ValidatePattern" ($null -ne $install["WslDistro"].ValidatePattern)
foreach ($value in @("Ub untu", "it's", "a; rm", "..", "-x")) {
    ok "-WslDistro would reject '$value'" (-not ($value -match $install["WslDistro"].ValidatePattern))
}
foreach ($value in @("Ubuntu", "Ubuntu-24.04", "openSUSE_Leap_15.6", "")) {
    ok "-WslDistro accepts '$value'" ($value -match $install["WslDistro"].ValidatePattern)
}

# The self-elevation must not hand-quote parameters onto a command line any more.
ok "self-elevation passes an encoded script, not quoted parameters" (
    $installAstText -match '"-EncodedCommand", \$encoded' -and
    -not ($installAstText -match '\$argList \+= "-\$\(\$kv\.Key\)"'))

# ── The UNINSTALLER crosses the same boundary ────────────────────────────────
# It is the more attractive target of the two: a value that can inject an argument
# after UAC turns a harmless uninstall into -RemoveData against a path of somebody
# else's choosing. PowerShell flattens -ArgumentList back into ONE command-line
# string, so hand-wrapping values in quotes is not an escape.
ok "the uninstaller passes an encoded script, not quoted parameters" (
    $uninstallAstText -match '"-EncodedCommand", \$encoded')
ok "the uninstaller no longer hand-quotes bound parameters onto a command line" (
    -not ($uninstallAstText -match '\$argList \+= "-\$\(\$kv\.Key\)"') -and
    -not ($uninstallAstText -match '\$argList \+= "`"\$\(\$kv\.Value\)`""'))
ok "the uninstaller stages nothing in the user's temp directory" (
    -not ($uninstallAstText -match 'GetTempPath'))

foreach ($value in $nasty) {
    $payload = ConvertTo-UninstallPayload -Values @{ DataDir = $value; RemoveData = [switch]$true }
    $script  = New-UninstallRelaunchScript `
        -ScriptPath "C:\Construct\service\host\Uninstall-ConstructHost.ps1" -PayloadJson $payload

    ok "uninstaller: no generated script contains $([regex]::Escape($value).Substring(0, [Math]::Min(14, $value.Length)))..." (
        -not $script.Contains($value))

    $b64 = [regex]::Match($script, "FromBase64String\('([A-Za-z0-9+/=]*)'\)").Groups[1].Value
    $decoded = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)))
    ok "uninstaller: the elevated copy decodes that value unchanged" ($decoded.DataDir -eq $value)
    ok "uninstaller: the switch survives as a boolean" ($decoded.RemoveData -eq $true)
}

# The specific attack: a -RemoveData that the caller did NOT ask for must not appear.
$benign = ConvertTo-UninstallPayload -Values @{ DataDir = 'C:\x" -RemoveData "' }
ok "an injected -RemoveData stays data, not a switch" (
    $null -eq (ConvertFrom-Json $benign).RemoveData)

# ── (e) The settings the installer writes ────────────────────────────────────
Write-Host ""
Write-Host "=== Written settings ===" -ForegroundColor Cyan

$installText = Get-Content -Raw -LiteralPath $installer

# Every key here is bound by ConstructdOptions; a typo yields a service that
# starts with defaults rather than one that refuses to start.
foreach ($key in @("Persistence", "DatabasePath", "ListenUrl", "CertThumbprint", "ScriptsDir",
                   "PublicHost", "SwitchName", "ListenAddress", "WslDistro",
                   "SshForwardPorts", "AppForwardPorts", "Iso",
                   "SeedUser", "BootstrapPublicKeyPath", "CacheDir", "SourcePath", "SourceUrl", "Sha256")) {
    ok "settings carry $key" ($installText -match [regex]::Escape($key))
}

ok "settings are written next to the executable" ($installText -match 'appsettings\.Production\.json')
ok "the service is registered as LocalSystem" ($installText -match 'obj= LocalSystem' -or $installText -match 'New-Service')
ok "the certificate thumbprint is printed for pinning" ($installText -match 'Certificate\s+:')
ok "the admin is created through the admin CLI" ($installText -match '"admin", "users", "add"')
ok "the token is issued through the admin CLI" ($installText -match '"admin", "tokens", "issue"')

# ── (f) Idempotence of the admin/token step ──────────────────────────────────
Write-Host ""
Write-Host "=== Re-run behaviour ===" -ForegroundColor Cyan

# "Already exists" is not "already an admin": a pre-existing plain User would
# otherwise leave the host with no admin while the installer reported success.
ok "an existing account's role is verified, not assumed" ($installText -match '"admin", "users", "list", "--json"')
ok "a non-admin existing account is a hard error" ($installText -match "not Admin")
# A token per reinstall would leave a pile of permanent credentials behind.
ok "a token is only issued on creation or on -RotateAdminToken" ($installText -match '\$created -or \$RotateAdminToken')

# ── (g) The ISO build: as the ADMIN, not as the service ──────────────────────
Write-Host ""
Write-Host "=== The autoinstall ISO step ===" -ForegroundColor Cyan

# Field finding (2026-09-02, standpc, WSL 2.6.3): wsl.exe as LocalSystem exits with
# Wsl/WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED, and LocalSystem is the identity the service
# runs as. So the media is built HERE, by the administrator running the installer, and
# the service only consumes it (plan section 4.10). Every trace of the old
# "run WSL as LocalSystem" path must be gone -- a leftover would fail every install.
ok "no LocalSystem command runner is left" (-not ($installText -match 'Invoke-AsLocalSystem'))
ok "no scheduled task is registered any more" (-not ($installText -match 'Register-ScheduledTask'))
ok "no LocalSystem task principal is left" (-not ($installText -match 'New-ScheduledTaskPrincipal'))
ok "no LocalSystem script is generated any more" (-not ($installText -match 'New-ConstructLocalSystemScript'))
ok "the 'check WSL under LocalSystem' step is gone" (
    -not ($installText -match 'Checking WSL under the service identity'))
ok "nothing runs wsl.exe as LocalSystem any more" (
    -not ($installText -match 'wsl\.exe" -ArgumentList'))
ok "the WSL distro is no longer exported/imported" (
    -not ($installText -match 'wsl\.exe --export') -and -not ($installText -match '"--import"'))

# What replaces it.
ok "the interactive WSL check stays" ($installText -match 'Checking WSL \(the ISO build runs xorriso inside it, as YOU\)')
ok "a missing WSL is still a hard error" ($installText -match 'WSL is not installed')
ok "a missing distro is still a hard error" ($installText -match 'WSL is installed but has no distro')
ok "xorriso + whois are ensured in the ADMIN's distro" (
    $installText -match 'Ensuring xorriso \+ whois inside your WSL distro')
ok "...as root inside the distro, through an argument list" (
    $installText -match '"-u", "root", "--", "bash", "-lc"' -and $installText -match '& wsl\.exe @ensureArgs')
ok "a failed package install fails the install, showing what it printed" (
    $installText -match 'Could not install xorriso/whois inside WSL' -and
    $installText -match 'Format-ConstructCommandOutput -Output \$ensureOutput')

# The CLI reads appsettings.Production.json for the cache directory and the source ISO, so
# the environment has to be selected before the FIRST invocation, not before the admin steps.
$envAt = $installText.IndexOf('$env:DOTNET_ENVIRONMENT = "Production"')
ok "the production environment is selected before the executable is ever run" (
    $envAt -gt 0 -and
    $envAt -lt $installText.IndexOf('Invoke-ConstructIsoBuild -Exe $exe') -and
    $envAt -lt $installText.IndexOf('Invoke-ConstructdAdmin -Exe $exe'))
ok "...and only once" (
    ([regex]::Matches($installText, [regex]::Escape('$env:DOTNET_ENVIRONMENT = "Production"'))).Count -eq 1)
ok "the ISO is built through the service's own admin CLI" (
    $installText -match '@\("admin", "iso", "build"\)')
ok "the build is invoked with an argument list, never a command string" (
    $installText -match '& \$Exe @arguments')
ok "the step says whose WSL it uses" (
    $installText -match 'Building the autoinstall ISO \(as you, via WSL\)')
ok "a failed ISO build fails the install (fail closed)" (
    $installText -match 'Building the autoinstall ISO failed \(exit \$isoExit\)')
ok "...and shows what the build printed" (
    $installText -match 'Format-ConstructCommandOutput -Output \$text -MaxLines 20')
ok "the published path is read from the CLI's last line" ($installText -match '\$_ -like "ISO: \*"')
ok "the build streams while it runs and is still captured" ($installText -match 'Tee-Object -Variable isoOutput')

# -SkipIsoBuild defers it; -IsoBuildOnly is nothing but it.
ok "-SkipIsoBuild skips the build and says what will fail" (
    $installText -match '-SkipIsoBuild: no install media was built')
ok "-SkipIsoBuild prints the command to run later" (
    $installText -match '\$exe`" admin iso build')
ok "-IsoBuildOnly rebuilds and stops before anything else" (
    $installText -match 'Rebuilding the autoinstall ISO only \(-IsoBuildOnly\)')
ok "-IsoBuildOnly refuses to run without the settings the build reads" (
    $installText -match '-IsoBuildOnly needs an existing install')
ok "-IsoBuildOnly forces a rebuild (that is what it is for)" (
    $installText -match 'Invoke-ConstructIsoBuild -Exe \$exe -Force')
ok "-IsoBuildOnly and -SkipIsoBuild together are refused" (
    $installText -match '-IsoBuildOnly and -SkipIsoBuild contradict each other')
ok "the enrollment summary prints the rebuild command" (
    $installText -match 'admin iso build --force')
ok "the enrollment summary prints how to inspect the media" (
    $installText -match 'admin iso status')

# The settings must select the strategy the installer just built for.
ok "the service is configured for the pre-built strategy" ($installText -match 'Mode\s+= "Prebuilt"')
ok "the guest identity source is written into the settings" (
    $installText -match 'HostnameSource\s+= "hyperv-kvp"')

ok "output formatter: empty output" ((Format-ConstructCommandOutput -Output '') -eq ' It printed nothing.')
ok "output formatter: strips WSL's UTF-16 NULs and blank lines" ((Format-ConstructCommandOutput -Output "W`0S`0L`0 `0e`0r`0r`0o`0r`0`r`n`r`n") -eq " It said:`n    WSL error")
ok "output formatter: caps the lines shown" ((Format-ConstructCommandOutput -Output ((1..12 | ForEach-Object { "l$_" }) -join "`n") -MaxLines 3) -match '\(\+9 more line\(s\)\)$')

ok "the OpenSSH client is installed rather than warned about" (
    $installText -match 'Add-WindowsCapability -Online -Name \$capability\.Name')
ok "a missing OpenSSH client is a hard error" ($installText -match 'The OpenSSH client is required')

# Step order: settings -> ISO -> admin -> service. The ISO build needs the settings
# (it reads the service's own configuration for the cache directory and the source
# ISO), and the service is registered only after the admin exists, so the host is
# reachable the moment it comes up and nothing contends for the SQLite file.
$settingsAt = $installText.IndexOf("Writing appsettings.Production.json")
$isoBuildAt = $installText.IndexOf("Building the autoinstall ISO (as you, via WSL)")
$adminAt    = $installText.IndexOf('"admin", "users", "add"')
$serviceAt  = $installText.IndexOf("Registering the Windows service")
ok "the settings are written before the ISO is built" ($settingsAt -gt 0 -and $settingsAt -lt $isoBuildAt)
ok "the ISO is built before the service is registered" ($isoBuildAt -gt 0 -and $isoBuildAt -lt $serviceAt)
ok "the admin is created before the service is registered" ($adminAt -gt 0 -and $adminAt -lt $serviceAt)

# ── (h) The uninstaller removes the ISO catalog with -RemoveData ─────────────
Write-Host ""
Write-Host "=== Uninstall: the ISO catalog ===" -ForegroundColor Cyan

ok "-RemoveData removes the versioned ISOs" ($uninstallText -match 'construct-autoinstall-\*')
ok "-RemoveData removes the pointer" ($uninstallText -match 'current\.pointer')
ok "an ISO a VM still holds open is reported, not fatal" (
    $uninstallText -match 'a VM probably still has it attached')
ok "the data note mentions the media" ($uninstallText -match 'autoinstall ISOs')
ok "nothing in the uninstaller talks about a LocalSystem WSL distro" (
    -not ($uninstallText -match 'imports it for LocalSystem'))

# ── (i) The host's sleep settings ────────────────────────────────────────────
Write-Host ""
Write-Host "=== Host sleep settings ===" -ForegroundColor Cyan

# powercfg prints LOCALIZED labels. A parser that matches on words reports "never" on a
# German host that sleeps in 30 minutes -- which is exactly the failure this batch is
# about. So the same block, in two languages, must read identically.
$queryEnglish = @"
Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)
  Subgroup GUID: 238c9fa8-0aad-41ed-83f4-97be242c8f20  (Sleep)
    Power Setting GUID: 29f6c1db-86da-48c5-9fdb-f2b67b1f44da  (Sleep after)
      Minimum Possible Setting: 0x00000000
      Maximum Possible Setting: 0xffffffff
      Possible Settings increment: 0x00000001
      Possible Settings units: Seconds
    Current AC Power Setting Index: 0x00000708
    Current DC Power Setting Index: 0x00000384
"@

$queryGerman = @"
Energieschema-GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Ausbalanciert)
  Untergruppen-GUID: 238c9fa8-0aad-41ed-83f4-97be242c8f20  (Energie sparen)
    Energieeinstellungs-GUID: 29f6c1db-86da-48c5-9fdb-f2b67b1f44da  (Standbymodus nach)
      Moegliche Mindesteinstellung: 0x00000000
      Moegliche Hoechsteinstellung: 0xffffffff
      Moegliche Einstellungen: Schritte: 0x00000001
      Moegliche Einstellungen: Einheiten: Sekunden
    Aktueller Wechselstromwert-Index: 0x00000708
    Aktueller Gleichstromwert-Index: 0x00000384
"@

$parsedEnglish = ConvertFrom-ConstructPowerQuery -Output $queryEnglish
$parsedGerman  = ConvertFrom-ConstructPowerQuery -Output $queryGerman

ok "powercfg parse: the AC index is read (English)" ($parsedEnglish.Ac -eq 1800)
ok "powercfg parse: the DC index is read (English)" ($parsedEnglish.Dc -eq 900)
ok "powercfg parse: a German host reads exactly the same" (
    $parsedGerman.Ac -eq $parsedEnglish.Ac -and $parsedGerman.Dc -eq $parsedEnglish.Dc)
ok "powercfg parse: the minimum/maximum/increment lines are not mistaken for the value" (
    $parsedEnglish.Ac -ne 0 -and $parsedEnglish.Ac -ne 4294967295 -and $parsedEnglish.Ac -ne 1)
ok "powercfg parse: 'never' comes through as 0" (
    (ConvertFrom-ConstructPowerQuery -Output "  X: 0x00000001`n  A: 0x00000000`n  B: 0x00000000").Ac -eq 0)
ok "powercfg parse: output with no indices yields nothing" (
    $null -eq (ConvertFrom-ConstructPowerQuery -Output "Ungueltige Parameter"))
ok "powercfg parse: empty output yields nothing" ($null -eq (ConvertFrom-ConstructPowerQuery -Output ""))
ok "powercfg parse: a CRLF dump still parses" (
    (ConvertFrom-ConstructPowerQuery -Output "  A: 0x0000000a`r`n  B: 0x00000014`r`n").Dc -eq 20)

ok "active scheme: the GUID is read, not the localized name" (
    (ConvertFrom-ConstructActiveScheme -Output "Energieschema-GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Ausbalanciert)") -eq
    "381b4222-f694-41f0-9685-ff5bb260df2e")
ok "active scheme: no GUID yields an empty string, not an error" (
    (ConvertFrom-ConstructActiveScheme -Output "access denied") -eq "")

ok "timeout format: 0 is never" ((Format-ConstructPowerTimeout -Seconds 0) -eq "never")
ok "timeout format: whole minutes" ((Format-ConstructPowerTimeout -Seconds 1800) -eq "30 min")
ok "timeout format: seconds that are not whole minutes" ((Format-ConstructPowerTimeout -Seconds 90) -eq "90 s")
ok "timeout format: an unreadable value says so" ((Format-ConstructPowerTimeout -Seconds $null) -eq "unavailable")

# The three settings, by GUID -- the hidden unattended-sleep timeout among them, which
# has no powercfg alias at all.
$powerSettings = @(Get-ConstructPowerSetting)
ok "three sleep settings are reported on" ($powerSettings.Count -eq 3)
ok "all three sit in SUB_SLEEP" (
    @($powerSettings | Where-Object { $_.SubGroup -eq '238c9fa8-0aad-41ed-83f4-97be242c8f20' }).Count -eq 3)
ok "STANDBYIDLE is one of them" (
    @($powerSettings | Where-Object { $_.Setting -eq '29f6c1db-86da-48c5-9fdb-f2b67b1f44da' }).Count -eq 1)
ok "HIBERNATEIDLE is one of them" (
    @($powerSettings | Where-Object { $_.Setting -eq '9d7815a6-7ee4-497e-8888-515a05f02364' }).Count -eq 1)
ok "the hidden unattended-sleep timeout is one of them" (
    @($powerSettings | Where-Object { $_.Setting -eq '7bc4a2f9-d8fc-4469-b07b-33eb785aaca0' }).Count -eq 1)
ok "every setting travels as a GUID, never as a localizable alias" (
    @($powerSettings | Where-Object { $_.Setting -notmatch '^[0-9a-f]{8}-' }).Count -eq 0)

# ── The report and the setter, against a MOCKED powercfg ─────────────────────
# No powercfg on this machine, and none needed: the seam is the -Query scriptblock.

# Script-scope state rather than a closure: a scriptblock from GetNewClosure() gets its
# own module scope, and $script: inside it would then not be this script's.
$script:powercfgCalls   = @()
$script:powercfgValues  = @{}
$script:powercfgExitCode = 0

$mockPowercfg = {
    param([string[]]$Arguments)

    $script:powercfgCalls += ,@($Arguments)

    if ($script:powercfgExitCode -ne 0) { return @{ ExitCode = $script:powercfgExitCode; Output = "denied" } }
    if ($Arguments[0] -ne "/q") { return @{ ExitCode = 0; Output = "" } }

    # The shape powercfg prints: some hex lines that are NOT the value, then AC and DC.
    $setting = $Arguments[3]
    $seconds = 0
    if ($script:powercfgValues.ContainsKey($setting)) { $seconds = $script:powercfgValues[$setting] }
    $hex = "0x{0:x8}" -f $seconds
    return @{ ExitCode = 0; Output = "  GUID: $setting  (whatever)`n  Min: 0x00000000`n  AC: $hex`n  DC: $hex" }
}

function Reset-MockPowercfg {
    param([hashtable]$Values = @{}, [int]$ExitCode = 0)
    $script:powercfgCalls    = @()
    $script:powercfgValues   = $Values
    $script:powercfgExitCode = $ExitCode
}

Reset-MockPowercfg -Values @{ '29f6c1db-86da-48c5-9fdb-f2b67b1f44da' = 1800 }
$report = @(Get-ConstructPowerReport -SchemeGuid "SCHEME_CURRENT" -Query $mockPowercfg)

ok "report: one row per setting" ($report.Count -eq 3)
ok "report: every row was queried by GUID against the active scheme" (
    @($script:powercfgCalls | Where-Object { $_[0] -eq "/q" -and $_[1] -eq "SCHEME_CURRENT" }).Count -eq 3)
ok "report: nothing but /q is run while reading" (
    @($script:powercfgCalls | Where-Object { $_[0] -ne "/q" }).Count -eq 0)
ok "report: the standby row carries the queried value" (
    @($report | Where-Object { $_.Key -eq 'StandbyIdle' })[0].Ac -eq 1800)
ok "report: a setting this host has at never reads as 0" (
    @($report | Where-Object { $_.Key -eq 'HibernateIdle' })[0].Ac -eq 0)

Reset-MockPowercfg -ExitCode 1
$failing = @(Get-ConstructPowerReport -SchemeGuid "SCHEME_CURRENT" -Query $mockPowercfg)
ok "report: a refused query reports 'unavailable' rather than a wrong number" (
    (Format-ConstructPowerTimeout -Seconds $failing[0].Ac) -eq "unavailable")

# The setter: it writes only what is not already "never".
Reset-MockPowercfg
$standbyRow = @($report | Where-Object { $_.Key -eq 'StandbyIdle' })[0]
$changed = Set-ConstructPowerNever -SchemeGuid "SCHEME_CURRENT" -Row $standbyRow -Query $mockPowercfg
ok "set: a non-zero timeout is changed" ($changed -eq $true)
ok "set: through /setacvalueindex, by GUID, to 0" (
    @($script:powercfgCalls | Where-Object {
        $_[0] -eq "/setacvalueindex" -and $_[1] -eq "SCHEME_CURRENT" -and
        $_[2] -eq '238c9fa8-0aad-41ed-83f4-97be242c8f20' -and
        $_[3] -eq '29f6c1db-86da-48c5-9fdb-f2b67b1f44da' -and $_[4] -eq "0" }).Count -eq 1)
ok "set: the DC (battery) timeouts are left alone" (
    @($script:powercfgCalls | Where-Object { $_[0] -eq "/setdcvalueindex" }).Count -eq 0)

Reset-MockPowercfg
$alreadyNever = @($report | Where-Object { $_.Key -eq 'HibernateIdle' })[0]
$changed = Set-ConstructPowerNever -SchemeGuid "SCHEME_CURRENT" -Row $alreadyNever -Query $mockPowercfg
ok "set: idempotent -- a timeout that is already never is not written again" (
    $changed -eq $false -and $script:powercfgCalls.Count -eq 0)

Reset-MockPowercfg -ExitCode 5
$refused = Set-ConstructPowerNever -SchemeGuid "SCHEME_CURRENT" -Row $standbyRow `
    -Query $mockPowercfg -WarningAction SilentlyContinue
ok "set: a refused write warns and reports no change, it does not fail the install" ($refused -eq $false)

Reset-MockPowercfg
$whatIf = Set-ConstructPowerNever -SchemeGuid "SCHEME_CURRENT" -Row $standbyRow -Query $mockPowercfg -WhatIf
ok "set: -WhatIf writes nothing" ($whatIf -eq $false -and $script:powercfgCalls.Count -eq 0)

# ── The step in the installer itself ─────────────────────────────────────────

ok "the installer has a sleep-settings step" ($installText -match "The host's sleep settings")
ok "...that reports the timeouts before changing anything" (
    $installText.IndexOf('Get-ConstructPowerReport -SchemeGuid $activeScheme') -gt 0 -and
    $installText.IndexOf('Get-ConstructPowerReport -SchemeGuid $activeScheme') -lt
    $installText.IndexOf('Set-ConstructPowerNever -SchemeGuid $activeScheme'))
ok "...against the ACTIVE scheme, through powercfg's own alias" ($installText -match '\$activeScheme = "SCHEME_CURRENT"')
# The seam the tests above mock is the one the installer really fills, with its own runner.
ok "the report and the setter both get the real powercfg runner" (
    ([regex]::Matches($installText, '-Query \$\{function:Invoke-ConstructPowercfg\}')).Count -eq 2)
ok "-SkipPowerSettings skips the whole step" ($installText -match 'if \(\$SkipPowerSettings\)')
ok "an explicit -KeepHostAwake decides instead of the prompt" (
    $installText -match '\$PSBoundParameters\.ContainsKey\("KeepHostAwake"\)')
ok "an unattended run leaves the power plan alone" (
    $installText -match 'elseif \(-not \(Test-ConstructInteractive\)\)' -and
    $installText -match 'Unattended run: leaving the power plan alone')
ok "the changed scheme is re-applied so it takes effect" ($installText -match '"/setactive", \$activeScheme')
ok "the summary says how to see the request the service holds" ($installText -match 'powercfg /requests')
ok "the hidden unattended-sleep GUID is in the installer" (
    $installText -match '7bc4a2f9-d8fc-4469-b07b-33eb785aaca0')
# The whole point of the hex-index rule: nothing may key off a word powercfg prints.
ok "nothing parses powercfg's localized labels" (
    -not ($installText -match 'Current AC Power Setting') -and
    -not ($installText -match 'Sleep after:'))

# ── (j) "May we prompt?" -- decided BEFORE the self-elevation ────────────────
Write-Host ""
Write-Host "=== Unattended detection ===" -ForegroundColor Cyan

# Read-Host does not merely hang under -NonInteractive: it throws. So an automation run
# that self-elevates must not reach the prompt at all -- and the elevated copy gets a
# brand new console, so it cannot work this out for itself.
foreach ($spelling in @("-NonInteractive", "-noninteractive", "-NONINTERACTIVE", "-noni", "/NonInteractive", "--NonInteractive")) {
    ok "non-interactive detected: '$spelling'" (
        Test-ConstructNonInteractiveArgument -Arguments @("powershell.exe", $spelling, "-File", "x.ps1"))
}
foreach ($spelling in @("-NoProfile", "-NoExit", "-NoLogo", "-File", "-Command", "C:\Construct\Install-ConstructHost.ps1")) {
    ok "not mistaken for non-interactive: '$spelling'" (
        -not (Test-ConstructNonInteractiveArgument -Arguments @("powershell.exe", $spelling)))
}
ok "non-interactive detection: an empty command line is not non-interactive" (
    -not (Test-ConstructNonInteractiveArgument -Arguments @()))
ok "non-interactive detection: a null command line is not non-interactive" (
    -not (Test-ConstructNonInteractiveArgument -Arguments $null))

ok "prompt allowed when nothing says otherwise" (
    Test-ConstructPromptAllowed -InputRedirected $false -NonInteractiveHost $false -UserInteractive $true -WhatIf $false)
ok "prompt refused when input is redirected" (
    -not (Test-ConstructPromptAllowed -InputRedirected $true -NonInteractiveHost $false -UserInteractive $true -WhatIf $false))
ok "prompt refused under -NonInteractive" (
    -not (Test-ConstructPromptAllowed -InputRedirected $false -NonInteractiveHost $true -UserInteractive $true -WhatIf $false))
ok "prompt refused without an interactive session (scheduled task, remoting, CI)" (
    -not (Test-ConstructPromptAllowed -InputRedirected $false -NonInteractiveHost $false -UserInteractive $false -WhatIf $false))
ok "prompt refused under -WhatIf" (
    -not (Test-ConstructPromptAllowed -InputRedirected $false -NonInteractiveHost $false -UserInteractive $true -WhatIf $true))

# The elevation boundary: the decision is resolved on the CALLER's session and carried,
# not inferred on the other side.
$resolveAt  = $installText.IndexOf("if (-not `$forward.ContainsKey('KeepHostAwake') -and -not (Test-ConstructInteractive))")
$relaunchAt = $installText.IndexOf('$relaunch = New-ConstructRelaunchScript')
$elevateAt  = $installText.IndexOf('Start-Process powershell.exe -Verb RunAs')
ok "the prompt decision is resolved before the elevated copy is built" (
    $resolveAt -gt 0 -and $relaunchAt -gt 0 -and $resolveAt -lt $relaunchAt)
ok "...and before anything is started elevated" ($resolveAt -gt 0 -and $elevateAt -gt 0 -and $resolveAt -lt $elevateAt)
ok "the helpers exist before the elevation block that calls them" (
    $installText.IndexOf('function Test-ConstructInteractive') -gt 0 -and
    $installText.IndexOf('function Test-ConstructInteractive') -lt $elevateAt)
ok "the elevated copy is handed the resolved parameters, not the raw bound ones" (
    $installText -match 'ConvertTo-ConstructPayload -Values \$forward' -and
    -not ($installText -match 'ConvertTo-ConstructPayload -Values \$PSBoundParameters'))
ok 'resolving does not mutate the live $PSBoundParameters' (
    $installText -match 'foreach \(\$bound in \$PSBoundParameters\.GetEnumerator\(\)\) \{ \$forward\[\$bound\.Key\] = \$bound\.Value \}')

# And the resolved answer survives the trip as a real $false, not as a missing key.
$unattended = ConvertFrom-Json (ConvertTo-ConstructPayload -Values @{ ScriptsDir = "C:\Construct"; KeepHostAwake = $false })
ok "an unattended run reaches the elevated copy as -KeepHostAwake:`$false" (
    $unattended.PSObject.Properties.Name -contains 'KeepHostAwake' -and $unattended.KeepHostAwake -eq $false)

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== $script:pass passed, $script:fail failed ===" -ForegroundColor $(if ($script:fail -eq 0) { "Green" } else { "Red" })
if ($script:fail -gt 0) { exit 1 }
exit 0
