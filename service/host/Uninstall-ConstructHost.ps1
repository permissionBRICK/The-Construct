#Requires -Version 5.1
<#
.SYNOPSIS
    Remove the constructd host service from this machine.

.DESCRIPTION
    The companion to Install-ConstructHost.ps1. It stops and deletes the Windows
    service, removes the firewall rules the installer created, and optionally the
    port-proxy rules, the TLS certificate and the data directory.

    Two things are deliberately NOT removed unless you ask:

      * The DATA DIRECTORY (users, tokens, the VM registry, the audit trail). It is
        the record of who had what, and a reinstall on the same host is expected to
        find it. -RemoveData deletes it.
      * The VMs themselves. This script never touches Hyper-V: deleting a colleague's
        VM is not an uninstall step. Remove them through the API (or
        Remove-ConstructVm) first if that is what you want.

    Also NOT removed: the Hyper-V features, WSL and the OpenSSH client. They are
    machine-wide and were probably not installed for this service alone.

    Everything honours -WhatIf.

.PARAMETER RemovePortProxies
    Also delete the netsh portproxy rules inside the configured ranges. Without this
    the VMs' forwards keep working (and keep listening) after the service is gone.

.PARAMETER RemoveCertificate
    Also delete the self-signed certificate the installer created for -PublicHost.

.PARAMETER RemoveData
    Also delete the data directory. This is irreversible: users, tokens and the
    audit trail go with it.

.EXAMPLE
    .\Uninstall-ConstructHost.ps1 -WhatIf

.EXAMPLE
    .\Uninstall-ConstructHost.ps1 -RemovePortProxies -RemoveCertificate -RemoveData
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$ServiceName = "constructd",

    [string]$DataDir = "C:\ProgramData\Construct\service",

    [string]$PublicHost = $env:COMPUTERNAME,

    [ValidatePattern('^\d+-\d+$')]
    [string]$SshPortRange = "2201-2299",

    [ValidatePattern('^\d+-\d+$')]
    [string]$AppPortRange = "2300-2999",

    [string]$ListenAddress = "0.0.0.0",

    [switch]$RemovePortProxies,

    [switch]$RemoveCertificate,

    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"

function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }

# ── Value transport ──────────────────────────────────────────────────────────
# Deliberately the same mechanism as Install-ConstructHost.ps1, for the same reason:
# this script also crosses a privilege boundary, and an uninstaller is the more
# attractive target of the two -- a value that can inject an argument after UAC turns
# a harmless uninstall into -RemoveData against a path of somebody else's choosing.
#
# So no bound value is ever quoted into a command line, and none of them travels
# through a file: PowerShell FLATTENS -ArgumentList back into one command-line string,
# so a value containing a quote can close the intended token and start another switch,
# and a payload file would have to live in the unelevated caller's own writable %TEMP%
# where it could be rewritten while the UAC prompt is up. Instead the values are
# serialized to JSON, embedded as inert base64 INSIDE the encoded command (which is
# fixed once Start-Process has been called), then decoded and SPLATTED at this script.

function ConvertTo-ConstructPayload {
    <#
        Serialize a hashtable for the elevated side to read back. JSON, because it
        round-trips arbitrary text -- spaces, quotes, semicolons, apostrophes,
        newlines -- with no escaping decisions of ours. Switch parameters become
        plain booleans so splatting them back works ($ht['RemoveData'] = $true
        splats as -RemoveData:$true).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][hashtable]$Values)

    $plain = @{}
    foreach ($kv in $Values.GetEnumerator()) {
        $value = $kv.Value
        if ($value -is [System.Management.Automation.SwitchParameter]) { $value = [bool]$value.IsPresent }
        $plain[$kv.Key] = $value
    }
    return ($plain | ConvertTo-Json -Depth 5 -Compress)
}

function New-ConstructRelaunchScript {
    <#
        The script the ELEVATED copy runs: decode the parameters baked into it, splat
        them at this script, propagate the exit code. Base64 is inert
        ([A-Za-z0-9+/=] only), so no value can end the literal it sits in; the only
        thing interpolated is this script's own path.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ScriptPath,
        [Parameter(Mandatory = $true)][AllowEmptyString()][string]$PayloadJson
    )

    # Single-quoted PowerShell literals expand nothing; doubling the quote is the
    # complete escape, and this path is ours rather than a caller's.
    $script  = $ScriptPath.Replace("'", "''")
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($PayloadJson))

    return @"
`$ErrorActionPreference = 'Stop'
try {
    `$raw = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$payload')) | ConvertFrom-Json
    `$bound = @{}
    foreach (`$p in `$raw.PSObject.Properties) { `$bound[`$p.Name] = `$p.Value }
    & '$script' @bound
    exit 0
} catch {
    Write-Host ""
    Write-Host `$_.Exception.Message -ForegroundColor Red
    Write-Host ""
    Read-Host "Press Enter to close"
    exit 1
}
"@
}

# ── Self-elevate to Administrator ────────────────────────────────────────────
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Relaunching as Administrator..." -ForegroundColor Yellow

    $relaunch = New-ConstructRelaunchScript -ScriptPath $PSCommandPath `
                    -PayloadJson (ConvertTo-ConstructPayload -Values $PSBoundParameters)
    $encoded  = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($relaunch))

    $elevated = Start-Process powershell.exe -Verb RunAs -PassThru -Wait -ArgumentList @(
        "-NoProfile", "-ExecutionPolicy", "Bypass", "-EncodedCommand", $encoded)
    exit $elevated.ExitCode
}

function Split-PortRange {
    param([Parameter(Mandatory = $true)][string]$Range, [Parameter(Mandatory = $true)][string]$Name)

    $parts = $Range.Split("-")
    $start = [int]$parts[0]
    $end   = [int]$parts[1]
    if ($start -lt 1 -or $end -gt 65535 -or $end -lt $start) {
        throw "$Name must be a port range like 2201-2299 (1-65535, start <= end); got '$Range'."
    }
    return @{ Start = $start; End = $end }
}

function ConvertFrom-PortProxyRow {
    <#
        One row of `netsh interface portproxy show v4tov4`, or $null when the line is
        not a rule. Deliberately the same rule the service's own parser uses
        (Constructd.Windows PortProxyParser): read rows BY SHAPE -- four whitespace-
        separated tokens that are address, port, address, port -- never by column
        header, because netsh is localized and a German host prints
        "Lauschen auf ipv4:" / "Adresse Port".

        "*" is netsh's wildcard listen address and means 0.0.0.0; some Windows builds
        print it instead of the literal, and not normalizing it here would leave every
        Construct rule behind while reporting that none were found.
    #>
    param([Parameter(Mandatory = $true)][AllowEmptyString()][string]$Line)

    $tokens = @($Line -split '\s+' | Where-Object { $_ })
    if ($tokens.Count -ne 4) { return $null }

    $listenAddress = $tokens[0]
    if ($listenAddress -eq "*") { $listenAddress = "0.0.0.0" }

    $connectAddress = $tokens[2]
    if ($connectAddress -eq "*") { $connectAddress = "0.0.0.0" }

    $parsedListen = $null
    $parsedConnect = $null
    if (-not [System.Net.IPAddress]::TryParse($listenAddress, [ref]$parsedListen)) { return $null }
    if (-not [System.Net.IPAddress]::TryParse($connectAddress, [ref]$parsedConnect)) { return $null }
    if ($parsedListen.AddressFamily -ne 'InterNetwork' -or $parsedConnect.AddressFamily -ne 'InterNetwork') { return $null }

    $listenPort = 0
    $connectPort = 0
    if (-not [int]::TryParse($tokens[1], [ref]$listenPort)) { return $null }
    if (-not [int]::TryParse($tokens[3], [ref]$connectPort)) { return $null }
    if ($listenPort -lt 1 -or $listenPort -gt 65535 -or $connectPort -lt 1 -or $connectPort -gt 65535) { return $null }

    return @{
        ListenAddress  = $parsedListen.ToString()
        ListenPort     = $listenPort
        ConnectAddress = $parsedConnect.ToString()
        ConnectPort    = $connectPort
    }
}

# ── 1. Service ───────────────────────────────────────────────────────────────

Write-Step "Removing the Windows service"
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if (-not $service) {
    Write-Note "No service named '$ServiceName' -- nothing to remove"
} else {
    if ($service.Status -ne "Stopped" -and $PSCmdlet.ShouldProcess($ServiceName, "Stop the service")) {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", (New-TimeSpan -Seconds 60))
    }

    if ($PSCmdlet.ShouldProcess($ServiceName, "Delete the service")) {
        # Remove-Service is PowerShell 6+; sc.exe is what PS 5.1 has.
        & sc.exe delete $ServiceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed with exit code $LASTEXITCODE." }
    }
    Write-Ok "Service removed"
}

# ── 2. Firewall rules ────────────────────────────────────────────────────────

Write-Step "Removing the firewall rules"
foreach ($name in @(
    "Construct constructd API",
    "Construct constructd SSH forwards",
    "Construct constructd app forwards")) {

    $rule = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
    if ($rule) {
        if ($PSCmdlet.ShouldProcess($name, "Remove the firewall rule")) { $rule | Remove-NetFirewallRule }
        Write-Ok $name
    } else {
        Write-Note "$name (not present)"
    }
}

# ── 3. Port-proxy rules ──────────────────────────────────────────────────────

Write-Step "Host port forwards"
if (-not $RemovePortProxies) {
    Write-Note "Left in place. They keep forwarding to the VMs; pass -RemovePortProxies to delete them."
} else {
    $sshRange = Split-PortRange -Range $SshPortRange -Name "-SshPortRange"
    $appRange = Split-PortRange -Range $AppPortRange -Name "-AppPortRange"

    # Only rules inside the service's own ranges, on its own listen address: the
    # host may have port proxies that have nothing to do with Construct.
    $shown = & netsh.exe interface portproxy show v4tov4
    if ($LASTEXITCODE -ne 0) {
        throw "netsh could not list the port-proxy rules (exit $LASTEXITCODE); nothing was removed."
    }

    $wildcardListen = ($ListenAddress -eq "0.0.0.0")
    $removed = 0
    $failed  = 0

    foreach ($line in $shown) {
        $rule = ConvertFrom-PortProxyRow -Line ([string]$line)
        if (-not $rule) { continue }
        if ($rule.ListenAddress -ne $ListenAddress) { continue }

        $inRange = ($rule.ListenPort -ge $sshRange.Start -and $rule.ListenPort -le $sshRange.End) -or
                   ($rule.ListenPort -ge $appRange.Start -and $rule.ListenPort -le $appRange.End)
        if (-not $inRange) { continue }

        if ($PSCmdlet.ShouldProcess("$ListenAddress`:$($rule.ListenPort)", "Delete the portproxy rule")) {
            & netsh.exe interface portproxy delete v4tov4 "listenaddress=$ListenAddress" "listenport=$($rule.ListenPort)" | Out-Null
            if ($LASTEXITCODE -ne 0) {
                Write-Warning "Could not delete the rule on ${ListenAddress}:$($rule.ListenPort) (netsh exit $LASTEXITCODE)."
                $failed++
                continue
            }
        }
        $removed++
    }

    Write-Ok "Removed $removed port-proxy rule(s)"
    if ($failed -gt 0) {
        Write-Warning "$failed rule(s) could not be removed; check 'netsh interface portproxy show v4tov4'."
    }
    if ($removed -eq 0 -and -not $wildcardListen) {
        Write-Note "Nothing matched on $ListenAddress. If the service listened on a different address, pass -ListenAddress."
    }
}

# ── 4. Certificate ───────────────────────────────────────────────────────────

Write-Step "TLS certificate"
if (-not $RemoveCertificate) {
    Write-Note "Left in place. Pass -RemoveCertificate to delete the self-signed one."
} else {
    $friendly = "Construct constructd ($PublicHost)"
    $certs = @(Get-ChildItem -Path Cert:\LocalMachine\My | Where-Object { $_.FriendlyName -eq $friendly })
    if ($certs.Count -eq 0) {
        Write-Note "No certificate named '$friendly'"
    } else {
        foreach ($cert in $certs) {
            if ($PSCmdlet.ShouldProcess($cert.Thumbprint, "Delete the certificate")) {
                Remove-Item -Path $cert.PSPath -Force
            }
            Write-Ok "Removed $($cert.Thumbprint)"
        }
    }
}

# ── 5. Data ──────────────────────────────────────────────────────────────────

Write-Step "Data directory"
if (-not $RemoveData) {
    Write-Note "$DataDir left in place (users, tokens, VM registry, audit trail). Pass -RemoveData to delete it."
} elseif (-not (Test-Path -LiteralPath $DataDir)) {
    Write-Note "$DataDir does not exist"
} else {
    if ($PSCmdlet.ShouldProcess($DataDir, "Delete the data directory and everything in it")) {
        Remove-Item -LiteralPath $DataDir -Recurse -Force
    }
    Write-Ok "Removed $DataDir"
}

Write-Host ""
Write-Host "constructd has been removed from this machine." -ForegroundColor Cyan
Write-Note "The VMs themselves are untouched -- this script never talks to Hyper-V."
Write-Note "Hyper-V, WSL and the OpenSSH client were left installed."
Write-Host ""
