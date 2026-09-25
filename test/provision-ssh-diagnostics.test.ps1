#Requires -Version 5.1
# The provisioner's SSH diagnostics, driven with ssh.exe shadowed by a function that
# replays real ssh output. A port forward that accepts TCP but reaches no sshd must
# fail at the reachability check or as a connection error -- never read as "reachable"
# or as a key that "did not authenticate" (which led to a pointless password prompt).
# The capture path itself is exercised with a REAL native child process, because
# Windows PowerShell 5.1 discards native stderr under EAP SilentlyContinue and a
# shadow function cannot reproduce that.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'Provision-AgentVM.ps1'), [ref]$null, [ref]$null)

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

$re = $ast.Find({ param($n)
    $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $n.Left.Extent.Text -eq '$script:SshTransportFailureRe' }, $true)
Invoke-Expression $re.Extent.Text
foreach ($fname in @('Get-SshFailureLine', 'Test-SshServerAnswered', 'Invoke-SshCapture', 'Ensure-VmReachable', 'Enter-RootKeyFastPath')) {
    $fn = $ast.Find({ param($n)
        $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fname }, $true)
    ok "extract: Provision-AgentVM.ps1 defines $fname" ($null -ne $fn)
    if ($fn) { Invoke-Expression $fn.Extent.Text }
}

# Output as ssh prints it (Windows OpenSSH wording where it differs).
$deadForward = "kex_exchange_identification: write: Connection refused`r`nbanner exchange: Connection to UNKNOWN port -1: Connection refused"
$reset       = "kex_exchange_identification: read: Connection reset by peer`r`nConnection reset by 10.0.0.5 port 2201"
$denied      = "agent@buildbox.example.local: Permission denied (publickey,password)."
$refused     = "ssh: connect to host buildbox.example.local port 2201: Connection refused"
$unknown     = "something ssh has never printed before"

Write-Host ""
Write-Host "=== Test-SshServerAnswered ===" -ForegroundColor Cyan
ok "answered: sshd's permission-denied proves it is up"      (Test-SshServerAnswered -Output $denied -ExitCode 255)
ok "answered: exit 0 proves it too"                          (Test-SshServerAnswered -Output "" -ExitCode 0)
ok "answered: a dead forward (kex/banner exchange) does not" (-not (Test-SshServerAnswered -Output $deadForward -ExitCode 255))
ok "answered: a reset before the banner does not"            (-not (Test-SshServerAnswered -Output $reset -ExitCode 255))
ok "answered: a refused connect does not"                    (-not (Test-SshServerAnswered -Output $refused -ExitCode 255))
ok "answered: output no pattern knows is not proof"          (-not (Test-SshServerAnswered -Output $unknown -ExitCode 255))
ok "failure line: names the transport error" ((Get-SshFailureLine -Output $deadForward) -match 'banner exchange')
ok "failure line: falls back to the last line" ((Get-SshFailureLine -Output "a`nb") -eq 'b')
ok "failure line: empty output is named, not a bare colon" ((Get-SshFailureLine -Output "") -eq 'ssh printed no error output')

Write-Host ""
Write-Host "=== Invoke-SshCapture (Windows PowerShell 5.1 stderr) ===" -ForegroundColor Cyan
# A REAL native child process, not a shadow function: Windows PowerShell 5.1 routes a
# native command's stderr through `$ErrorActionPreference` even when 2>&1-redirected,
# and under SilentlyContinue it DISCARDS the records -- a live probe on a 5.1 host
# returned exit 255 with EMPTY output, so every probe below was blind there. A
# PowerShell function standing in for ssh.exe can never reproduce that, because its
# output is not native stderr. Run under Windows PowerShell 5.1 (the suite supports
# it, see the #Requires) this exercises exactly the semantics that blinded the field
# run; under pwsh it still proves capture, merging and the exit code for a real child.
$isWinHost = ($PSVersionTable.PSEdition -eq 'Desktop') -or ("$env:OS" -eq 'Windows_NT')
if ($isWinHost) {
    $capExe  = 'cmd.exe'
    $capArgs = @('/d', '/c', 'echo out_marker& echo err_marker 1>&2& exit 23')
} else {
    $capExe  = '/bin/sh'
    $capArgs = @('-c', 'echo out_marker; echo err_marker >&2; exit 23')
}
foreach ($eap in @('SilentlyContinue', 'Stop', 'Continue')) {
    $prevCapEap = $ErrorActionPreference; $ErrorActionPreference = $eap
    try { $cap = Invoke-SshCapture -Exe $capExe -Arguments $capArgs } finally { $ErrorActionPreference = $prevCapEap }
    ok "capture: a real native child's stderr arrives under caller EAP $eap" ($cap.Output -match 'err_marker')
    ok "capture: ...stdout too (EAP $eap)" ($cap.Output -match 'out_marker')
    ok "capture: ...and the exit code (EAP $eap)" ($cap.ExitCode -eq 23)
}
# The probes must route through the helper -- a raw `2>&1 | Out-String` under a
# lowered EAP is exactly the 5.1-blind pattern this fixes.
$capFn = @{}
foreach ($fname in @('Ensure-VmReachable', 'Enter-RootKeyFastPath', 'Invoke-SshStream')) {
    $fn = $ast.Find({ param($n)
        $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $fname }, $true)
    $capFn[$fname] = if ($fn) { $fn.Extent.Text } else { "" }
}
ok "capture: the reachability probe goes through Invoke-SshCapture" ($capFn['Ensure-VmReachable'] -match 'Invoke-SshCapture')
ok "capture: the saved-root-key probe does too" ($capFn['Enter-RootKeyFastPath'] -match 'Invoke-SshCapture')
ok "capture: neither probe lowers EAP to SilentlyContinue" (
    ($capFn['Ensure-VmReachable'] + $capFn['Enter-RootKeyFastPath']) -notmatch '\$ErrorActionPreference = "SilentlyContinue"')
ok "capture: the streaming path pins EAP Continue for its 2>&1" (
    $capFn['Invoke-SshStream'] -match '\$ErrorActionPreference = "Continue"' -and
    $capFn['Invoke-SshStream'] -notmatch '\$ErrorActionPreference = "SilentlyContinue"')

# ssh.exe shadowed: a function wins over an application of the same name.
$script:sshOutput = ""; $script:sshExit = 0; $script:sshCalls = 0
$script:sshArgs = @()
$script:SshFamilyOpts = @()
function ssh.exe { $script:sshCalls++; $script:sshArgs = @($args); $global:LASTEXITCODE = $script:sshExit; return $script:sshOutput }
function icacls { }
function Write-Step { param($m) }
function Write-Ok { param($m) $script:lastOk = $m }
$script:warnings = New-Object System.Collections.Generic.List[string]
function Write-Warning { param($Message) $script:warnings.Add($Message) }

Write-Host ""
Write-Host "=== Ensure-VmReachable ===" -ForegroundColor Cyan
$script:VmHost = 'buildbox.example.local'; $SshPort = 2201; $SeedUser = 'construct'
$script:KnownHostsFile = Join-Path ([IO.Path]::GetTempPath()) 'kh-test'
$script:sshOutput = $denied; $script:sshExit = 255; $script:lastOk = ""
Ensure-VmReachable
ok "reachable: sshd answering passes" ($script:lastOk -match 'reachable')

# A dead forward must not pass. The real loop then asks for another address; the stub
# answers once with the same address, then the probe starts succeeding.
$script:readHost = 0
function Read-Host { param($Prompt) $script:readHost++; $script:sshOutput = $denied; return "" }
$script:sshOutput = $deadForward; $script:warnings.Clear(); $script:lastOk = ""
Ensure-VmReachable
ok "dead forward: the reachability check does NOT pass on the first probe" ($script:readHost -eq 1)
ok "dead forward: ...and says why" (@($script:warnings | Where-Object { $_ -match 'banner exchange' }).Count -eq 1)

Write-Host ""
Write-Host "=== Enter-RootKeyFastPath ===" -ForegroundColor Cyan
$fakeHome = Join-Path ([IO.Path]::GetTempPath()) ("fastpath-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fakeHome '.ssh') -Force | Out-Null
$savedHome = $HOME; $savedTemp = $env:TEMP
try {
    Set-Variable -Name HOME -Value $fakeHome -Force -Scope Global
    $env:TEMP = $fakeHome
    $LocalKeyName = 'agent_vm_ed25519'; $RemoteUser = 'root'; $VmHost = 'buildbox.example.local'
    $script:SshPortArgs = @('-p', '2201')
    Set-Content -LiteralPath (Join-Path $fakeHome ".ssh/$LocalKeyName") -Value 'key'

    $script:sshOutput = "root@buildbox.example.local: Permission denied (publickey)."; $script:sshExit = 255
    ok "fast path: a key sshd rejects falls back to the bootstrap path" ((Enter-RootKeyFastPath) -eq $false)
    ok "fast path: ...saying the key did not authenticate" ($script:lastOk -match 'did not authenticate')

    $script:sshOutput = $deadForward; $script:lastOk = ""
    $msg = ""
    try { [void](Enter-RootKeyFastPath) } catch { $msg = [string]$_.Exception.Message }
    ok "fast path: a connection that never reached sshd stops with the cause" ($msg -match 'Could not connect' -and $msg -match 'banner exchange')
    ok "fast path: ...and does not claim the key failed" ($script:lastOk -notmatch 'did not authenticate')

    $script:sshOutput = ""; $script:sshExit = 0
    ok "fast path: a key that works switches to root" ((Enter-RootKeyFastPath) -eq $true -and $script:UseRootKey)
} finally {
    Set-Variable -Name HOME -Value $savedHome -Force -Scope Global
    $env:TEMP = $savedTemp
    Remove-Item -LiteralPath $fakeHome -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "=== IPv4 for a host service's endpoint ===" -ForegroundColor Cyan
. (Join-Path $repoRoot 'lib/AgentVm.InstanceTarget.ps1')
$inet = 'AddressFamily=inet'
ok "family: a service endpoint name is dialled over IPv4" ((@(Get-ConstructSshFamilyOptions -Ipv4 $true -VmHost 'buildbox.example.local') -join ' ') -eq "-o $inet")
ok "family: ...and so is an IPv4 literal"                 ((@(Get-ConstructSshFamilyOptions -Ipv4 $true -VmHost '10.0.0.5') -join ' ') -eq "-o $inet")
ok "family: an IPv6 literal is dialled as written"        (@(Get-ConstructSshFamilyOptions -Ipv4 $true -VmHost '2001:db8::5').Count -eq 0)
ok "family: ...bracketed and scoped too"                  (@(Get-ConstructSshFamilyOptions -Ipv4 $true -VmHost '[fe80::1%20]').Count -eq 0)
ok "family: a local VM is untouched"                      (@(Get-ConstructSshFamilyOptions -Ipv4 $false -VmHost 'agent-vm.mshome.net').Count -eq 0)

# The real probes carry the options the script computed.
$script:SshFamilyOpts = @('-o', $inet)
$script:sshOutput = $denied; $script:sshExit = 255
Ensure-VmReachable
ok "family: the reachability probe dials over IPv4" ($script:sshArgs -contains $inet)
$fakeHome2 = Join-Path ([IO.Path]::GetTempPath()) ("fastpath-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path (Join-Path $fakeHome2 '.ssh') -Force | Out-Null
$savedHome = $HOME; $savedTemp = $env:TEMP
try {
    Set-Variable -Name HOME -Value $fakeHome2 -Force -Scope Global
    $env:TEMP = $fakeHome2
    Set-Content -LiteralPath (Join-Path $fakeHome2 ".ssh/$LocalKeyName") -Value 'key'
    $script:sshOutput = ""; $script:sshExit = 0
    [void](Enter-RootKeyFastPath)
    ok "family: the saved-root-key probe dials over IPv4" ($script:sshArgs -contains $inet)
    ok "family: ...and the whole run keeps it" ($script:SshOpts -contains $inet)
} finally {
    Set-Variable -Name HOME -Value $savedHome -Force -Scope Global
    $env:TEMP = $savedTemp
    Remove-Item -LiteralPath $fakeHome2 -Recurse -Force -ErrorAction SilentlyContinue
}
$script:SshFamilyOpts = @()

$provSrc = $ast.Extent.Text
ok "family: a service URL or a remote registry entry implies IPv4" (
    $provSrc -match 'if \(\$ServiceUrl\) \{ \$SshIpv4 = \$true \}' -and
    $provSrc -match "if \(\[string\]\`$instanceTarget\.Backend -eq 'hyperv-remote'\) \{ \`$SshIpv4 = \`$true \}")
ok "family: the bootstrap and base option lists carry it" (
    ([regex]::Matches($provSrc, '\) \+ \$script:SshFamilyOpts')).Count -ge 5)
ok "family: ssh-keyscan is told -4" ($provSrc -match '\$keyscanFamily = if \(\$script:SshFamilyOpts\.Count -gt 0\) \{ @\("-4"\) \}')
ok "family: the ssh_config Host block (VS Code) gets AddressFamily inet" ($provSrc -match '"`n    AddressFamily inet"')
foreach ($script in @('Get-AgentUsage.ps1', 'Get-ConstructT3PairingLink.ps1')) {
    $src = Get-Content -Raw (Join-Path $repoRoot $script)
    ok "family: $script dials a remote instance over IPv4" (
        $src -match "Get-ConstructSshFamilyOptions -Ipv4 \(\[string\]\`$instanceTarget\.Backend -eq 'hyperv-remote'\)" -and
        ([regex]::Matches($src, '\) \+ \$script:SshFamilyOpts')).Count -eq 2)
}
$autoSrc = Get-Content -Raw (Join-Path $repoRoot 'Auto-Install.ps1')
ok "family: the remote config export asks for IPv4" ($autoSrc -match "ContainsKey\('SshIpv4'\)\) \{ \`$a\['SshIpv4'\] = \`$true \}")

Write-Host ""
Write-Host "=== Host-key step ===" -ForegroundColor Cyan
$provText = $ast.Extent.Text
ok "host key: 'Host key stored' is printed only when ssh-keyscan returned a key" (
    $provText -match '(?s)if \(\$hostKeys\.Count -gt 0\) \{\s*Write-Ok "Host key stored"')
ok "host key: an empty scan warns instead" ($provText -match 'ssh-keyscan returned no host key')

Write-Host ""
Write-Host "provision ssh diagnostics -- $script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
if ($script:fail -gt 0) { exit 1 }
exit 0
