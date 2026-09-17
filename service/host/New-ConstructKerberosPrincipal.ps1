#requires -Version 5.1
<#
.SYNOPSIS
    Prepares Active Directory for a Construct host on Linux (Proxmox) that should accept Windows
    sign-in: a service account, its HTTP service principal name, a DNS record for the host, and the
    keytab the host authenticates with. Run ON A DOMAIN CONTROLLER (or a machine with RSAT) as a
    domain administrator.

.DESCRIPTION
    A Windows Construct host signs Kerberos tickets with its own domain identity. A Linux host has
    no such identity, so one is created for it:

      1. a user account `-AccountName` (default svc-constructd) with a random, never-expiring
         password, AES-256 only, no interactive rights,
      2. the service principal name HTTP/<host FQDN> on it — that is what a PC's
         Invoke-WebRequest -UseDefaultCredentials asks the KDC for when it dials
         https://<host FQDN>:7462, so the URL clients use MUST be this name,
      3. an A record <host> -> <address> in the AD DNS zone, unless it already resolves,
      4. a keytab (ktpass) written to -KeytabPath, to be copied to the host and installed with
         install-construct-host.sh --keytab <file> --netbios-domain <NETBIOS> --realm <REALM>.

    Re-running is safe: the account, the SPN and the record are kept; the keytab is regenerated
    only with -RotateKeytab, which resets the account password (the old keytab stops working).

.EXAMPLE
    .\New-ConstructKerberosPrincipal.ps1 -HostFqdn test-proxmox.corp.example.com -Address 203.0.113.184 -KeytabPath C:\constructd.keytab
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9]([a-z0-9-]*[a-z0-9])?(\.[a-z0-9]([a-z0-9-]*[a-z0-9])?)+$')][string]$HostFqdn,
    [Parameter(Mandatory)][ValidatePattern('^\d{1,3}(\.\d{1,3}){3}$')][string]$Address,
    [Parameter(Mandatory)][string]$KeytabPath,
    [ValidatePattern('^[A-Za-z][A-Za-z0-9._-]{0,19}$')][string]$AccountName = 'svc-constructd',
    [switch]$RotateKeytab
)
$ErrorActionPreference = 'Stop'
Import-Module ActiveDirectory -ErrorAction Stop

$domain = Get-ADDomain
$realm = $domain.DNSRoot.ToUpperInvariant()
$netbios = $domain.NetBIOSName
$spn = "HTTP/$HostFqdn"
$principal = "$spn@$realm"
$zone = $domain.DNSRoot
$label = $HostFqdn
if ($HostFqdn.EndsWith(".$zone", [StringComparison]::OrdinalIgnoreCase)) { $label = $HostFqdn.Substring(0, $HostFqdn.Length - $zone.Length - 1) }

function New-RandomPassword {
    $bytes = New-Object byte[] 32
    [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    return ([Convert]::ToBase64String($bytes)).TrimEnd('=')
}

Write-Host "==> Domain $($domain.DNSRoot) (realm $realm, NetBIOS $netbios)"

# ── 1. The service account ────────────────────────────────────────────────────
$marker = "The Construct host service on $HostFqdn (Kerberos service principal)"
$account = Get-ADUser -Filter "SamAccountName -eq '$AccountName'" -Properties Description -ErrorAction SilentlyContinue
$password = $null
if (-not $account) {
    $password = New-RandomPassword
    if ($PSCmdlet.ShouldProcess($AccountName, "create service account")) {
        # The bare create first; every extra attribute is its own write with its own error message,
        # so a refused ACL change (CannotChangePassword) or encryption-type write is visible as such
        # and never masks the creation.
        New-ADUser -Name $AccountName -SamAccountName $AccountName -UserPrincipalName "$AccountName@$($domain.DNSRoot)" `
            -Description $marker -AccountPassword (ConvertTo-SecureString $password -AsPlainText -Force) -Enabled $true
        $account = Get-ADUser -Identity $AccountName -Properties Description
        Write-Host "    created $AccountName"
    }
} else {
    Write-Host "    account $AccountName exists"
    # Our own account with no keytab yet (an earlier run stopped before ktpass): a new password is
    # the only way to a keytab, and nothing else uses it. A foreign account is only touched on request.
    $ours = [string]$account.Description -eq $marker
    if ($RotateKeytab -or ($ours -and -not (Test-Path -LiteralPath $KeytabPath))) {
        # ktpass sets a fresh random password itself while writing the keytab (step 4); the AD
        # module's password reset is not used, since it is refused on some domains where ktpass is not.
        $password = 'rotate'
        Write-Host "    the keytab step will reset the password"
    }
}
if ($account -and $PSCmdlet.ShouldProcess($AccountName, "set service-account attributes")) {
    foreach ($step in @(
        @{ What = 'PasswordNeverExpires';    Do = { Set-ADUser -Identity $AccountName -PasswordNeverExpires $true } },
        @{ What = 'KerberosEncryptionType';  Do = { Set-ADUser -Identity $AccountName -KerberosEncryptionType AES256 } },
        @{ What = 'CannotChangePassword';    Do = { Set-ADUser -Identity $AccountName -CannotChangePassword $true } }
    )) {
        try { & $step.Do; Write-Host "    $($step.What) set" }
        catch { Write-Warning "$($step.What) could not be set on ${AccountName} ($($_.Exception.Message)); continuing (ktpass sets AES-256 itself)." }
    }
}

# ── 2. The service principal name ─────────────────────────────────────────────
$existing = @(Get-ADUser -Identity $AccountName -Properties servicePrincipalName).servicePrincipalName
if ($existing -notcontains $spn) {
    $owner = Get-ADObject -Filter "servicePrincipalName -eq '$spn'" -ErrorAction SilentlyContinue
    if ($owner -and $owner.Name -ne $AccountName) { throw "$spn is already registered on $($owner.Name); remove it there first (setspn -D)." }
    if ($PSCmdlet.ShouldProcess($AccountName, "add SPN $spn")) {
        Set-ADUser -Identity $AccountName -ServicePrincipalNames @{ Add = $spn }
        Write-Host "    SPN $spn added"
    }
} else {
    Write-Host "    SPN $spn present"
}

# ── 3. The DNS record ─────────────────────────────────────────────────────────
$resolved = $null
try { $resolved = @(Resolve-DnsName -Name $HostFqdn -Type A -ErrorAction Stop | Where-Object { $_.Type -eq 'A' } | Select-Object -ExpandProperty IPAddress) } catch { $resolved = @() }
if ($resolved -contains $Address) {
    Write-Host "    $HostFqdn already resolves to $Address"
} elseif ($resolved.Count -gt 0) {
    Write-Warning "$HostFqdn resolves to $($resolved -join ', '), not $Address. Fix the record by hand (Set-DnsServerResourceRecord) or pass the address it should have."
} elseif ($PSCmdlet.ShouldProcess("$label.$zone", "add A record -> $Address")) {
    if (-not (Get-Module -ListAvailable DnsServer)) { throw "The DnsServer module is not available here; add the A record $HostFqdn -> $Address in the AD DNS zone $zone yourself." }
    Import-Module DnsServer
    Add-DnsServerResourceRecordA -ZoneName $zone -Name $label -IPv4Address $Address -TimeToLive (New-TimeSpan -Hours 1)
    Write-Host "    A record $HostFqdn -> $Address added"
}

# ── 4. The keytab ─────────────────────────────────────────────────────────────
if ($password) {
    if (-not $PSCmdlet.ShouldProcess($KeytabPath, "write keytab for $principal")) { return }
    $ktpass = Get-Command ktpass.exe -ErrorAction SilentlyContinue
    if (-not $ktpass) { throw "ktpass.exe not found (RSAT / a domain controller has it)." }
    if (Test-Path -LiteralPath $KeytabPath) { Remove-Item -LiteralPath $KeytabPath -Force }
    # /pass +rndPass: ktpass generates the password and sets it on the account itself (SAM, not the AD
    # web service), so the keytab and the account always agree and no password ever passes through
    # PowerShell. /mapop set: the SPN is already on the account; no second mapping is added.
    & $ktpass.Source /princ $principal /mapuser "$netbios\$AccountName" /crypto AES256-SHA1 /ptype KRB5_NT_PRINCIPAL /pass +rndPass /out $KeytabPath /mapop set /setupn 2>&1 | ForEach-Object { "    ktpass: $_" } | Write-Host
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $KeytabPath)) { throw "ktpass failed; no keytab written." }
    Write-Host "    keytab written to $KeytabPath (copy it to the host, then delete it here)"
} else {
    Write-Host "    keytab not regenerated (account existed; pass -RotateKeytab to reset the password and write a new one)"
}

Write-Host ""
Write-Host "On the Construct host (as root, from the checkout):"
Write-Host "    bash service/host/install-construct-host.sh --public-host $HostFqdn --keytab /root/constructd.keytab --netbios-domain $netbios --realm $realm"
Write-Host "Then enrol from a PC with your Windows account:"
Write-Host "    .\Auto-Install.ps1 -Backend hyperv-remote -ServiceUrl https://${HostFqdn}:7462 -InstanceName <name>"
Write-Host "and add users on the host as $netbios\<user>:"
Write-Host "    /opt/construct/host/Constructd.Api admin users add '$netbios\<user>' --max-vms 3"
