#Requires -Version 5.1
<#
    The-Construct -- CLIENT SIDE OF THE ENCRYPTED CONFIG BACKUPS.

    The contract is docs/plans/host-config-backups.md (sections 3.2, 3.3 and 3.8); the
    user-facing description is docs/backup-restore.md. This file holds everything the PC
    does for a backup, so Provision-AgentVM.ps1 and Auto-Install.ps1 only glue it to
    their connection and their prompts:

      * THE PC KEY. An RSA-3072 key pair per Windows user, created on first need in
        %LOCALAPPDATA%\The-Construct\backup\:
            pc-key.dpapi             DPAPI (CurrentUser) protected bytes of the key's XML form
            pc-key.pub.pem           the SubjectPublicKeyInfo PEM, DER built by hand so it
                                     works on .NET Framework (no ExportSubjectPublicKeyInfo)
            pc-key.json              {fingerprint, label, createdAt, exportedAt}
            pc-key.<fp>.dpapi        a key this PC used before an import replaced it
        The private key never leaves the PC except in a passphrase-protected export file.
      * UNWRAP. A backup is encrypted on the VM with an ephemeral age identity E; E is
        wrapped (RSA-OAEP-SHA256) to every enrolled PC key. The PC unwraps E with any of
        its keys and hands it to the VM on SSH STDIN -- never on a command line, never in
        a log, never on a disk.
      * KEY EXPORT / IMPORT. PBKDF2-SHA256 (600000 iterations) -> AES-256-CBC +
        HMAC-SHA256, encrypt-then-MAC, constant-time MAC check.
      * BINARY-SAFE SSH. PowerShell pipelines re-encode native output, so a backup
        container never goes through one: ssh runs as a System.Diagnostics.Process whose
        StandardOutput/StandardInput BASE STREAMS are copied to or from a FileStream.
        stderr is inherited, so the guest's progress shows up live in the console.
      * THE HOST API calls the client makes itself (the keys file, the list).

    Windows PowerShell 5.1 (.NET Framework 4.7.2+) AND PowerShell 7. On 5.1 RSA.Create()
    is RSACryptoServiceProvider, which cannot do OAEP-SHA256, so RSACng is used there;
    PowerShell 7 uses RSA.Create(), because RSACng is Windows-only and the tests run on
    Linux. DPAPI is Windows-only: the protection goes through Protect-/Unprotect-
    ConstructTokenBytes (lib/AgentVm.Remote.ps1), the seam the tests replace.

    Dot-source it. Self-contained apart from lib/AgentVm.Remote.ps1, which it loads when
    that is not loaded yet. Deliberately no Set-StrictMode and no $ErrorActionPreference
    change: it is dot-sourced into the installers' script scope.
#>

if (-not (Get-Command Invoke-ConstructApi -ErrorAction SilentlyContinue)) {
    $constructBackupRemoteLib = Join-Path $PSScriptRoot 'AgentVm.Remote.ps1'
    if (Test-Path -LiteralPath $constructBackupRemoteLib) { . $constructBackupRemoteLib }
    Remove-Variable -Name constructBackupRemoteLib -ErrorAction SilentlyContinue
}

$script:ConstructBackupKeyBits        = 3072
$script:ConstructBackupKdfIterations  = 600000
$script:ConstructBackupLabelPattern   = '^[A-Za-z0-9._ -]{1,64}$'
$script:ConstructBackupIdPattern      = '^[0-9a-f]{32}$'
$script:ConstructBackupFingerprintPattern = '^[0-9a-f]{64}$'
$script:ConstructBackupIdentityPattern = '^AGE-SECRET-KEY-1[0-9A-Z]+$'
$script:ConstructBackupContainerMagic = 'construct-backup/1'
# A container's keys line is at most 64 KiB (contract 3.2); the header read allows a margin.
$script:ConstructBackupHeaderLimit    = 72 * 1024
# The guest's PATH for the backup commands: a non-login shell, so nothing a profile
# prints can end up in a container on stdout.
$script:ConstructBackupGuestPath      = '/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin'

# ── Small byte helpers ───────────────────────────────────────────────────────

function ConvertTo-ConstructHex {
    param([byte[]]$Bytes)
    $sb = New-Object System.Text.StringBuilder ($Bytes.Length * 2)
    foreach ($b in $Bytes) { [void]$sb.Append($b.ToString('x2')) }
    return $sb.ToString()
}

function Test-ConstructBytesEqual {
    <# Constant-time comparison (the MAC check must not leak where the first difference is). #>
    param([byte[]]$A, [byte[]]$B)
    if ($null -eq $A -or $null -eq $B -or $A.Length -ne $B.Length) { return $false }
    $diff = 0
    for ($i = 0; $i -lt $A.Length; $i++) { $diff = $diff -bor ($A[$i] -bxor $B[$i]) }
    return ($diff -eq 0)
}

function Join-ConstructBytes {
    param([object[]]$Parts)
    $total = 0
    foreach ($p in $Parts) { $total += ([byte[]]$p).Length }
    $out = New-Object byte[] $total
    $at = 0
    foreach ($p in $Parts) {
        $b = [byte[]]$p
        [Array]::Copy($b, 0, $out, $at, $b.Length)
        $at += $b.Length
    }
    return ,$out
}

function Get-ConstructByteRange {
    param([byte[]]$Bytes, [int]$Offset, [int]$Count)
    $out = New-Object byte[] $Count
    [Array]::Copy($Bytes, $Offset, $out, 0, $Count)
    return ,$out
}

function New-ConstructRandomBytes {
    param([int]$Count)
    $bytes = New-Object byte[] $Count
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return ,$bytes
}

function Get-ConstructSha256 {
    param([byte[]]$Bytes)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { return ,($sha.ComputeHash($Bytes)) } finally { $sha.Dispose() }
}

function Clear-ConstructBytes {
    param([byte[]]$Bytes)
    if ($Bytes) { [Array]::Clear($Bytes, 0, $Bytes.Length) }
}

function Write-ConstructFileAtomic {
    <# Write bytes to a temporary file beside -Path, then rename it over -Path. #>
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][byte[]]$Bytes)
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $tmp = "$Path.tmp-" + [guid]::NewGuid().ToString('N')
    try {
        [System.IO.File]::WriteAllBytes($tmp, $Bytes)
        Move-Item -LiteralPath $tmp -Destination $Path -Force
    } finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
    }
}

function ConvertFrom-ConstructSecureString {
    <# The UTF-8 bytes of a SecureString; the caller clears them. The intermediate char
       buffer and the unmanaged copy are zeroed here. #>
    param([Parameter(Mandatory)][System.Security.SecureString]$Value)
    $length = $Value.Length
    $chars = New-Object char[] $length
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToGlobalAllocUnicode($Value)
    try {
        if ($length -gt 0) { [Runtime.InteropServices.Marshal]::Copy($ptr, $chars, 0, $length) }
        return ,([System.Text.Encoding]::UTF8.GetBytes($chars))
    } finally {
        [Array]::Clear($chars, 0, $chars.Length)
        [Runtime.InteropServices.Marshal]::ZeroFreeGlobalAllocUnicode($ptr)
    }
}

function Test-ConstructSecureStringEqual {
    param([System.Security.SecureString]$A, [System.Security.SecureString]$B)
    $first = ConvertFrom-ConstructSecureString $A
    $second = ConvertFrom-ConstructSecureString $B
    try { return (Test-ConstructBytesEqual $first $second) } finally { Clear-ConstructBytes $first; Clear-ConstructBytes $second }
}

# ── Paths and labels ─────────────────────────────────────────────────────────

function Get-ConstructBackupKeyDir {
    <# %LOCALAPPDATA%\The-Construct\backup, resolved like Get-ConstructRemoteStoreDir. Pure. #>
    $base = $env:LOCALAPPDATA
    if (-not $base) { $base = $env:TEMP }
    if (-not $base) { $base = [System.IO.Path]::GetTempPath() }
    return (Join-Path (Join-Path $base 'The-Construct') 'backup')
}

function ConvertTo-ConstructBackupLabel {
    <# A recipient label matching ^[A-Za-z0-9._ -]{1,64}$; the computer name by default. #>
    param([string]$Value)
    if (-not $Value) { $Value = $env:COMPUTERNAME }
    if (-not $Value) { try { $Value = [Environment]::MachineName } catch { $Value = '' } }
    $label = ([regex]::Replace([string]$Value, '[^A-Za-z0-9._ -]', '-')).Trim()
    if ($label.Length -gt 64) { $label = $label.Substring(0, 64).Trim() }
    if (-not $label) { $label = 'pc' }
    return $label
}

function Test-ConstructBackupId {
    param([string]$Id)
    return [bool]($Id -cmatch $script:ConstructBackupIdPattern)
}

# ── RSA, SubjectPublicKeyInfo, fingerprint ───────────────────────────────────

function New-ConstructBackupRsaXml {
    <#
        The private XML form (ToXmlString($true)) of a new 3072-bit key. The XML is the
        stored form, so a key is exported exactly once, right where it is generated -- on
        .NET Framework by RSACryptoServiceProvider, whose generated keys are always
        exportable (an RSACng key's export depends on its CNG export policy).
    #>
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        $csp = New-Object System.Security.Cryptography.RSACryptoServiceProvider ($script:ConstructBackupKeyBits)
        try {
            $csp.PersistKeyInCsp = $false
            return $csp.ToXmlString($true)
        } finally { $csp.Dispose() }
    }
    $rsa = [System.Security.Cryptography.RSA]::Create($script:ConstructBackupKeyBits)
    try { return $rsa.ToXmlString($true) } finally { $rsa.Dispose() }
}

function New-ConstructBackupRsa {
    <# The key in -Xml as an RSA object that can do OAEP-SHA256 on this runtime: RSACng on
       .NET Framework (whose RSA.Create() is RSACryptoServiceProvider, without OAEP-SHA256),
       RSA.Create() on PowerShell 7 (RSACng is Windows-only). Only public exports and
       Decrypt are used on it. #>
    param([Parameter(Mandatory)][string]$Xml)
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        try { Add-Type -AssemblyName System.Core -ErrorAction SilentlyContinue } catch { }
        $rsa = [System.Security.Cryptography.RSACng]::new()
    } else {
        $rsa = [System.Security.Cryptography.RSA]::Create()
    }
    $rsa.FromXmlString($Xml)
    return $rsa
}

function Get-ConstructDerLength {
    param([int]$Length)
    if ($Length -lt 0x80) { return ,([byte[]]@([byte]$Length)) }
    $bytes = New-Object System.Collections.Generic.List[byte]
    $n = $Length
    while ($n -gt 0) { $bytes.Insert(0, [byte]($n -band 0xff)); $n = $n -shr 8 }
    $bytes.Insert(0, [byte](0x80 -bor $bytes.Count))
    return ,($bytes.ToArray())
}

function New-ConstructDerElement {
    param([byte]$Tag, [byte[]]$Content)
    return ,(Join-ConstructBytes @(([byte[]]@($Tag)), (Get-ConstructDerLength $Content.Length), $Content))
}

function ConvertTo-ConstructDerInteger {
    <# A DER INTEGER from an unsigned big-endian magnitude (RSAParameters' form). #>
    param([byte[]]$Unsigned)
    $start = 0
    while ($start -lt ($Unsigned.Length - 1) -and $Unsigned[$start] -eq 0) { $start++ }
    $length = $Unsigned.Length - $start
    $pad = 0
    if ($Unsigned[$start] -band 0x80) { $pad = 1 }
    $content = New-Object byte[] ($length + $pad)
    [Array]::Copy($Unsigned, $start, $content, $pad, $length)
    return ,(New-ConstructDerElement 0x02 $content)
}

function Get-ConstructBackupSpkiDer {
    <#
        SubjectPublicKeyInfo DER of an RSA public key, built from the modulus and exponent:
        SEQUENCE { SEQUENCE { OID 1.2.840.113549.1.1.1, NULL }, BIT STRING { SEQUENCE { n, e } } }.
        By hand because .NET Framework has no ExportSubjectPublicKeyInfo.
    #>
    param([Parameter(Mandatory)]$Rsa)
    $p = $Rsa.ExportParameters($false)
    $rsaPublicKey = New-ConstructDerElement 0x30 (Join-ConstructBytes @((ConvertTo-ConstructDerInteger $p.Modulus), (ConvertTo-ConstructDerInteger $p.Exponent)))
    $bitString = New-ConstructDerElement 0x03 (Join-ConstructBytes @(([byte[]]@(0)), $rsaPublicKey))
    $algorithm = [byte[]](0x30, 0x0D, 0x06, 0x09, 0x2A, 0x86, 0x48, 0x86, 0xF7, 0x0D, 0x01, 0x01, 0x01, 0x05, 0x00)
    return ,(New-ConstructDerElement 0x30 (Join-ConstructBytes @($algorithm, $bitString)))
}

function ConvertTo-ConstructPem {
    param([Parameter(Mandatory)][byte[]]$Der, [string]$Label = 'PUBLIC KEY')
    $b64 = [Convert]::ToBase64String($Der)
    $lines = for ($i = 0; $i -lt $b64.Length; $i += 64) { $b64.Substring($i, [Math]::Min(64, $b64.Length - $i)) }
    return ("-----BEGIN $Label-----`n" + ($lines -join "`n") + "`n-----END $Label-----`n")
}

function ConvertFrom-ConstructPem {
    <# The DER bytes inside a PEM block (any label). #>
    param([Parameter(Mandatory)][string]$Pem)
    $m = [regex]::Match($Pem, '-----BEGIN [A-Z ]+-----(?<b64>[A-Za-z0-9+/=\s]+)-----END [A-Z ]+-----')
    if (-not $m.Success) { throw 'Not a PEM block.' }
    return ,([Convert]::FromBase64String(($m.Groups['b64'].Value -replace '\s', '')))
}

function Get-ConstructBackupFingerprint {
    <# Lowercase hex SHA-256 of the SubjectPublicKeyInfo DER, of an RSA key or of DER bytes. #>
    param($Rsa, [byte[]]$SpkiDer)
    if ($Rsa) { $SpkiDer = Get-ConstructBackupSpkiDer -Rsa $Rsa }
    return (ConvertTo-ConstructHex (Get-ConstructSha256 $SpkiDer))
}

# ── DPAPI seam ───────────────────────────────────────────────────────────────

function Protect-ConstructBackupKeyBytes {
    <# DPAPI CurrentUser through lib/AgentVm.Remote.ps1's seam (tests replace that seam). #>
    param([byte[]]$Bytes)
    if (-not (Get-Command Protect-ConstructTokenBytes -ErrorAction SilentlyContinue)) {
        throw 'lib/AgentVm.Remote.ps1 is missing from this install; it protects the backup key. Update The Construct.'
    }
    return ,(Protect-ConstructTokenBytes $Bytes)
}

function Unprotect-ConstructBackupKeyBytes {
    param([byte[]]$Bytes)
    if (-not (Get-Command Unprotect-ConstructTokenBytes -ErrorAction SilentlyContinue)) {
        throw 'lib/AgentVm.Remote.ps1 is missing from this install; it protects the backup key. Update The Construct.'
    }
    return ,(Unprotect-ConstructTokenBytes $Bytes)
}

function Save-ConstructBackupPrivateKeyFile {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Xml)
    $plain = [System.Text.Encoding]::UTF8.GetBytes($Xml)
    try {
        $protected = Protect-ConstructBackupKeyBytes $plain
        Write-ConstructFileAtomic -Path $Path -Bytes $protected
    } finally { Clear-ConstructBytes $plain }
}

function Read-ConstructBackupPrivateKeyXml {
    <# The private XML of the key stored DPAPI-protected at -Path. #>
    param([Parameter(Mandatory)][string]$Path)
    $plain = $null
    try {
        try { $plain = Unprotect-ConstructBackupKeyBytes ([System.IO.File]::ReadAllBytes($Path)) }
        catch { throw "Cannot open the backup key '$Path': it was protected for another Windows user or PC ($($_.Exception.Message))." }
        return [System.Text.Encoding]::UTF8.GetString($plain)
    } finally { Clear-ConstructBytes $plain }
}

function Open-ConstructBackupRsaKey {
    <# The RSA key stored DPAPI-protected at -Path. The caller disposes it. #>
    param([Parameter(Mandatory)][string]$Path)
    return (New-ConstructBackupRsa -Xml (Read-ConstructBackupPrivateKeyXml -Path $Path))
}

# ── The PC key ───────────────────────────────────────────────────────────────

function Get-ConstructBackupKeyPaths {
    param([string]$KeyDir)
    if (-not $KeyDir) { $KeyDir = Get-ConstructBackupKeyDir }
    return [pscustomobject]@{
        Dir     = $KeyDir
        Private = Join-Path $KeyDir 'pc-key.dpapi'
        Public  = Join-Path $KeyDir 'pc-key.pub.pem'
        Info    = Join-Path $KeyDir 'pc-key.json'
    }
}

function Save-ConstructBackupKeyInfo {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Fingerprint, [string]$Label,
          [string]$CreatedAt, [AllowNull()][string]$ExportedAt)
    $doc = [ordered]@{ fingerprint = $Fingerprint; label = $Label; createdAt = $CreatedAt; exportedAt = $null }
    if ($ExportedAt) { $doc.exportedAt = $ExportedAt }
    Write-ConstructFileAtomic -Path $Path -Bytes ([System.Text.Encoding]::UTF8.GetBytes(($doc | ConvertTo-Json -Compress)))
}

function Read-ConstructBackupKeyInfo {
    <# pc-key.json as written, or $null. Dates stay the strings that were written. #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
    try { $text = [System.IO.File]::ReadAllText($Path) } catch { return $null }
    $fp = [regex]::Match($text, '"fingerprint"\s*:\s*"([0-9a-f]{64})"')
    if (-not $fp.Success) { return $null }
    $field = {
        param($name)
        $m = [regex]::Match($text, '"' + $name + '"\s*:\s*"((?:[^"\\]|\\.)*)"')
        if ($m.Success) { return [regex]::Unescape($m.Groups[1].Value) }
        return $null
    }
    return [pscustomobject]@{
        Fingerprint = $fp.Groups[1].Value
        Label       = [string](& $field 'label')
        CreatedAt   = [string](& $field 'createdAt')
        ExportedAt  = (& $field 'exportedAt')
    }
}

function Get-ConstructBackupPcKey {
    <#
        This PC's backup key: {Fingerprint, Label, CreatedAt, ExportedAt, PublicKeyPem,
        KeyDir}. With -Create a missing key is generated (label: -Label or the computer
        name). Without it a missing key is $null. A public key or info file that does not
        match the private key is rebuilt from the private key.
    #>
    [CmdletBinding()]
    param([string]$KeyDir, [switch]$Create, [string]$Label)
    $paths = Get-ConstructBackupKeyPaths -KeyDir $KeyDir
    if (-not (Test-Path -LiteralPath $paths.Private -PathType Leaf)) {
        if (-not $Create) { return $null }
        if (-not (Test-Path -LiteralPath $paths.Dir)) { New-Item -ItemType Directory -Path $paths.Dir -Force | Out-Null }
        $newXml = New-ConstructBackupRsaXml
        Save-ConstructBackupPrivateKeyFile -Path $paths.Private -Xml $newXml
        $rsa = New-ConstructBackupRsa -Xml $newXml
        $newXml = $null
        try { $der = Get-ConstructBackupSpkiDer -Rsa $rsa } finally { $rsa.Dispose() }
        $fp = Get-ConstructBackupFingerprint -SpkiDer $der
        Write-ConstructFileAtomic -Path $paths.Public -Bytes ([System.Text.Encoding]::ASCII.GetBytes((ConvertTo-ConstructPem -Der $der)))
        Save-ConstructBackupKeyInfo -Path $paths.Info -Fingerprint $fp -Label (ConvertTo-ConstructBackupLabel $Label) `
            -CreatedAt ([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')) -ExportedAt $null
    }
    $info = Read-ConstructBackupKeyInfo -Path $paths.Info
    $pem = $null
    $pemFp = $null
    if (Test-Path -LiteralPath $paths.Public -PathType Leaf) {
        try {
            $pem = [System.IO.File]::ReadAllText($paths.Public)
            $pemFp = Get-ConstructBackupFingerprint -SpkiDer (ConvertFrom-ConstructPem $pem)
        } catch { $pem = $null; $pemFp = $null }
    }
    if (-not $info -or -not $pemFp -or $pemFp -ne $info.Fingerprint) {
        # Self-repair from the private key, the one file that is authoritative.
        $rsa = Open-ConstructBackupRsaKey -Path $paths.Private
        try { $der = Get-ConstructBackupSpkiDer -Rsa $rsa } finally { $rsa.Dispose() }
        $fp = Get-ConstructBackupFingerprint -SpkiDer $der
        $pem = ConvertTo-ConstructPem -Der $der
        Write-ConstructFileAtomic -Path $paths.Public -Bytes ([System.Text.Encoding]::ASCII.GetBytes($pem))
        $keepLabel = if ($info -and $info.Label -cmatch $script:ConstructBackupLabelPattern) { $info.Label } else { ConvertTo-ConstructBackupLabel $Label }
        $keepCreated = if ($info -and $info.CreatedAt) { $info.CreatedAt } else { [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ') }
        $keepExported = if ($info -and $info.Fingerprint -eq $fp) { $info.ExportedAt } else { $null }
        Save-ConstructBackupKeyInfo -Path $paths.Info -Fingerprint $fp -Label $keepLabel -CreatedAt $keepCreated -ExportedAt $keepExported
        $info = Read-ConstructBackupKeyInfo -Path $paths.Info
    }
    $label = $info.Label
    if ($label -cnotmatch $script:ConstructBackupLabelPattern) { $label = ConvertTo-ConstructBackupLabel $label }
    return [pscustomobject]@{
        Fingerprint  = $info.Fingerprint
        Label        = $label
        CreatedAt    = $info.CreatedAt
        ExportedAt   = $info.ExportedAt
        PublicKeyPem = $pem
        KeyDir       = $paths.Dir
    }
}

function Get-ConstructBackupPrivateKeyFiles {
    <# Every key this PC can unwrap with: the current one first, then the replaced ones
       (pc-key.<fingerprint>.dpapi). #>
    param([string]$KeyDir)
    $paths = Get-ConstructBackupKeyPaths -KeyDir $KeyDir
    $list = New-Object System.Collections.Generic.List[object]
    $current = $null
    try { $current = Get-ConstructBackupPcKey -KeyDir $paths.Dir } catch { $current = $null }
    if ($current) { $list.Add([pscustomobject]@{ Fingerprint = $current.Fingerprint; Path = $paths.Private; Current = $true }) }
    if (Test-Path -LiteralPath $paths.Dir) {
        foreach ($f in @(Get-ChildItem -LiteralPath $paths.Dir -File -ErrorAction SilentlyContinue | Sort-Object Name)) {
            if ($f.Name -cmatch '^pc-key\.([0-9a-f]{64})\.dpapi$') {
                $fp = $Matches[1]
                if ($current -and $fp -eq $current.Fingerprint) { continue }
                $list.Add([pscustomobject]@{ Fingerprint = $fp; Path = $f.FullName; Current = $false })
            }
        }
    }
    return $list.ToArray()
}

# ── Keys files and unwrapping ────────────────────────────────────────────────

function ConvertFrom-ConstructBackupKeysJson {
    <# A validated keys file (contract 3.2): {BackupId, CreatedAt, Recipients[{Fingerprint,Label,Wrapped}]}. #>
    param([Parameter(Mandatory)][string]$Json)
    $doc = $null
    try { $doc = $Json | ConvertFrom-Json } catch { $doc = $null }
    if (-not $doc) { throw "The backup's keys file is not valid JSON." }
    if ([string]$doc.format -cne 'construct-backup-keys/1') { throw "The backup's keys file has an unknown format ('$([string]$doc.format)')." }
    $id = [string]$doc.backupId
    if (-not (Test-ConstructBackupId $id)) { throw "The backup's keys file names no valid backup id." }
    $recipients = New-Object System.Collections.Generic.List[object]
    foreach ($r in @($doc.recipients)) {
        if (-not $r) { continue }
        $fp = [string]$r.fingerprint
        $wrapped = [string]$r.wrapped
        if ($fp -cnotmatch $script:ConstructBackupFingerprintPattern -or -not $wrapped) { continue }
        $recipients.Add([pscustomobject]@{ Fingerprint = $fp; Label = [string]$r.label; Wrapped = $wrapped })
    }
    if ($recipients.Count -eq 0) { throw "The backup's keys file names no recipient." }
    $created = $null
    $cm = [regex]::Match($Json, '"createdAt"\s*:\s*"([^"]*)"')
    if ($cm.Success) { $created = $cm.Groups[1].Value }
    return [pscustomobject]@{ BackupId = $id; CreatedAt = $created; Recipients = $recipients.ToArray() }
}

function Test-ConstructBackupKeysReadable {
    <# Is one of this PC's keys (current or replaced) among the recipients? No decryption. #>
    param([Parameter(Mandatory)]$Keys, [string]$KeyDir)
    if ($Keys -is [string]) { $Keys = ConvertFrom-ConstructBackupKeysJson -Json $Keys }
    $mine = @(Get-ConstructBackupPrivateKeyFiles -KeyDir $KeyDir | ForEach-Object { $_.Fingerprint })
    foreach ($r in @($Keys.Recipients)) { if ($mine -contains $r.Fingerprint) { return $true } }
    return $false
}

function Unprotect-ConstructBackupIdentity {
    <#
        The backup's age identity E, unwrapped with whichever of this PC's keys the keys
        file names (contract 3.2: "unwrap tries every key in the folder"). Returns the one
        line AGE-SECRET-KEY-1...; the caller sends it on SSH stdin and drops it.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$KeysJson, [string]$KeyDir, [string]$ExpectedBackupId)
    $keys = ConvertFrom-ConstructBackupKeysJson -Json $KeysJson
    if ($ExpectedBackupId -and $keys.BackupId -cne $ExpectedBackupId) {
        throw "The keys file belongs to backup $($keys.BackupId), not to $ExpectedBackupId."
    }
    $mine = @(Get-ConstructBackupPrivateKeyFiles -KeyDir $KeyDir)
    if ($mine.Count -eq 0) {
        throw "This PC has no backup key, so it cannot open backup $($keys.BackupId). Import the key of a PC the VM was enrolled with (Provision-AgentVM.ps1 -Action backup-key-import)."
    }
    $lastError = ''
    foreach ($r in @($keys.Recipients)) {
        $match = $mine | Where-Object { $_.Fingerprint -eq $r.Fingerprint } | Select-Object -First 1
        if (-not $match) { continue }
        $rsa = $null
        $plain = $null
        try {
            $rsa = Open-ConstructBackupRsaKey -Path $match.Path
            if ((Get-ConstructBackupFingerprint -Rsa $rsa) -ne $r.Fingerprint) { $lastError = "the key file $($match.Path) does not hold the key its name says"; continue }
            $plain = $rsa.Decrypt([Convert]::FromBase64String($r.Wrapped), [System.Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
            $identity = ([System.Text.Encoding]::ASCII.GetString($plain)).Trim()
            if ($identity -cnotmatch $script:ConstructBackupIdentityPattern) { $lastError = 'the unwrapped value is not an age identity'; continue }
            return $identity
        } catch {
            $lastError = $_.Exception.Message
        } finally {
            Clear-ConstructBytes $plain
            if ($rsa) { $rsa.Dispose() }
        }
    }
    if ($lastError) { throw "Could not unwrap the key of backup $($keys.BackupId) with this PC's key: $lastError" }
    $ours = ($mine | ForEach-Object { $_.Fingerprint.Substring(0, 16) }) -join ', '
    $theirs = (@($keys.Recipients) | ForEach-Object { if ($_.Label) { "$($_.Label) ($($_.Fingerprint.Substring(0, 16)))" } else { $_.Fingerprint.Substring(0, 16) } }) -join ', '
    throw ("Backup $($keys.BackupId) was not made for this PC's backup key ($ours). It can be opened with: $theirs. " +
           "Import that PC's key here (Provision-AgentVM.ps1 -Action backup-key-import), or restore from that PC.")
}

# ── Key export / import (contract 3.2) ───────────────────────────────────────

function Get-ConstructBackupKdfKeys {
    <# PBKDF2-SHA256 -> 64 bytes, split into the AES-256 key and the HMAC-SHA256 key. #>
    param([byte[]]$Password, [byte[]]$Salt, [int]$Iterations)
    $kdf = [System.Security.Cryptography.Rfc2898DeriveBytes]::new($Password, $Salt, $Iterations, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    try { $material = $kdf.GetBytes(64) } finally { $kdf.Dispose() }
    try {
        return [pscustomobject]@{ EncKey = (Get-ConstructByteRange $material 0 32); MacKey = (Get-ConstructByteRange $material 32 32) }
    } finally { Clear-ConstructBytes $material }
}

function Invoke-ConstructAesCbc {
    param([byte[]]$Key, [byte[]]$Iv, [byte[]]$Data, [switch]$Decrypt)
    $aes = [System.Security.Cryptography.Aes]::Create()
    try {
        $aes.KeySize = 256
        $aes.Mode = [System.Security.Cryptography.CipherMode]::CBC
        $aes.Padding = [System.Security.Cryptography.PaddingMode]::PKCS7
        $aes.Key = $Key
        $aes.IV = $Iv
        $transform = if ($Decrypt) { $aes.CreateDecryptor() } else { $aes.CreateEncryptor() }
        try { return ,($transform.TransformFinalBlock($Data, 0, $Data.Length)) } finally { $transform.Dispose() }
    } finally { $aes.Dispose() }
}

function Get-ConstructHmacSha256 {
    param([byte[]]$Key, [byte[]]$Data)
    $h = [System.Security.Cryptography.HMACSHA256]::new($Key)
    try { return ,($h.ComputeHash($Data)) } finally { $h.Dispose() }
}

function Export-ConstructBackupKey {
    <#
        Write this PC's private key to -Path, encrypted with -Passphrase (contract 3.2:
        PBKDF2-SHA256 600000 -> AES-256-CBC + HMAC-SHA256 over iv || ciphertext), and record
        exportedAt. Creates the key first when this PC has none yet.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][System.Security.SecureString]$Passphrase, [string]$KeyDir)
    if ($Passphrase.Length -eq 0) { throw 'An empty passphrase cannot protect the backup key.' }
    $key = Get-ConstructBackupPcKey -KeyDir $KeyDir -Create
    $paths = Get-ConstructBackupKeyPaths -KeyDir $key.KeyDir
    $xml = $null; $pw = $null; $kdf = $null
    try {
        # The stored XML is the plaintext; the key is never re-exported from an RSA object.
        $xmlText = Read-ConstructBackupPrivateKeyXml -Path $paths.Private
        $rsa = New-ConstructBackupRsa -Xml $xmlText
        try { $fp = Get-ConstructBackupFingerprint -Rsa $rsa } finally { $rsa.Dispose() }
        $xml = [System.Text.Encoding]::UTF8.GetBytes($xmlText)
        $xmlText = $null
        $pw = ConvertFrom-ConstructSecureString $Passphrase
        $salt = New-ConstructRandomBytes 16
        $iv = New-ConstructRandomBytes 16
        $kdf = Get-ConstructBackupKdfKeys -Password $pw -Salt $salt -Iterations $script:ConstructBackupKdfIterations
        $ciphertext = Invoke-ConstructAesCbc -Key $kdf.EncKey -Iv $iv -Data $xml
        $mac = Get-ConstructHmacSha256 -Key $kdf.MacKey -Data (Join-ConstructBytes @($iv, $ciphertext))
        $doc = [ordered]@{
            format     = 'construct-backup-key/1'
            kdf        = 'pbkdf2-sha256'
            iterations = $script:ConstructBackupKdfIterations
            salt       = [Convert]::ToBase64String($salt)
            iv         = [Convert]::ToBase64String($iv)
            ciphertext = [Convert]::ToBase64String($ciphertext)
            mac        = [Convert]::ToBase64String($mac)
            fingerprint = $fp
            label      = $key.Label
        }
        $full = [System.IO.Path]::GetFullPath($Path)
        Write-ConstructFileAtomic -Path $full -Bytes ([System.Text.Encoding]::UTF8.GetBytes((($doc | ConvertTo-Json) -replace "`r`n", "`n") + "`n"))
        Save-ConstructBackupKeyInfo -Path $paths.Info -Fingerprint $key.Fingerprint -Label $key.Label -CreatedAt $key.CreatedAt `
            -ExportedAt ([DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ'))
        return [pscustomobject]@{ Path = $full; Fingerprint = $fp; Label = $key.Label }
    } finally {
        Clear-ConstructBytes $xml; Clear-ConstructBytes $pw
        if ($kdf) { Clear-ConstructBytes $kdf.EncKey; Clear-ConstructBytes $kdf.MacKey }
    }
}

function Import-ConstructBackupKey {
    <#
        Install the key in an export file as this PC's key. A wrong passphrase (or a changed
        file) fails the MAC check: CryptographicException, nothing changed. A DIFFERENT key
        already on this PC is replaced only when -ConfirmReplace (called with the current
        key info, the new fingerprint and label) returns $true; the old key stays as
        pc-key.<fingerprint>.dpapi so backups wrapped to it stay readable.

        Result: {Result = installed|replaced|unchanged|cancelled, Fingerprint, Label, PreviousFingerprint}.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][System.Security.SecureString]$Passphrase,
          [string]$KeyDir, [scriptblock]$ConfirmReplace)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Key file not found: $Path" }
    $doc = $null
    try { $doc = [System.IO.File]::ReadAllText($Path) | ConvertFrom-Json } catch { $doc = $null }
    if (-not $doc -or [string]$doc.format -cne 'construct-backup-key/1') { throw "'$Path' is not a Construct backup key file." }
    if ([string]$doc.kdf -cne 'pbkdf2-sha256') { throw "Unsupported key derivation '$([string]$doc.kdf)' in '$Path'." }
    $iterations = 0
    try { $iterations = [int]$doc.iterations } catch { $iterations = 0 }
    if ($iterations -lt $script:ConstructBackupKdfIterations -or $iterations -gt 10000000) { throw "Unsupported iteration count in '$Path'." }
    try {
        $salt = [Convert]::FromBase64String([string]$doc.salt)
        $iv = [Convert]::FromBase64String([string]$doc.iv)
        $ciphertext = [Convert]::FromBase64String([string]$doc.ciphertext)
        $mac = [Convert]::FromBase64String([string]$doc.mac)
    } catch { throw "'$Path' is damaged (invalid base64)." }
    if ($salt.Length -lt 16 -or $iv.Length -ne 16 -or $mac.Length -ne 32 -or $ciphertext.Length -eq 0 -or ($ciphertext.Length % 16) -ne 0) {
        throw "'$Path' is damaged."
    }
    $expectedFp = [string]$doc.fingerprint
    if ($expectedFp -cnotmatch $script:ConstructBackupFingerprintPattern) { throw "'$Path' names no valid fingerprint." }

    $pw = $null; $kdf = $null; $xml = $null
    try {
        $pw = ConvertFrom-ConstructSecureString $Passphrase
        $kdf = Get-ConstructBackupKdfKeys -Password $pw -Salt $salt -Iterations $iterations
        $actualMac = Get-ConstructHmacSha256 -Key $kdf.MacKey -Data (Join-ConstructBytes @($iv, $ciphertext))
        if (-not (Test-ConstructBytesEqual $actualMac $mac)) {
            throw [System.Security.Cryptography.CryptographicException]::new('Wrong passphrase, or the key file was changed.')
        }
        $xml = Invoke-ConstructAesCbc -Key $kdf.EncKey -Iv $iv -Data $ciphertext -Decrypt
        $xmlText = [System.Text.Encoding]::UTF8.GetString($xml)
        if ($xmlText -notmatch '<D>[A-Za-z0-9+/=]+</D>') { throw "'$Path' holds no private key." }
        $rsa = New-ConstructBackupRsa -Xml $xmlText
        try {
            if ($rsa.KeySize -lt $script:ConstructBackupKeyBits) { throw "The key in '$Path' is shorter than $($script:ConstructBackupKeyBits) bits." }
            $der = Get-ConstructBackupSpkiDer -Rsa $rsa
        } finally { $rsa.Dispose() }
        # The decrypted XML is stored as it is: the key is never re-exported from an RSA object.
        $privateXml = $xmlText
        $fp = Get-ConstructBackupFingerprint -SpkiDer $der
        if ($fp -ne $expectedFp) { throw "The key in '$Path' does not match the fingerprint the file names." }
        $label = ConvertTo-ConstructBackupLabel ([string]$doc.label)

        $paths = Get-ConstructBackupKeyPaths -KeyDir $KeyDir
        $current = Get-ConstructBackupPcKey -KeyDir $paths.Dir
        if ($current -and $current.Fingerprint -eq $fp) {
            return [pscustomobject]@{ Result = 'unchanged'; Fingerprint = $fp; Label = $current.Label; PreviousFingerprint = $null }
        }
        if ($current) {
            $ok = $false
            if ($ConfirmReplace) { $ok = [bool](& $ConfirmReplace $current $fp $label) }
            if (-not $ok) {
                return [pscustomobject]@{ Result = 'cancelled'; Fingerprint = $fp; Label = $label; PreviousFingerprint = $current.Fingerprint }
            }
        }
        if (-not (Test-Path -LiteralPath $paths.Dir)) { New-Item -ItemType Directory -Path $paths.Dir -Force | Out-Null }
        # Stage the new key completely before the current one is moved aside.
        $staged = "$($paths.Private).import-" + [guid]::NewGuid().ToString('N')
        Save-ConstructBackupPrivateKeyFile -Path $staged -Xml $privateXml
        $privateXml = $null
        try {
            if ($current) {
                Move-Item -LiteralPath $paths.Private -Destination (Join-Path $paths.Dir "pc-key.$($current.Fingerprint).dpapi") -Force
            }
            Move-Item -LiteralPath $staged -Destination $paths.Private -Force
        } finally {
            if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force -ErrorAction SilentlyContinue }
        }
        $sameAsOld = Join-Path $paths.Dir "pc-key.$fp.dpapi"
        if (Test-Path -LiteralPath $sameAsOld) { Remove-Item -LiteralPath $sameAsOld -Force }
        Write-ConstructFileAtomic -Path $paths.Public -Bytes ([System.Text.Encoding]::ASCII.GetBytes((ConvertTo-ConstructPem -Der $der)))
        $now = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
        # The key exists in an export file (the one just imported), so it counts as exported.
        Save-ConstructBackupKeyInfo -Path $paths.Info -Fingerprint $fp -Label $label -CreatedAt $now -ExportedAt $now
        $result = if ($current) { 'replaced' } else { 'installed' }
        $previous = if ($current) { $current.Fingerprint } else { $null }
        return [pscustomobject]@{ Result = $result; Fingerprint = $fp; Label = $label; PreviousFingerprint = $previous }
    } finally {
        Clear-ConstructBytes $pw; Clear-ConstructBytes $xml
        if ($kdf) { Clear-ConstructBytes $kdf.EncKey; Clear-ConstructBytes $kdf.MacKey }
    }
}

# ── Containers (.cbk) ────────────────────────────────────────────────────────

function Read-ConstructBackupContainerHeader {
    <#
        Line 1 "construct-backup/1", line 2 the keys JSON, then the raw age payload
        (contract 3.3). Returns {KeysJson, Keys, PayloadOffset, Length}.
    #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Backup file not found: $Path" }
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
    try {
        $buffer = New-Object byte[] $script:ConstructBackupHeaderLimit
        $filled = 0
        while ($filled -lt $buffer.Length) {
            $n = $fs.Read($buffer, $filled, $buffer.Length - $filled)
            if ($n -le 0) { break }
            $filled += $n
        }
        $first = [Array]::IndexOf($buffer, [byte]10, 0, $filled)
        $magic = if ($first -gt 0) { ([System.Text.Encoding]::ASCII.GetString($buffer, 0, $first)).TrimEnd("`r") } else { '' }
        if ($magic -cne $script:ConstructBackupContainerMagic) { throw "'$Path' is not a Construct backup file." }
        $second = if ($first + 1 -lt $filled) { [Array]::IndexOf($buffer, [byte]10, $first + 1, $filled - $first - 1) } else { -1 }
        if ($second -lt 0) { throw "'$Path' is not a complete Construct backup file (no keys line)." }
        $keysJson = ([System.Text.Encoding]::UTF8.GetString($buffer, $first + 1, $second - $first - 1)).TrimEnd("`r")
        $keys = ConvertFrom-ConstructBackupKeysJson -Json $keysJson
        $offset = [long]($second + 1)
        if ($offset -ge $fs.Length) { throw "'$Path' holds no backup payload." }
        return [pscustomobject]@{ KeysJson = $keysJson; Keys = $keys; PayloadOffset = $offset; Length = [long]$fs.Length }
    } finally { $fs.Dispose() }
}

# ── Binary-safe SSH ──────────────────────────────────────────────────────────

function ConvertTo-ConstructWindowsArgument {
    <# One argv entry quoted for the MSVCRT parser (ssh.exe), for .NET Framework, whose
       ProcessStartInfo takes a single command-line string. Pure. #>
    param([AllowEmptyString()][string]$Value)
    if ($Value -eq '') { return '""' }
    if ($Value -notmatch '[\s"]') { return $Value }
    $sb = New-Object System.Text.StringBuilder
    [void]$sb.Append('"')
    $backslashes = 0
    foreach ($c in $Value.ToCharArray()) {
        if ($c -eq '\') { $backslashes++; continue }
        if ($c -eq '"') {
            [void]$sb.Append([char]'\', $backslashes * 2 + 1)
            [void]$sb.Append('"')
        } else {
            if ($backslashes -gt 0) { [void]$sb.Append([char]'\', $backslashes) }
            [void]$sb.Append($c)
        }
        $backslashes = 0
    }
    if ($backslashes -gt 0) { [void]$sb.Append([char]'\', $backslashes * 2) }
    [void]$sb.Append('"')
    return $sb.ToString()
}

function Join-ConstructWindowsCommandLine {
    param([string[]]$ArgumentList)
    return ((@($ArgumentList) | ForEach-Object { ConvertTo-ConstructWindowsArgument $_ }) -join ' ')
}

function Invoke-ConstructSshProcess {
    <#
        Run -FilePath (ssh) with -ArgumentList as a System.Diagnostics.Process.

        stdin  : -FirstLine (followed by "\n"), then -StdinBytes, then -StdinFile from
                 -StdinOffset -- written to StandardInput.BaseStream, then closed. Nothing
                 at all closes stdin at once (ssh -n's effect).
        stdout : 'File' -> copied raw into -StdoutFile; 'Capture' -> returned as UTF-8 text;
                 'Console' -> not redirected, shown live.
        stderr : never redirected; the guest's progress and ssh's own errors show live.

        Returns {ExitCode, Output, BytesOut, StdinClosedEarly}. Never logs -FirstLine.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [AllowEmptyString()][string]$FirstLine = '',
        [byte[]]$StdinBytes,
        [string]$StdinFile,
        [long]$StdinOffset = 0,
        [ValidateSet('Capture', 'File', 'Console')][string]$Stdout = 'Capture',
        [string]$StdoutFile
    )
    if ($Stdout -eq 'File' -and -not $StdoutFile) { throw 'Invoke-ConstructSshProcess: -Stdout File needs -StdoutFile.' }
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $FilePath
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = ($Stdout -ne 'Console')
    $psi.RedirectStandardError = $false
    $hasArgumentList = [bool]($psi | Get-Member -Name ArgumentList -MemberType Property)
    if ($hasArgumentList) {
        foreach ($a in @($ArgumentList)) { [void]$psi.ArgumentList.Add([string]$a) }
    } else {
        $psi.Arguments = Join-ConstructWindowsCommandLine -ArgumentList $ArgumentList
    }
    # The first bytes on stdin are the protocol (E). A byte-order mark in front of them
    # would break it: .NET Framework's Process writes the console input encoding's
    # preamble as soon as stdin is redirected, so a UTF-8 console is switched to the same
    # code page without a preamble for the duration of the start. PowerShell 7 takes the
    # encoding as a property.
    $restoreInputEncoding = $null
    if ($psi | Get-Member -Name StandardInputEncoding -MemberType Property) {
        $psi.StandardInputEncoding = New-Object System.Text.UTF8Encoding $false
    } else {
        try {
            $currentInput = [Console]::InputEncoding
            if ($currentInput -and $currentInput.GetPreamble().Length -gt 0) {
                $restoreInputEncoding = $currentInput
                [Console]::InputEncoding = New-Object System.Text.UTF8Encoding $false
            }
        } catch { $restoreInputEncoding = $null }
    }

    $proc = New-Object System.Diagnostics.Process
    $proc.StartInfo = $psi
    $outStream = $null
    $memory = $null
    $copyTask = $null
    $stdinClosedEarly = $false
    $lineBytes = $null
    try {
        try { [void]$proc.Start() }
        finally {
            if ($restoreInputEncoding) { try { [Console]::InputEncoding = $restoreInputEncoding } catch { } }
        }
        if ($Stdout -eq 'File') {
            $outStream = [System.IO.File]::Open($StdoutFile, [System.IO.FileMode]::Create, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        } elseif ($Stdout -eq 'Capture') {
            $memory = New-Object System.IO.MemoryStream
            $outStream = $memory
        }
        # Read stdout concurrently with the stdin writes, so neither pipe can fill up and
        # block the other.
        if ($outStream) { $copyTask = $proc.StandardOutput.BaseStream.CopyToAsync($outStream, 1048576) }
        $stdin = $proc.StandardInput.BaseStream
        try {
            if ($FirstLine) {
                $lineBytes = [System.Text.Encoding]::ASCII.GetBytes($FirstLine + "`n")
                $stdin.Write($lineBytes, 0, $lineBytes.Length)
            }
            if ($StdinBytes) { $stdin.Write($StdinBytes, 0, $StdinBytes.Length) }
            if ($StdinFile) {
                $in = [System.IO.File]::Open($StdinFile, [System.IO.FileMode]::Open, [System.IO.FileAccess]::Read, [System.IO.FileShare]::Read)
                try {
                    if ($StdinOffset -gt 0) { [void]$in.Seek($StdinOffset, [System.IO.SeekOrigin]::Begin) }
                    $in.CopyTo($stdin, 1048576)
                } finally { $in.Dispose() }
            }
            $stdin.Flush()
        } catch [System.IO.IOException] {
            # The remote side stopped reading (it failed, or refused the input): its exit
            # code below says why.
            $stdinClosedEarly = $true
        } finally {
            Clear-ConstructBytes $lineBytes
            try { $proc.StandardInput.Close() } catch { $stdinClosedEarly = $true }
        }
        if ($copyTask) {
            try { $copyTask.Wait() }
            catch {
                $inner = $_.Exception
                while ($inner.InnerException) { $inner = $inner.InnerException }
                throw "Writing the SSH output failed: $($inner.Message)"
            }
        }
        $proc.WaitForExit()
        $exit = $proc.ExitCode
        $text = ''
        $bytesOut = 0L
        if ($memory) {
            $bytesOut = $memory.Length
            $text = [System.Text.Encoding]::UTF8.GetString($memory.ToArray())
        } elseif ($outStream) {
            $outStream.Flush()
            $bytesOut = $outStream.Length
        }
        return [pscustomobject]@{ ExitCode = $exit; Output = $text; BytesOut = $bytesOut; StdinClosedEarly = $stdinClosedEarly }
    } finally {
        if ($outStream) { $outStream.Dispose() }
        $proc.Dispose()
    }
}

function Invoke-ConstructSshStreamToFile {
    <# The remote command's stdout, byte for byte, into -OutFile (optionally after sending
       -FirstLine on stdin). #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$ArgumentList = @(),
          [Parameter(Mandatory)][string]$OutFile, [AllowEmptyString()][string]$FirstLine = '')
    return (Invoke-ConstructSshProcess -FilePath $FilePath -ArgumentList $ArgumentList -FirstLine $FirstLine -Stdout File -StdoutFile $OutFile)
}

function Invoke-ConstructSshStreamFromFile {
    <# -FirstLine, then -InFile's bytes from -Offset, byte for byte, into the remote
       command's stdin. The remote output shows on the console. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$FilePath, [string[]]$ArgumentList = @(),
          [Parameter(Mandatory)][string]$InFile, [long]$Offset = 0, [AllowEmptyString()][string]$FirstLine = '',
          [ValidateSet('Capture', 'Console')][string]$Stdout = 'Console')
    return (Invoke-ConstructSshProcess -FilePath $FilePath -ArgumentList $ArgumentList -FirstLine $FirstLine -StdinFile $InFile -StdinOffset $Offset -Stdout $Stdout)
}

# ── The guest command line ───────────────────────────────────────────────────

function ConvertTo-ConstructPosixArgument {
    param([AllowEmptyString()][string]$Value)
    if ($Value -cmatch '^[A-Za-z0-9._/=:@+-]+$') { return $Value }
    return "'" + $Value.Replace("'", "'\''") + "'"
}

function Format-ConstructBackupCommand {
    <# "construct backup <args>", every argument quoted for the guest's shell. Pure. #>
    param([Parameter(Mandatory)][string[]]$Arguments)
    return ('construct backup ' + ((@($Arguments) | ForEach-Object { ConvertTo-ConstructPosixArgument $_ }) -join ' '))
}

function Get-ConstructBackupRemoteCommand {
    <#
        The string ssh runs, executing -Command as root with ssh's stdin left to it. Pure.

          -Direct (connected as root): bash -c '<command>'
          otherwise (the seed user):   sudo without a password when the VM allows it
                                       (provision.sh grants the seed user NOPASSWD), else
                                       the seed password as the first stdin line of sudo -S
                                       -- sudo reads exactly that line, the rest of stdin
                                       stays the command's.

        A non-login shell with a fixed PATH: nothing a login profile prints can end up in
        a container streamed on stdout.
    #>
    param([Parameter(Mandatory)][string]$Command, [switch]$Direct, [string]$SeedPassword = '')
    $inner = "export HOME=/root PATH=$($script:ConstructBackupGuestPath); cd / && $Command"
    $esc = $inner.Replace("'", "'\''")
    if ($Direct) { return "bash -c '$esc'" }
    $escPw = $SeedPassword.Replace("'", "'\''")
    return "if sudo -n true 2>/dev/null; then sudo -n bash -c '$esc'; else { printf '%s\n' '$escPw'; cat; } | sudo -S -p '' bash -c '$esc'; fi"
}

function New-ConstructBackupConnection {
    <#
        How to reach the VM's root for the backup commands: the ssh executable, the
        options that come before the target (port, key, known_hosts...), the target
        (user@host), and whether that user IS root.
    #>
    param([Parameter(Mandatory)][string]$SshExe, [string[]]$Options = @(), [Parameter(Mandatory)][string]$Target,
          [switch]$Direct, [string]$SeedPassword = '')
    $opts = @($Options)
    # Long transfers: notice a dead connection instead of hanging on it.
    if (-not ($opts | Where-Object { $_ -match '^ServerAliveInterval=' })) {
        $opts += @('-o', 'ServerAliveInterval=15', '-o', 'ServerAliveCountMax=8')
    }
    return [pscustomobject]@{ SshExe = $SshExe; Options = $opts; Target = $Target; Direct = [bool]$Direct; SeedPassword = $SeedPassword }
}

function Get-ConstructBackupSshArguments {
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)][string[]]$Arguments)
    $remote = Get-ConstructBackupRemoteCommand -Command (Format-ConstructBackupCommand -Arguments $Arguments) `
                  -Direct:$Connection.Direct -SeedPassword $Connection.SeedPassword
    return @(@($Connection.Options) + @($Connection.Target, $remote))
}

function Get-ConstructBackupExitMessage {
    <# What a `construct backup` exit code means (contract 3.3), for an error a user can act on. #>
    param([int]$ExitCode)
    switch ($ExitCode) {
        1   { return 'the VM reported an error (a VM provisioned by an older Construct has no "construct backup" yet: reprovision it once)' }
        2   { return "the VM's construct backup did not understand the request (reprovision the VM to update it)" }
        3   { return 'no backup key is enrolled on the VM' }
        4   { return 'the VM has no host service, or its host does not offer config backups' }
        5   { return 'the VM has not enough free disk space for the backup (skipped)' }
        6   { return 'the host refused the backup (backups disabled, no space or quota left, or another backup in progress)' }
        7   { return 'the transfer between the VM and the host failed' }
        8   { return 'decryption or verification failed (a different key, or a damaged backup)' }
        9   { return 'another backup or restore is running on the VM' }
        10  { return 'the export or restore step on the VM failed' }
        127 { return 'the VM has no construct CLI (reprovision it)' }
        255 { return 'the SSH connection failed' }
        default { return "exit $ExitCode" }
    }
}

function Invoke-ConstructBackupCli {
    <# One `construct backup ...` on the VM over -Connection (see Invoke-ConstructSshProcess). #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)][string[]]$Arguments,
          [AllowEmptyString()][string]$FirstLine = '', [byte[]]$StdinBytes, [string]$InFile, [long]$Offset = 0,
          [string]$OutFile, [ValidateSet('Capture', 'Console')][string]$Stdout = 'Capture')
    $sshArgs = Get-ConstructBackupSshArguments -Connection $Connection -Arguments $Arguments
    if ($OutFile) {
        return (Invoke-ConstructSshStreamToFile -FilePath $Connection.SshExe -ArgumentList $sshArgs -OutFile $OutFile -FirstLine $FirstLine)
    }
    if ($InFile) {
        return (Invoke-ConstructSshStreamFromFile -FilePath $Connection.SshExe -ArgumentList $sshArgs -InFile $InFile -Offset $Offset -FirstLine $FirstLine -Stdout $Stdout)
    }
    return (Invoke-ConstructSshProcess -FilePath $Connection.SshExe -ArgumentList $sshArgs -FirstLine $FirstLine -StdinBytes $StdinBytes -Stdout $Stdout)
}

function Get-ConstructLastOutputLine {
    param([string]$Text)
    $lines = @(($Text -split '\r?\n') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($lines.Count -eq 0) { return '' }
    return $lines[$lines.Count - 1]
}

# ── The actions, over a connection ───────────────────────────────────────────

function Register-ConstructBackupPcKey {
    <# Enrol this PC's key on the VM (`construct backup recipients add`, PEM on stdin).
       Creates the key on first need. Returns the key info. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Connection, [string]$KeyDir)
    $key = Get-ConstructBackupPcKey -KeyDir $KeyDir -Create
    $res = Invoke-ConstructBackupCli -Connection $Connection -Arguments @('recipients', 'add', '--label', $key.Label) `
               -StdinBytes ([System.Text.Encoding]::ASCII.GetBytes($key.PublicKeyPem)) -Stdout Capture
    if ($res.ExitCode -ne 0) {
        throw "Enrolling this PC's backup key on the VM failed: $(Get-ConstructBackupExitMessage $res.ExitCode)."
    }
    $reported = Get-ConstructLastOutputLine $res.Output
    if ($reported -ne $key.Fingerprint) {
        throw "The VM enrolled a key with fingerprint '$reported', not this PC's $($key.Fingerprint)."
    }
    return $key
}

function ConvertFrom-ConstructBackupCreateOutput {
    <# The JSON line `construct backup create` prints: {Id, SizeBytes, Sha256, Trigger,
       GitCredentialHosts}. GitCredentialHosts is $null when the guest did not report it. #>
    param([string]$Text)
    $lines = @(($Text -split '\r?\n') | ForEach-Object { $_.Trim() } | Where-Object { $_ -like '{*' })
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        $doc = $null
        try { $doc = $lines[$i] | ConvertFrom-Json } catch { continue }
        if (-not $doc -or -not (Test-ConstructBackupId ([string]$doc.id))) { continue }
        $hosts = $null
        if ($doc.PSObject.Properties['gitCredentialHosts'] -and $null -ne $doc.gitCredentialHosts) {
            $hosts = @(@($doc.gitCredentialHosts) | Where-Object { $_ } | ForEach-Object { [string]$_ })
        }
        $size = 0L
        try { $size = [long]$doc.sizeBytes } catch { $size = 0L }
        return [pscustomobject]@{ Id = [string]$doc.id; SizeBytes = $size; Sha256 = [string]$doc.sha256; Trigger = [string]$doc.trigger; GitCredentialHosts = $hosts }
    }
    throw 'construct backup create did not report a backup id.'
}

function New-ConstructHostBackup {
    <# Enrol, then `construct backup create --trigger <t>`: a backup stored on the VM's host. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Connection, [ValidateSet('manual', 'pre-reinstall')][string]$Trigger = 'manual', [string]$KeyDir)
    $null = Register-ConstructBackupPcKey -Connection $Connection -KeyDir $KeyDir
    $res = Invoke-ConstructBackupCli -Connection $Connection -Arguments @('create', '--trigger', $Trigger) -Stdout Capture
    if ($res.ExitCode -ne 0) { throw "Backing up the VM to its host failed: $(Get-ConstructBackupExitMessage $res.ExitCode)." }
    return (ConvertFrom-ConstructBackupCreateOutput -Text $res.Output)
}

function Save-ConstructBackupFile {
    <#
        A backup container into -Path: a fresh one (`create --stdout`, no host involved) or
        the host's backup -BackupId (`download <id>`). Enrols first, so the container is
        wrapped for this PC. Written to <path>.partial and renamed only once the header
        checks out. Returns {Path, BackupId, Bytes, Readable}.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)][string]$Path, [string]$BackupId, [string]$KeyDir)
    if ($BackupId -and -not (Test-ConstructBackupId $BackupId)) { throw "'$BackupId' is not a backup id (32 lowercase hex characters)." }
    $null = Register-ConstructBackupPcKey -Connection $Connection -KeyDir $KeyDir
    $full = [System.IO.Path]::GetFullPath($Path)
    $dir = Split-Path -Parent $full
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $partial = "$full.partial"
    $cliArgs = if ($BackupId) { @('download', $BackupId) } else { @('create', '--stdout') }
    $done = $false
    try {
        $res = Invoke-ConstructBackupCli -Connection $Connection -Arguments $cliArgs -OutFile $partial
        if ($res.ExitCode -ne 0) {
            $what = if ($BackupId) { "Downloading backup $BackupId" } else { 'Creating the backup' }
            throw "$what failed: $(Get-ConstructBackupExitMessage $res.ExitCode)."
        }
        $header = Read-ConstructBackupContainerHeader -Path $partial
        if ($BackupId -and $header.Keys.BackupId -cne $BackupId) { throw "The VM sent backup $($header.Keys.BackupId) instead of $BackupId." }
        Move-Item -LiteralPath $partial -Destination $full -Force
        $done = $true
        return [pscustomobject]@{ Path = $full; BackupId = $header.Keys.BackupId; Bytes = $header.Length
                                  Readable = (Test-ConstructBackupKeysReadable -Keys $header.Keys -KeyDir $KeyDir) }
    } finally {
        if (-not $done -and (Test-Path -LiteralPath $partial)) { Remove-Item -LiteralPath $partial -Force -ErrorAction SilentlyContinue }
    }
}

function Restore-ConstructBackupFile {
    <# Restore a container file onto the VM: E from its keys line, then
       `restore --stdin --expect-id <id>` with E and the payload on stdin. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)][string]$Path, [string]$KeyDir)
    $header = Read-ConstructBackupContainerHeader -Path $Path
    $identity = Unprotect-ConstructBackupIdentity -KeysJson $header.KeysJson -KeyDir $KeyDir
    try {
        $res = Invoke-ConstructBackupCli -Connection $Connection -Arguments @('restore', '--stdin', '--expect-id', $header.Keys.BackupId) `
                   -FirstLine $identity -InFile $Path -Offset $header.PayloadOffset -Stdout Console
    } finally { $identity = $null }
    if ($res.ExitCode -ne 0) { throw "Restoring backup $($header.Keys.BackupId) on the VM failed: $(Get-ConstructBackupExitMessage $res.ExitCode)." }
    return $header.Keys.BackupId
}

function Restore-ConstructHostBackup {
    <# Restore the host's backup -BackupId on the VM: E from -KeysJson (GET .../keys),
       then `restore --from-host <id>` with E on stdin. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Connection, [Parameter(Mandatory)][string]$BackupId, [Parameter(Mandatory)][string]$KeysJson, [string]$KeyDir)
    $identity = Unprotect-ConstructBackupIdentity -KeysJson $KeysJson -KeyDir $KeyDir -ExpectedBackupId $BackupId
    try {
        $res = Invoke-ConstructBackupCli -Connection $Connection -Arguments @('restore', '--from-host', $BackupId) -FirstLine $identity -Stdout Console
    } finally { $identity = $null }
    if ($res.ExitCode -ne 0) { throw "Restoring host backup $BackupId on the VM failed: $(Get-ConstructBackupExitMessage $res.ExitCode)." }
}

# ── Host API (contract 3.5) ──────────────────────────────────────────────────

function Resolve-ConstructBackupApiAuth {
    param([string]$BaseUrl, $Auth)
    if ($Auth) { return $Auth }
    $token = ''
    try { $token = Get-ConstructRemoteToken -BaseUrl $BaseUrl } catch { $token = '' }
    if ($token) { return (New-ConstructApiAuth -Mode token -Token $token) }
    return (New-ConstructApiAuth -Mode negotiate)
}

function Get-ConstructHostBackupKeysJson {
    <# GET /vms/{name}/backups/{id}/keys, as the raw JSON text. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUrl, [Parameter(Mandatory)][string]$VmName, [Parameter(Mandatory)][string]$BackupId, $Auth)
    if (-not (Test-ConstructBackupId $BackupId)) { throw "'$BackupId' is not a backup id (32 lowercase hex characters)." }
    $Auth = Resolve-ConstructBackupApiAuth -BaseUrl $BaseUrl -Auth $Auth
    $path = '/vms/' + [Uri]::EscapeDataString($VmName) + '/backups/' + $BackupId + '/keys'
    $raw = Invoke-ConstructApi -BaseUrl $BaseUrl -Method GET -Path $path -Auth $Auth -RawResponse
    if (-not $raw) { throw "The host returned no keys for backup $BackupId." }
    return [string]$raw
}

function Get-ConstructHostBackupList {
    <# GET /vms/{name}/backups: the complete backups, newest first, as
       {Id, CreatedAt, Trigger, SizeBytes, PinnedUntil}. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUrl, [Parameter(Mandatory)][string]$VmName, $Auth)
    $Auth = Resolve-ConstructBackupApiAuth -BaseUrl $BaseUrl -Auth $Auth
    $raw = Invoke-ConstructApi -BaseUrl $BaseUrl -Method GET -Path ('/vms/' + [Uri]::EscapeDataString($VmName) + '/backups') -Auth $Auth -RawResponse
    if (-not $raw) { return @() }
    $doc = [string]$raw | ConvertFrom-Json
    $out = New-Object System.Collections.Generic.List[object]
    foreach ($b in @($doc.backups)) {
        if (-not $b -or -not (Test-ConstructBackupId ([string]$b.id))) { continue }
        $size = 0L
        try { $size = [long]$b.sizeBytes } catch { $size = 0L }
        $out.Add([pscustomobject]@{ Id = [string]$b.id; CreatedAt = $b.createdAt; Trigger = [string]$b.trigger; SizeBytes = $size; PinnedUntil = $b.pinnedUntil })
    }
    return $out.ToArray()
}

function Find-ConstructReadableHostBackup {
    <# The newest of the host's backups of -VmName that one of this PC's keys can open, or $null. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUrl, [Parameter(Mandatory)][string]$VmName, $Auth, [string]$KeyDir)
    $mine = @(Get-ConstructBackupPrivateKeyFiles -KeyDir $KeyDir)
    if ($mine.Count -eq 0) { return $null }
    foreach ($b in @(Get-ConstructHostBackupList -BaseUrl $BaseUrl -VmName $VmName -Auth $Auth)) {
        try {
            $keys = ConvertFrom-ConstructBackupKeysJson -Json (Get-ConstructHostBackupKeysJson -BaseUrl $BaseUrl -VmName $VmName -BackupId $b.Id -Auth $Auth)
        } catch { continue }
        if ($keys.BackupId -ceq $b.Id -and (Test-ConstructBackupKeysReadable -Keys $keys -KeyDir $KeyDir)) { return $b }
    }
    return $null
}

function Format-ConstructBackupSize {
    param([long]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:N1} GiB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:N1} MiB' -f ($Bytes / 1MB)) }
    if ($Bytes -ge 1KB) { return ('{0:N0} KiB' -f ($Bytes / 1KB)) }
    return "$Bytes bytes"
}

function Format-ConstructBackupTime {
    <# A backup's createdAt (string or the DateTime PowerShell 7's ConvertFrom-Json makes of
       it) as local time. #>
    param($Value)
    if ($null -eq $Value -or "$Value" -eq '') { return 'an unknown time' }
    $dt = $null
    if ($Value -is [DateTime]) { $dt = $Value }
    elseif ($Value -is [DateTimeOffset]) { $dt = $Value.UtcDateTime }
    else {
        $parsed = [DateTimeOffset]::MinValue
        if ([DateTimeOffset]::TryParse([string]$Value, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$parsed)) { $dt = $parsed.UtcDateTime }
    }
    if (-not $dt) { return [string]$Value }
    if ($dt.Kind -eq [DateTimeKind]::Unspecified) { $dt = [DateTime]::SpecifyKind($dt, [DateTimeKind]::Utc) }
    return $dt.ToLocalTime().ToString('yyyy-MM-dd HH:mm')
}

# ── Git credential hosts (contract 3.8) ──────────────────────────────────────

function Test-ConstructBackupGitHostsCover {
    <#
        Does a fresh host backup's credential store (the host names `create` reported in
        gitCredentialHosts; no secrets) cover every http(s) repo host in -Urls? The
        host-backup counterpart of Test-BackupHasGitCredentials: $false for an empty store,
        $true when no URL needs a credential. A reported name matches a URL's host, or its
        host:port.
    #>
    param([AllowNull()][string[]]$Hosts, [AllowNull()][string[]]$Urls)
    $known = @{}
    foreach ($h in @($Hosts)) {
        $t = ([string]$h).Trim().ToLowerInvariant()
        if ($t) { $known[$t] = $true }
    }
    if ($known.Count -eq 0) { return $false }
    foreach ($url in @($Urls)) {
        if ([string]::IsNullOrWhiteSpace($url)) { continue }
        $u = $null
        if (-not [uri]::TryCreate($url.Trim(), [UriKind]::Absolute, [ref]$u) -or $u.Scheme -notin @('http', 'https')) { continue }
        if (-not ($known.ContainsKey($u.Host.ToLowerInvariant()) -or $known.ContainsKey($u.Authority.ToLowerInvariant()))) { return $false }
    }
    return $true
}
