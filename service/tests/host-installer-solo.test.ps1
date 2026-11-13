#Requires -Version 5.1
<#
    Tests for the host installer's solo mode (Install-ConstructHost.ps1 -Solo). Run:

        pwsh -NoProfile -File service/tests/host-installer-solo.test.ps1

    The installer's functions are loaded from its AST and run against stubbed
    commands (firewall, ACL, ssh-keygen, icacls), so nothing here touches the machine.
    What is checked: the port is opened to the PC's own VMs only, the token file is
    created with an ACL that names the installing user alone, the settings turn
    Kerberos off and mark the install solo, and the bootstrap key lands in the data
    directory owned by LocalSystem. The .NET suite runs this file too (HostInstallerTests).
#>
$ErrorActionPreference = 'Stop'

$here       = Split-Path -Parent $MyInvocation.MyCommand.Path
$serviceDir = Split-Path -Parent $here
$installer  = Join-Path $serviceDir 'host/Install-ConstructHost.ps1'
$uninstaller = Join-Path $serviceDir 'host/Uninstall-ConstructHost.ps1'
$installText = Get-Content -Raw -LiteralPath $installer

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

$ast = [System.Management.Automation.Language.Parser]::ParseFile($installer, [ref]$null, [ref]$null)
foreach ($name in @('Get-ConstructSoloHostName', 'Set-ConstructGuestFirewallRule', 'Set-ConstructHostFirewall', 'Add-ConstructSoloSettings',
                    'Install-ConstructBootstrapKey', 'Get-ConstructPrivateFileSddl', 'Write-ConstructPrivateFile',
                    'New-ConstructSoloTokenDocument', 'Get-ConstructCertificateSha256')) {
    $fn = $ast.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    if (-not $fn) { throw "installer function $name is missing" }
    . ([ScriptBlock]::Create($fn.Extent.Text))
}
function Write-Ok($msg) { }
function Write-Note($msg) { }

# ── Parameters and the elevation boundary ────────────────────────────────────
Write-Host ""
Write-Host "=== Parameters ===" -ForegroundColor Cyan
$params = @{}
foreach ($p in $ast.ParamBlock.Parameters) { $params[$p.Name.VariablePath.UserPath] = $p.StaticType.Name }
ok "-Solo is a switch" ($params['Solo'] -eq 'SwitchParameter')
ok "-TokenFile and -BootstrapKeyPath are strings" ($params['TokenFile'] -eq 'String' -and $params['BootstrapKeyPath'] -eq 'String')
ok "both paths are made absolute before the elevated relaunch" ($installText -match "@\('ScriptsDir', 'PublishDir', 'DataDir', 'IsoSourcePath', 'TokenFile', 'BootstrapKeyPath'\)")
$guard = $installText.IndexOf('if ($Solo -and -not $TokenFile)')
ok "a solo install without -TokenFile stops before the elevation prompt" ($guard -gt 0 -and $guard -lt $installText.IndexOf('# ── Self-elevate to Administrator'))
ok "solo names the user who started the install as the admin, before elevation" (
    $installText -match "if \(\`$Solo -and -not \`$forward\.ContainsKey\('AdminUser'\)\) \{\s*\r?\n\s*\`$forward\['AdminUser'\] = \[Security\.Principal\.WindowsIdentity\]::GetCurrent\(\)\.Name")
ok "the service package, not a client checkout, holds the driver: no drivers\ check" (-not ($installText -match 'drivers\\\\Load-ConstructDriver\.ps1'))

# ── The firewall: the PC's own VMs, and nothing else ─────────────────────────
Write-Host ""
Write-Host "=== Firewall ===" -ForegroundColor Cyan
$script:rules = @(); $script:shared = @(); $script:removed = 0
function Get-NetFirewallRule { param($DisplayName, $ErrorAction) if ($script:existing) { [pscustomobject]@{ DisplayName = $DisplayName } } }
function Remove-NetFirewallRule { param([Parameter(ValueFromPipeline = $true)]$InputObject) process { $script:removed++ } }
function New-NetFirewallRule { param($DisplayName, $Direction, $Action, $Protocol, $LocalPort, $InterfaceAlias, $Profile) $script:rules += @{ Name = $DisplayName; Port = $LocalPort; Interface = $InterfaceAlias; Profile = $Profile; Direction = $Direction; Action = $Action } }
function Set-ConstructFirewallRule { param($DisplayName, $LocalPort) $script:shared += $DisplayName }
$ssh = @{ Start = 2201; End = 2299 }; $app = @{ Start = 2300; End = 2999 }

Set-ConstructHostFirewall -Solo $true -ListenPort 7462 -SshRange $ssh -AppRange $app -SwitchName 'Default Switch'
ok "solo creates exactly one rule" ($script:rules.Count -eq 1 -and $script:shared.Count -eq 0)
ok "...inbound allow for the API port" ($script:rules[0].Port -eq '7462' -and $script:rules[0].Direction -eq 'Inbound' -and $script:rules[0].Action -eq 'Allow')
ok "...on the Default Switch's host interface only" ($script:rules[0].Interface -eq 'vEthernet (Default Switch)')
ok "...named so the uninstaller removes it" ($script:rules[0].Name -eq 'Construct constructd guests')
ok "no forward range is opened in solo" (-not ($script:rules | Where-Object { $_.Port -match '-' }))
$script:existing = $true; $script:rules = @()
Set-ConstructHostFirewall -Solo $true -ListenPort 7462 -SshRange $ssh -AppRange $app -SwitchName 'Default Switch'
ok "a re-run replaces the rule instead of adding a second" ($script:removed -eq 1 -and $script:rules.Count -eq 1)
$script:existing = $false; $script:rules = @()
Set-ConstructHostFirewall -Solo $false -ListenPort 7462 -SshRange $ssh -AppRange $app -SwitchName 'Default Switch'
ok "a shared host keeps its three rules and gets no guest rule" ($script:shared.Count -eq 3 -and $script:rules.Count -eq 0)
ok "the installer's firewall step goes through that function" ($installText -match 'Set-ConstructHostFirewall -Solo \(\[bool\]\$Solo\)')
$uninstallText = Get-Content -Raw -LiteralPath $uninstaller
ok "the uninstaller removes the guest rule and the LAN rule" ($uninstallText -match '"Construct constructd guests"' -and $uninstallText -match '"ConstructEngineLan"')

# ── Host name and settings ───────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Host name and settings ===" -ForegroundColor Cyan
ok "the solo host name is the PC's name on the Default Switch" ((Get-ConstructSoloHostName -ComputerName 'DESKTOP-AB12') -eq 'desktop-ab12.mshome.net')
ok "the certificate and the VMs' URL use it (it replaces -PublicHost in solo)" ($installText -match '\$PublicHost = Get-ConstructSoloHostName -ComputerName \$env:COMPUTERNAME')
$settings = [ordered]@{ Constructd = [ordered]@{ ListenUrl = 'https://0.0.0.0:7462'; PublicHost = 'desktop-ab12.mshome.net' } }
Add-ConstructSoloSettings -Settings $settings
$json = $settings | ConvertTo-Json -Depth 6 | ConvertFrom-Json
ok "solo marks the install solo (the LAN switch exists only then)" ($json.Constructd.Solo -eq $true)
ok "solo turns Kerberos off" ($json.Constructd.Negotiate.Enabled -eq $false)
ok "solo does not hold the PC awake while its VM runs" ($json.Constructd.Power.KeepHostAwake -eq $false)
$options = Get-Content -Raw -LiteralPath (Join-Path $serviceDir 'src/Constructd.Core/Configuration/ConstructdOptions.cs')
ok "...and those are names the service binds" ($options -match 'public bool Solo \{' -and $options -match 'public NegotiateOptions Negotiate' -and $options -match 'public bool KeepHostAwake')
ok "the settings get the solo values before they are written" (
    $installText.IndexOf('if ($Solo) { Add-ConstructSoloSettings -Settings $settings }') -gt 0 -and
    $installText.IndexOf('if ($Solo) { Add-ConstructSoloSettings -Settings $settings }') -lt $installText.IndexOf('if ($PSCmdlet.ShouldProcess($settingsPath, "Write the service configuration"))'))
ok "the power plan step is skipped in solo" ($installText -match 'if \(\$SkipPowerSettings -or \$Solo\)')

# ── The token file ───────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Token file ===" -ForegroundColor Cyan
$sid = 'S-1-5-21-1111111111-2222222222-3333333333-1001'
function Resolve-ConstructUserSid { param($User) if ($User -ne 'DESKTOP-AB12\ada') { throw "unexpected user $User" }; $sid }
$script:created = @()
function New-ConstructFileWithSddl { param($Path, $Sddl, [byte[]]$Bytes) $script:created += @{ Path = $Path; Sddl = $Sddl; Text = [Text.Encoding]::UTF8.GetString($Bytes); Existed = (Test-Path -LiteralPath $Path) } }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('solo-token-' + [guid]::NewGuid().ToString('n'))
try {
    $file = Join-Path $temp 'agent/engine-token.json'
    $doc = New-ConstructSoloTokenDocument -Url 'https://127.0.0.1:7462' -User 'DESKTOP-AB12\ada' -Token 'cd_secret' -CertificateSha256 'AA:BB'
    Write-ConstructPrivateFile -Path $file -User 'DESKTOP-AB12\ada' -Content $doc
    ok "the file is created with its ACL in one call" ($script:created.Count -eq 1 -and $script:created[0].Path -eq $file)
    ok "the ACL is protected and names the installing user alone, with full access" ($script:created[0].Sddl -ceq "D:P(A;;FA;;;$sid)")
    ok "no other principal appears in it" (([regex]::Matches($script:created[0].Sddl, '\(A;')).Count -eq 1 -and $script:created[0].Sddl -notmatch 'BA|SY|WD|AU|BU')
    $read = $script:created[0].Text | ConvertFrom-Json
    ok "the document carries the loopback URL, the user, the token and the pin" ($read.url -eq 'https://127.0.0.1:7462' -and $read.user -eq 'DESKTOP-AB12\ada' -and $read.token -eq 'cd_secret' -and $read.certificateSha256 -eq 'AA:BB')
    ok "the directory for it is created" (Test-Path -LiteralPath (Split-Path -Parent $file))
    Set-Content -LiteralPath $file -Value 'old'
    Write-ConstructPrivateFile -Path $file -User 'DESKTOP-AB12\ada' -Content $doc
    ok "an earlier file is removed first, so the new one gets the ACL" ($script:created[1].Existed -eq $false)
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
ok "the pin is the certificate's SHA-256 in colon pairs" ((Get-ConstructCertificateSha256 -RawData ([byte[]](1, 2, 3))) -eq '03:90:58:C6:F2:C0:CB:49:2C:53:3B:0A:4D:14:EF:77:CC:0F:78:AB:CC:CE:D5:28:7D:84:A1:A2:01:1C:FB:81')
ok "solo issues a token when the file is missing too" ($installText -match '\$created -or \$RotateAdminToken -or \(\$Solo -and -not \(Test-Path -LiteralPath \$TokenFile\)\)')
ok "solo never prints the token" ($installText -match '(?s)if \(\$Solo\) \{\s*\$pin = "".*?\$token = ""')

# ── The bootstrap key ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Bootstrap key ===" -ForegroundColor Cyan
$script:keygen = 0; $script:icacls = @()
function Invoke-ConstructSshKeygen { param($Path) $script:keygen++; Set-Content -LiteralPath $Path -Value 'new-private'; Set-Content -LiteralPath "$Path.pub" -Value 'new-public' }
function icacls.exe { $script:icacls += ,@($args); $global:LASTEXITCODE = 0 }
$temp = Join-Path ([IO.Path]::GetTempPath()) ('solo-key-' + [guid]::NewGuid().ToString('n'))
try {
    $keys = Join-Path $temp 'data/keys'
    $pub = Install-ConstructBootstrapKey -KeysDir $keys
    ok "without an old key a pair is generated in the data directory" ($script:keygen -eq 1 -and $pub -eq (Join-Path $keys 'bootstrap_ed25519.pub'))
    ok "the private key is given to LocalSystem" ($script:icacls.Count -eq 1 -and $script:icacls[0][0] -eq (Join-Path $keys 'bootstrap_ed25519') -and ($script:icacls[0] -join ' ') -match '/setowner \*S-1-5-18$')
    $null = Install-ConstructBootstrapKey -KeysDir $keys
    ok "a re-run keeps the generated pair" ($script:keygen -eq 1)
    $old = Join-Path $temp 'client/keys/bootstrap_ed25519'
    New-Item -ItemType Directory -Path (Split-Path -Parent $old) -Force | Out-Null
    Set-Content -LiteralPath $old -Value 'old-private'; Set-Content -LiteralPath "$old.pub" -Value 'old-public'
    $null = Install-ConstructBootstrapKey -KeysDir $keys -SourceKey $old
    ok "the old client's pair is taken over whole" ((Get-Content -Raw (Join-Path $keys 'bootstrap_ed25519')).Trim() -eq 'old-private' -and (Get-Content -Raw (Join-Path $keys 'bootstrap_ed25519.pub')).Trim() -eq 'old-public' -and $script:keygen -eq 1)
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force }
}
ok "the service is pointed at the solo key" ($installText -match '\$bootstrapKey = Install-ConstructBootstrapKey -KeysDir \(Join-Path \$DataDir ''keys''\) -SourceKey \$BootstrapKeyPath' -and $installText -match 'BootstrapPublicKeyPath = \$bootstrapKey')
$keyStep = $installText.IndexOf('$bootstrapKey = Install-ConstructBootstrapKey')
ok "the key is installed after the data directory is hardened" ($keyStep -gt $installText.IndexOf('Set-ConstructPathAcl -Path $entry.Path -Kind $entry.Kind -Name $entry.Name', $installText.IndexOf('# ── 1b.')))
ok "ssh-keygen gets an empty passphrase that Windows PowerShell 5.1 does not drop" ($installText -match [regex]::Escape('$start.Arguments = ''-q -t ed25519 -N "" -C bootstrap@construct -f "'''))

Write-Host ""
Write-Host "host-installer-solo: $($script:pass) passed, $($script:fail) failed"
if ($script:fail -gt 0) { exit 1 }
