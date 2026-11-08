#Requires -Version 5.1
<#
    The backup actions of the PowerShell client (lib/AgentVm.Backup.ps1 and their glue in
    Provision-AgentVM.ps1): the guest command lines, the binary-safe SSH streams, the
    actions end to end against a FAKE ssh and a FAKE `construct backup`, the host-backup
    restore step of provisioning, and the key file actions. Run:

        pwsh -NoProfile -File test/backup-actions.test.ps1

    Never talks to a VM or a host service: the fake ssh runs the remote command locally,
    with `construct` and `sudo` replaced by shell functions, inside a temporary directory.
    Needs bash, openssl and age (the guest's tools, used by the fake guest).
#>
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

$script:pass = 0; $script:fail = 0; $script:skip = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}
function skip($name, $why) { $script:skip++; Write-Host "  SKIP  $name -- $why" -ForegroundColor Yellow }
function Get-ThrowMessage([scriptblock]$Script) {
    try { & $Script | Out-Null; return '' } catch { return [string]$_.Exception.Message }
}
function Get-FileSha([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }

. (Join-Path $repoRoot 'lib/AgentVm.Backup.ps1')
# DPAPI stand-in (test/backup-keys.test.ps1 covers the file format with it).
function Protect-ConstructTokenBytes { param([byte[]]$Bytes) return ,([byte[]]@($Bytes | ForEach-Object { $_ -bxor 0x33 })) }
function Unprotect-ConstructTokenBytes { param([byte[]]$Bytes) return ,([byte[]]@($Bytes | ForEach-Object { $_ -bxor 0x33 })) }

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('construct-backup-actions-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null
$keyDir = Join-Path $tmp 'keys'
$fakeDir = Join-Path $tmp 'guest'
New-Item -ItemType Directory -Path $fakeDir -Force | Out-Null
$savedLocalAppData = $env:LOCALAPPDATA
# Everything that resolves the default key folder lands in the fixture, never in a profile.
$env:LOCALAPPDATA = Join-Path $tmp 'localappdata'
# The fake ssh is a shell script; Windows runs only the pure parts of this file.
$onWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or [bool]$IsWindows
$hasTools = [bool]((-not $onWindows) -and (Get-Command bash -ErrorAction SilentlyContinue) -and (Get-Command openssl -ErrorAction SilentlyContinue) -and
                   (Get-Command age -ErrorAction SilentlyContinue) -and (Get-Command age-keygen -ErrorAction SilentlyContinue))

try {
# ── (a) The guest command lines ─────────────────────────────────────────────
Write-Host ''
Write-Host '=== Guest command lines ===' -ForegroundColor Cyan
ok 'cli: plain arguments stay bare' ((Format-ConstructBackupCommand @('restore', '--stdin', '--expect-id', ('a' * 32))) -ceq "construct backup restore --stdin --expect-id $('a' * 32)")
ok 'cli: a label with a space is single-quoted' ((Format-ConstructBackupCommand @('recipients', 'add', '--label', 'Desk PC')) -ceq "construct backup recipients add --label 'Desk PC'")
ok "cli: an apostrophe cannot break out" ((Format-ConstructBackupCommand @('x', "it's")) -ceq "construct backup x 'it'\''s'")
$direct = Get-ConstructBackupRemoteCommand -Command 'construct backup list' -Direct
ok 'remote: as root it is a non-login bash -c' ($direct.StartsWith("bash -c '") -and $direct -notmatch 'bash -lc' -and $direct -notmatch 'sudo')
ok 'remote: ...with a fixed PATH and HOME' ($direct -match 'export HOME=/root PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin;')
$viaSudo = Get-ConstructBackupRemoteCommand -Command "construct backup recipients add --label 'Desk PC'" -SeedPassword "pa'ss"
ok 'remote: the seed user tries sudo without a password first' ($viaSudo.StartsWith('if sudo -n true 2>/dev/null; then sudo -n bash -c '))
ok 'remote: ...else the password is the first stdin line of sudo -S, the rest stays stdin' ($viaSudo -match "\{ printf '%s\\n' 'pa'\\''ss'; cat; \} \| sudo -S -p '' bash -c ")
$conn0 = New-ConstructBackupConnection -SshExe 'ssh' -Options @('-p', '2201', '-o', 'BatchMode=yes') -Target 'root@vm.example.com' -Direct
ok 'connection: long transfers get keepalives' ((@($conn0.Options) -join ' ') -match 'ServerAliveInterval=15')
$conn1 = New-ConstructBackupConnection -SshExe 'ssh' -Options @('-o', 'ServerAliveInterval=30') -Target 'root@vm.example.com' -Direct
ok "connection: ...but not twice" (@($conn1.Options | Where-Object { $_ -like 'ServerAliveInterval=*' }).Count -eq 1)
$sshArgs = @(Get-ConstructBackupSshArguments -Connection $conn0 -Arguments @('list'))
ok 'ssh args: options, then the target, then ONE remote command string' (
    $sshArgs[0] -eq '-p' -and $sshArgs[1] -eq '2201' -and $sshArgs[$sshArgs.Count - 2] -eq 'root@vm.example.com' -and $sshArgs[$sshArgs.Count - 1] -like "bash -c '*construct backup list'")
ok 'ssh args: no -n (stdin carries the data)' ($sshArgs -notcontains '-n')
ok 'exit: 5 is the VM disk space' ((Get-ConstructBackupExitMessage 5) -match 'disk space')
ok 'exit: 4 is no host service / feature' ((Get-ConstructBackupExitMessage 4) -match 'host service')
ok 'exit: 8 is decryption' ((Get-ConstructBackupExitMessage 8) -match 'decryption')
ok 'exit: 1 names the older-VM case' ((Get-ConstructBackupExitMessage 1) -match 'reprovision')
ok 'exit: unknown codes are stated' ((Get-ConstructBackupExitMessage 42) -eq 'exit 42')

# The .NET Framework path joins the argv itself; ssh.exe parses it with the MSVCRT rules.
function Split-MsvcrtCommandLine([string]$Line) {
    $out = New-Object System.Collections.Generic.List[string]
    $i = 0; $n = $Line.Length
    while ($i -lt $n) {
        while ($i -lt $n -and ($Line[$i] -eq ' ' -or $Line[$i] -eq "`t")) { $i++ }
        if ($i -ge $n) { break }
        $sb = New-Object System.Text.StringBuilder; $inQuotes = $false
        while ($i -lt $n) {
            $c = $Line[$i]
            if (-not $inQuotes -and ($c -eq ' ' -or $c -eq "`t")) { break }
            if ($c -eq '\') {
                $bs = 0; while ($i -lt $n -and $Line[$i] -eq '\') { $bs++; $i++ }
                if ($i -lt $n -and $Line[$i] -eq '"') {
                    [void]$sb.Append([char]'\', [int][Math]::Floor($bs / 2))
                    if ($bs % 2 -eq 1) { [void]$sb.Append('"'); $i++ }
                } else { [void]$sb.Append([char]'\', $bs) }
                continue
            }
            if ($c -eq '"') { $inQuotes = -not $inQuotes; $i++; continue }
            [void]$sb.Append($c); $i++
        }
        $out.Add($sb.ToString())
    }
    return ,($out.ToArray())
}
ok 'winargs: a bare word stays bare' ((ConvertTo-ConstructWindowsArgument 'root@vm') -ceq 'root@vm')
ok 'winargs: empty becomes ""' ((ConvertTo-ConstructWindowsArgument '') -ceq '""')
ok 'winargs: a space is quoted' ((ConvertTo-ConstructWindowsArgument 'a b') -ceq '"a b"')
ok 'winargs: an embedded quote is escaped' ((ConvertTo-ConstructWindowsArgument 'a"b') -ceq '"a\"b"')
ok 'winargs: trailing backslashes are doubled inside quotes' ((ConvertTo-ConstructWindowsArgument 'a b\') -ceq '"a b\\"')
$samples = @('plain', 'two words', 'quote"inside', 'back\slash', 'tail\\', 'mix \" of\\" both\', "tab`there", '', '"', '\', $viaSudo, $direct,
             "UserKnownHostsFile=C:\Users\x y\AppData\Local\Temp\construct-known_hosts")
$joined = Join-ConstructWindowsCommandLine -ArgumentList $samples
$parsed = Split-MsvcrtCommandLine $joined
$same = ($parsed.Count -eq $samples.Count)
for ($k = 0; $same -and $k -lt $samples.Count; $k++) { if ($parsed[$k] -cne $samples[$k]) { $same = $false } }
ok 'winargs: the joined command line parses back to the same argv (MSVCRT rules)' $same

if (-not $hasTools) {
    skip 'streams and actions' 'a Linux/macOS shell with bash, openssl, age and age-keygen is needed (the fake ssh is a shell script)'
} else {
# ── (b) A fake ssh and a fake guest ─────────────────────────────────────────
$fakeSsh = Join-Path $tmp 'ssh'
$fakeConstruct = Join-Path $tmp 'construct'
$sshLog = Join-Path $tmp 'ssh-argv.log'
[IO.File]::WriteAllText($fakeSsh, @'
#!/usr/bin/env bash
# Fake ssh: records its argv, then runs the remote command (the last argument) here, with
# `construct` and `sudo` replaced by functions. Never connects anywhere.
{ printf 'ARGV'; printf ' <%s>' "$@"; printf '\n'; } >> "$FAKE_SSH_LOG"
cmd="${!#}"
construct() { "$FAKE_CONSTRUCT" "$@"; }
sudo() {
  local needpw=0
  while [[ $# -gt 0 ]]; do
    case "$1" in
      -n) shift; [[ -n "${FAKE_SUDO_PASSWORD:-}" ]] && return 1 ;;
      -S) needpw=1; shift ;;
      -p) shift 2 ;;
      *) break ;;
    esac
  done
  # Like sudo -S: exactly one line of stdin is the password, the rest is the command's.
  if [[ $needpw == 1 ]]; then IFS= read -r pw; printf '%s\n' "$pw" > "$FAKE_DIR/sudo-password"; fi
  "$@"
}
export -f construct sudo
exec bash -c "$cmd"
'@.Replace("`r`n", "`n"))
[IO.File]::WriteAllText($fakeConstruct, @'
#!/usr/bin/env bash
# Fake `construct`: only the `backup` subcommands the client uses (contract 3.3).
set -euo pipefail
d="${FAKE_DIR:?}"
printf '%s\n' "$*" >> "$d/construct-calls"
[[ "${1:-}" == backup ]] || { echo "Unknown command: ${1:-}" >&2; exit 1; }
shift
if [[ -n "${FAKE_EXIT:-}" ]]; then echo "fake guest: exit ${FAKE_EXIT}" >&2; exit "${FAKE_EXIT}"; fi
case "$1" in
  recipients)
    [[ "$2" == add && "$3" == --label ]] || exit 2
    printf '%s\n' "$4" > "$d/label"
    cat > "$d/recipient.pem"
    openssl pkey -pubin -in "$d/recipient.pem" -outform DER | sha256sum | cut -d' ' -f1 ;;
  create)
    if [[ "${2:-}" == --stdout ]]; then
      echo "fake guest: packing" >&2
      cat "$d/container.cbk"
    else
      [[ "$2" == --trigger ]] || exit 2
      echo "fake guest: uploading" >&2
      printf '{"id":"%s","sizeBytes":4096,"sha256":"%064d","trigger":"%s","gitCredentialHosts":["github.com"]}\n' "$FAKE_ID" 0 "$3"
    fi ;;
  download)
    [[ "$2" == "$FAKE_ID" ]] || exit 4
    cat "$d/container.cbk" ;;
  restore)
    if [[ "$2" == --stdin ]]; then
      [[ "$3" == --expect-id ]] || exit 2
      printf '%s\n' "$4" > "$d/expect-id"
      IFS= read -r e
      printf '%s\n' "$e" > "$d/identity"
      cat > "$d/payload.age"
      age -d -i <(printf '%s\n' "$e") -o "$d/restored.bin" "$d/payload.age" || exit 8
    else
      [[ "$2" == --from-host ]] || exit 2
      IFS= read -r e
      printf '%s\n' "$e" > "$d/identity"
      printf '%s\n' "$3" > "$d/from-host"
      age -d -i <(printf '%s\n' "$e") -o "$d/restored.bin" "$d/payload-only.age" || exit 8
    fi ;;
  *) exit 2 ;;
esac
'@.Replace("`r`n", "`n"))
& chmod +x $fakeSsh $fakeConstruct
$env:FAKE_SSH_LOG = $sshLog
$env:FAKE_CONSTRUCT = $fakeConstruct
$env:FAKE_DIR = $fakeDir
$env:FAKE_ID = 'c0ffee00' * 4
$conn = New-ConstructBackupConnection -SshExe $fakeSsh -Options @('-p', '2201', '-o', 'BatchMode=yes') -Target 'root@vm.example.com' -Direct

# ── (c) The raw streams ─────────────────────────────────────────────────────
Write-Host ''
Write-Host '=== Binary-safe streams ===' -ForegroundColor Cyan
$blob = Join-Path $tmp 'blob.bin'
$bytes = New-Object byte[] (3MB + 17)
(New-Object Random 7).NextBytes($bytes)
for ($b = 0; $b -lt 256; $b++) { $bytes[$b] = [byte]$b }      # every byte value, CR and LF included
[IO.File]::WriteAllBytes($blob, $bytes)
$outFile = Join-Path $tmp 'blob.out'
$res = Invoke-ConstructSshStreamToFile -FilePath $fakeSsh -ArgumentList @('root@vm.example.com', "cat '$blob'") -OutFile $outFile
ok 'to-file: exit 0 and every byte arrives unchanged' ($res.ExitCode -eq 0 -and (Get-FileSha $outFile) -eq (Get-FileSha $blob) -and $res.BytesOut -eq $bytes.Length)
$inCopy = Join-Path $tmp 'blob.in'
$res = Invoke-ConstructSshStreamFromFile -FilePath $fakeSsh -ArgumentList @('root@vm.example.com', "IFS= read -r line; printf '%s' `"`$line`" > '$inCopy.line'; cat > '$inCopy'") `
          -InFile $blob -Offset 1000 -FirstLine 'AGE-SECRET-KEY-1FIRSTLINE'
$expected = New-Object byte[] ($bytes.Length - 1000); [Array]::Copy($bytes, 1000, $expected, 0, $expected.Length)
$expectedFile = Join-Path $tmp 'blob.expected'; [IO.File]::WriteAllBytes($expectedFile, $expected)
ok 'from-file: the first line arrives alone, without a byte-order mark' ([IO.File]::ReadAllText("$inCopy.line") -ceq 'AGE-SECRET-KEY-1FIRSTLINE' -and
    ([IO.File]::ReadAllBytes("$inCopy.line"))[0] -eq [byte][char]'A')
ok 'from-file: ...then the file from the offset, byte for byte' ($res.ExitCode -eq 0 -and (Get-FileSha $inCopy) -eq (Get-FileSha $expectedFile))
$res = Invoke-ConstructSshProcess -FilePath $fakeSsh -ArgumentList @('root@vm.example.com', 'echo out; echo err >&2; exit 3') -Stdout Capture
ok 'capture: stdout text and the exit code' ($res.ExitCode -eq 3 -and $res.Output.Trim() -eq 'out')
$sw = [Diagnostics.Stopwatch]::StartNew()
$res = Invoke-ConstructSshStreamFromFile -FilePath $fakeSsh -ArgumentList @('root@vm.example.com', 'exit 8') -InFile $blob
ok 'early exit: a remote side that stops reading gives its exit code, no hang' ($res.ExitCode -eq 8 -and $sw.Elapsed.TotalSeconds -lt 30)
$res = Invoke-ConstructSshProcess -FilePath $fakeSsh -ArgumentList @('root@vm.example.com', 'if read -r x; then echo got; else echo eof; fi') -Stdout Capture
ok 'stdin: with nothing to send, stdin is closed at once (ssh -n behaviour)' ($res.Output.Trim() -eq 'eof')

# ── (d) The actions against the fake guest ──────────────────────────────────
Write-Host ''
Write-Host '=== Actions against a fake guest ===' -ForegroundColor Cyan
$key = Register-ConstructBackupPcKey -Connection $conn -KeyDir $keyDir
ok 'enrol: the PC key is created on first need' ($key.Fingerprint -cmatch '^[0-9a-f]{64}$' -and (Test-Path (Join-Path $keyDir 'pc-key.dpapi')))
ok 'enrol: the guest received this PC''s PEM on stdin' ([IO.File]::ReadAllText((Join-Path $fakeDir 'recipient.pem')) -ceq $key.PublicKeyPem)
ok 'enrol: ...with the label as an argument' ([IO.File]::ReadAllText((Join-Path $fakeDir 'label')).Trim() -ceq $key.Label)

# A real container, wrapped for the PEM the guest received (what construct-backup.sh does).
& age-keygen -o (Join-Path $fakeDir 'identity.key') 2>$null
$identity = (Get-Content (Join-Path $fakeDir 'identity.key') | Where-Object { $_ -like 'AGE-SECRET-KEY-1*' }).Trim()
$recipient = (& age-keygen -y (Join-Path $fakeDir 'identity.key')).Trim()
$plain = Join-Path $tmp 'config.tar.zst'
[IO.File]::WriteAllBytes($plain, $bytes)
& age -r $recipient -o (Join-Path $fakeDir 'payload-only.age') $plain
$env:CONSTRUCT_TEST_IDENTITY = $identity
$wrapped = (& bash -c 'printf %s "$CONSTRUCT_TEST_IDENTITY" | openssl pkeyutl -encrypt -pubin -inkey "$1" -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 -pkeyopt rsa_mgf1_md:sha256 | base64 -w0' _ (Join-Path $fakeDir 'recipient.pem')).Trim()
Remove-Item Env:CONSTRUCT_TEST_IDENTITY
$keysJson = ([ordered]@{ format = 'construct-backup-keys/1'; backupId = $env:FAKE_ID; createdAt = '2026-11-08T10:00:00Z'
                         recipients = @([ordered]@{ fingerprint = $key.Fingerprint; label = $key.Label; wrapped = $wrapped }) } | ConvertTo-Json -Depth 4 -Compress)
[IO.File]::WriteAllBytes((Join-Path $fakeDir 'container.cbk'), (Join-ConstructBytes @(
    [Text.Encoding]::UTF8.GetBytes("construct-backup/1`n$keysJson`n"), [IO.File]::ReadAllBytes((Join-Path $fakeDir 'payload-only.age')))))

$saveTo = Join-Path $tmp 'saved/vm.cbk'
$saved = Save-ConstructBackupFile -Connection $conn -Path $saveTo -KeyDir $keyDir
ok 'save: the container arrives byte for byte' ((Get-FileSha $saveTo) -eq (Get-FileSha (Join-Path $fakeDir 'container.cbk')))
ok 'save: id, size and readability are reported' ($saved.BackupId -eq $env:FAKE_ID -and $saved.Bytes -eq (Get-Item $saveTo).Length -and $saved.Readable)
ok 'save: no .partial is left behind' (-not (Test-Path "$saveTo.partial"))
ok 'save: it enrolled first and ran create --stdout' ((Get-Content (Join-Path $fakeDir 'construct-calls'))[-2] -like 'backup recipients add*' -and
    (Get-Content (Join-Path $fakeDir 'construct-calls'))[-1] -eq 'backup create --stdout')
$saved2 = Save-ConstructBackupFile -Connection $conn -Path (Join-Path $tmp 'saved/host.cbk') -BackupId $env:FAKE_ID -KeyDir $keyDir
ok 'save -BackupId: download <id> of a host backup' ($saved2.BackupId -eq $env:FAKE_ID -and (Get-Content (Join-Path $fakeDir 'construct-calls'))[-1] -eq "backup download $($env:FAKE_ID)")
$msg = Get-ThrowMessage { Save-ConstructBackupFile -Connection $conn -Path (Join-Path $tmp 'saved/none.cbk') -BackupId ('0' * 32) -KeyDir $keyDir }
ok 'save -BackupId: a guest refusal is an error naming its meaning' ($msg -match 'Downloading backup' -and $msg -match 'host service')
ok 'save: ...and leaves no file' (-not (Test-Path (Join-Path $tmp 'saved/none.cbk')) -and -not (Test-Path (Join-Path $tmp 'saved/none.cbk.partial')))
[IO.File]::WriteAllText((Join-Path $fakeDir 'container.cbk.bad'), "construct-backup/1`nnot json`nxx")
Copy-Item (Join-Path $fakeDir 'container.cbk') (Join-Path $fakeDir 'container.cbk.good')
Copy-Item (Join-Path $fakeDir 'container.cbk.bad') (Join-Path $fakeDir 'container.cbk') -Force
$msg = Get-ThrowMessage { Save-ConstructBackupFile -Connection $conn -Path (Join-Path $tmp 'saved/bad.cbk') -KeyDir $keyDir }
ok 'save: output that is not a container is refused and removed' ($msg -match 'not valid JSON' -and -not (Test-Path (Join-Path $tmp 'saved/bad.cbk.partial')) -and -not (Test-Path (Join-Path $tmp 'saved/bad.cbk')))
Copy-Item (Join-Path $fakeDir 'container.cbk.good') (Join-Path $fakeDir 'container.cbk') -Force

Remove-Item (Join-Path $fakeDir 'restored.bin') -ErrorAction SilentlyContinue
$restoredId = Restore-ConstructBackupFile -Connection $conn -Path $saveTo -KeyDir $keyDir
ok 'restore file: the guest got E on stdin and decrypted the payload to the original' ($restoredId -eq $env:FAKE_ID -and (Get-FileSha (Join-Path $fakeDir 'restored.bin')) -eq (Get-FileSha $plain))
ok 'restore file: E is the backup''s identity' ((Get-Content (Join-Path $fakeDir 'identity')).Trim() -ceq $identity)
ok 'restore file: --expect-id is the container''s backup id' ((Get-Content (Join-Path $fakeDir 'expect-id')).Trim() -eq $env:FAKE_ID)

Remove-Item (Join-Path $fakeDir 'restored.bin') -ErrorAction SilentlyContinue
Restore-ConstructHostBackup -Connection $conn -BackupId $env:FAKE_ID -KeysJson $keysJson -KeyDir $keyDir
ok 'restore host: restore --from-host <id> with E on stdin' ((Get-Content (Join-Path $fakeDir 'from-host')).Trim() -eq $env:FAKE_ID -and
    (Get-FileSha (Join-Path $fakeDir 'restored.bin')) -eq (Get-FileSha $plain))
ok 'restore host: a keys file of another backup is refused before anything runs' (
    (Get-ThrowMessage { Restore-ConstructHostBackup -Connection $conn -BackupId ('1' * 32) -KeysJson $keysJson -KeyDir $keyDir }) -match 'belongs to backup')

$created = New-ConstructHostBackup -Connection $conn -Trigger 'pre-reinstall' -KeyDir $keyDir
ok 'create: the host backup id and the git credential hosts come back' ($created.Id -eq $env:FAKE_ID -and @($created.GitCredentialHosts) -contains 'github.com')
ok 'create: --trigger pre-reinstall reached the guest' ((Get-Content (Join-Path $fakeDir 'construct-calls'))[-1] -eq 'backup create --trigger pre-reinstall')

$env:FAKE_EXIT = '5'
try {
    $msg = Get-ThrowMessage { New-ConstructHostBackup -Connection $conn -Trigger 'manual' -KeyDir $keyDir }
    ok 'errors: a failed enrolment stops the backup, with the meaning of the exit code' ($msg -match 'Enrolling' -and $msg -match 'disk space')
    $env:FAKE_EXIT = '8'
    $msg = Get-ThrowMessage { Restore-ConstructBackupFile -Connection $conn -Path $saveTo -KeyDir $keyDir }
    ok 'errors: a guest that refuses the restore early is reported, not hung on' ($msg -match 'decryption')
} finally { Remove-Item Env:FAKE_EXIT -ErrorAction SilentlyContinue }

# The seed user's sudo: password branch and passwordless branch keep stdin for the data.
$connSudo = New-ConstructBackupConnection -SshExe $fakeSsh -Options @('-o', 'BatchMode=yes') -Target 'construct@vm.example.com' -SeedPassword "s33d'pw"
$env:FAKE_SUDO_PASSWORD = '1'
try {
    Remove-Item (Join-Path $fakeDir 'restored.bin'), (Join-Path $fakeDir 'sudo-password') -ErrorAction SilentlyContinue
    $null = Restore-ConstructBackupFile -Connection $connSudo -Path $saveTo -KeyDir $keyDir
    ok 'sudo -S: the password is sudo''s line, E and the payload still reach the command' (
        (Get-Content (Join-Path $fakeDir 'sudo-password')).Trim() -ceq "s33d'pw" -and
        (Get-Content (Join-Path $fakeDir 'identity')).Trim() -ceq $identity -and
        (Get-FileSha (Join-Path $fakeDir 'restored.bin')) -eq (Get-FileSha $plain))
} finally { Remove-Item Env:FAKE_SUDO_PASSWORD -ErrorAction SilentlyContinue }
Remove-Item (Join-Path $fakeDir 'restored.bin'), (Join-Path $fakeDir 'sudo-password') -ErrorAction SilentlyContinue
$null = Restore-ConstructBackupFile -Connection $connSudo -Path $saveTo -KeyDir $keyDir
ok 'sudo -n: passwordless sudo reads no password and passes stdin through' (
    -not (Test-Path (Join-Path $fakeDir 'sudo-password')) -and (Get-FileSha (Join-Path $fakeDir 'restored.bin')) -eq (Get-FileSha $plain))

# E travels on stdin only: never in any argv ssh was given.
$argvLog = [IO.File]::ReadAllText($sshLog)
ok 'secrecy: E never appears on an ssh command line' (-not $argvLog.Contains($identity) -and -not $argvLog.Contains('AGE-SECRET-KEY-1'))
ok 'secrecy: ...nor the PC key' (-not $argvLog.Contains('BEGIN PUBLIC KEY') -and -not $argvLog.Contains('RSAKeyValue'))

# ── (d2) The host API calls the client makes itself ─────────────────────────
Write-Host ''
Write-Host '=== Host API (stubbed Invoke-ConstructApi) ===' -ForegroundColor Cyan
$realApi = ${function:Invoke-ConstructApi}
$script:apiCalls = New-Object System.Collections.Generic.List[string]
$unreadableKeys = ([ordered]@{ format = 'construct-backup-keys/1'; backupId = ('9' * 32); createdAt = '2026-11-08T11:00:00Z'
                               recipients = @([ordered]@{ fingerprint = ('e' * 64); label = 'Other PC'; wrapped = 'AAAA' }) } | ConvertTo-Json -Depth 4 -Compress)
function Invoke-ConstructApi {
    param($BaseUrl, $Method, $Path, $Body, $Auth, [switch]$RawResponse, [switch]$NoThrow, $TimeoutSec)
    $script:apiCalls.Add("$Method $Path $($Auth.Mode)")
    if ($Path -like '*/backups') {
        return ('{"backups":[{"id":"' + ('9' * 32) + '","createdAt":"2026-11-08T11:00:00Z","trigger":"scheduled","sizeBytes":10,"pinnedUntil":null},' +
                '{"id":"' + $env:FAKE_ID + '","createdAt":"2026-11-07T11:00:00Z","trigger":"manual","sizeBytes":20,"pinnedUntil":null}]}')
    }
    if ($Path -like "*/backups/$('9' * 32)/keys") { return $unreadableKeys }
    if ($Path -like "*/backups/$($env:FAKE_ID)/keys") { return $keysJson }
    throw "unexpected $Path"
}
$tokenAuth = @{ Mode = 'token'; Token = 'x' }
$list = @(Get-ConstructHostBackupList -BaseUrl 'https://host.example.com:7462' -VmName 'work vm' -Auth $tokenAuth)
ok 'api: GET /vms/<escaped name>/backups' ($script:apiCalls[0] -eq 'GET /vms/work%20vm/backups token')
ok 'api: the list keeps the host''s newest-first order' ($list.Count -eq 2 -and $list[0].Id -eq ('9' * 32) -and $list[1].SizeBytes -eq 20)
$script:apiCalls.Clear()
$found = Find-ConstructReadableHostBackup -BaseUrl 'https://host.example.com:7462' -VmName 'work-vm' -Auth $tokenAuth -KeyDir $keyDir
ok 'api: the newest backup THIS PC can open is chosen (an unreadable newer one is skipped)' ($found.Id -eq $env:FAKE_ID)
ok 'api: ...by reading the keys files, newest first' ($script:apiCalls[1] -eq "GET /vms/work-vm/backups/$('9' * 32)/keys token" -and $script:apiCalls[2] -eq "GET /vms/work-vm/backups/$($env:FAKE_ID)/keys token")
ok 'api: a PC without any key finds nothing and asks nothing' ($null -eq (Find-ConstructReadableHostBackup -BaseUrl 'https://h' -VmName 'w' -Auth $tokenAuth -KeyDir (Join-Path $tmp 'nokey')))
ok 'api: a malformed id never reaches a URL' ((Get-ThrowMessage { Get-ConstructHostBackupKeysJson -BaseUrl 'https://h' -VmName 'w' -BackupId '../x' -Auth $tokenAuth }) -match 'not a backup id')
${function:Invoke-ConstructApi} = $realApi

# An export file whose content is not a private key is refused even with the right passphrase.
$pw = [Text.Encoding]::UTF8.GetBytes('long enough one')
$salt = New-ConstructRandomBytes 16; $iv = New-ConstructRandomBytes 16
$kdf = Get-ConstructBackupKdfKeys -Password $pw -Salt $salt -Iterations 600000
$publicOnly = [Security.Cryptography.RSA]::Create(3072)
$ct = Invoke-ConstructAesCbc -Key $kdf.EncKey -Iv $iv -Data ([Text.Encoding]::UTF8.GetBytes($publicOnly.ToXmlString($false)))
$mac = Get-ConstructHmacSha256 -Key $kdf.MacKey -Data (Join-ConstructBytes @($iv, $ct))
$fakeExport = Join-Path $tmp 'public-only.cbkey'
[IO.File]::WriteAllText($fakeExport, ([ordered]@{ format = 'construct-backup-key/1'; kdf = 'pbkdf2-sha256'; iterations = 600000
    salt = [Convert]::ToBase64String($salt); iv = [Convert]::ToBase64String($iv); ciphertext = [Convert]::ToBase64String($ct)
    mac = [Convert]::ToBase64String($mac); fingerprint = (Get-ConstructBackupFingerprint -Rsa $publicOnly); label = 'x' } | ConvertTo-Json))
ok 'import: a file holding only a public key is refused' ((Get-ThrowMessage {
    Import-ConstructBackupKey -Path $fakeExport -Passphrase (ConvertTo-SecureString 'long enough one' -AsPlainText -Force) -KeyDir (Join-Path $tmp 'pubonly') }) -match 'holds no private key')

# ── (e) Provision-AgentVM.ps1's glue ────────────────────────────────────────
Write-Host ''
Write-Host '=== Provision-AgentVM.ps1: the backup actions ===' -ForegroundColor Cyan
$provErrors = $null
$provAst = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'Provision-AgentVM.ps1'), [ref]$null, [ref]$provErrors)
ok 'parse: Provision-AgentVM.ps1 has zero errors' ($provErrors.Count -eq 0)
function Get-ProvisionFunctionText([string]$Name) {
    $fn = $provAst.Find({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name }, $true)
    if ($fn) { return [string]$fn.Extent.Text } else { return '' }
}
foreach ($fname in 'Get-BackupConnection', 'Assert-BackupArguments', 'Invoke-BackupSshAction', 'Invoke-BackupKeyFileAction',
                   'Confirm-BackupKeyReplace', 'Write-BackupKeyExportHint', 'Invoke-DeferredProjectCommands') {
    $text = Get-ProvisionFunctionText $fname
    ok "extract: Provision-AgentVM.ps1 defines $fname" ($text -ne '')
    if ($text) { Invoke-Expression $text }
}
$paramBlock = $provAst.ParamBlock.Extent.Text
ok 'params: the five contract actions and backup-create are accepted' (
    $paramBlock -match '"backup-enroll", "backup-save", "backup-restore",\s+"backup-key-export", "backup-key-import", "backup-create"')
foreach ($p in 'BackupFile', 'BackupId', 'BackupKeyFile', 'BackupTrigger', 'RestoreHostBackupId') {
    ok "params: -$p exists" ($null -ne ($provAst.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq $p }))
}
function Write-Step($m) { }
function Write-Ok($m) { $script:okLines += $m }

# Argument checks, before anything connects.
$Action = 'backup-restore'; $BackupFile = ''; $BackupId = ''; $BackupKeyFile = ''; $RestoreHostBackupId = ''; $RestoreDir = ''
$ServiceUrl = ''; $InstanceName = ''
ok 'args: backup-restore needs a file or an id' ((Get-ThrowMessage { Assert-BackupArguments }) -match 'either -BackupFile')
$BackupId = $env:FAKE_ID
ok 'args: a host backup id needs the remote instance' ((Get-ThrowMessage { Assert-BackupArguments }) -match 'instance')
$ServiceUrl = 'https://host.example.com:7462'; $InstanceName = 'work-vm'
ok 'args: ...and passes with it' ((Get-ThrowMessage { Assert-BackupArguments }) -eq '')
$BackupFile = $saveTo
ok 'args: not both a file and an id' ((Get-ThrowMessage { Assert-BackupArguments }) -match 'either -BackupFile')
$BackupFile = ''; $BackupId = 'NOT-AN-ID'
ok 'args: a malformed id is refused' ((Get-ThrowMessage { Assert-BackupArguments }) -match 'not a backup id')
$Action = 'backup-save'; $BackupId = ''
ok 'args: backup-save needs -BackupFile' ((Get-ThrowMessage { Assert-BackupArguments }) -match 'needs -BackupFile')
$Action = 'backup-key-import'; $BackupKeyFile = Join-Path $tmp 'missing.cbkey'
ok 'args: an import file must exist' ((Get-ThrowMessage { Assert-BackupArguments }) -match 'Key file not found')
$Action = 'provision'; $RestoreHostBackupId = $env:FAKE_ID; $RestoreDir = 'C:\backup'
ok 'args: -RestoreDir and -RestoreHostBackupId exclude each other' ((Get-ThrowMessage { Assert-BackupArguments }) -match 'not both')
$RestoreDir = ''; $ServiceUrl = ''
ok 'args: -RestoreHostBackupId needs the host service' ((Get-ThrowMessage { Assert-BackupArguments }) -match 'host service')
$RestoreHostBackupId = ''; $ServiceUrl = 'https://host.example.com:7462'

# The connection is this run's ssh identity.
$script:SshPortArgs = @('-p', '2201'); $script:SshOpts = @('-i', 'key', '-o', 'BatchMode=yes')
$script:ConnectUser = 'construct'; $VmHost = 'vm.example.com'; $script:UseRootKey = $false; $SeedPassword = 'agent'
if (Get-Command ssh -CommandType Application -ErrorAction SilentlyContinue) {
    $c = Get-BackupConnection
    ok 'connection: port, options, user@host and the sudo mode of this run' (
        $c.Options[0] -eq '-p' -and $c.Options[1] -eq '2201' -and $c.Options -contains 'key' -and $c.Target -eq 'construct@vm.example.com' -and
        -not $c.Direct -and $c.SeedPassword -eq 'agent')
    $script:UseRootKey = $true; $script:ConnectUser = 'root'
    ok 'connection: the root fast path is direct' ((Get-BackupConnection).Direct)
} else { skip 'connection: Get-BackupConnection' 'no ssh client on this machine' }

# The SSH actions end to end, through the real lib against the fake guest.
function Get-BackupConnection { return $conn }
$env:LOCALAPPDATA = Join-Path $tmp 'localappdata'
$defaultKeyDir = Get-ConstructBackupKeyDir
New-Item -ItemType Directory -Path $defaultKeyDir -Force | Out-Null
Copy-Item (Join-Path $keyDir '*') $defaultKeyDir
$Action = 'backup-create'; $BackupTrigger = 'pre-reinstall'; $global:ConstructBackupCreateResult = $null
Invoke-BackupSshAction
ok 'action backup-create: the result is handed to the in-process caller' ($global:ConstructBackupCreateResult.Id -eq $env:FAKE_ID)
$global:ConstructBackupCreateResult = $null
$Action = 'backup-save'; $BackupFile = Join-Path $tmp 'panel/save.cbk'; $BackupId = ''
Invoke-BackupSshAction
ok 'action backup-save: the file is written' ((Get-FileSha $BackupFile) -eq (Get-FileSha (Join-Path $fakeDir 'container.cbk')))
$Action = 'backup-restore'; $BackupId = ''
Remove-Item (Join-Path $fakeDir 'restored.bin') -ErrorAction SilentlyContinue
Invoke-BackupSshAction
ok 'action backup-restore -BackupFile: restored' ((Get-FileSha (Join-Path $fakeDir 'restored.bin')) -eq (Get-FileSha $plain))
$BackupFile = ''; $BackupId = $env:FAKE_ID; $ServiceApiAuth = @{ Mode = 'token'; Token = 'x' }
function Get-ConstructHostBackupKeysJson { param($BaseUrl, $VmName, $BackupId, $Auth) $script:keysAsked = "$BaseUrl|$VmName|$BackupId|$($Auth.Mode)"; return $keysJson }
Remove-Item (Join-Path $fakeDir 'restored.bin') -ErrorAction SilentlyContinue
Invoke-BackupSshAction
ok 'action backup-restore -BackupId: keys from GET /vms/<name>/backups/<id>/keys, then restore --from-host' (
    $script:keysAsked -eq "https://host.example.com:7462|work-vm|$($env:FAKE_ID)|token" -and (Get-FileSha (Join-Path $fakeDir 'restored.bin')) -eq (Get-FileSha $plain))
Remove-Item function:Get-ConstructHostBackupKeysJson
$Action = 'backup-enroll'
Invoke-BackupSshAction
ok 'action backup-enroll: enrols' ((Get-Content (Join-Path $fakeDir 'construct-calls'))[-1] -like 'backup recipients add --label*')

# The key file actions, with the console prompt replaced.
Write-Host ''
Write-Host '=== Provision-AgentVM.ps1: key export and import ===' -ForegroundColor Cyan
$script:answers = New-Object System.Collections.Queue
function Read-BackupPassphrase { param($Prompt) $script:prompts++; return (ConvertTo-SecureString ([string]$script:answers.Dequeue()) -AsPlainText -Force) }
$script:prompts = 0
$Action = 'backup-key-export'; $BackupKeyFile = Join-Path $tmp 'export/pc.cbkey'
foreach ($a in 'short', 'long enough one', 'long enough two', 'long enough one', 'long enough one') { $script:answers.Enqueue($a) }
Invoke-BackupKeyFileAction
ok 'export: a short passphrase and a mismatch are asked again; the third try writes the file' ($script:prompts -eq 5 -and (Test-Path $BackupKeyFile))
ok 'export: exportedAt is set' ([bool](Get-ConstructBackupPcKey).ExportedAt)
$script:answers.Clear()
foreach ($a in 'x', 'y', 'z') { $script:answers.Enqueue($a) }
$Action = 'backup-key-export'; $BackupKeyFile = Join-Path $tmp 'export/never.cbkey'
ok 'export: no valid passphrase in three tries exports nothing' ((Get-ThrowMessage { Invoke-BackupKeyFileAction }) -match 'not exported' -and -not (Test-Path $BackupKeyFile))
$env:LOCALAPPDATA = Join-Path $tmp 'second-pc'
$Action = 'backup-key-import'; $BackupKeyFile = Join-Path $tmp 'export/pc.cbkey'
$script:answers.Clear(); $script:prompts = 0
foreach ($a in 'wrong', 'long enough one') { $script:answers.Enqueue($a) }
Invoke-BackupKeyFileAction
ok 'import: a wrong passphrase is asked again, then the key is installed' ($script:prompts -eq 2 -and (Get-ConstructBackupPcKey).Fingerprint -eq $key.Fingerprint)
$script:answers.Clear()
foreach ($a in 'a', 'b', 'c') { $script:answers.Enqueue($a) }
ok 'import: three wrong passphrases end the action' ((Get-ThrowMessage { Invoke-BackupKeyFileAction }) -match 'Wrong passphrase three times')
$env:LOCALAPPDATA = Join-Path $tmp 'third-pc'
$other = Get-ConstructBackupPcKey -Create
$realConfirm = ${function:Confirm-BackupKeyReplace}
function Confirm-BackupKeyReplace { param($Current, $NewFingerprint, $NewLabel) $script:confirmAsked = "$($Current.Fingerprint)|$NewFingerprint"; return $script:confirmAnswer }
$script:answers.Clear(); $script:answers.Enqueue('long enough one'); $script:confirmAnswer = $false
Invoke-BackupKeyFileAction
ok 'import: a different key is kept unless the console confirms' ((Get-ConstructBackupPcKey).Fingerprint -eq $other.Fingerprint -and
    $script:confirmAsked -eq "$($other.Fingerprint)|$($key.Fingerprint)")
$script:answers.Enqueue('long enough one'); $script:confirmAnswer = $true
Invoke-BackupKeyFileAction
ok 'import: confirmed, it replaces the key and keeps the old key file' ((Get-ConstructBackupPcKey).Fingerprint -eq $key.Fingerprint -and
    (Test-Path (Join-Path (Get-ConstructBackupKeyDir) "pc-key.$($other.Fingerprint).dpapi")))
${function:Confirm-BackupKeyReplace} = $realConfirm
if ([Console]::IsInputRedirected) {
    ok 'import: without a console the replacement is never confirmed' (-not (Confirm-BackupKeyReplace -Current $other -NewFingerprint $key.Fingerprint -NewLabel 'x' 3>$null))
}
$env:LOCALAPPDATA = Join-Path $tmp 'localappdata'

# ── (f) Provisioning: enrolment and the host-backup restore step ────────────
Write-Host ''
Write-Host '=== Provision-AgentVM.ps1: provisioning with a host backup ===' -ForegroundColor Cyan
$provText = $provAst.Extent.Text
$iCheck = $provText.IndexOf('} elseif ($script:ProvisionResult.ErrorCount -ne 0) {')
$iEnrol = $provText.IndexOf("Write-Step `"Enrolling this PC's backup key on the VM`"`n    try {")
$iRestore = $provText.IndexOf('# Restore a saved config onto the freshly provisioned VM')
ok 'enrol: provisioning enrols after provision.sh''s result and before the restore' ($iCheck -gt 0 -and $iEnrol -gt $iCheck -and $iRestore -gt $iEnrol)
$enrolIf = $provAst.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and
    $n.Clauses[0].Item1.Extent.Text -eq 'Get-Command Register-ConstructBackupPcKey -ErrorAction SilentlyContinue' }, $true)
ok 'enrol: the step exists' ($null -ne $enrolIf)
if ($enrolIf) {
    function Register-ConstructBackupPcKey { param($Connection) throw 'guest has no construct backup' }
    $warnings = @()
    & { . ([scriptblock]::Create($enrolIf.Extent.Text)) } 3>&1 | ForEach-Object { $warnings += [string]$_ }
    ok 'enrol: a failure is a warning, not a provisioning error' (($warnings -join ' ') -match 'Could not enrol' -and ($warnings -join ' ') -match 'backup-enroll')
    Remove-Item function:Register-ConstructBackupPcKey
}
ok 'defer: a host backup defers the project phase like a PC backup' ($provText -match '\$deferProjectCommands = \[bool\]\(\(\$RestoreDir -and \(Test-Path -LiteralPath \(Join-Path \$RestoreDir "backup.tar.gz"\)\)\) -or \$RestoreHostBackupId\)')
ok 'skip: backup actions neither pack nor fetch the repository' (
    $provText -match "if \(\`$Action -notin @\('export', 't3update'\) -and -not \`$script:IsBackupAction\) \{ \`$archivePath = New-RepoArchive \}" -and
    $provText -match "if \(\`$ServiceUrl -and \`$Action -ne 't3update' -and -not \`$script:IsBackupAction\) \{")
ok 'panel: a failed backup action keeps the console open and exits 1' (
    $provText -match '\(-not \$FromPanel\) -or \$global:ConstructProvisionHadErrors -or \$script:BackupActionFailed' -and
    $provText -match 'if \(\$script:BackupActionFailed -and -not \$Auto\) \{ exit 1 \}')

$restoreIf = $provAst.Find({ param($n) $n -is [System.Management.Automation.Language.IfStatementAst] -and
    $n.Clauses[0].Item1.Extent.Text -eq '$RestoreDir' -and $n.Extent.Text.Contains('Restoring saved agent config onto the VM') }, $true)
ok 'restore: the restore step has a host-backup branch' ($restoreIf -and $restoreIf.Clauses.Count -eq 2 -and $restoreIf.Clauses[1].Item1.Extent.Text -eq '$RestoreHostBackupId')
$restoreBlock = [scriptblock]::Create($restoreIf.Extent.Text)
$script:calls = New-Object System.Collections.Generic.List[string]
$script:cliExit = @{}
function Invoke-Ssh { param([switch]$Sudo, [string]$Command) $script:calls.Add("ssh: $Command"); return '' }
function Invoke-SshStream { param([switch]$Sudo, [switch]$PassThru, [switch]$NoThrow, [string]$Command) $script:calls.Add("stream: $Command"); return @{ ExitCode = $script:restoreConfigExit; Lines = @() } }
function Get-BackupConnection { return 'connection' }
function Get-ConstructHostBackupKeysJson { param($BaseUrl, $VmName, $BackupId, $Auth) $script:calls.Add("keys: $VmName $BackupId"); return 'KEYS' }
function Unprotect-ConstructBackupIdentity { param($KeysJson, $ExpectedBackupId, $KeyDir) $script:calls.Add("unwrap: $KeysJson $ExpectedBackupId"); return 'AGE-SECRET-KEY-1TESTIDENTITY' }
function Invoke-ConstructBackupCli {
    param($Connection, [string[]]$Arguments, [string]$FirstLine, $Stdout)
    $script:calls.Add("cli: $($Arguments -join ' ') | stdin=$FirstLine")
    $exit = $script:cliExit[$Arguments[0]]
    if ($null -eq $exit) { $exit = 0 }
    return [pscustomobject]@{ ExitCode = $exit }
}
function Invoke-DeferredProjectCommands { $script:calls.Add('deferred project commands') }
$RestoreDir = ''; $RestoreHostBackupId = $env:FAKE_ID; $InstanceName = 'work-vm'; $deferProjectCommands = $true; $constructVersion = 'abc1234'
$script:restoreConfigExit = 0
. $restoreBlock
$seq = @($script:calls)
$fetchAt = [array]::FindIndex([string[]]$seq, [Predicate[string]]{ param($x) $x -like 'cli: fetch *' })
ok 'host restore: keys from the host, unwrapped for this backup id' ($seq[0] -eq "keys: work-vm $($env:FAKE_ID)" -and $seq[1] -eq "unwrap: KEYS $($env:FAKE_ID)")
ok 'host restore: fetch --from-host <id> --out /tmp/construct-config-restore.tar.zst with E on stdin' (
    $fetchAt -gt 0 -and $seq[$fetchAt] -ceq "cli: fetch --from-host $($env:FAKE_ID) --out /tmp/construct-config-restore.tar.zst | stdin=AGE-SECRET-KEY-1TESTIDENTITY")
ok 'host restore: then restore-config.sh on that file, logged like today' ($seq[$fetchAt + 1] -like 'stream: *BACKUP_TGZ=/tmp/construct-config-restore.tar.zst*restore-config.sh*tee /var/log/construct/restore-config.log')
ok 'host restore: then the extras on the file, the deferred project phase and restored <id>' (
    $seq[$fetchAt + 2] -ceq 'cli: restore-extras /tmp/construct-config-restore.tar.zst | stdin=' -and
    $seq[$fetchAt + 3] -eq 'deferred project commands' -and
    $seq[$fetchAt + 4] -ceq "cli: restored $($env:FAKE_ID) | stdin=")
ok 'host restore: the cleartext file is deleted at the end' ($seq[$seq.Count - 1] -like 'ssh: rm -f /tmp/construct-config-restore.tar.zst*')
ok 'host restore: E is only ever stdin' (@($seq | Where-Object { $_ -like '*AGE-SECRET-KEY*' -and $_ -notlike 'cli: fetch *' }).Count -eq 0)

$script:calls.Clear(); $script:cliExit = @{ fetch = 8 }
$msg = Get-ThrowMessage { . $restoreBlock }
ok 'host restore: a failed fetch fails the restore, naming the retry action' ($msg -match 'decryption' -and $msg -match "backup-restore -InstanceName work-vm -BackupId $($env:FAKE_ID)")
ok 'host restore: ...and nothing runs after it' (@($script:calls | Where-Object { $_ -like 'stream:*' -or $_ -like 'deferred*' }).Count -eq 0)
$script:calls.Clear(); $script:cliExit = @{}; $script:restoreConfigExit = 4
$msg = Get-ThrowMessage { . $restoreBlock }
ok 'host restore: a failed restore-config.sh keeps the archive like today' ($msg -match 'retained archive: /var/lib/construct/construct-config-restore.failed.tar.zst' -and
    @($script:calls | Where-Object { $_ -like 'ssh: chmod 600 /tmp/construct-config-restore.tar.zst; mkdir -p /var/lib/construct; mv -f *' }).Count -eq 1)
ok 'host restore: ...and does not delete it' (@($script:calls | Where-Object { $_ -like 'ssh: rm -f /tmp/construct-config-restore.tar.zst /var/lib*' }).Count -eq 0)
$script:calls.Clear(); $script:cliExit = @{ 'restore-extras' = 10; restored = 7 }; $script:restoreConfigExit = 0
$warnings = @()
& { . $restoreBlock } 3>&1 | ForEach-Object { $warnings += [string]$_ }
ok 'host restore: failed extras or restored are warnings, the restore stands' (($warnings -join ' ') -match 'serve-web token' -and ($warnings -join ' ') -match 'stays pinned' -and
    @($script:calls | Where-Object { $_ -like 'ssh: rm -f /tmp/construct-config-restore.tar.zst /var/lib/*' }).Count -eq 1)
function Unprotect-ConstructBackupIdentity { param($KeysJson, $ExpectedBackupId, $KeyDir) throw 'Backup x was not made for this PC''s backup key.' }
$script:calls.Clear()
$msg = Get-ThrowMessage { . $restoreBlock }
ok 'host restore: a backup this PC cannot open fails before the VM is touched' ($msg -match 'not made for this PC' -and $msg -match 'backup-restore' -and
    @($script:calls | Where-Object { $_ -like 'cli:*' -or $_ -like 'stream:*' }).Count -eq 0)
}
} finally {
    $env:LOCALAPPDATA = $savedLocalAppData
    foreach ($v in 'FAKE_SSH_LOG', 'FAKE_CONSTRUCT', 'FAKE_DIR', 'FAKE_ID', 'FAKE_EXIT', 'FAKE_SUDO_PASSWORD') { Remove-Item "Env:$v" -ErrorAction SilentlyContinue }
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "  $($script:pass) passed, $($script:fail) failed, $($script:skip) skipped" -ForegroundColor $(if ($script:fail -gt 0) { 'Red' } else { 'Green' })
if ($script:fail -gt 0) { exit 1 }
exit 0
