#Requires -Version 5.1
# The provisioner's SSH diagnostics, driven with ssh.exe shadowed by a function that
# replays real ssh output. A port forward that accepts TCP but reaches no sshd must
# fail at the reachability check or as a connection error -- never read as "reachable"
# or as a key that "did not authenticate" (which led to a pointless password prompt).
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
foreach ($fname in @('Get-SshFailureLine', 'Test-SshServerAnswered', 'Ensure-VmReachable', 'Enter-RootKeyFastPath')) {
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

# ssh.exe shadowed: a function wins over an application of the same name.
$script:sshOutput = ""; $script:sshExit = 0; $script:sshCalls = 0
function ssh.exe { $script:sshCalls++; $global:LASTEXITCODE = $script:sshExit; return $script:sshOutput }
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
Write-Host "=== Host-key step ===" -ForegroundColor Cyan
$provText = $ast.Extent.Text
ok "host key: 'Host key stored' is printed only when ssh-keyscan returned a key" (
    $provText -match '(?s)if \(\$hostKeys\.Count -gt 0\) \{\s*Write-Ok "Host key stored"')
ok "host key: an empty scan warns instead" ($provText -match 'ssh-keyscan returned no host key')

Write-Host ""
Write-Host "provision ssh diagnostics -- $script:pass passed, $script:fail failed" -ForegroundColor $(if ($script:fail) { 'Red' } else { 'Green' })
if ($script:fail -gt 0) { exit 1 }
exit 0
