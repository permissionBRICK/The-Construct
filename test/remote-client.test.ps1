#Requires -Version 5.1
<#
    Unit tests for the remote host-service client (lib/AgentVm.Remote.ps1), batch B7. Run:

        pwsh -NoProfile -File test/remote-client.test.ps1

    Self-contained and network-free: Invoke-WebRequest is SHADOWED by a function defined
    in this script's scope, so every request the client makes is recorded instead of
    sent. PowerShell resolves functions before cmdlets, and the library is dot-sourced
    into this same scope, so its calls land on the stub.

    What this pins:
      * URL/slug normalisation -- the two clients (PS + extension) must derive the SAME
        host key, because they share one pin file;
      * the three credential providers' request shapes;
      * the 401 fallback path -NoThrow exists for;
      * certificate PINNING: no pin -> no call, wrong pin -> no call;
      * the DPAPI token store round-trip (skipped, loudly, off Windows);
      * Wait-ConstructJob, including the ONE-TIME VM token in the terminal poll.
#>
$ErrorActionPreference = "Stop"

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

$script:pass = 0; $script:fail = 0; $script:skip = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}
function skip($name, $why) {
    $script:skip++
    Write-Host "  SKIP  $name -- $why" -ForegroundColor Yellow
}
# PowerShell has no try EXPRESSION, so "did this throw?" is a helper.
function Test-Throws([scriptblock]$Script) {
    try { & $Script | Out-Null; return $false } catch { return $true }
}

# ── (a) Parser checks: every .ps1 this batch touches parses with zero errors ─
Write-Host ""
Write-Host "=== Parser checks ===" -ForegroundColor Cyan
$touchedScripts = @(
    "lib/AgentVm.Remote.ps1",
    "lib/AgentVm.Instances.ps1",
    "drivers/Load-ConstructDriver.ps1",
    "drivers/hyperv-remote/HyperVRemote.Driver.ps1",
    "drivers/hyperv-local/HyperVLocal.Driver.ps1",
    "Auto-Install.ps1",
    "Provision-AgentVM.ps1"
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

. (Join-Path $repoRoot "lib/AgentVm.Remote.ps1")

# ── (b) Service URLs and host keys ──────────────────────────────────────────
Write-Host ""
Write-Host "=== Service URLs and host keys ===" -ForegroundColor Cyan
ok "url: a bare name gets https and the service port" ((ConvertTo-ConstructServiceUrl -Value "buildbox") -eq "https://buildbox:7462")
ok "url: an explicit port is kept" ((ConvertTo-ConstructServiceUrl -Value "buildbox:9000") -eq "https://buildbox:9000")
ok "url: a trailing path/slash is dropped" ((ConvertTo-ConstructServiceUrl -Value "https://b.local:7462/api/") -eq "https://b.local:7462")
ok "url: http stays http (development/fake services)" ((ConvertTo-ConstructServiceUrl -Value "http://127.0.0.1:7999") -eq "http://127.0.0.1:7999")
ok "url: whitespace is trimmed" ((ConvertTo-ConstructServiceUrl -Value "  buildbox:7462 ") -eq "https://buildbox:7462")
ok "url: an IPv6 literal keeps its brackets" ((ConvertTo-ConstructServiceUrl -Value "https://[fe80::1]:7462") -eq "https://[fe80::1]:7462")
ok "url: an empty value throws" (Test-Throws { ConvertTo-ConstructServiceUrl -Value "  " })
ok "url: a non-http scheme throws" (Test-Throws { ConvertTo-ConstructServiceUrl -Value "ftp://x" })
# THE SHARED CONTRACT: extension/src/remotehost.js hostSlug() must produce the same key,
# because the two write and read ONE pin file per host.
ok "slug: host_port, lowercased" ((Get-ConstructRemoteHostSlug -BaseUrl "https://BuildBox.Example.local:7462") -eq "buildbox.example.local_7462")
ok "slug: an IPv6 literal is sanitised to file-name-safe characters" ((Get-ConstructRemoteHostSlug -BaseUrl "https://[fe80::1]:7462") -eq "fe80__1_7462")
ok "slug: the same host in another spelling is the same key" `
    ((Get-ConstructRemoteHostSlug -BaseUrl "buildbox.example.local:7462") -eq (Get-ConstructRemoteHostSlug -BaseUrl "https://buildbox.example.local:7462"))

# ── (c) Fingerprints ────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Fingerprints ===" -ForegroundColor Cyan
$FP_A = "AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99"
$FP_B = "11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00:11:22:33:44:55:66:77:88:99:AA:BB:CC:DD:EE:FF:00"
ok "fp: colon-separated uppercase is canonical" ((Format-ConstructFingerprint -Value $FP_A.ToLower()) -eq $FP_A)
ok "fp: an unseparated spelling normalises to the same value" ((Format-ConstructFingerprint -Value ($FP_A -replace ':', '')) -eq $FP_A)
ok "fp: a spaced spelling normalises too" ((Format-ConstructFingerprint -Value ($FP_A -replace ':', ' ')) -eq $FP_A)
ok "fp: something that isn't 64 hex digits is rejected" ((Format-ConstructFingerprint -Value "AB:CD") -eq "")
ok "fp: match is spelling-insensitive" (Test-ConstructFingerprintMatch -Expected $FP_A -Actual ($FP_A.ToLower() -replace ':', ''))
ok "fp: a different certificate does NOT match" (-not (Test-ConstructFingerprintMatch -Expected $FP_A -Actual $FP_B))
ok "fp: an empty pin never matches" `
    ((-not (Test-ConstructFingerprintMatch -Expected "" -Actual $FP_A)) -and (-not (Test-ConstructFingerprintMatch -Expected $FP_A -Actual "")))

# ── (d) The pin store (a public hash; plain text by design) ─────────────────
Write-Host ""
Write-Host "=== Pin store ===" -ForegroundColor Cyan
$store = Join-Path ([System.IO.Path]::GetTempPath()) ("construct-remote-" + [Guid]::NewGuid().ToString("N"))
$SVC = "https://buildbox.example.local:7462"
try {
    ok "pin: an unknown host reads back empty" ((Get-ConstructRemotePin -BaseUrl $SVC -StoreDir $store) -eq "")
    $pinFile = Save-ConstructRemotePin -BaseUrl $SVC -Fingerprint $FP_A.ToLower() -StoreDir $store
    ok "pin: the file is named after the host slug" ((Split-Path -Leaf $pinFile) -eq "buildbox.example.local_7462.pin")
    ok "pin: round-trips in the canonical spelling" ((Get-ConstructRemotePin -BaseUrl $SVC -StoreDir $store) -eq $FP_A)
    ok "pin: stored as plain text (a hash is not a secret)" ([System.IO.File]::ReadAllText($pinFile) -eq $FP_A)
    ok "pin: a malformed fingerprint is refused" (Test-Throws { Save-ConstructRemotePin -BaseUrl $SVC -Fingerprint "nope" -StoreDir $store })
    [void](Remove-ConstructRemotePin -BaseUrl $SVC -StoreDir $store)
    ok "pin: removal clears it" ((Get-ConstructRemotePin -BaseUrl $SVC -StoreDir $store) -eq "")

    # ── (e) The DPAPI token store ───────────────────────────────────────────
    Write-Host ""
    Write-Host "=== DPAPI token store ===" -ForegroundColor Cyan
    if (Test-ConstructDpapiAvailable) {
        $secret = "tok_" + [Guid]::NewGuid().ToString("N")
        $tokenFile = Save-ConstructRemoteToken -BaseUrl $SVC -Token $secret -StoreDir $store
        ok "token: the file is named after the host slug" ((Split-Path -Leaf $tokenFile) -eq "buildbox.example.local_7462.token")
        $onDisk = [System.IO.File]::ReadAllText($tokenFile)
        ok "token: the PLAINTEXT never appears on disk" (-not $onDisk.Contains($secret))
        ok "token: round-trips through DPAPI" ((Get-ConstructRemoteToken -BaseUrl $SVC -StoreDir $store) -eq $secret)
        ok "token: an unknown host reads back empty" ((Get-ConstructRemoteToken -BaseUrl "https://other:7462" -StoreDir $store) -eq "")
        [void](Remove-ConstructRemoteToken -BaseUrl $SVC -StoreDir $store)
        ok "token: removal clears it" ((Get-ConstructRemoteToken -BaseUrl $SVC -StoreDir $store) -eq "")
        ok "token: an empty token is refused" (Test-Throws { Save-ConstructRemoteToken -BaseUrl $SVC -Token "" -StoreDir $store })
    } else {
        skip "token: DPAPI round-trip" "Windows DPAPI ([Security.Cryptography.ProtectedData], CurrentUser scope) is not available on this platform -- the store is Windows-only by design, and this test needs a real Windows session to exercise it"
        # What MUST hold everywhere: it refuses rather than falling back to plaintext.
        $threw = $false
        try { Save-ConstructRemoteToken -BaseUrl $SVC -Token "s3cret" -StoreDir $store } catch { $threw = $true }
        ok "token: without DPAPI it REFUSES rather than writing plaintext" $threw
        ok "token: ...and nothing was written" (-not (Test-Path -LiteralPath (Get-ConstructRemoteTokenPath -BaseUrl $SVC -StoreDir $store)))
    }

    # ── (f) Credential providers ────────────────────────────────────────────
    Write-Host ""
    Write-Host "=== Credential providers ===" -ForegroundColor Cyan
    $negotiate = New-ConstructApiAuth -Mode negotiate
    ok "auth: negotiate carries no secret at all" ($negotiate['Mode'] -eq 'negotiate' -and -not $negotiate.ContainsKey('Token'))
    $tokenAuth = New-ConstructApiAuth -Mode token -Token "abc123"
    ok "auth: an explicit token wins" ($tokenAuth['Mode'] -eq 'token' -and $tokenAuth['Token'] -eq "abc123")
    ok "auth: token mode with nothing available throws" `
        (Test-Throws { New-ConstructApiAuth -Mode token -BaseUrl "https://nobody:7462" -StoreDir $store })
    $secure = ConvertTo-SecureString "pw" -AsPlainText -Force
    $cred = New-Object System.Management.Automation.PSCredential("DOMAIN\alice", $secure)
    $credAuth = New-ConstructApiAuth -Mode credential -Credential $cred
    ok "auth: an explicit credential is held, not stored" ($credAuth['Mode'] -eq 'credential' -and $credAuth['Credential'].UserName -eq "DOMAIN\alice")
    ok "auth: credential mode without a credential throws" (Test-Throws { New-ConstructApiAuth -Mode credential })
    ok "auth: -Headers cannot smuggle an Authorization header past the provider" `
        (Test-Throws { New-ConstructApiAuth -Mode negotiate -Headers @{ Authorization = "Bearer x" } })
    $extra = New-ConstructApiAuth -Mode negotiate -Headers @{ 'X-Constructd-Test-Identity' = 'dev-admin' }
    ok "auth: -Headers ride every request" ($extra['Headers']['X-Constructd-Test-Identity'] -eq 'dev-admin')

    # ── (g) Invoke-ConstructApi, against a shadowed Invoke-WebRequest ───────
    Write-Host ""
    Write-Host "=== Invoke-ConstructApi ===" -ForegroundColor Cyan

    # A real exception type carrying a Response.StatusCode, so the error path is
    # exercised the way Invoke-WebRequest really fails (a note property added to a
    # System.Exception does not survive $ErrorRecord.Exception).
    if (-not ('ConstructTest.FakeWebException' -as [type])) {
        Add-Type -TypeDefinition @"
namespace ConstructTest {
    public class FakeWebResponse { public int StatusCode; }
    public class FakeWebException : System.Exception {
        public FakeWebResponse Response;
        public FakeWebException(string message, int status) : base(message) {
            Response = new FakeWebResponse();
            Response.StatusCode = status;
        }
    }
}
"@
    }

    $script:calls = New-Object System.Collections.Generic.List[object]
    $script:nextStatus = 200
    $script:nextContent = '{"ok":true}'
    $script:nextBody = ""
    function Invoke-WebRequest {
        [CmdletBinding()]
        param(
            $Uri, $Method, $Headers, $Body, $ContentType, $TimeoutSec,
            [switch]$UseBasicParsing, [switch]$UseDefaultCredentials, [switch]$SkipCertificateCheck,
            $Credential
            # NOT $ErrorAction: [CmdletBinding()] already supplies it as a common
            # parameter, and re-declaring it is a binding error.
        )
        $script:calls.Add([pscustomobject]@{
            Uri = $Uri; Method = $Method; Headers = $Headers; Body = $Body; ContentType = $ContentType
            UseDefaultCredentials = [bool]$UseDefaultCredentials
            SkipCertificateCheck = [bool]$SkipCertificateCheck
            Credential = $Credential
        })
        if ($script:nextStatus -ge 200 -and $script:nextStatus -lt 300) {
            return [pscustomobject]@{ StatusCode = $script:nextStatus; Content = $script:nextContent }
        }
        $ex = New-Object ConstructTest.FakeWebException("Response status code does not indicate success: $($script:nextStatus).", $script:nextStatus)
        $er = New-Object System.Management.Automation.ErrorRecord($ex, 'FakeWeb', [System.Management.Automation.ErrorCategory]::InvalidOperation, $null)
        if ($script:nextBody) { $er.ErrorDetails = New-Object System.Management.Automation.ErrorDetails($script:nextBody) }
        throw $er
    }
    function Reset-Calls { $script:calls.Clear(); $script:nextStatus = 200; $script:nextContent = '{"ok":true}'; $script:nextBody = "" }

    # http, so pinning is out of the picture for the shape tests.
    $DEV = "http://127.0.0.1:7999"

    Reset-Calls
    $script:nextContent = '{"name":"DOMAIN\\alice","known":true,"role":"user","maxVms":2}'
    $me = Invoke-ConstructApi -BaseUrl $DEV -Method GET -Path '/whoami' -Auth $negotiate
    ok "api: the JSON body comes back parsed" ($me.name -eq 'DOMAIN\alice' -and $me.known -eq $true)
    ok "api: exactly one request" ($script:calls.Count -eq 1)
    ok "api: /whoami is prefixed with /api/v1" ($script:calls[0].Uri -eq "$DEV/api/v1/whoami")
    ok "api: negotiate uses the process identity" ($script:calls[0].UseDefaultCredentials -eq $true)
    ok "api: negotiate sends no Authorization header" (-not $script:calls[0].Headers.ContainsKey('Authorization'))
    ok "api: a GET carries no body" ($null -eq $script:calls[0].Body)
    ok "api: the status is readable afterwards" ((Get-ConstructApiLastStatus) -eq 200)

    Reset-Calls
    [void](Invoke-ConstructApi -BaseUrl $DEV -Method GET -Path 'whoami' -Auth $tokenAuth)
    ok "api: a bare route is prefixed too" ($script:calls[0].Uri -eq "$DEV/api/v1/whoami")
    ok "api: token mode sends a bearer header" ($script:calls[0].Headers['Authorization'] -eq "Bearer abc123")
    ok "api: token mode does NOT use the process identity" ($script:calls[0].UseDefaultCredentials -eq $false)

    Reset-Calls
    [void](Invoke-ConstructApi -BaseUrl $DEV -Method GET -Path '/api/v1/whoami' -Auth $credAuth)
    ok "api: an already-prefixed route is left alone" ($script:calls[0].Uri -eq "$DEV/api/v1/whoami")
    ok "api: credential mode passes the PSCredential" ($script:calls[0].Credential.UserName -eq 'DOMAIN\alice')
    ok "api: credential mode does not also use the process identity" ($script:calls[0].UseDefaultCredentials -eq $false)

    Reset-Calls
    $script:nextStatus = 202
    $script:nextContent = '{"jobId":"j1"}'
    $accepted = Invoke-ConstructApi -BaseUrl $DEV -Method POST -Path '/vms' -Body @{ name = 'work-vm'; cpu = 4; ramGb = 8; diskGb = 50 } -Auth $tokenAuth
    ok "api: a 202 body is parsed" ($accepted.jobId -eq 'j1')
    ok "api: a body is serialised as JSON" ($script:calls[0].Body -match '"name":"work-vm"' -and $script:calls[0].ContentType -eq 'application/json')
    ok "api: the method is forwarded" ($script:calls[0].Method -eq 'POST')

    Reset-Calls
    [void](Invoke-ConstructApi -BaseUrl $DEV -Method GET -Path '/whoami' -Auth $extra)
    ok "api: provider headers ride the request" ($script:calls[0].Headers['X-Constructd-Test-Identity'] -eq 'dev-admin')

    Reset-Calls
    $script:nextStatus = 204
    $script:nextContent = ''
    ok "api: an empty body is `$null, not an error" ($null -eq (Invoke-ConstructApi -BaseUrl $DEV -Method DELETE -Path '/x' -Auth $tokenAuth))

    # ── the 401 fallback path -NoThrow exists for ───────────────────────────
    Reset-Calls
    $script:nextStatus = 401
    $script:nextBody = '{"title":"Unauthorized","detail":"credentials refused"}'
    $threw = $false
    try { [void](Invoke-ConstructApi -BaseUrl $DEV -Method GET -Path '/whoami' -Auth $negotiate) } catch { $threw = $true }
    ok "api: a 401 throws by default" $threw
    Reset-Calls
    $script:nextStatus = 401
    $script:nextBody = '{"title":"Unauthorized","detail":"credentials refused"}'
    $r = Invoke-ConstructApi -BaseUrl $DEV -Method GET -Path '/whoami' -Auth $negotiate -NoThrow
    ok "api: -NoThrow returns `$null instead" ($null -eq $r)
    ok "api: ...and the STATUS is what the caller branches on" ((Get-ConstructApiLastStatus) -eq 401)
    ok "api: ...and the problem document's detail is the message" ((Get-ConstructApiLastError) -match 'credentials refused')
    ok "api: the problem TITLE is included too" ((Get-ConstructApiLastError) -match 'Unauthorized')

    Reset-Calls
    $script:nextStatus = 404
    $script:nextBody = '{"title":"Not found","detail":"No VM named ''nope''."}'
    [void](Invoke-ConstructApi -BaseUrl $DEV -Method GET -Path '/vms/nope/state' -Auth $tokenAuth -NoThrow)
    ok "api: a 404 keeps its status (the driver's ONLY 'absent')" ((Get-ConstructApiLastStatus) -eq 404)

    Reset-Calls
    $script:nextStatus = 409
    $script:nextBody = 'not json at all'
    [void](Invoke-ConstructApi -BaseUrl $DEV -Method GET -Path '/vms/x/endpoint' -Auth $tokenAuth -NoThrow)
    ok "api: a non-JSON error body is still surfaced" ((Get-ConstructApiLastStatus) -eq 409 -and (Get-ConstructApiLastError) -match 'not json')

    # ── (h) Pinning: no pin -> no call; wrong pin -> no call ────────────────
    Write-Host ""
    Write-Host "=== Certificate pinning ===" -ForegroundColor Cyan
    Reset-Calls
    $threw = $false; $msg = ""
    try { [void](Invoke-ConstructApi -BaseUrl $SVC -Method GET -Path '/whoami' -Auth $negotiate -StoreDir $store) }
    catch { $threw = $true; $msg = $_.Exception.Message }
    ok "pinning: an https host with NO pin is refused" $threw
    ok "pinning: ...naming the enrolment step" ($msg -match 'Add the host')
    ok "pinning: ...and NO request was made" ($script:calls.Count -eq 0)

    # With a pin in place, the PS7 path verifies the presented certificate BEFORE the
    # request. Shadow the reader so no socket is opened.
    $script:presented = $FP_A
    function Get-ConstructRemoteFingerprint { param([string]$BaseUrl, [int]$TimeoutMs = 10000) return $script:presented }
    [void](Save-ConstructRemotePin -BaseUrl $SVC -Fingerprint $FP_A -StoreDir $store)

    Reset-Calls
    $script:presented = $FP_A
    [void](Invoke-ConstructApi -BaseUrl $SVC -Method GET -Path '/whoami' -Auth $tokenAuth -StoreDir $store)
    ok "pinning: a matching certificate lets the request through" ($script:calls.Count -eq 1)
    if (Test-ConstructPwshCore) {
        ok "pinning: PS7 skips the (useless) chain check after verifying itself" ($script:calls[0].SkipCertificateCheck -eq $true)
    } else {
        ok "pinning: PS 5.1 pins inside the handshake, so no skip flag is passed" ($script:calls[0].SkipCertificateCheck -eq $false)
    }

    if (Test-ConstructPwshCore) {
        Reset-Calls
        $script:presented = $FP_B
        $threw = $false; $msg = ""
        try { [void](Invoke-ConstructApi -BaseUrl $SVC -Method GET -Path '/whoami' -Auth $tokenAuth -StoreDir $store) }
        catch { $threw = $true; $msg = $_.Exception.Message }
        ok "pinning: a CHANGED certificate is refused" $threw
        ok "pinning: ...naming both fingerprints" ($msg -match [regex]::Escape($FP_A) -and $msg -match [regex]::Escape($FP_B))
        ok "pinning: ...and NO request was made" ($script:calls.Count -eq 0)
    } else {
        skip "pinning: a changed certificate is refused" "on Windows PowerShell 5.1 the pin is enforced inside the TLS handshake (ServicePointManager), which a shadowed Invoke-WebRequest cannot exercise -- the PS7 branch covers the comparison itself"
    }
    Reset-Calls
    $script:presented = $FP_B
    [void](Invoke-ConstructApi -BaseUrl $SVC -Method GET -Path '/whoami' -Auth $tokenAuth -Pin $FP_B -StoreDir $store)
    ok "pinning: an explicit -Pin overrides the stored one" ($script:calls.Count -eq 1)

    Reset-Calls
    [void](Invoke-ConstructApi -BaseUrl $DEV -Method GET -Path '/whoami' -Auth $tokenAuth -StoreDir $store)
    ok "pinning: http needs no pin (there is no certificate)" ($script:calls.Count -eq 1)

    # ── (i) Wait-ConstructJob ───────────────────────────────────────────────
    Write-Host ""
    Write-Host "=== Wait-ConstructJob ===" -ForegroundColor Cyan
    # Shadow the API call itself: this function's job is polling and progress, not HTTP.
    $script:jobSteps = @()
    $script:jobIndex = 0
    $script:jobPolls = 0
    function Invoke-ConstructApi {
        [CmdletBinding()]
        param([string]$BaseUrl, [string]$Method = 'GET', [string]$Path, $Body, $Auth, [string]$Pin, [string]$StoreDir, [int]$TimeoutSec = 100, [switch]$NoThrow)
        $script:jobPolls++
        $i = [Math]::Min($script:jobIndex, $script:jobSteps.Count - 1)
        $script:jobIndex++
        return $script:jobSteps[$i]
    }
    function New-FakeJob([string]$State, [string[]]$Progress, $Result, [string]$JobError) {
        $lines = @()
        foreach ($t in $Progress) { $lines += [pscustomobject]@{ at = "now"; text = $t } }
        return [pscustomobject]@{ id = 'j1'; kind = 'create-vm'; state = $State; progress = $lines; result = $Result; error = $JobError }
    }

    $seen = New-Object System.Collections.Generic.List[string]
    $script:jobSteps = @(
        (New-FakeJob 'running'   @('building autoinstall ISO')                                 $null ''),
        (New-FakeJob 'running'   @('building autoinstall ISO', 'creating vm work-vm')          $null ''),
        (New-FakeJob 'succeeded' @('building autoinstall ISO', 'creating vm work-vm', 'ready') ([pscustomobject]@{ name = 'work-vm'; endpoint = [pscustomobject]@{ sshHost = 'buildbox'; sshPort = 2201 }; vmToken = 'ONE-TIME' }) '')
    )
    $script:jobIndex = 0; $script:jobPolls = 0
    $result = Wait-ConstructJob -BaseUrl $DEV -JobId 'j1' -Auth $tokenAuth -PollSeconds 1 -OnProgress { param($line) $seen.Add($line) }
    ok "job: it polls until the job is terminal" ($script:jobPolls -eq 3)
    ok "job: every progress line is emitted ONCE, in order" (($seen -join '|') -eq 'building autoinstall ISO|creating vm work-vm|ready')
    ok "job: the result object is returned" ($result.name -eq 'work-vm' -and $result.endpoint.sshPort -eq 2201)
    # The one-time secret: it rides the TERMINAL poll, which is this call.
    ok "job: the one-time VM token is in the returned result" ($result.vmToken -eq 'ONE-TIME')
    ok "job: the token is never printed as progress" (-not (($seen -join '|') -match 'ONE-TIME'))

    $script:jobSteps = @((New-FakeJob 'failed' @('building autoinstall ISO') $null 'IsoBuildFailedException'))
    $script:jobIndex = 0
    $threw = $false; $msg = ""
    try { [void](Wait-ConstructJob -BaseUrl $DEV -JobId 'j1' -Auth $tokenAuth -PollSeconds 1 -OnProgress { }) }
    catch { $threw = $true; $msg = $_.Exception.Message }
    ok "job: a failed job throws" $threw
    ok "job: ...carrying the service's own error" ($msg -match 'IsoBuildFailedException')

    $script:jobSteps = @((New-FakeJob 'running' @() $null ''))
    $script:jobIndex = 0
    $threw = $false; $msg = ""
    try { [void](Wait-ConstructJob -BaseUrl $DEV -JobId 'j1' -Auth $tokenAuth -PollSeconds 1 -TimeoutSeconds 1 -OnProgress { }) }
    catch { $threw = $true; $msg = $_.Exception.Message }
    ok "job: a job that never finishes times out" $threw
    ok "job: ...and says how to check on it" ($msg -match '/jobs/j1')

} finally {
    if (Test-Path -LiteralPath $store) { Remove-Item -LiteralPath $store -Recurse -Force -ErrorAction SilentlyContinue }
}

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "  $($script:pass) passed, $($script:fail) failed, $($script:skip) skipped" -ForegroundColor $(if ($script:fail -gt 0) { "Red" } else { "Green" })
Write-Host "==============================" -ForegroundColor Cyan
if ($script:fail -gt 0) { exit 1 }
exit 0
