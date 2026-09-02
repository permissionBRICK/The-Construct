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
    "ProvisionWslForService", "RotateAdminToken", "NoStart")

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
ok "installer -ProvisionWslForService is opt-in" (
    $install["ProvisionWslForService"].Type -eq "SwitchParameter" -and $null -eq $install["ProvisionWslForService"].Default)
ok "installer -RotateAdminToken is opt-in" (
    $install["RotateAdminToken"].Type -eq "SwitchParameter" -and $null -eq $install["RotateAdminToken"].Default)

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
                       "Get-ConstructWriteRiskMask", "Get-ConstructUnsafeAce", "Resolve-ConstructAceSid", "Sort-ConstructHardeningOrder",
                       "ConvertTo-ConstructPayload", "New-ConstructRelaunchScript",
                       "New-ConstructLocalSystemScript")) {
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
ok "the WSL distro is imported under the protected root" ($installAstText -match '\$wslRoot = Join-Path \$serviceRoot "wsl"')
ok "the imported WSL distro is hardened too" ($installAstText -match 'Set-ConstructPathAcl -Path \$importTo -Kind Data')
ok "the installer refuses reparse points" ($installAstText -match 'ReparsePoint')
ok "the installer refuses paths under a user profile root" ($installAstText -match 'user profile root')
ok "the installer rejects overlapping port ranges" ($installAstText -match 'overlap')

# Hardening has to happen before anything is put in those directories, and long
# before the service can ever run from them.
$aclAt      = $installAstText.IndexOf('foreach ($entry in (Sort-ConstructHardeningOrder -Entries $hardening))')
$importAt   = $installAstText.IndexOf("Provisioning WSL distro")
$registerAt = $installAstText.IndexOf("Registering the Windows service")
ok "paths are hardened before the WSL import" ($aclAt -gt 0 -and $aclAt -lt $importAt)
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
    $localSystemScript = New-ConstructLocalSystemScript `
        -PayloadFile "C:\Windows\Temp\constructd-1.json" -OutputFile "C:\Windows\Temp\constructd-1.txt"
    $relaunchScript = New-ConstructRelaunchScript `
        -ScriptPath "C:\Construct\service\host\Install-ConstructHost.ps1" -PayloadJson $payload

    ok "no generated script contains that value" (
        -not $localSystemScript.Contains($value) -and -not $relaunchScript.Contains($value))

    # And it really is still there, recoverable byte for byte from the base64 the
    # elevated copy decodes -- inertness that lost the value would be no good.
    $b64 = [regex]::Match($relaunchScript, "FromBase64String\('([A-Za-z0-9+/=]*)'\)").Groups[1].Value
    $decoded = ConvertFrom-Json ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)))
    ok "the elevated copy decodes that value unchanged" ($decoded.WslDistro -eq $value)
}

ok "a switch is serialized as a boolean so it splats back" (
    (ConvertFrom-Json (ConvertTo-ConstructPayload -Values @{ NoStart = [switch]$true })).NoStart -eq $true)

# The generated scripts must read their inputs, not embed them.
$localSystemScript = New-ConstructLocalSystemScript `
    -PayloadFile "C:\Windows\Temp\constructd-1.json" -OutputFile "C:\Windows\Temp\constructd-1.txt"
ok "the LocalSystem script reads a payload file" ($localSystemScript -match 'ConvertFrom-Json')
ok "the LocalSystem script splats the argument list" ($localSystemScript -match '@\(\$spec\.ArgumentList\)')
ok "the LocalSystem script propagates the exit code" ($localSystemScript -match 'exit \$LASTEXITCODE')

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
ok "the only temp paths the installer uses are under SystemRoot" (
    $installAstText -match 'Join-Path \$env:SystemRoot "Temp\\')

# A path of ours with an apostrophe still cannot end the literal.
$quoted = New-ConstructLocalSystemScript -PayloadFile "C:\Temp\it's.json" -OutputFile "C:\Temp\out.txt"
ok "our own paths are escaped as PowerShell literals" ($quoted -match "it''s")

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

# ── (g) WSL under the service identity ───────────────────────────────────────
Write-Host ""
Write-Host "=== WSL under LocalSystem ===" -ForegroundColor Cyan

# WSL distros are registered per Windows user; checking them as the elevated
# administrator says nothing about what LocalSystem will see at the first VM build.
ok "the installer can run a command as LocalSystem" ($installText -match 'function Invoke-AsLocalSystem')
ok "it uses the LocalSystem SID for the task principal" ($installText -match 'New-ScheduledTaskPrincipal -UserId "S-1-5-18"')
ok "it lists the distros LocalSystem can see" (
    $installText -match 'Invoke-AsLocalSystem -FilePath "wsl\.exe" -ArgumentList @\("-l", "-q"\)')
ok "a LocalSystem check that cannot run fails the install" ($installText -match 'Could not list the WSL distros as LocalSystem')
ok "-WhatIf does not read an empty answer as 'no distro'" ($installText -match '\$listing\.Simulated')
ok "a distro missing for LocalSystem is a hard error" ($installText -match 'NOT for LocalSystem')
ok "xorriso/whois are ensured inside the service's distro" (
    $installText -match 'Ensuring xorriso \+ whois inside WSL \(as LocalSystem\)')
ok "the OpenSSH client is installed rather than warned about" (
    $installText -match 'Add-WindowsCapability -Online -Name \$capability\.Name')
ok "a missing OpenSSH client is a hard error" ($installText -match 'The OpenSSH client is required')

# The service is registered only after the admin exists, so the host is reachable
# the moment it comes up and nothing contends for the SQLite file.
$adminAt   = $installText.IndexOf('"admin", "users", "add"')
$serviceAt = $installText.IndexOf("Registering the Windows service")
ok "the admin is created before the service is registered" ($adminAt -gt 0 -and $adminAt -lt $serviceAt)

# ── Summary ──────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== $script:pass passed, $script:fail failed ===" -ForegroundColor $(if ($script:fail -eq 0) { "Green" } else { "Red" })
if ($script:fail -gt 0) { exit 1 }
exit 0
