#Requires -Version 5.1
<#
    The-Construct -- CLIENT FOR THE REMOTE HOST SERVICE (`constructd`).

    The PowerShell half of the remote backend: one HTTP client with three credential
    providers, a DPAPI-protected token store, certificate PINNING, and a job waiter.
    drivers\hyperv-remote\HyperVRemote.Driver.ps1 is built entirely on top of it, and
    Auto-Install.ps1's remote flow uses it directly for enrolment.

    Dot-source it:

        . "$PSScriptRoot\lib\AgentVm.Remote.ps1"

    Self-contained on purpose (no dependency on AgentVm.Common.ps1 or
    AgentVm.Instances.ps1) so the driver, the installer and the tests can each load it
    on its own. Windows PowerShell 5.1 AND PowerShell 7 compatible -- which is not a
    formality here, because the two editions pin certificates differently (see
    "TLS pinning" below and docs\remote-host.md section 5).

    DELIBERATELY NOT Set-StrictMode: this file is dot-sourced into Auto-Install.ps1's
    script scope, and turning strict mode on there would change how the LOCAL install
    path behaves. Every access below is written to be safe without it.

    ── Functions ────────────────────────────────────────────────────────────────
      ConvertTo-ConstructServiceUrl  -Value            -> normalised base URL
      Get-ConstructRemoteHostSlug    -BaseUrl          -> file-name-safe host key
      Get-ConstructRemoteStoreDir                      -> %LOCALAPPDATA%\The-Construct\remote
      Save-/Get-/Remove-ConstructRemoteToken           -> DPAPI token store (CurrentUser)
      Save-/Get-/Remove-ConstructRemotePin             -> pinned SHA-256 thumbprint
      Get-ConstructRemoteFingerprint -BaseUrl          -> the host's live SHA-256 fingerprint
      Format-ConstructFingerprint / Test-ConstructFingerprintMatch
      New-ConstructApiAuth -Mode negotiate|token|credential
      Invoke-ConstructApi  -BaseUrl -Method -Path [-Body] -Auth [-NoThrow]
      Get-ConstructApiLastStatus / Get-ConstructApiLastError
      Wait-ConstructJob    -BaseUrl -JobId -Auth       -> the job's result object

    ── TLS pinning ──────────────────────────────────────────────────────────────
    The service presents a SELF-SIGNED certificate, so chain validation cannot work.
    The client pins its SHA-256 thumbprint at enrolment and enforces it on every call.

      * Windows PowerShell 5.1 -- Invoke-WebRequest goes through ServicePointManager,
        so the pin is checked INSIDE the handshake: the validation callback is set for
        the duration of the call (restored in a finally) and a mismatch fails the
        request before it is sent.
      * PowerShell 7 -- Invoke-WebRequest uses SocketsHttpHandler, which IGNORES
        ServicePointManager. So the certificate is read over a TLS connection of our
        own and compared with the pin BEFORE the request, which then runs with
        -SkipCertificateCheck. The verification is real; it happens one connection
        earlier.

    NO PIN, NO CALL: an https base URL with no pinned thumbprint is refused, naming the
    enrolment step. A CHANGED fingerprint is a hard failure naming both values -- never
    a prompt to click through.

    NO TLS AT ALL is refused too, unless the service is on THIS machine
    (Assert-ConstructTransportSafe): plain http to a remote host would carry a bearer
    token or a Windows credential in clear, with no certificate to pin. Loopback keeps
    working, which is what the fake service the tests drive listens on.

    ── Secrets ──────────────────────────────────────────────────────────────────
    A token is stored DPAPI-encrypted (CurrentUser) and is never written in plaintext,
    never echoed, and never included in an error message. Kerberos and an explicit
    credential store nothing at all.
#>

# The port the service listens on by default (service/README.md, Constructd:ListenUrl).
$script:ConstructRemoteDefaultPort = 7462
# The last HTTP status / error text Invoke-ConstructApi saw, so a caller that used
# -NoThrow can branch on the status (401 -> offer another credential) instead of
# pattern-matching a message.
$script:ConstructApiLastStatus = 0
$script:ConstructApiLastError  = ""
$script:ConstructApiLastProblem = @{ Status = 0; Code = ""; Class = "none"; Detail = "" }
# One warning per session for the http + Windows-credential case (see Invoke-ConstructApi).
$script:ConstructApiWarnedUnencrypted = $false

function Test-ConstructPwshCore {
    <# PowerShell 7+ (SocketsHttpHandler) rather than Windows PowerShell 5.1. #>
    return [bool]($PSVersionTable.PSVersion.Major -ge 6)
}

function ConvertTo-ConstructServiceUrl {
    <#
        Normalise what a user typed into the service's base URL:

            buildbox                       -> https://buildbox:7462
            buildbox:7462                  -> https://buildbox:7462
            https://buildbox.local:7462/   -> https://buildbox.local:7462
            http://127.0.0.1:7999          -> http://127.0.0.1:7999   (dev/fake only)

        A bare name gets https and the service's default port, because that is what the
        installer creates and a typo'd scheme must not silently downgrade the transport.
        Any trailing path/slash is dropped -- every call appends /api/v1/... itself.
        Throws on something that is not a URL at all. Pure.
    #>
    [CmdletBinding()]
    param([string]$Value)

    $v = ""
    if ($null -ne $Value) { $v = ([string]$Value).Trim() }
    if (-not $v) { throw "No service URL given." }
    if ($v -notmatch '^[A-Za-z][A-Za-z0-9+.-]*://') { $v = "https://$v" }

    $uri = $null
    if (-not [System.Uri]::TryCreate($v, [System.UriKind]::Absolute, [ref]$uri)) {
        throw "'$Value' is not a usable service URL (expected something like https://buildbox.example.local:7462)."
    }
    if ($uri.Scheme -ne 'https' -and $uri.Scheme -ne 'http') {
        throw "Service URL '$Value' must use https (or http for a local development service), not '$($uri.Scheme)'."
    }
    if (-not $uri.Host) {
        throw "Service URL '$Value' has no host name."
    }
    # IsDefaultPort is true when the user typed no port at all; https then means 443,
    # which is NOT what the service listens on -- so supply the service's own default.
    $port = $uri.Port
    if ($uri.IsDefaultPort) { $port = $script:ConstructRemoteDefaultPort }
    # IPv6 literals must stay bracketed in a URL.
    $hostPart = $uri.Host
    if ($hostPart.Contains(':') -and -not $hostPart.StartsWith('[')) { $hostPart = "[$hostPart]" }
    return ("{0}://{1}:{2}" -f $uri.Scheme, $hostPart, $port)
}

function Test-ConstructLoopbackHost {
    <#
        Is this host name THIS machine, reached over the loopback interface?
        "localhost", anything in 127.0.0.0/8, and ::1 (in any spelling).

        It exists for one decision: plain http is tolerable ONLY when the bytes never
        leave the machine -- the fake service the tests drive, or a development run. For
        anything else http would carry a bearer token, or a Windows credential, in clear
        over the wire AND with no certificate to pin, which is precisely the situation
        pinning exists to prevent. Pure.
    #>
    [CmdletBinding()]
    param([string]$HostName)
    $h = ""
    if ($null -ne $HostName) { $h = ([string]$HostName).Trim().Trim('[', ']').ToLowerInvariant() }
    if (-not $h) { return $false }
    if ($h -eq 'localhost') { return $true }
    $ip = $null
    if ([System.Net.IPAddress]::TryParse($h, [ref]$ip)) {
        return [bool][System.Net.IPAddress]::IsLoopback($ip)
    }
    return $false
}

function Assert-ConstructTransportSafe {
    <#
        Refuse to send a credential over a transport that protects neither its contents
        nor the identity of the far end. https is fine (the pin is checked separately);
        http is fine ONLY to loopback. Everything else throws BEFORE any credential is
        attached to a request.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUrl)
    $uri = [System.Uri](ConvertTo-ConstructServiceUrl -Value $BaseUrl)
    if ($uri.Scheme -eq 'https') { return }
    if (Test-ConstructLoopbackHost -HostName $uri.DnsSafeHost) { return }
    throw "Refusing to talk to the Construct host service at $($uri.Scheme)://$($uri.DnsSafeHost):$($uri.Port) over plain http: your credentials would cross the network unencrypted, and there is no certificate to verify the host with. Use https (plain http is accepted only for a service on this machine)."
}

function Get-ConstructRemoteHostSlug {
    <#
        A file-name-safe key for one service host, so two hosts never share a token or
        a pin file: "https://buildbox.example.local:7462" -> "buildbox.example.local_7462".
        Lowercased; anything outside [a-z0-9._-] becomes '_' (an IPv6 literal's colons
        and brackets included). Pure.
    #>
    [CmdletBinding()]
    param([string]$BaseUrl)
    $normal = ConvertTo-ConstructServiceUrl -Value $BaseUrl
    $uri = [System.Uri]$normal
    # DnsSafeHost, not Host: .NET's Host KEEPS an IPv6 literal's brackets (and so does
    # Node's URL#hostname). Both clients key off the BARE form, so they derive the same
    # slug -- which is what lets them share one pin file per host.
    $slug = ("{0}_{1}" -f $uri.DnsSafeHost, $uri.Port).ToLowerInvariant()
    return ([regex]::Replace($slug, '[^a-z0-9._-]', '_'))
}

function Get-ConstructRemoteStoreDir {
    <#
        %LOCALAPPDATA%\The-Construct\remote -- the per-user store for remote-host
        credentials and pins, beside the existing config\ dir and instances.json.
        Pure path math (the directory is created on write).
    #>
    $base = $env:LOCALAPPDATA
    if (-not $base) { $base = $env:TEMP }
    if (-not $base) { $base = [System.IO.Path]::GetTempPath() }
    return (Join-Path (Join-Path $base "The-Construct") "remote")
}

function Get-ConstructRemoteTokenPath {
    [CmdletBinding()]
    param([Parameter(Mandatory, ParameterSetName="Url")][string]$BaseUrl,
        [Parameter(Mandatory, ParameterSetName="Slug")][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$')][string]$Slug, [string]$StoreDir)
    if (-not $StoreDir) { $StoreDir = Get-ConstructRemoteStoreDir }
    $tokenSlug = if ($Slug) { $Slug } else { Get-ConstructRemoteHostSlug -BaseUrl $BaseUrl }
    return (Join-Path $StoreDir ($tokenSlug + ".token"))
}

function Get-ConstructRemotePinPath {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUrl, [string]$StoreDir)
    if (-not $StoreDir) { $StoreDir = Get-ConstructRemoteStoreDir }
    return (Join-Path $StoreDir ((Get-ConstructRemoteHostSlug -BaseUrl $BaseUrl) + ".pin"))
}

function Test-ConstructDpapiAvailable {
    <#
        Is Windows DPAPI usable here? [Security.Cryptography.ProtectedData] exists on
        .NET but throws PlatformNotSupportedException off Windows, and on Windows
        PowerShell 5.1 its assembly has to be loaded first. Probed rather than assumed,
        so the token store can say WHY it is unavailable instead of failing obscurely.
    #>
    try {
        if (-not ('System.Security.Cryptography.ProtectedData' -as [type])) {
            Add-Type -AssemblyName System.Security -ErrorAction Stop
        }
    } catch {
        return $false
    }
    if (-not ('System.Security.Cryptography.ProtectedData' -as [type])) { return $false }
    # A real round trip: the type resolving proves nothing on a non-Windows runtime.
    try {
        $probe = [byte[]]@(1, 2, 3)
        $enc = [System.Security.Cryptography.ProtectedData]::Protect($probe, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        [void][System.Security.Cryptography.ProtectedData]::Unprotect($enc, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser)
        return $true
    } catch {
        return $false
    }
}

# Small protection seams keep the file-format round trip testable off Windows.
function Protect-ConstructTokenBytes {
    param([byte[]]$Bytes)
    return ,([System.Security.Cryptography.ProtectedData]::Protect($Bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
}
function Unprotect-ConstructTokenBytes {
    param([byte[]]$Bytes)
    return ,([System.Security.Cryptography.ProtectedData]::Unprotect($Bytes, $null, [System.Security.Cryptography.DataProtectionScope]::CurrentUser))
}

function Save-ConstructRemoteToken {
    <#
        Store an API token for one service host, DPAPI-encrypted for the CURRENT USER
        (so no other account on this machine -- and no copy of the file elsewhere --
        can read it). The plaintext never touches the disk and is never echoed.

        Throws where DPAPI is unavailable (non-Windows): storing a bearer token in
        plaintext would be worse than not storing it, so the caller must fall back to
        passing the token per run.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory, ParameterSetName="Url")][string]$BaseUrl,
        [Parameter(Mandatory, ParameterSetName="Slug")][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$')][string]$Slug,
        [Parameter(Mandatory)][string]$Token,
        [string]$StoreDir
    )
    if ([string]::IsNullOrWhiteSpace($Token)) { throw "Refusing to store an empty API token." }
    if (-not (Test-ConstructDpapiAvailable)) {
        throw "Cannot store the API token: Windows DPAPI (per-user encryption) is not available on this system. Pass the token explicitly instead -- The Construct never writes it in plaintext."
    }
    $key = @{}
    if ($Slug) { $key.Slug = $Slug } else { $key.BaseUrl = $BaseUrl }
    $path = Get-ConstructRemoteTokenPath @key -StoreDir $StoreDir
    $dir  = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($Token)
    $enc   = Protect-ConstructTokenBytes $bytes
    # Base64 text, not raw bytes: the file stays copy-pasteable/diffable and Set-Content
    # cannot corrupt it with an encoding conversion.
    [System.IO.File]::WriteAllText($path, [Convert]::ToBase64String($enc))
    # Zero the plaintext copy we made; the caller's own string is its own business.
    [Array]::Clear($bytes, 0, $bytes.Length)
    return $path
}

function Get-ConstructRemoteToken {
    <#
        The stored API token for a host, or "" when there is none (or it cannot be
        decrypted -- a file copied from another account/machine). Never throws.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory, ParameterSetName="Url")][string]$BaseUrl,
        [Parameter(Mandatory, ParameterSetName="Slug")][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$')][string]$Slug, [string]$StoreDir)
    $key = @{}
    if ($Slug) { $key.Slug = $Slug } else { $key.BaseUrl = $BaseUrl }
    $path = Get-ConstructRemoteTokenPath @key -StoreDir $StoreDir
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return "" }
    try {
        $b64 = [System.IO.File]::ReadAllText($path).Trim()
        if (-not $b64) { return "" }
        $enc = [Convert]::FromBase64String($b64)
        $raw = Unprotect-ConstructTokenBytes $enc
        return [System.Text.Encoding]::UTF8.GetString($raw)
    } catch {
        return ""
    }
}

function Remove-ConstructRemoteToken {
    [CmdletBinding()]
    param([Parameter(Mandatory, ParameterSetName="Url")][string]$BaseUrl,
        [Parameter(Mandatory, ParameterSetName="Slug")][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,127}$')][string]$Slug, [string]$StoreDir)
    $key = @{}
    if ($Slug) { $key.Slug = $Slug } else { $key.BaseUrl = $BaseUrl }
    $path = Get-ConstructRemoteTokenPath @key -StoreDir $StoreDir
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
    return $path
}

function Format-ConstructFingerprint {
    <#
        One canonical spelling of a SHA-256 certificate fingerprint: uppercase hex,
        colon-separated pairs. Accepts the same value with colons, spaces or neither,
        so a pin file written by any version -- or pasted by a human -- compares equal.
        Returns "" for anything that is not 64 hex digits. Pure.
    #>
    [CmdletBinding()]
    param([string]$Value)
    $v = ""
    if ($null -ne $Value) { $v = ([string]$Value) }
    $hex = ([regex]::Replace($v, '[^0-9A-Fa-f]', '')).ToUpperInvariant()
    if ($hex.Length -ne 64) { return "" }
    $pairs = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $hex.Length; $i += 2) { $pairs.Add($hex.Substring($i, 2)) }
    return ($pairs -join ':')
}

function Test-ConstructFingerprintMatch {
    <# Do two fingerprint spellings denote the same certificate? Pure; "" never matches. #>
    [CmdletBinding()]
    param([string]$Expected, [string]$Actual)
    $a = Format-ConstructFingerprint -Value $Expected
    $b = Format-ConstructFingerprint -Value $Actual
    if (-not $a -or -not $b) { return $false }
    return [bool]($a -ceq $b)
}

function Get-ConstructCertificateFingerprint {
    <# SHA-256 fingerprint of an X509 certificate, in the canonical spelling. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Certificate)
    $cert = $Certificate
    if (-not ($cert -is [System.Security.Cryptography.X509Certificates.X509Certificate2])) {
        $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($Certificate)
    }
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($cert.GetRawCertData())
    } finally {
        $sha.Dispose()
    }
    return (Format-ConstructFingerprint -Value ((($hash | ForEach-Object { $_.ToString('x2') })) -join ''))
}

function Get-ConstructRemoteFingerprint {
    <#
        The SHA-256 fingerprint the service is presenting RIGHT NOW, read over a TLS
        connection of our own (deliberately accepting any certificate -- reading it is
        the whole point) so the user can compare it with what the admin published and
        confirm it once.

        Returns "" for an http:// base URL: there is no certificate to pin, which is
        why plain http is only for a local development/fake service.
        Throws when the host cannot be reached -- the caller needs to tell "wrong URL"
        apart from "wrong certificate".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [int]$TimeoutMs = 10000
    )
    $normal = ConvertTo-ConstructServiceUrl -Value $BaseUrl
    $uri = [System.Uri]$normal
    if ($uri.Scheme -ne 'https') { return "" }

    $watch = [Diagnostics.Stopwatch]::StartNew()
    $client = New-Object System.Net.Sockets.TcpClient
    $ssl = $null
    try {
        # DnsSafeHost: a socket (and SNI) needs the bare host, without an IPv6 literal's
        # URL brackets.
        $dialHost = $uri.DnsSafeHost
        $iar = $client.BeginConnect($dialHost, $uri.Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs)) {
            throw [TimeoutException]::new("Timed out connecting to $($dialHost):$($uri.Port).")
        }
        $client.EndConnect($iar)
        # Accept ANY certificate here: this call exists to LOOK at the certificate.
        # Nothing is trusted as a result -- the caller compares the fingerprint.
        $validate = [System.Net.Security.RemoteCertificateValidationCallback]{ param($s, $c, $ch, $e) return $true }
        $ssl = New-Object System.Net.Security.SslStream($client.GetStream(), $false, $validate)
        $remainingMs = [int][Math]::Max(1, $TimeoutMs - $watch.ElapsedMilliseconds)
        $ssl.ReadTimeout = $remainingMs; $ssl.WriteTimeout = $remainingMs
        $ssl.AuthenticateAsClient($dialHost)
        $remote = $ssl.RemoteCertificate
        if (-not $remote) { throw "The service at $normal presented no certificate." }
        return (Get-ConstructCertificateFingerprint -Certificate $remote)
    } catch {
        $script:ConstructCertificateFailureClass = Get-ConstructApiFailureClass $_.Exception
        throw "Could not read the certificate of $normal`: $($_.Exception.Message)"
    } finally {
        if ($ssl) { try { $ssl.Dispose() } catch { } }
        try { $client.Close() } catch { }
    }
}

function Get-ConstructRemoteCertificatePem {
    <#
        The host service's certificate as PEM, for a GUEST to verify the service with
        (CONSTRUCT_SERVICE_CA_FILE: the guest's `construct expose` and idle heartbeat pass
        it to curl --cacert). Read over a fresh TLS handshake and REFUSED unless its
        SHA-256 fingerprint matches -Pin (default: the fingerprint pinned on this PC), so
        the guest only ever trusts what this PC already confirmed at enrolment.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [string]$Pin = "",
        [string]$StoreDir,
        [int]$TimeoutMs = 10000
    )
    $normal = ConvertTo-ConstructServiceUrl -Value $BaseUrl
    $uri = [System.Uri]$normal
    if ($uri.Scheme -ne 'https') { return "" }
    $expected = $Pin
    if (-not $expected) { $expected = Get-ConstructRemotePin -BaseUrl $normal -StoreDir $StoreDir }
    if (-not $expected) { throw "No certificate fingerprint is pinned for $normal on this PC; add the host first." }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $client = New-Object System.Net.Sockets.TcpClient
    $ssl = $null
    try {
        $dialHost = $uri.DnsSafeHost
        $iar = $client.BeginConnect($dialHost, $uri.Port, $null, $null)
        if (-not $iar.AsyncWaitHandle.WaitOne($TimeoutMs)) { throw [TimeoutException]::new("Timed out connecting to $($dialHost):$($uri.Port).") }
        $client.EndConnect($iar)
        $validate = [System.Net.Security.RemoteCertificateValidationCallback]{ param($s, $c, $ch, $e) return $true }
        $ssl = New-Object System.Net.Security.SslStream($client.GetStream(), $false, $validate)
        $remainingMs = [int][Math]::Max(1, $TimeoutMs - $watch.ElapsedMilliseconds)
        $ssl.ReadTimeout = $remainingMs; $ssl.WriteTimeout = $remainingMs
        $ssl.AuthenticateAsClient($dialHost)
        $remote = $ssl.RemoteCertificate
        if (-not $remote) { throw "The service at $normal presented no certificate." }
        $actual = Get-ConstructCertificateFingerprint -Certificate $remote
        if (-not (Test-ConstructFingerprintMatch -Expected $expected -Actual $actual)) {
            throw "Certificate fingerprint mismatch for $normal (pinned $expected, presented $actual)."
        }
        $b64 = [Convert]::ToBase64String($remote.GetRawCertData())
        $lines = New-Object System.Collections.Generic.List[string]
        $lines.Add("-----BEGIN CERTIFICATE-----")
        for ($i = 0; $i -lt $b64.Length; $i += 64) { $lines.Add($b64.Substring($i, [Math]::Min(64, $b64.Length - $i))) }
        $lines.Add("-----END CERTIFICATE-----")
        return (($lines -join "`n") + "`n")
    } catch {
        $script:ConstructCertificateFailureClass = Get-ConstructApiFailureClass $_.Exception
        throw "Could not read the certificate of $normal`: $($_.Exception.Message)"
    } finally {
        if ($ssl) { try { $ssl.Dispose() } catch { } }
        try { $client.Close() } catch { }
    }
}

function Save-ConstructRemotePin {
    <#
        Pin a host's certificate fingerprint. NOT a secret (it is a public hash), so it
        is stored as plain text next to the token -- the token is what DPAPI protects.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [Parameter(Mandatory)][string]$Fingerprint,
        [string]$StoreDir
    )
    $fp = Format-ConstructFingerprint -Value $Fingerprint
    if (-not $fp) { throw "'$Fingerprint' is not a SHA-256 certificate fingerprint (64 hex digits)." }
    $path = Get-ConstructRemotePinPath -BaseUrl $BaseUrl -StoreDir $StoreDir
    $dir  = Split-Path -Parent $path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllText($path, $fp)
    return $path
}

function Get-ConstructRemotePin {
    <# The pinned fingerprint for a host, or "" when it has never been enrolled. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUrl, [string]$StoreDir)
    $path = Get-ConstructRemotePinPath -BaseUrl $BaseUrl -StoreDir $StoreDir
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { return "" }
    try { return (Format-ConstructFingerprint -Value ([System.IO.File]::ReadAllText($path))) }
    catch { return "" }
}

function Remove-ConstructRemotePin {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUrl, [string]$StoreDir)
    $path = Get-ConstructRemotePinPath -BaseUrl $BaseUrl -StoreDir $StoreDir
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
    return $path
}

function New-ConstructApiAuth {
    <#
        A CREDENTIAL PROVIDER for Invoke-ConstructApi. The driver and the installer take
        one of these rather than a scheme name, which is the seam plan section 4.4 asks
        for: a future OIDC or Proxmox-token provider is a new Mode here and nothing else.

          -Mode negotiate   Kerberos/NTLM as the CURRENT PROCESS IDENTITY
                            (Invoke-WebRequest -UseDefaultCredentials). Stores nothing.
          -Mode credential  an explicit domain user + password (-Credential). Held for
                            this run only; never written anywhere.
          -Mode token       an admin-issued API token as `Authorization: Bearer <t>`.
                            -Token wins; otherwise the DPAPI store for -BaseUrl is read.

        -Headers adds request headers to every call made with this provider (used by the
        end-to-end tests against the fake service, and by hosts that sit behind another
        authenticator). It cannot set Authorization -- that is the provider's own job.
        Pure: no network call, no prompt.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('negotiate', 'token', 'credential')]
        [string]$Mode = 'negotiate',
        [string]$Token,
        [System.Management.Automation.PSCredential]$Credential,
        [string]$BaseUrl,
        [hashtable]$Headers,
        [string]$StoreDir
    )

    $extra = @{}
    if ($Headers) {
        foreach ($k in $Headers.Keys) {
            if ([string]$k -ieq 'Authorization') {
                throw "New-ConstructApiAuth -Headers cannot set 'Authorization'; use -Mode token."
            }
            $extra[[string]$k] = [string]$Headers[$k]
        }
    }

    switch ($Mode) {
        'token' {
            $t = $Token
            if ([string]::IsNullOrWhiteSpace($t) -and $BaseUrl) {
                $t = Get-ConstructRemoteToken -BaseUrl $BaseUrl -StoreDir $StoreDir
            }
            if ([string]::IsNullOrWhiteSpace($t)) {
                throw "No API token available for this host. Pass -Token, or enrol the host once so the token is stored."
            }
            return @{ Mode = 'token'; Token = $t; Headers = $extra }
        }
        'credential' {
            if (-not $Credential) { throw "-Mode credential needs a -Credential." }
            return @{ Mode = 'credential'; Credential = $Credential; Headers = $extra }
        }
        default {
            return @{ Mode = 'negotiate'; Headers = $extra }
        }
    }
}

function Get-ConstructApiErrorInfo {
    <#
        Pull the STATUS and a human message out of a failed Invoke-WebRequest, across
        both editions: Windows PowerShell 5.1 raises a WebException carrying an
        HttpWebResponse (body readable from its stream), PowerShell 7 raises an
        HttpResponseException and puts the body in $_.ErrorDetails.Message.

        RFC 7807 problem documents are unwrapped to their title/detail, because that is
        the sentence the service wrote for the user. Never throws; a transport failure
        (no route, TLS refused) comes back as status 0 with the exception's message.
    #>
    [CmdletBinding()]
    param($ErrorRecord)

    $status = 0
    $body   = ""
    $msg    = ""

    try { $msg = [string]$ErrorRecord.Exception.Message } catch { $msg = "" }

    $resp = $null
    try { $resp = $ErrorRecord.Exception.Response } catch { $resp = $null }
    if ($resp) {
        try { $status = [int]$resp.StatusCode } catch { $status = 0 }
    }

    # PS 7 (and PS 5.1 for some failures) surfaces the body here.
    try {
        if ($ErrorRecord.ErrorDetails -and $ErrorRecord.ErrorDetails.Message) {
            $body = [string]$ErrorRecord.ErrorDetails.Message
        }
    } catch { }

    # PS 5.1: read it off the HttpWebResponse stream.
    if (-not $body -and $resp) {
        try {
            $stream = $resp.GetResponseStream()
            if ($stream) {
                $reader = New-Object System.IO.StreamReader($stream)
                try { $body = $reader.ReadToEnd() } finally { $reader.Dispose() }
            }
        } catch { }
    }

    $code = ""
    $detail = ""
    if ($body) {
        try {
            $doc = $body | ConvertFrom-Json
            if ($doc.PSObject.Properties['code'] -and [string]$doc.code -cmatch '^[a-z0-9-]{1,100}$') { $code = [string]$doc.code }
            $parts = New-Object System.Collections.Generic.List[string]
            foreach ($f in @('title', 'detail')) {
                $p = $null
                try { $p = $doc.PSObject.Properties[$f] } catch { $p = $null }
                if ($p -and $p.Value) { $parts.Add([string]$p.Value) }
            }
            if ($parts.Count -gt 0) { $detail = ($parts -join ' -- ') }
            if (-not $status) {
                $sp = $null
                try { $sp = $doc.PSObject.Properties['status'] } catch { $sp = $null }
                if ($sp -and $sp.Value) { try { $status = [int]$sp.Value } catch { } }
            }
        } catch {
            # Not JSON: keep the raw body, trimmed, as the detail.
            $detail = $body.Trim()
            if ($detail.Length -gt 400) { $detail = $detail.Substring(0, 400) + "..." }
        }
    }

    $text = $detail
    if (-not $text) { $text = $msg }
    return @{ Status = $status; Message = $text; Body = $body; Code = $code }
}

function Get-ConstructApiLastStatus {
    <# HTTP status of the last Invoke-ConstructApi call (0 = transport failure). The
       reason -NoThrow callers can branch on 401 rather than on a message. #>
    return [int]$script:ConstructApiLastStatus
}

function Get-ConstructApiLastError {
    <# The last Invoke-ConstructApi failure's message ("" after a success). #>
    return [string]$script:ConstructApiLastError
}

function Resolve-ConstructApiPin {
    <#
        The fingerprint that must be presented for this call: -Pin when given, else the
        stored one. An https host with neither is REFUSED -- pinning is the only thing
        standing in for chain validation against a self-signed certificate, so "no pin"
        can never mean "trust anything". http (a local development/fake service) has no
        certificate at all and returns "".
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUrl, [string]$Pin, [string]$StoreDir)

    # A transport that cannot carry a credential safely is refused here, at the same
    # choke point as the pin -- so no call can reach the network without passing both.
    Assert-ConstructTransportSafe -BaseUrl $BaseUrl
    $uri = [System.Uri](ConvertTo-ConstructServiceUrl -Value $BaseUrl)
    if ($uri.Scheme -ne 'https') { return "" }

    $fp = Format-ConstructFingerprint -Value $Pin
    if (-not $fp) { $fp = Get-ConstructRemotePin -BaseUrl $BaseUrl -StoreDir $StoreDir }
    if (-not $fp) {
        throw "The certificate of $($uri.DnsSafeHost) has not been confirmed on this machine. Add the host once (Auto-Install.ps1 -Backend hyperv-remote, or 'The Construct: Add Remote Host') so its fingerprint can be checked and pinned."
    }
    return $fp
}

function Get-ConstructPinValidatorCallback {
    <#
        A RemoteCertificateValidationCallback that accepts exactly the certificate whose
        SHA-256 fingerprint is -Expected, as a COMPILED delegate (Add-Type, once per
        process). Windows PowerShell 5.1 invokes this callback on a worker thread without
        a runspace, where a PowerShell scriptblock cannot run; the compiled validator can.
        Falls back to a scriptblock only when no C# compiler is available, which keeps
        older hosts exactly where they were.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Expected)
    $fp = Format-ConstructFingerprint -Value $Expected
    if (-not $fp) { throw "'$Expected' is not a SHA-256 certificate fingerprint." }
    if (-not ('Construct.PinValidator' -as [type])) {
        try {
            Add-Type -TypeDefinition @'
using System;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
namespace Construct {
    public static class PinValidator {
        // The pinned SHA-256 fingerprint, "AA:BB:...", set before every call.
        public static string Expected;
        public static bool Rejected;
        public static bool Validate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors errors) {
            if (certificate == null || string.IsNullOrEmpty(Expected)) { return false; }
            byte[] hash;
            using (var sha = SHA256.Create()) { hash = sha.ComputeHash(certificate.GetRawCertData()); }
            var actual = BitConverter.ToString(hash).Replace("-", ":");
            var matched = string.Equals(actual, Expected, StringComparison.OrdinalIgnoreCase);
            if (!matched) Rejected = true;
            return matched;
        }
    }
}
'@ -ErrorAction Stop
        } catch {
            Write-Warning "Could not compile the certificate pin validator ($($_.Exception.Message)); using the scriptblock fallback."
        }
    }
    $type = 'Construct.PinValidator' -as [type]
    if ($type) {
        $type::Expected = $fp
        if ($type.GetField('Rejected')) { $type::Rejected = $false }
        return [System.Delegate]::CreateDelegate([System.Net.Security.RemoteCertificateValidationCallback], $type.GetMethod('Validate'))
    }
    $pinned = $fp
    return {
        param($sender, $certificate, $chain, $errors)
        if (-not $certificate) { return $false }
        $actual = Get-ConstructCertificateFingerprint -Certificate $certificate
        return (Test-ConstructFingerprintMatch -Expected $pinned -Actual $actual)
    }.GetNewClosure()
}

function Invoke-ConstructApi {
    <#
        One call against the host service. Returns the parsed response body ($null for
        an empty one).

        -BaseUrl   the service, in any spelling ConvertTo-ConstructServiceUrl accepts
        -Method    GET | POST | PUT | DELETE ...
        -Path      "/whoami" or "whoami" or "/api/v1/whoami" -- /api/v1 is prefixed when
                   it is missing, so callers can write the route as the README does
        -Body      a hashtable/object, serialised as JSON (a string is sent verbatim)
        -Auth      a provider from New-ConstructApiAuth (default: negotiate)
        -Pin       an explicit expected fingerprint (default: the stored pin)
        -RawResponse return the unchanged response JSON (preserves empty/singleton arrays).
        -Bounded   include the certificate preflight in TimeoutSec (source operations).
        -NoThrow   return $null instead of throwing; the status and message stay
                   readable via Get-ConstructApiLastStatus / Get-ConstructApiLastError.
                   This is how the enrolment flow tries Negotiate and falls back on 401.

        The certificate pin is enforced for every https call -- see the header comment
        for why the two PowerShell editions do it differently.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        $Body,
        $Auth,
        [string]$Pin,
        [string]$StoreDir,
        [int]$TimeoutSec = 100,
        [switch]$NoThrow,
        [switch]$RawResponse,
        [switch]$Bounded
    )

    $script:ConstructApiLastStatus = 0
    $script:ConstructApiLastError  = ""
    $script:ConstructApiLastProblem = @{ Status = 0; Code = ""; Class = "none"; Detail = "" }

    $requestWatch = [Diagnostics.Stopwatch]::StartNew()
    $preflight = $true
    $problemClass = 'none'; $base = $BaseUrl; $p = $Path
    $prevCallback = $null; $prevProtocol = $null; $restoreSpm = $false
    try {
        $base = ConvertTo-ConstructServiceUrl -Value $BaseUrl
        # BEFORE a credential is even selected, let alone attached: an unencrypted, unpinned
        # transport to anywhere but this machine is refused outright.
        Assert-ConstructTransportSafe -BaseUrl $base
        $p = ([string]$Path).Trim()
        if (-not $p.StartsWith('/')) { $p = "/$p" }
        if (-not $p.StartsWith('/api/')) { $p = "/api/v1$p" }
        $uri = "$base$p"

        if (-not $Auth) { $Auth = New-ConstructApiAuth -Mode negotiate }

        $headers = @{ 'Accept' = 'application/json' }
        if ($Auth.ContainsKey('Headers') -and $Auth['Headers']) {
            foreach ($k in $Auth['Headers'].Keys) { $headers[$k] = $Auth['Headers'][$k] }
        }

        $req = @{
            Uri             = $uri
            Method          = $Method
            Headers         = $headers
            TimeoutSec      = $TimeoutSec
            UseBasicParsing = $true
            ErrorAction     = 'Stop'
        }
        $windowsCredential = $false
        switch ([string]$Auth['Mode']) {
            'token'      { $headers['Authorization'] = "Bearer $($Auth['Token'])" }
            'credential' { $req['Credential'] = $Auth['Credential']; $windowsCredential = $true }
            default      { $req['UseDefaultCredentials'] = $true; $windowsCredential = $true }
        }
        # PowerShell 7 REFUSES to send a Windows credential (-UseDefaultCredentials or
        # -Credential) over plain http -- "the cmdlet cannot protect plain text secrets sent
        # over unencrypted connections" -- while Windows PowerShell 5.1 sends it without
        # comment. Left alone, the same code would work on 5.1 and fail on 7 against a
        # development/fake service, which is exactly the kind of edition split this client
        # exists to hide. So opt in explicitly for http, and SAY SO once: a Windows
        # credential on an unencrypted connection is a development-only arrangement.
        if ($windowsCredential -and ([System.Uri]$base).Scheme -eq 'http') {
            $iwr = Get-Command Invoke-WebRequest -ErrorAction SilentlyContinue
            if ($iwr -and $iwr.Parameters.ContainsKey('AllowUnencryptedAuthentication')) {
                $req['AllowUnencryptedAuthentication'] = $true
            }
            if (-not $script:ConstructApiWarnedUnencrypted) {
                $script:ConstructApiWarnedUnencrypted = $true
                Write-Warning "Sending a Windows credential to $base over plain http -- it is not encrypted in transit. Only a service on this machine is allowed to be reached this way; anything else needs https."
            }
        }
        if ($null -ne $Body) {
            if ($Body -is [string]) { $req['Body'] = $Body }
            else { $req['Body'] = ($Body | ConvertTo-Json -Depth 8 -Compress) }
            $req['ContentType'] = 'application/json'
        }

        $problemClass = 'pin'
        $expected = Resolve-ConstructApiPin -BaseUrl $base -Pin $Pin -StoreDir $StoreDir
        $problemClass = 'none'
        $isHttps  = ([System.Uri]$base).Scheme -eq 'https'


        $preflight = $false
        if ($isHttps -and (Test-ConstructPwshCore)) {
            # PS 7: SocketsHttpHandler ignores ServicePointManager, so verify the
            # presented certificate ourselves first and then skip the (useless) chain
            # check. docs\remote-host.md section 5 documents the ordering.
            $problemClass = 'certificate'
            $script:ConstructCertificateFailureClass = 'other'
            if ($Bounded) { $actual = Get-ConstructRemoteFingerprint -BaseUrl $base -TimeoutMs ([int][Math]::Max(1, [Math]::Min(10000, $TimeoutSec * 1000 - $requestWatch.ElapsedMilliseconds))) }
            else { $actual = Get-ConstructRemoteFingerprint -BaseUrl $base }
            $problemClass = 'none'
            if (-not (Test-ConstructFingerprintMatch -Expected $expected -Actual $actual)) {
                $problemClass = 'pin'
                throw "Certificate fingerprint mismatch for $base.`n    pinned:    $expected`n    presented: $actual`nRefusing to connect. If the host's certificate was legitimately replaced, remove the pin file ($(Get-ConstructRemotePinPath -BaseUrl $base -StoreDir $StoreDir)) and add the host again."
            }
            $req['SkipCertificateCheck'] = $true
        } elseif ($isHttps) {
            # Windows PowerShell 5.1: pin INSIDE the handshake. Scoped to this call and
            # restored in the finally -- this is process-global state.
            #
            # The validator is COMPILED (Get-ConstructPinValidatorCallback), not a
            # scriptblock: Invoke-WebRequest runs the certificate callback on a thread
            # that has no PowerShell runspace, where a scriptblock cannot execute -- the
            # handshake then fails with "An unexpected error occurred on a send" on every
            # call (field, 2026-09-04). A compiled delegate has no such dependency.
            $restoreSpm   = $true
            $prevCallback = [System.Net.ServicePointManager]::ServerCertificateValidationCallback
            $prevProtocol = [System.Net.ServicePointManager]::SecurityProtocol
            $protocols = [System.Net.SecurityProtocolType]::Tls12 -bor [System.Net.ServicePointManager]::SecurityProtocol
            # TLS 1.3 (12288) exists on .NET Framework 4.8 / Windows 11 only; offer it when the enum knows it.
            if ([enum]::IsDefined([System.Net.SecurityProtocolType], 12288)) { $protocols = $protocols -bor 12288 }
            [System.Net.ServicePointManager]::SecurityProtocol = $protocols
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback =
                Get-ConstructPinValidatorCallback -Expected $expected
        }

        # These are small JSON replies, not the host's ISO transfer. PowerShell 5.1's
        # download popup otherwise flashes on every job poll and looks like ISO progress.
        $previousProgress = $ProgressPreference
        try {
            $ProgressPreference = 'SilentlyContinue'
            if ($Bounded) {
                $remaining = $TimeoutSec - $requestWatch.Elapsed.TotalSeconds
                if ($remaining -le 0) { throw [TimeoutException]::new('Source request deadline elapsed.') }
                $req.TimeoutSec = [int][Math]::Max(1, [Math]::Floor($remaining))
            }
            $resp = Invoke-WebRequest @req
        } finally { $ProgressPreference = $previousProgress }
        $script:ConstructApiLastStatus = [int]$resp.StatusCode
        $script:ConstructApiLastProblem = @{ Status = [int]$resp.StatusCode; Code = ''; Class = 'none'; Detail = '' }
        $content = ""
        try { $content = [string]$resp.Content } catch { $content = "" }
        if ([string]::IsNullOrWhiteSpace($content)) { return $null }
        if ($RawResponse) { return $content }
        try { return ($content | ConvertFrom-Json) }
        catch { return $content }
    } catch {
        if ($preflight -and -not $Bounded) { throw }
        $info = Get-ConstructApiErrorInfo -ErrorRecord $_
        $script:ConstructApiLastStatus = [int]$info.Status
        $script:ConstructApiLastError  = [string]$info.Message
        $class = Get-ConstructApiFailureClass $_.Exception
        if ($info.Status) { $class = 'http' }
        elseif ($problemClass -eq 'pin') { $class = 'pin' }
        elseif ($problemClass -eq 'certificate') { $class = $script:ConstructCertificateFailureClass }
        elseif ($restoreSpm -and ('Construct.PinValidator' -as [type]) -and [Construct.PinValidator].GetField('Rejected') -and [Construct.PinValidator]::Rejected) { $class = 'pin' }
        $script:ConstructApiLastProblem = @{ Status = [int]$info.Status; Code = [string]$info.Code; Class = $class; Detail = [string]$info.Message }
        if ($NoThrow) { return $null }
        $where = "$Method $p"
        if ($info.Status) {
            throw "Construct host service refused $where (HTTP $($info.Status)): $($info.Message)"
        }
        throw "Could not reach the Construct host service at $base ($where): $($info.Message)"
    } finally {
        if ($restoreSpm) {
            [System.Net.ServicePointManager]::ServerCertificateValidationCallback = $prevCallback
            [System.Net.ServicePointManager]::SecurityProtocol = $prevProtocol
        }
    }
}

function ConvertFrom-ConstructVmEndpoint {
    <#
        The ONE reading of an endpoint document -- GET /vms/{name}/endpoint, and the
        `endpoint` object inside a creation job's result, which have the same shape:

            { "sshHost": "buildbox.local", "sshPort": 2201, "publicHost": "work-vm.vpn.example" }

        Returns @{ SshHost; SshPort; PublicHost }, or $null when the document carries no
        usable SSH host.

        publicHost (plan section 4.12) is where this VM's WEB endpoints live -- the
        service's rendered Constructd:PublicHostPattern. SSH is dialled on SshHost either
        way. A service that does not send it (an older build, or one with no pattern) is
        NOT a special case for the caller: PublicHost then falls back to SshHost, which is
        exactly what "no pattern configured" means on the service side.

        PURE (no HTTP), so the shape is unit-testable; mirrors readEndpoint() in
        extension/src/remotehost.js.
    #>
    [CmdletBinding()]
    param($Response)

    if ($null -eq $Response) { return $null }
    $sshHost = ""
    if ($Response.PSObject.Properties['sshHost']) { $sshHost = [string]$Response.sshHost }
    $sshHost = $sshHost.Trim()
    if (-not $sshHost) { return $null }

    $sshPort = 22
    if ($Response.PSObject.Properties['sshPort']) {
        $parsed = 0
        if ([int]::TryParse([string]$Response.sshPort, [ref]$parsed) -and $parsed -ge 1 -and $parsed -le 65535) {
            $sshPort = $parsed
        }
    }

    $publicHost = ""
    if ($Response.PSObject.Properties['publicHost']) { $publicHost = ([string]$Response.publicHost).Trim() }
    if (-not $publicHost) { $publicHost = $sshHost }

    return @{ SshHost = $sshHost; SshPort = $sshPort; PublicHost = $publicHost }
}

function Get-ConstructRemoteCpuDefault {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUrl, $Auth)
    try { $defaults = Invoke-ConstructApi -BaseUrl $BaseUrl -Path '/vm-defaults' -Auth $Auth }
    catch { throw "Could not determine the host's CPU allowance. Update the host service or pass -VmCpuCount explicitly. $($_.Exception.Message)" }
    $count = 0
    if (-not $defaults -or -not $defaults.PSObject.Properties['recommendedCpus'] -or
        -not [int]::TryParse([string]$defaults.recommendedCpus, [ref]$count) -or $count -lt 1 -or $count -gt 64) {
        throw 'The host reports no available CPU allowance for a new VM. Check the user allowance and host capacity.'
    }
    return $count
}

function Wait-ConstructJob {
    <#
        Follow a long operation to its end: poll GET /jobs/{id}, print every NEW
        progress line as it appears, and return the finished job's `result` object.
        Throws when the job fails, is cancelled, or does not finish inside -TimeoutSeconds.

        Polling, not SSE, on purpose: it needs no streaming HTTP client on either
        PowerShell edition, and the service records every progress line so a poller sees
        the same log a subscriber would (service/README.md).

        THE ONE-TIME SECRET: a creation job's result carries `vmToken` on the FIRST
        authorised retrieval of the SUCCEEDED job and never again. This function is that
        retrieval -- the terminal poll -- so the token is in the returned object. It is
        never printed and never logged; the caller passes it straight to the provisioner.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseUrl,
        [Parameter(Mandatory)][string]$JobId,
        $Auth,
        [string]$Pin,
        [string]$StoreDir,
        [int]$PollSeconds = 2,
        [int]$TimeoutSeconds = 3600,
        # Where progress lines go. Default Write-Host so a job logs like the local
        # install; tests pass a collector.
        [scriptblock]$OnProgress
    )

    $shown    = 0
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    $emit = $OnProgress
    if (-not $emit) { $emit = { param($line) Write-Host "    $line" -ForegroundColor DarkGray } }

    while ($true) {
        $job = Invoke-ConstructApi -BaseUrl $BaseUrl -Method GET -Path "/jobs/$JobId" -Auth $Auth -Pin $Pin -StoreDir $StoreDir

        $progress = @()
        if ($job -and $job.PSObject.Properties['progress'] -and $job.progress) { $progress = @($job.progress) }
        for ($i = $shown; $i -lt $progress.Count; $i++) {
            $text = ""
            try { $text = [string]$progress[$i].text } catch { $text = "" }
            if ($text) { & $emit $text }
        }
        if ($progress.Count -gt $shown) { $shown = $progress.Count }

        $state = ""
        if ($job -and $job.PSObject.Properties['state']) { $state = ([string]$job.state).ToLowerInvariant() }

        if ($state -eq 'succeeded') {
            if ($job.PSObject.Properties['result']) { return $job.result }
            return $null
        }
        if ($state -eq 'failed' -or $state -eq 'cancelled' -or $state -eq 'canceled') {
            $err = ""
            if ($job.PSObject.Properties['error'] -and $job.error) { $err = [string]$job.error }
            if (-not $err) { $err = "the host service reported the job as $state" }
            throw "The host service job $JobId $state`: $err"
        }

        if ((Get-Date) -gt $deadline) {
            throw "The host service job $JobId did not finish within $TimeoutSeconds seconds (last state: $state). It may still be running -- check it with GET /api/v1/jobs/$JobId."
        }
        Start-Sleep -Seconds ([Math]::Max(1, $PollSeconds))
    }
}

# Host-administration client hooks. Reports are advisory; rotation is explicit.
function Send-ConstructGuestReport {
    [CmdletBinding()]
    param([string]$BaseUrl, [string]$VmName, [ValidateSet('provisioned','reinstalled','attempt')][string]$Event,
          [ValidateSet('succeeded','failed')][string]$Outcome, [string]$ConstructCommit, $Auth,
          [string]$Pin, [string]$StoreDir)
    if (-not $BaseUrl -or -not $VmName) { return $false }
    try {
        $body = @{ event = $Event; reporter = 'Provision-AgentVM.ps1'; at = [DateTimeOffset]::UtcNow.ToString('o') }
        if ($Event -eq 'attempt') { $body['outcome'] = $Outcome }
        elseif ($ConstructCommit -match '^[0-9a-fA-F]{7,64}$') { $body['constructCommit'] = $ConstructCommit }
        if (-not $Auth) {
            $savedToken = Get-ConstructRemoteToken -BaseUrl $BaseUrl -StoreDir $StoreDir
            if ($savedToken) { $Auth = New-ConstructApiAuth -Mode token -Token $savedToken }
            else { $Auth = New-ConstructApiAuth -Mode negotiate }
        }
        $path = '/vms/' + [Uri]::EscapeDataString($VmName) + '/guest-report'
        $null = Invoke-ConstructApi -BaseUrl $BaseUrl -Method POST -Path $path -Body $body -Auth $Auth -Pin $Pin -StoreDir $StoreDir -TimeoutSec 5 -NoThrow
        return [bool]((Get-ConstructApiLastStatus) -ge 200 -and (Get-ConstructApiLastStatus) -lt 300)
    } catch { return $false } # Never render a transport exception or affect provisioning.
}

function Request-ConstructVmTokenRotation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BaseUrl, [Parameter(Mandatory)][string]$VmName,
          [ValidateSet('primary','legacy')][string]$Kind = 'primary', $Auth, [string]$Pin, [string]$StoreDir)
    if (-not $Auth) {
        $savedToken = Get-ConstructRemoteToken -BaseUrl $BaseUrl -StoreDir $StoreDir
        if ($savedToken) { $Auth = New-ConstructApiAuth -Mode token -Token $savedToken }
        else { $Auth = New-ConstructApiAuth -Mode negotiate }
    }
    $path = '/vms/' + [Uri]::EscapeDataString($VmName) + '/token'
    try {
        $result = Invoke-ConstructApi -BaseUrl $BaseUrl -Method POST -Path $path -Body @{ kind = $Kind } -Auth $Auth -Pin $Pin -StoreDir $StoreDir
        if (-not $result -or -not $result.vmToken) { throw 'Missing credential.' }
        return $result
    } catch { throw 'The host service could not rotate this VM credential.' }
}

function Get-ConstructApiLastProblem {
    return $script:ConstructApiLastProblem
}

function Get-ConstructApiFailureClass {
    param($Exception)
    for ($depth = 0; $Exception -and $depth -lt 12; $depth++) {
        if ($Exception -is [System.TimeoutException] -or $Exception -is [System.OperationCanceledException]) { return 'timeout' }
        if ($Exception -is [System.Security.Authentication.AuthenticationException]) { return 'tls' }
        if ($Exception -is [System.Net.WebException]) {
            switch ([string]$Exception.Status) {
                'Timeout' { return 'timeout' }
                'TrustFailure' { return 'tls' }
                'SecureChannelFailure' { return 'tls' }
                'NameResolutionFailure' { return 'dns' }
                'ProxyNameResolutionFailure' { return 'dns' }
                'ConnectFailure' { return 'connection' }
            }
        }
        if ($Exception -is [System.Net.Sockets.SocketException]) {
            if ([string]$Exception.SocketErrorCode -in @('HostNotFound','NoData','TryAgain')) { return 'dns' }
            if ([string]$Exception.SocketErrorCode -eq 'TimedOut') { return 'timeout' }
            return 'connection'
        }
        if ($Exception.PSObject.Properties['HttpRequestError']) {
            switch ([string]$Exception.HttpRequestError) {
                'NameResolutionError' { return 'dns' }
                'SecureConnectionError' { return 'tls' }
                'ConnectionError' { return 'connection' }
            }
        }
        $Exception = $Exception.InnerException
    }
    return 'other'
}

function Get-ConstructSourceMessage {
    param([string]$Reason, [string]$Commit, [string]$Ref = 'main', [int]$Divergence, [int]$TimeoutSeconds = 900, [string]$Mode = 'auto')
    $short = $Commit; if ($short.Length -gt 7) { $short = $short.Substring(0,7) }
    $message = ''
    switch -Regex ($Reason) {
        '^service-without-source-cache$' { $message = 'This host service does not offer the source cache (apiFeatures lack "source-cache"); uploading the Construct checkout as before. Update the host service to skip the upload.'; break }
        '^ref-not-main$' { $message = "Construct source ref '$Ref' is not main, so no host release exists for it; uploading the checkout as before."; break }
        '^commit-unknown$' { $message = "Could not determine this checkout's commit (no git or no .construct-revision); uploading the checkout as before."; break }
        '^archive-unverified$' { $message = 'This Construct install has no source manifest (it was installed before the host cache existed); uploading the checkout as before. Run Update-Construct.ps1 once to enable the host cache.'; break }
        '^local-changes$' { $message = "This checkout differs from commit $short in $Divergence file(s) (the differing files could not be listed); uploading it as before so the VM gets them. Commit or ignore them to use the host cache, or pass -SourceMode cache to send commit $short without them."; break }
        '^cache-overlay$' { $message = "This checkout differs from commit $short in $Divergence file(s); the VM fetches commit $short from the host cache and only those file(s) are uploaded from this PC."; break }
        '^local-changes-too-large$' { $message = "This checkout differs from commit $short in $Divergence file(s), too many or too large for an overlay; uploading it as before$([char]0x2026)"; break }
        '^overlay-pack-failed:(.*)$' { $message = "Could not pack the $Divergence differing file(s) ($($Matches[1])); uploading the checkout as before."; break }
        '^overlay-upload-failed$' { $message = 'Could not upload the source overlay to the VM; uploading the checkout as before.'; break }
        '^ensure-denied:(.*)$' { $message = "The host service refused the source request (HTTP $($Matches[1])); uploading the checkout as before."; break }
        '^ensure-maintenance$' { $message = 'The host service is in maintenance; uploading the checkout as before.'; break }
        '^ensure-(refused|failed):(.*)$' {
            $detail = $Matches[2]; if ($detail -eq 'source-cache-full') { $detail += ': ask the host admin to delete unused entries' }
            $message = "The host service could not cache commit $short ($detail); uploading the checkout as before."; break
        }
        '^ensure-cancelled$' { $message = "The host service's source download was cancelled; uploading the checkout as before."; break }
        '^ensure-timeout$' { $message = "The host service did not finish caching commit $short within $TimeoutSeconds s; uploading the checkout as before (the host keeps downloading for the next run)."; break }
        '^guest-token-staging-failed$' { $message = 'Could not hand the VM its service token for the source fetch; uploading the checkout as before.'; break }
        '^guest-fetch-failed:repo-lost$' {
            if ($Mode -eq 'cache') { return "The VM's Construct repo was lost while swapping in commit $short. Rerun with -SourceMode upload to restore it." }
            return "The VM's Construct repo was lost while swapping in commit $short; uploading the checkout to restore it."
        }
        '^guest-fetch-failed:(.*)$' { $message = "The VM could not fetch commit $short from the host service ($($Matches[1])); uploading the checkout as before."; break }
    }
    if ($Mode -eq 'cache' -and $message) { return ($message -replace '; uploading.*$', '.') }
    return $message
}

function Get-ConstructSourceTransportPlan {
    [CmdletBinding()]
    param([bool]$ServiceManaged, [ValidateSet('auto','cache','upload')][string]$Mode = 'auto', [bool]$IncludeGit,
        [bool]$FeatureAvailable, [string]$Ref = 'main', [string]$Commit = '',
        [ValidateSet('equivalent','divergent','unverified','unknown')][string]$TreeState = 'unknown', [int]$Divergence = 0, [hashtable]$Changes = $null)
    if (-not $Ref) { $Ref = 'main' }
    $transport = 'upload'; $reason = ''; $overlay = $null
    if (-not $ServiceManaged) { $reason = 'local-install' }
    elseif ($Mode -eq 'upload') { $reason = 'mode-upload' }
    elseif ($IncludeGit) { $reason = 'include-git' }
    elseif (-not $FeatureAvailable) { $reason = 'service-without-source-cache' }
    elseif ($Mode -eq 'auto' -and $Ref -ne 'main') { $reason = 'ref-not-main' }
    elseif ($Commit -notmatch '^[0-9a-f]{40}$' -or ($Mode -eq 'auto' -and $TreeState -eq 'unknown')) { $reason = 'commit-unknown' }
    elseif ($Mode -eq 'cache') { $transport = 'cache'; $reason = 'cache-forced' }
    elseif ($TreeState -eq 'unverified') { $reason = 'archive-unverified' }
    elseif ($TreeState -eq 'divergent') {
        if ($null -eq $Changes) { $reason = 'local-changes' }
        elseif (@($Changes.Modified).Count + @($Changes.Added).Count + @($Changes.Deleted).Count -gt 5000) { $reason = 'local-changes-too-large' }
        else { $transport = 'cache'; $reason = 'cache-overlay'; $overlay = $Changes }
    }
    else { $transport = 'cache'; $reason = 'cache' }
    $message = Get-ConstructSourceMessage -Reason $reason -Commit $Commit -Ref $Ref -Divergence $Divergence -Mode $Mode
    if ($ServiceManaged -and $Mode -eq 'cache' -and $reason -in @('service-without-source-cache','commit-unknown')) { throw $message }
    return @{ Transport = $transport; Reason = $reason; Message = $message; Commit = $Commit.ToLowerInvariant(); Mode = $Mode; Ref = $Ref; Divergence = $Divergence; Overlay = $overlay }
}

function Get-ConstructSourceFailureCode {
    param($Result)
    if ($Result.Code -and [string]$Result.Code -cmatch '^[a-z0-9-]{1,100}$') { return [string]$Result.Code }
    if ($Result.Class -and $Result.Class -ne 'none') { return [string]$Result.Class }
    if ($Result.Outcome) { return [string]$Result.Outcome }
    if ($Result.State) { return [string]$Result.State }
    return 'other'
}

function Test-ConstructApiFeature {
    [CmdletBinding()]
    param([string]$BaseUrl, [string]$Feature = 'source-cache', $Auth, [string]$Pin, [string]$StoreDir, [int]$TimeoutSec = 10)
    try {
        $result = Invoke-ConstructApi -BaseUrl $BaseUrl -Path '/health' -Auth $Auth -Pin $Pin -StoreDir $StoreDir -TimeoutSec $TimeoutSec -NoThrow -Bounded
        return [bool]($result -and $result.apiFeatures -contains $Feature)
    } catch { return $false }
}

function Request-ConstructSourceEnsure {
    [CmdletBinding()]
    param([string]$BaseUrl, [string]$VmName, [string]$Commit, [string]$OperationKey, $Auth, [string]$Pin, [string]$StoreDir, [int]$TimeoutSec = 30)
    $result = @{ Outcome = 'error'; Status = 0; Code = ''; Class = 'other'; JobId = ''; SizeBytes = 0; Sha256 = ''; ReleaseTag = ''; Replayed = $false }
    try {
        if (-not $Auth) { $Auth = New-ConstructApiAuth -Mode negotiate }
        $requestAuth = @{}; foreach ($key in $Auth.Keys) { $requestAuth[$key] = $Auth[$key] }
        $headers = @{}; if ($Auth.Headers) { foreach ($key in $Auth.Headers.Keys) { $headers[$key] = $Auth.Headers[$key] } }
        $headers['X-Construct-Operation-Key'] = $OperationKey; $requestAuth.Headers = $headers
        $body = Invoke-ConstructApi -BaseUrl $BaseUrl -Method POST -Path ('/vms/' + [Uri]::EscapeDataString($VmName) + '/source') -Body @{ commit = $Commit } -Auth $requestAuth -Pin $Pin -StoreDir $StoreDir -TimeoutSec $TimeoutSec -NoThrow -Bounded
        $problem = Get-ConstructApiLastProblem
        $result.Status = $problem.Status; $result.Code = $problem.Code; $result.Class = $problem.Class
        if ($problem.Status -ge 200 -and $problem.Status -lt 300) {
            $result.Outcome = 'malformed'; $result.Replayed = [bool]$body.replayed
            if ($body.state -eq 'ready' -and (Test-ConstructSourceResult $body)) {
                $result.Outcome = 'ready'; $result.SizeBytes = [long]$body.sizeBytes; $result.Sha256 = [string]$body.sha256; $result.ReleaseTag = [string]$body.releaseTag
            } elseif ($body.state -eq 'downloading' -and $body.jobId) { $result.Outcome = 'downloading'; $result.JobId = [string]$body.jobId }
        } elseif ($problem.Status -eq 404 -or ($problem.Status -eq 409 -and $problem.Code -eq 'unsupported-capability')) { $result.Outcome = 'unsupported' }
        elseif ($problem.Status -in @(401,403)) { $result.Outcome = 'denied' }
        elseif ($problem.Status -eq 503) { $result.Outcome = 'maintenance' }
        elseif ($problem.Status -ge 400 -and $problem.Status -lt 500) { $result.Outcome = 'refused' }
    } catch { $result.Class = Get-ConstructApiFailureClass $_.Exception }
    return $result
}

function Test-ConstructSourceResult {
    param($Value)
    try { return [bool]($Value -and [long]$Value.sizeBytes -gt 0 -and [string]$Value.sha256 -match '^[0-9a-f]{64}$') } catch { return $false }
}

function Wait-ConstructSourceJob {
    [CmdletBinding()]
    param([string]$BaseUrl, [string]$JobId, $Auth, [string]$Pin, [string]$StoreDir,
        [datetime]$Deadline, [double]$PollSeconds = 2, [scriptblock]$OnProgress = {})
    $failures = 0; $seen = 0
    while ([datetime]::UtcNow -lt $Deadline) {
        $remaining = ($Deadline - [datetime]::UtcNow).TotalSeconds
        $timeout = [int][Math]::Max(1, [Math]::Min(30, [Math]::Floor($remaining)))
        $job = $null; $problem = @{ Status = 0; Code = ''; Class = 'other' }
        try {
            $job = Invoke-ConstructApi -BaseUrl $BaseUrl -Path ('/jobs/' + [Uri]::EscapeDataString($JobId)) -Auth $Auth -Pin $Pin -StoreDir $StoreDir -TimeoutSec $timeout -NoThrow -Bounded
            $problem = Get-ConstructApiLastProblem
        } catch { $problem.Class = Get-ConstructApiFailureClass $_.Exception }
        if ($problem.Status -in @(401,403)) { return @{ State = 'denied'; Code = [string]$problem.Code; Class = 'http' } }
        if ($problem.Status -lt 200 -or $problem.Status -ge 500) {
            $failures++
            if ($failures -ge 3) { return @{ State = 'error'; Code = [string]$problem.Code; Class = $problem.Class } }
        } elseif ($problem.Status -ge 300 -or -not $job) { return @{ State = 'malformed'; Code = [string]$problem.Code; Class = $problem.Class } }
        else {
            $failures = 0
            # The service records progress as {at, text} rows; older services returned plain strings.
            $lines = @($job.progress); while ($seen -lt $lines.Count) { $row = $lines[$seen]; & $OnProgress $(if ($row -is [string]) { $row } elseif ($null -ne $row -and $row.PSObject.Properties['text']) { [string]$row.text } else { [string]$row }); $seen++ }
            switch ([string]$job.state) {
                'succeeded' {
                    if (-not (Test-ConstructSourceResult $job.result)) { return @{ State = 'malformed'; Code = 'malformed'; Class = 'none' } }
                    return @{ State = 'succeeded'; Code = ''; Class = 'none'; SizeBytes = [long]$job.result.sizeBytes; Sha256 = [string]$job.result.sha256; ReleaseTag = [string]$job.result.releaseTag }
                }
                'failed' { return @{ State = 'failed'; Code = [string]$job.error; Class = 'none' } }
                'cancelled' { return @{ State = 'cancelled'; Code = 'cancelled'; Class = 'none' } }
                { $_ -notin @('queued','running') } { return @{ State = 'malformed'; Code = 'malformed'; Class = 'none' } }
            }
        }
        $sleep = [Math]::Min($PollSeconds, [Math]::Max(0, ($Deadline - [datetime]::UtcNow).TotalSeconds))
        if ($sleep -gt 0) { Start-Sleep -Milliseconds ([int]($sleep * 1000)) }
    }
    return @{ State = 'timeout'; Code = 'timeout'; Class = 'timeout' }
}

function Invoke-ConstructSourceTransport {
    [CmdletBinding()]
    param([ValidateSet('begin','complete')][string]$Phase, $Plan, $State, [scriptblock]$Ensure,
        [scriptblock]$WaitJob, [scriptblock]$StageToken = { $true }, [scriptblock]$RunGuestFetch,
        [scriptblock]$Pack = {}, [scriptblock]$Upload, [scriptblock]$PackOverlay, [scriptblock]$UploadOverlay, [scriptblock]$Warn = { param($text) Write-Warning $text },
        [scriptblock]$Info = { param($text) Write-Host $text }, [datetime]$Deadline = ([datetime]::UtcNow.AddSeconds(900)), [int]$TimeoutSeconds = 900)
    $reason = ''
    if ($Phase -eq 'begin') {
        $State = @{}; foreach ($key in $Plan.Keys) { $State[$key] = $Plan[$key] }
        $State.ArchivePath = $null; $State.OperationKey = 'source-' + [guid]::NewGuid().ToString('N')
        if ($State.Transport -eq 'cache') {
            if ($State.Reason -eq 'cache-overlay') { & $Info $State.Message }
            & $Info ('==> Ensuring Construct source ' + $State.Commit.Substring(0,7) + ' on the host service')
            try { $result = & $Ensure $State.Commit $State.OperationKey } catch { $result = @{ Outcome = 'error'; Class = 'other' } }
            $State.Ensure = $result
            if ($result.Outcome -eq 'ready') {
                $State.SizeBytes = $result.SizeBytes; $State.Sha256 = $result.Sha256
                & $Info ('    Source cached on the host (' + [Math]::Round($State.SizeBytes / 1KB) + ' KB)')
            } elseif ($result.Outcome -eq 'downloading') {
                & $Info ('    Host is fetching commit ' + $State.Commit.Substring(0,7) + ' from the release (job ' + $result.JobId + ')')
            } else {
                switch ($result.Outcome) {
                    'unsupported' { $reason = 'service-without-source-cache' }
                    'denied' { $reason = 'ensure-denied:' + $result.Status }
                    'maintenance' { $reason = 'ensure-maintenance' }
                    'refused' { $reason = 'ensure-refused:' + (Get-ConstructSourceFailureCode $result) }
                    default { $reason = 'ensure-failed:' + (Get-ConstructSourceFailureCode $result) }
                }
            }
        } else { $reason = $State.Reason }
    } else {
        if ($State.Transport -eq 'cache') {
            if ($State.Ensure.Outcome -eq 'downloading') {
                try { $result = & $WaitJob $State.Ensure.JobId $Deadline } catch { $result = @{ State = 'error'; Class = 'other' } }
                switch ($result.State) {
                    'succeeded' { $State.SizeBytes = $result.SizeBytes; $State.Sha256 = $result.Sha256 }
                    'cancelled' { $reason = 'ensure-cancelled' }
                    'timeout' { $reason = 'ensure-timeout' }
                    default { $reason = 'ensure-failed:' + (Get-ConstructSourceFailureCode $result) }
                }
            }
            if (-not $reason -and $null -ne $State.Overlay) {
                try {
                    $State.OverlayArchive = & $PackOverlay $State.Overlay
                    if (-not $State.OverlayArchive) { throw 'empty-overlay-result' }
                } catch {
                    if ($_.Exception.Data['ConstructSourceOverlayTooLarge']) { $reason = 'local-changes-too-large' }
                    else {
                        # Only the packer's own codes, the exception type and the file's path are reported: never a message that could carry file contents.
                        $detail = $_.Exception.GetType().Name
                        if ($_.Exception.Message -cmatch '^(invalid-overlay-path|overlay-reparse-point|overlay-not-file|overlay-file-missing|overlay-file-unreadable|duplicate-overlay-path|conflicting-overlay-path|empty-overlay-result)$') { $detail = $_.Exception.Message }
                        if ($_.Exception.Data -and $_.Exception.Data.Contains('ConstructSourceOverlayError') -and [string]$_.Exception.Data['ConstructSourceOverlayError'] -cmatch '^[A-Za-z0-9.]{1,80}$') { $detail += '/' + $_.Exception.Data['ConstructSourceOverlayError'] }
                        if ($_.Exception.Data -and $_.Exception.Data.Contains('ConstructSourceOverlayPath') -and (Test-ConstructSourceRelativePath ([string]$_.Exception.Data['ConstructSourceOverlayPath']))) { $detail += ' at ' + $_.Exception.Data['ConstructSourceOverlayPath'] }
                        $reason = 'overlay-pack-failed:' + $detail
                    }
                }
                if (-not $reason) {
                    try {
                        $State.OverlayPath = & $UploadOverlay $State.OverlayArchive
                        if (-not $State.OverlayPath) { throw 'empty-overlay-path' }
                    } catch { $reason = 'overlay-upload-failed' }
                }
            }
            if (-not $reason) {
                try { $staged = & $StageToken } catch { $staged = $false }
                if (-not $staged) { $reason = 'guest-token-staging-failed' }
            }
            if (-not $reason) {
                try { $fetch = & $RunGuestFetch $State } catch { $fetch = @{ ExitCode = 255; Lines = @() } }
                if ($fetch.ExitCode -eq 7) { $reason = 'guest-fetch-failed:repo-lost' }
                elseif ($fetch.ExitCode -ne 0) {
                    $detail = [string]$fetch.ExitCode; if ($fetch.ExitCode -notin @(2,3,4,5,6)) { $detail = 'ssh' }
                    $reason = 'guest-fetch-failed:' + $detail
                } elseif (@($fetch.Lines) -cnotcontains ('CONSTRUCT_SOURCE_INSTALLED=' + $State.Commit)) { $reason = 'guest-fetch-failed:unverified' }
            }
        }
    }
    if ($reason) {
        $message = Get-ConstructSourceMessage -Reason $reason -Commit $State.Commit -Ref $State.Ref -Divergence $State.Divergence -Mode $State.Mode -TimeoutSeconds $TimeoutSeconds
        if ($State.Mode -eq 'cache' -and $message) { throw $message }
        if ($message) { & $Warn $message }
        $State.Transport = 'upload'; $State.Reason = $reason
    }
    if ($State.Transport -eq 'upload') {
        if (-not $State.ArchivePath) { $State.ArchivePath = & $Pack }
        if ($Phase -eq 'complete') { & $Upload $State.ArchivePath | Out-Null }
    } elseif ($Phase -eq 'complete') {
        if ($null -ne $State.Overlay) {
            & $Info ('    Construct source: host cache (commit ' + $State.Commit.Substring(0,7) + ', ' + [Math]::Round($State.SizeBytes / 1KB) + ' KB) + ' + $State.Divergence + ' differing file(s) (' + [Math]::Round($State.OverlayArchive.CompressedSizeBytes / 1KB) + ' KB) uploaded from this PC.')
        } else {
            & $Info ('    Construct source: host cache (commit ' + $State.Commit.Substring(0,7) + ', ' + [Math]::Round($State.SizeBytes / 1KB) + ' KB); nothing uploaded from this PC.')
        }
    }
    return $State
}
