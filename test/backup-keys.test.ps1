#Requires -Version 5.1
<#
    The PC side of the encrypted config backups (lib/AgentVm.Backup.ps1): the PC key, its
    SubjectPublicKeyInfo and fingerprint, unwrapping a backup's identity, the key export
    file. Run:

        pwsh -NoProfile -File test/backup-keys.test.ps1

    Needs openssl and age on PATH (the guest's tools, used here as the other side of the
    contract). DPAPI does not exist off Windows, so the protection seam
    (Protect-/Unprotect-ConstructTokenBytes) is replaced by a reversible stand-in; under
    Windows PowerShell 5.1 the real DPAPI runs instead (test/run-local-windows-checks.ps1).
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
function Get-ThrowType([scriptblock]$Script) {
    try { & $Script | Out-Null; return $null } catch { return $_.Exception.GetType().FullName }
}

$errors = $null
[void][System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'lib/AgentVm.Backup.ps1'), [ref]$null, [ref]$errors)
ok 'parse: lib/AgentVm.Backup.ps1 has zero errors' ($errors.Count -eq 0)

. (Join-Path $repoRoot 'lib/AgentVm.Backup.ps1')
ok 'load: the backup lib brings lib/AgentVm.Remote.ps1 along' ([bool](Get-Command Invoke-ConstructApi -ErrorAction SilentlyContinue))

$isDesktop = $PSVersionTable.PSEdition -eq 'Desktop'
$realDpapi = $false
if ($isDesktop -and (Test-ConstructDpapiAvailable)) { $realDpapi = $true }
if (-not $realDpapi) {
    # The stand-in keeps the file format (protected bytes, not the XML) testable off Windows.
    function Protect-ConstructTokenBytes { param([byte[]]$Bytes) return ,([byte[]]@($Bytes | ForEach-Object { $_ -bxor 0x5A })) }
    function Unprotect-ConstructTokenBytes { param([byte[]]$Bytes) return ,([byte[]]@($Bytes | ForEach-Object { $_ -bxor 0x5A })) }
}

$tools = @{}
foreach ($t in 'openssl', 'age', 'age-keygen', 'bash') { $tools[$t] = [bool](Get-Command $t -ErrorAction SilentlyContinue) }
# The openssl comparisons drive openssl through bash (pipes of binary DER).
$tools.openssl = $tools.openssl -and $tools.bash
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ('construct-backup-keys-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

function New-WrappedIdentity {
    <# A fresh age identity, wrapped by openssl exactly as the guest wraps it (contract 3.2). #>
    param([string]$PublicPem)
    $keyFile = Join-Path $tmp ('age-' + [guid]::NewGuid().ToString('N'))
    & age-keygen -o $keyFile 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'age-keygen failed' }
    $identity = (Get-Content -LiteralPath $keyFile | Where-Object { $_ -like 'AGE-SECRET-KEY-1*' } | Select-Object -First 1).Trim()
    $recipient = (& age-keygen -y $keyFile).Trim()
    $pemFile = Join-Path $tmp ('pub-' + [guid]::NewGuid().ToString('N') + '.pem')
    [IO.File]::WriteAllText($pemFile, $PublicPem)
    $env:CONSTRUCT_TEST_IDENTITY = $identity
    try {
        $wrapped = (& bash -c 'printf %s "$CONSTRUCT_TEST_IDENTITY" | openssl pkeyutl -encrypt -pubin -inkey "$1" -pkeyopt rsa_padding_mode:oaep -pkeyopt rsa_oaep_md:sha256 -pkeyopt rsa_mgf1_md:sha256 | base64 -w0' _ $pemFile).Trim()
    } finally { Remove-Item Env:CONSTRUCT_TEST_IDENTITY }
    if ($LASTEXITCODE -ne 0 -or -not $wrapped) { throw 'openssl pkeyutl failed' }
    Remove-Item -LiteralPath $keyFile, $pemFile -Force
    return [pscustomobject]@{ Identity = $identity; Recipient = $recipient; Wrapped = $wrapped }
}

function New-KeysJson {
    param([string]$BackupId, [object[]]$Recipients)
    $doc = [ordered]@{ format = 'construct-backup-keys/1'; backupId = $BackupId; createdAt = '2026-11-08T10:00:00Z'; recipients = @($Recipients) }
    return ($doc | ConvertTo-Json -Depth 5 -Compress)
}

try {
    # ── The label and the small helpers ─────────────────────────────────────
    Write-Host ''
    Write-Host '=== Labels and helpers ===' -ForegroundColor Cyan
    ok 'label: a plain computer name is kept' ((ConvertTo-ConstructBackupLabel 'DESKTOP-01') -ceq 'DESKTOP-01')
    ok 'label: characters outside the pattern become -' ((ConvertTo-ConstructBackupLabel "my/pc's*box") -ceq 'my-pc-s-box')
    ok 'label: capped at 64 characters' ((ConvertTo-ConstructBackupLabel ('x' * 80)).Length -eq 64)
    ok 'label: never empty' ((ConvertTo-ConstructBackupLabel '   ') -match '^[A-Za-z0-9._ -]{1,64}$')
    ok 'label: the default matches the contract pattern' ((ConvertTo-ConstructBackupLabel) -cmatch '^[A-Za-z0-9._ -]{1,64}$')
    ok 'bytes: equal arrays compare equal' (Test-ConstructBytesEqual ([byte[]](1, 2, 3)) ([byte[]](1, 2, 3)))
    ok 'bytes: a difference in the last byte is found' (-not (Test-ConstructBytesEqual ([byte[]](1, 2, 3)) ([byte[]](1, 2, 4))))
    ok 'bytes: different lengths are unequal' (-not (Test-ConstructBytesEqual ([byte[]](1, 2)) ([byte[]](1, 2, 3))))
    ok 'der: short length' ((ConvertTo-ConstructHex (Get-ConstructDerLength 5)) -eq '05')
    ok 'der: two-byte length' ((ConvertTo-ConstructHex (Get-ConstructDerLength 300)) -eq '82012c')
    ok 'der: integer with the high bit set gets a 00 pad' ((ConvertTo-ConstructHex (ConvertTo-ConstructDerInteger ([byte[]](0x80, 0x01)))) -eq '0203008001')
    ok 'der: leading zero bytes are stripped' ((ConvertTo-ConstructHex (ConvertTo-ConstructDerInteger ([byte[]](0, 0, 5)))) -eq '020105')
    ok 'der: the exponent 65537' ((ConvertTo-ConstructHex (ConvertTo-ConstructDerInteger ([byte[]](1, 0, 1)))) -eq '0203010001')
    ok 'id: 32 lowercase hex is a backup id' (Test-ConstructBackupId ('a' * 32))
    ok 'id: uppercase is not' (-not (Test-ConstructBackupId ('A' * 32)))
    ok 'id: a path is not' (-not (Test-ConstructBackupId '../../etc/passwd'))

    # ── Key creation ────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '=== The PC key ===' -ForegroundColor Cyan
    $pcA = Join-Path $tmp 'pc-a'
    ok 'key: no key, no -Create -> $null' ($null -eq (Get-ConstructBackupPcKey -KeyDir $pcA))
    $keyA = Get-ConstructBackupPcKey -KeyDir $pcA -Create -Label 'Desk PC'
    ok 'key: created with a 64-hex fingerprint' ($keyA.Fingerprint -cmatch '^[0-9a-f]{64}$')
    ok 'key: the label is kept' ($keyA.Label -ceq 'Desk PC')
    ok 'key: exportedAt starts null' (-not $keyA.ExportedAt)
    foreach ($f in 'pc-key.dpapi', 'pc-key.pub.pem', 'pc-key.json') {
        ok "key: $f exists" (Test-Path -LiteralPath (Join-Path $pcA $f))
    }
    $privateBytes = [IO.File]::ReadAllBytes((Join-Path $pcA 'pc-key.dpapi'))
    ok 'key: pc-key.dpapi holds protected bytes, never the XML' (-not ([Text.Encoding]::UTF8.GetString($privateBytes)).Contains('<RSAKeyValue>'))
    $info = Get-Content -LiteralPath (Join-Path $pcA 'pc-key.json') -Raw
    ok 'key: pc-key.json has fingerprint, label, createdAt and exportedAt' (
        $info -match '"fingerprint":"[0-9a-f]{64}"' -and $info -match '"label":"Desk PC"' -and $info -match '"createdAt":"\d{4}-\d\d-\d\dT' -and $info -match '"exportedAt":null')
    $again = Get-ConstructBackupPcKey -KeyDir $pcA -Create
    ok 'key: asking again returns the same key (no regeneration)' ($again.Fingerprint -eq $keyA.Fingerprint)
    $rsaA = Open-ConstructBackupRsaKey -Path (Join-Path $pcA 'pc-key.dpapi')
    ok 'key: 3072 bits' ($rsaA.KeySize -eq 3072)
    ok 'key: OAEP-SHA256 round trip on this runtime' (
        [Text.Encoding]::ASCII.GetString($rsaA.Decrypt($rsaA.Encrypt([Text.Encoding]::ASCII.GetBytes('probe'), [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256), [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)) -eq 'probe')

    # ── SubjectPublicKeyInfo, PEM, fingerprint ──────────────────────────────
    Write-Host ''
    Write-Host '=== SubjectPublicKeyInfo and fingerprint ===' -ForegroundColor Cyan
    $der = Get-ConstructBackupSpkiDer -Rsa $rsaA
    if ($rsaA | Get-Member -Name ExportSubjectPublicKeyInfo) {
        ok 'spki: the hand-built DER equals .NET''s ExportSubjectPublicKeyInfo' ([Convert]::ToBase64String($der) -ceq [Convert]::ToBase64String($rsaA.ExportSubjectPublicKeyInfo()))
    } else { skip 'spki: compare with ExportSubjectPublicKeyInfo' 'this runtime has no ExportSubjectPublicKeyInfo (.NET Framework)' }
    if ($tools.openssl) {
        $pemFile = Join-Path $pcA 'pc-key.pub.pem'
        $osslFp = (& bash -c 'openssl pkey -pubin -in "$1" -outform DER | sha256sum | cut -d" " -f1' _ $pemFile).Trim()
        ok 'fingerprint: equals openssl pkey -outform DER | sha256sum' ($osslFp -ceq $keyA.Fingerprint)
        if ($rsaA | Get-Member -Name ExportPkcs8PrivateKey) {
            $privPem = Join-Path $tmp 'a-private.pem'
            [IO.File]::WriteAllText($privPem, (ConvertTo-ConstructPem -Der $rsaA.ExportPkcs8PrivateKey() -Label 'PRIVATE KEY'))
            $osslPub = (& openssl pkey -in $privPem -pubout | Out-String) -replace "`r", ''
            Remove-Item -LiteralPath $privPem -Force
            ok 'pem: pc-key.pub.pem is byte-identical to openssl''s -pubout' ([IO.File]::ReadAllText($pemFile) -ceq $osslPub)
        } else { skip 'pem: compare with openssl -pubout' 'this runtime cannot export PKCS#8 (.NET Framework)' }
        # The other direction: a key openssl generated, read into .NET, gets openssl's SPKI.
        $osslKey = Join-Path $tmp 'ossl.pem'
        & openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out $osslKey 2>$null
        $rsaO = [Security.Cryptography.RSA]::Create()
        if ($rsaO | Get-Member -Name ImportFromPem) {
            $rsaO.ImportFromPem([IO.File]::ReadAllText($osslKey))
            $osslPub = (& openssl pkey -in $osslKey -pubout | Out-String) -replace "`r", ''
            ok 'pem: an openssl-generated key yields openssl''s own public PEM' ((ConvertTo-ConstructPem -Der (Get-ConstructBackupSpkiDer -Rsa $rsaO)) -ceq $osslPub)
            $osslFp2 = (& bash -c 'openssl pkey -in "$1" -pubout -outform DER | sha256sum | cut -d" " -f1' _ $osslKey).Trim()
            ok 'fingerprint: ...and openssl''s fingerprint' ((Get-ConstructBackupFingerprint -Rsa $rsaO) -ceq $osslFp2)
        } else { skip 'pem: openssl-generated key' 'this runtime has no ImportFromPem' }
        $rsaO.Dispose()
    } else { skip 'spki: openssl comparisons' 'openssl is not installed' }
    ok 'pem: round trip through ConvertFrom-ConstructPem' ([Convert]::ToBase64String((ConvertFrom-ConstructPem $keyA.PublicKeyPem)) -ceq [Convert]::ToBase64String($der))
    $rsaA.Dispose()

    # Self-repair: the private key is the one authoritative file.
    Remove-Item -LiteralPath (Join-Path $pcA 'pc-key.pub.pem') -Force
    ok 'repair: a missing public key is rebuilt from the private key' ((Get-ConstructBackupPcKey -KeyDir $pcA).Fingerprint -eq $keyA.Fingerprint -and (Test-Path (Join-Path $pcA 'pc-key.pub.pem')))
    [IO.File]::WriteAllText((Join-Path $pcA 'pc-key.pub.pem'), (ConvertTo-ConstructPem -Der (Get-ConstructBackupSpkiDer -Rsa ([Security.Cryptography.RSA]::Create(2048)))))
    $repaired = Get-ConstructBackupPcKey -KeyDir $pcA
    ok 'repair: a public key that is not the private key''s is replaced' ($repaired.Fingerprint -eq $keyA.Fingerprint -and
        (Get-ConstructBackupFingerprint -SpkiDer (ConvertFrom-ConstructPem $repaired.PublicKeyPem)) -eq $keyA.Fingerprint)
    ok 'repair: ...keeping the label' ($repaired.Label -ceq 'Desk PC')

    # ── Unwrapping an identity the guest wrapped ────────────────────────────
    Write-Host ''
    Write-Host '=== Unwrap (openssl pkeyutl OAEP-SHA256 / MGF1-SHA256) ===' -ForegroundColor Cyan
    if ($tools.openssl -and $tools['age-keygen'] -and $tools.age) {
        $w = New-WrappedIdentity -PublicPem $keyA.PublicKeyPem
        $foreign = New-WrappedIdentity -PublicPem (ConvertTo-ConstructPem -Der (Get-ConstructBackupSpkiDer -Rsa ([Security.Cryptography.RSA]::Create(3072))))
        $id1 = 'b' * 32
        $keysJson = New-KeysJson -BackupId $id1 -Recipients @(
            [ordered]@{ fingerprint = ('c' * 64); label = 'Other PC'; wrapped = $foreign.Wrapped },
            [ordered]@{ fingerprint = $keyA.Fingerprint; label = 'Desk PC'; wrapped = $w.Wrapped })
        $unwrapped = Unprotect-ConstructBackupIdentity -KeysJson $keysJson -KeyDir $pcA
        ok 'unwrap: the identity openssl wrapped comes back' ($unwrapped -ceq $w.Identity)
        ok 'unwrap: ...matched by fingerprint, skipping the other recipient' ($unwrapped -cmatch '^AGE-SECRET-KEY-1[0-9A-Z]+$')
        $plainFile = Join-Path $tmp 'plain.txt'; $encFile = Join-Path $tmp 'plain.age'
        [IO.File]::WriteAllText($plainFile, 'payload')
        & age -r $w.Recipient -o $encFile $plainFile
        $env:CONSTRUCT_TEST_IDENTITY = $unwrapped
        try { $dec = (& bash -c 'age -d -i <(printf "%s\n" "$CONSTRUCT_TEST_IDENTITY") "$1"' _ $encFile) } finally { Remove-Item Env:CONSTRUCT_TEST_IDENTITY }
        ok 'unwrap: ...and it decrypts an age payload (the VM''s age -d -i <(printf ...))' ($dec -ceq 'payload')
        ok 'unwrap: the expected backup id is checked' ((Get-ThrowMessage { Unprotect-ConstructBackupIdentity -KeysJson $keysJson -KeyDir $pcA -ExpectedBackupId ('d' * 32) }) -match 'belongs to backup')
        $notMine = New-KeysJson -BackupId $id1 -Recipients @([ordered]@{ fingerprint = ('c' * 64); label = 'Other PC'; wrapped = $foreign.Wrapped })
        $msg = Get-ThrowMessage { Unprotect-ConstructBackupIdentity -KeysJson $notMine -KeyDir $pcA }
        ok 'unwrap: a backup not made for this PC is refused, naming who can open it' ($msg -match 'was not made for this PC' -and $msg -match 'Other PC')
        ok 'unwrap: ...and naming the import action' ($msg -match 'backup-key-import')
        ok 'unwrap: a PC without any key says so' ((Get-ThrowMessage { Unprotect-ConstructBackupIdentity -KeysJson $keysJson -KeyDir (Join-Path $tmp 'empty') }) -match 'has no backup key')
        ok 'keys: an unknown format is refused' ((Get-ThrowMessage { ConvertFrom-ConstructBackupKeysJson -Json '{"format":"x","backupId":"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb","recipients":[]}' }) -match 'unknown format')
        ok 'keys: a bad backup id is refused' ((Get-ThrowMessage { ConvertFrom-ConstructBackupKeysJson -Json '{"format":"construct-backup-keys/1","backupId":"../x","recipients":[]}' }) -match 'no valid backup id')
        ok 'keys: no JSON is refused' ((Get-ThrowMessage { ConvertFrom-ConstructBackupKeysJson -Json 'not json' }) -match 'not valid JSON')
        ok 'keys: readable by this PC (no decryption)' (Test-ConstructBackupKeysReadable -Keys $keysJson -KeyDir $pcA)
        ok 'keys: ...and not when only another PC is named' (-not (Test-ConstructBackupKeysReadable -Keys $notMine -KeyDir $pcA))
    } else {
        skip 'unwrap: openssl-wrapped identity' 'openssl, age and age-keygen are needed'
    }

    # ── Export / import ─────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '=== Key export and import ===' -ForegroundColor Cyan
    $passphrase = ConvertTo-SecureString 'correct horse battery' -AsPlainText -Force
    $wrong = ConvertTo-SecureString 'correct horse battery!' -AsPlainText -Force
    $exportFile = Join-Path $tmp 'export/pc-key.cbkey'
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $exported = Export-ConstructBackupKey -Path $exportFile -Passphrase $passphrase -KeyDir $pcA
    Write-Host "        (export took $($sw.ElapsedMilliseconds) ms)" -ForegroundColor DarkGray
    $doc = Get-Content -LiteralPath $exportFile -Raw | ConvertFrom-Json
    ok 'export: format, kdf and 600000 iterations' ($doc.format -ceq 'construct-backup-key/1' -and $doc.kdf -ceq 'pbkdf2-sha256' -and [int]$doc.iterations -eq 600000)
    ok 'export: salt, iv, ciphertext and a 32-byte mac' (
        ([Convert]::FromBase64String($doc.salt)).Length -ge 16 -and ([Convert]::FromBase64String($doc.iv)).Length -eq 16 -and
        ([Convert]::FromBase64String($doc.ciphertext)).Length -gt 1000 -and ([Convert]::FromBase64String($doc.mac)).Length -eq 32)
    ok 'export: fingerprint and label of the key' ($doc.fingerprint -ceq $keyA.Fingerprint -and $doc.label -ceq 'Desk PC')
    ok 'export: no plaintext key in the file' (-not (Get-Content -LiteralPath $exportFile -Raw).Contains('RSAKeyValue'))
    ok 'export: exportedAt is recorded' ([bool](Get-ConstructBackupPcKey -KeyDir $pcA).ExportedAt)
    ok 'export: an empty passphrase is refused' ((Get-ThrowMessage { Export-ConstructBackupKey -Path (Join-Path $tmp 'x') -Passphrase (New-Object Security.SecureString) -KeyDir $pcA }) -match 'empty passphrase')

    $pcB = Join-Path $tmp 'pc-b'
    $type = Get-ThrowType { Import-ConstructBackupKey -Path $exportFile -Passphrase $wrong -KeyDir $pcB }
    ok 'import: a wrong passphrase is refused (CryptographicException)' ($type -eq 'System.Security.Cryptography.CryptographicException')
    ok 'import: ...with the wording the console retries on' ((Get-ThrowMessage { Import-ConstructBackupKey -Path $exportFile -Passphrase $wrong -KeyDir $pcB }) -like 'Wrong passphrase*')
    ok 'import: ...and nothing is installed' (-not (Test-Path -LiteralPath (Join-Path $pcB 'pc-key.dpapi')))
    $tampered = Join-Path $tmp 'tampered.cbkey'
    $ct = [Convert]::FromBase64String($doc.ciphertext); $ct[10] = $ct[10] -bxor 1
    $docT = Get-Content -LiteralPath $exportFile -Raw | ConvertFrom-Json
    $docT.ciphertext = [Convert]::ToBase64String($ct)
    [IO.File]::WriteAllText($tampered, ($docT | ConvertTo-Json))
    ok 'import: a changed ciphertext fails the MAC with the right passphrase' ((Get-ThrowMessage { Import-ConstructBackupKey -Path $tampered -Passphrase $passphrase -KeyDir $pcB }) -like 'Wrong passphrase*')
    $docT = Get-Content -LiteralPath $exportFile -Raw | ConvertFrom-Json
    $docT.iterations = 1000
    [IO.File]::WriteAllText($tampered, ($docT | ConvertTo-Json))
    ok 'import: a weakened iteration count is refused' ((Get-ThrowMessage { Import-ConstructBackupKey -Path $tampered -Passphrase $passphrase -KeyDir $pcB }) -match 'iteration count')

    $imported = Import-ConstructBackupKey -Path $exportFile -Passphrase $passphrase -KeyDir $pcB
    ok 'import: a PC without a key installs it' ($imported.Result -eq 'installed' -and $imported.Fingerprint -eq $keyA.Fingerprint)
    $keyB = Get-ConstructBackupPcKey -KeyDir $pcB
    ok 'import: the second PC now has the same key, label and an exportedAt' ($keyB.Fingerprint -eq $keyA.Fingerprint -and $keyB.Label -ceq 'Desk PC' -and $keyB.ExportedAt)
    ok 'import: the public key file is regenerated' ((Get-ConstructBackupFingerprint -SpkiDer (ConvertFrom-ConstructPem $keyB.PublicKeyPem)) -eq $keyA.Fingerprint)
    if ($w) { ok 'import: the imported key unwraps the same backup' ((Unprotect-ConstructBackupIdentity -KeysJson $keysJson -KeyDir $pcB) -ceq $w.Identity) }
    ok 'import: the same key again changes nothing' ((Import-ConstructBackupKey -Path $exportFile -Passphrase $passphrase -KeyDir $pcB).Result -eq 'unchanged')

    # ── Replacing a key keeps the old one ───────────────────────────────────
    Write-Host ''
    Write-Host '=== Old keys are kept and tried ===' -ForegroundColor Cyan
    $pcC = Join-Path $tmp 'pc-c'
    $keyC = Get-ConstructBackupPcKey -KeyDir $pcC -Create -Label 'Laptop'
    $wC = $null
    if ($tools.openssl -and $tools['age-keygen']) { $wC = New-WrappedIdentity -PublicPem $keyC.PublicKeyPem }
    $script:asked = 0
    $cancel = Import-ConstructBackupKey -Path $exportFile -Passphrase $passphrase -KeyDir $pcC -ConfirmReplace { param($c, $f, $l) $script:asked++; $script:askedAbout = "$($c.Fingerprint)|$f|$l"; $false }
    ok 'replace: a different key on this PC is only replaced after confirmation' ($cancel.Result -eq 'cancelled' -and $script:asked -eq 1)
    ok 'replace: the confirmation is told the current key and the new one' ($script:askedAbout -eq "$($keyC.Fingerprint)|$($keyA.Fingerprint)|Desk PC")
    ok 'replace: ...declining keeps the current key' ((Get-ConstructBackupPcKey -KeyDir $pcC).Fingerprint -eq $keyC.Fingerprint)
    ok 'replace: no confirmation callback means no replacement' ((Import-ConstructBackupKey -Path $exportFile -Passphrase $passphrase -KeyDir $pcC).Result -eq 'cancelled')
    $replace = Import-ConstructBackupKey -Path $exportFile -Passphrase $passphrase -KeyDir $pcC -ConfirmReplace { $true }
    ok 'replace: confirmed, the imported key becomes current' ($replace.Result -eq 'replaced' -and (Get-ConstructBackupPcKey -KeyDir $pcC).Fingerprint -eq $keyA.Fingerprint)
    ok 'replace: ...and reports the previous key' ($replace.PreviousFingerprint -eq $keyC.Fingerprint)
    ok 'replace: the old key stays as pc-key.<fingerprint>.dpapi' (Test-Path -LiteralPath (Join-Path $pcC "pc-key.$($keyC.Fingerprint).dpapi"))
    $files = @(Get-ConstructBackupPrivateKeyFiles -KeyDir $pcC)
    ok 'replace: both keys are listed, the current one first' ($files.Count -eq 2 -and $files[0].Current -and $files[0].Fingerprint -eq $keyA.Fingerprint -and $files[1].Fingerprint -eq $keyC.Fingerprint)
    if ($wC) {
        $oldKeys = New-KeysJson -BackupId ('e' * 32) -Recipients @([ordered]@{ fingerprint = $keyC.Fingerprint; label = 'Laptop'; wrapped = $wC.Wrapped })
        ok 'replace: a backup made for the OLD key still unwraps (every key is tried)' ((Unprotect-ConstructBackupIdentity -KeysJson $oldKeys -KeyDir $pcC) -ceq $wC.Identity)
        ok 'replace: ...and counts as readable' (Test-ConstructBackupKeysReadable -Keys $oldKeys -KeyDir $pcC)
    } else { skip 'replace: unwrap with the old key' 'openssl and age-keygen are needed' }
    # Importing the old key back swaps the two again; nothing is lost.
    $exportC = Join-Path $tmp 'c.cbkey'
    $null = Export-ConstructBackupKey -Path $exportC -Passphrase $passphrase -KeyDir $pcA   # pcA's key = current key of pcC
    $rsaOld = Open-ConstructBackupRsaKey -Path (Join-Path $pcC "pc-key.$($keyC.Fingerprint).dpapi")
    ok 'replace: the kept key file opens and holds the old key' ((Get-ConstructBackupFingerprint -Rsa $rsaOld) -eq $keyC.Fingerprint)
    $rsaOld.Dispose()

    # ── Containers ──────────────────────────────────────────────────────────
    Write-Host ''
    Write-Host '=== Container header ===' -ForegroundColor Cyan
    $keysLine = New-KeysJson -BackupId ('f' * 32) -Recipients @([ordered]@{ fingerprint = $keyA.Fingerprint; label = 'Desk PC'; wrapped = 'AAAA' })
    $payload = [byte[]](0..255) + [byte[]](10, 13, 0, 10)
    $cbk = Join-Path $tmp 'one.cbk'
    $head = [Text.Encoding]::UTF8.GetBytes("construct-backup/1`n$keysLine`n")
    [IO.File]::WriteAllBytes($cbk, (Join-ConstructBytes @($head, $payload)))
    $h = Read-ConstructBackupContainerHeader -Path $cbk
    ok 'container: the keys line is read' ($h.Keys.BackupId -eq ('f' * 32) -and $h.KeysJson -ceq $keysLine)
    ok 'container: the payload starts right after the second newline' ($h.PayloadOffset -eq $head.Length -and $h.Length -eq ($head.Length + $payload.Length))
    [IO.File]::WriteAllBytes($cbk, (Join-ConstructBytes @([Text.Encoding]::UTF8.GetBytes("construct-backup/2`n$keysLine`n"), $payload)))
    ok 'container: another magic is refused' ((Get-ThrowMessage { Read-ConstructBackupContainerHeader -Path $cbk }) -match 'not a Construct backup file')
    [IO.File]::WriteAllBytes($cbk, [Text.Encoding]::UTF8.GetBytes("construct-backup/1`n$keysLine"))
    ok 'container: a file without the keys line end is refused' ((Get-ThrowMessage { Read-ConstructBackupContainerHeader -Path $cbk }) -match 'not a complete')
    [IO.File]::WriteAllBytes($cbk, [Text.Encoding]::UTF8.GetBytes("construct-backup/1`n$keysLine`n"))
    ok 'container: a file without a payload is refused' ((Get-ThrowMessage { Read-ConstructBackupContainerHeader -Path $cbk }) -match 'no backup payload')

    # ── The create result and the git hosts ─────────────────────────────────
    Write-Host ''
    Write-Host '=== create output and git credential hosts ===' -ForegroundColor Cyan
    $created = ConvertFrom-ConstructBackupCreateOutput -Text ("noise from a profile`n" + '{"id":"' + ('a' * 32) + '","sizeBytes":1234,"sha256":"' + ('0' * 64) + '","trigger":"pre-reinstall","gitCredentialHosts":["github.com","git.example.com:8443"]}' + "`n")
    ok 'create: the id, size and trigger are read past noise' ($created.Id -eq ('a' * 32) -and $created.SizeBytes -eq 1234 -and $created.Trigger -eq 'pre-reinstall')
    ok 'create: the git credential hosts are read' (@($created.GitCredentialHosts).Count -eq 2 -and $created.GitCredentialHosts[1] -eq 'git.example.com:8443')
    $noHosts = ConvertFrom-ConstructBackupCreateOutput -Text ('{"id":"' + ('a' * 32) + '","sizeBytes":1,"sha256":"x","trigger":"manual","gitCredentialHosts":[]}')
    ok 'create: an empty host list stays an empty list (known: no credentials)' ($null -ne $noHosts.GitCredentialHosts -and @($noHosts.GitCredentialHosts).Count -eq 0)
    $unknown = ConvertFrom-ConstructBackupCreateOutput -Text ('{"id":"' + ('a' * 32) + '","sizeBytes":1,"sha256":"x","trigger":"manual"}')
    ok 'create: no host list is $null (unknown)' ($null -eq $unknown.GitCredentialHosts)
    ok 'create: output without an id is an error' ((Get-ThrowMessage { ConvertFrom-ConstructBackupCreateOutput -Text '{"error":"x"}' }) -match 'did not report a backup id')
    ok 'git: hosts that cover every repo' (Test-ConstructBackupGitHostsCover -Hosts @('github.com', 'git.example.com') -Urls @('https://github.com/o/r.git', 'https://git.example.com/x/y.git'))
    ok 'git: a repo host that is missing' (-not (Test-ConstructBackupGitHostsCover -Hosts @('github.com') -Urls @('https://github.com/o/r.git', 'https://git.example.com/x/y.git')))
    ok 'git: host:port entries match a URL with that port' (Test-ConstructBackupGitHostsCover -Hosts @('git.example.com:8443') -Urls @('https://git.example.com:8443/x.git'))
    ok 'git: a bare host matches a URL with a port too' (Test-ConstructBackupGitHostsCover -Hosts @('git.example.com') -Urls @('https://git.example.com:8443/x.git'))
    ok 'git: case does not matter' (Test-ConstructBackupGitHostsCover -Hosts @('GitHub.com') -Urls @('https://github.com/o/r.git'))
    ok 'git: ssh URLs need no stored credential' (Test-ConstructBackupGitHostsCover -Hosts @('github.com') -Urls @('git@example.com:o/r.git'))
    ok 'git: an empty store covers nothing' (-not (Test-ConstructBackupGitHostsCover -Hosts @() -Urls @()))
    ok 'git: $null (unknown) covers nothing' (-not (Test-ConstructBackupGitHostsCover -Hosts $null -Urls @('https://github.com/o/r.git')))
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host "  $($script:pass) passed, $($script:fail) failed, $($script:skip) skipped" -ForegroundColor $(if ($script:fail -gt 0) { 'Red' } else { 'Green' })
if ($script:fail -gt 0) { exit 1 }
exit 0
