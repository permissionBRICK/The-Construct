#Requires -Version 5.1
<#
.SYNOPSIS
    Install (or update) the constructd host service on this Windows machine.

.DESCRIPTION
    Turns a Windows box with Hyper-V into a Construct host that several users can
    create and manage their own VMs on (docs/plans/modular-remote-architecture.md
    section 4.4). It is idempotent: run it again after publishing a new build and
    it updates the binaries, the settings and the service in place.

    What it does, in order:

      1. Self-elevates (the whole thing needs Administrator).
      2. Prerequisites: Hyper-V and the platform features (via the repo's own
         Ensure-HyperV in lib\AgentVm.Common.ps1 -- the same check the local
         installer runs), a WSL distro with xorriso + whois inside it, and the
         Windows OpenSSH client. No .NET runtime is required: publish the service
         self-contained.
      3. Data directory (database + ISO cache) under ProgramData.
      4. TLS certificate: -CertThumbprint, or a self-signed one bound to
         -PublicHost. The thumbprint is printed prominently -- clients pin it at
         enrollment, so it is the one value that has to leave this machine.
      5. Firewall: inbound TCP for the API port and both forward port ranges.
      6. appsettings.Production.json next to the published executable.
      7. The first admin user plus an API token, created through the service's own
         admin CLI BEFORE the service starts (so nothing contends for the
         database, and so the host is reachable the moment it comes up).
      8. Registers the Windows service as LocalSystem and starts it.
      9. Prints the enrollment details.

    Everything that changes the machine honours -WhatIf.

.PARAMETER ScriptsDir
    The Construct checkout the service invokes: it must contain drivers\,
    lib\ and bin\. The service builds ISOs and drives Hyper-V through the files in
    here, so it stays on disk after the install.

.PARAMETER PublishDir
    Directory holding the published constructd executable (dotnet publish, ideally
    self-contained so no runtime install is needed).

.PARAMETER ListenUrl
    What the service listens on. The port from this URL is the one opened in the
    firewall.

.PARAMETER PublicHost
    LAN name clients dial and the self-signed certificate is bound to. Defaults to
    this machine's name.

.PARAMETER DataDir
    Where the database and the ISO cache live.

.PARAMETER SshPortRange
    Per-VM SSH forward range, as "start-end".

.PARAMETER AppPortRange
    Range for host-target forwards ("construct expose --to host"), as "start-end".

.PARAMETER CertThumbprint
    Use an existing certificate from LocalMachine\My instead of creating one.

.PARAMETER AdminUser
    Identity seeded as the first admin. Defaults to the user running this script
    (DOMAIN\name), who then authenticates with Kerberos/Negotiate.

.PARAMETER IsoSourcePath / IsoSourceUrl / IsoSha256
    The Ubuntu ISO the autoinstall image is remastered from: a local copy, or a URL
    the service downloads once into the cache. Admin-configured on purpose -- the
    service never goes looking for "the current LTS" on its own.

.PARAMETER SkipAclHardening
    Do NOT lock -PublishDir, -ScriptsDir and -DataDir down to SYSTEM +
    Administrators. Only for a host where you manage those ACLs yourself: the
    service runs as LocalSystem and executes what it finds in those directories, so
    anything an unprivileged user can write there runs as LocalSystem, and anything
    they can write in the data directory is the authorization database.

.PARAMETER ProvisionWslForService
    WSL distros are registered PER WINDOWS USER, and the service runs as LocalSystem.
    With this switch the installer exports the distro from the current user and
    imports it for LocalSystem when it is missing there. Without it, a missing distro
    is a hard error with the commands to fix it (the export can be several GB, so it
    is not done behind your back).

.PARAMETER RotateAdminToken
    Issue a fresh API token even when the admin already exists. Without it, a re-run
    creates no new credential -- otherwise every reinstall would leave another
    permanent token behind.

.EXAMPLE
    .\Install-ConstructHost.ps1 -ScriptsDir C:\Construct -PublishDir C:\Construct\service\publish `
        -PublicHost buildbox.example.local `
        -IsoSourceUrl https://releases.ubuntu.com/24.04/ubuntu-24.04.3-live-server-amd64.iso
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$ScriptsDir,

    [Parameter(Mandatory = $true)]
    [string]$PublishDir,

    [string]$ListenUrl = "https://0.0.0.0:7462",

    [string]$PublicHost = $env:COMPUTERNAME,

    [string]$DataDir = "C:\ProgramData\Construct\service",

    [ValidatePattern('^\d+-\d+$')]
    [string]$SshPortRange = "2201-2299",

    [ValidatePattern('^\d+-\d+$')]
    [string]$AppPortRange = "2300-2999",

    [string]$CertThumbprint = "",

    [string]$ServiceName = "constructd",

    [string]$SwitchName = "Default Switch",

    # Constrained on purpose: this value is handed to wsl.exe and to a LocalSystem
    # task, and WSL's own distro names are a narrow set anyway.
    [ValidatePattern('^$|^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$')]
    [string]$WslDistro = "Ubuntu",

    [string]$ListenAddress = "0.0.0.0",

    [string]$IsoSourcePath = "",

    [string]$IsoSourceUrl = "",

    [string]$IsoSha256 = "",

    [string]$AdminUser = "",

    [int]$AdminMaxVms = 10,

    [switch]$SkipPrereqs,

    [switch]$SkipAclHardening,

    [switch]$ProvisionWslForService,

    [switch]$RotateAdminToken,

    [switch]$NoStart
)

$ErrorActionPreference = "Stop"

# ── Absolute paths, first thing ──────────────────────────────────────────────
# Relative -ScriptsDir / -PublishDir / -DataDir (".", ".\service\publish") are only
# meaningful against THIS session's working directory: the elevated relaunch starts
# in another directory, and .NET's [System.IO.Path] resolves against the process cwd,
# not $PWD. Field failure: ".\service\publish" became C:\Windows\System32\service\publish,
# whose ancestor ACL then blew up the trust check. So every path parameter is made
# absolute here and only absolute values travel to the elevated copy.
foreach ($pathParam in @('ScriptsDir', 'PublishDir', 'DataDir', 'IsoSourcePath')) {
    $value = Get-Variable -Name $pathParam -ValueOnly
    if ([string]::IsNullOrWhiteSpace($value)) { continue }
    $absolute = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($value)
    Set-Variable -Name $pathParam -Value $absolute
    if ($PSBoundParameters.ContainsKey($pathParam)) { $PSBoundParameters[$pathParam] = $absolute }
}

# ── Output helpers (the repo idiom; the driver and lib reuse these) ───────────
function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
function Write-Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }

# ── Value transport ──────────────────────────────────────────────────────────
# Two places here start a process in a MORE privileged context: the self-elevation
# below, and Invoke-AsLocalSystem later. Neither may build PowerShell source out of
# a value: a parameter carrying a quote, a semicolon or a newline would otherwise
# become another statement running elevated or as LocalSystem. So values NEVER
# appear in a generated script -- they are serialized to a JSON file whose path this
# script chose, and the generated script only reads that file.

function ConvertTo-ConstructPayload {
    <#
        Serialize a hashtable of values for the elevated/LocalSystem side to read
        back. JSON, because it round-trips arbitrary text -- spaces, quotes,
        semicolons, apostrophes, newlines -- with no escaping decisions of ours.

        Switch parameters become plain booleans so splatting them back works
        ($ht['SkipPrereqs'] = $true splats as -SkipPrereqs:$true).
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

function New-ConstructTempPath {
    <#
        A path under %SystemRoot%\Temp, which only SYSTEM and Administrators can
        write. The name is a GUID we generate, so nothing a caller supplied ever
        ends up inside a generated script.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Extension)

    return (Join-Path $env:SystemRoot "Temp\constructd-$([guid]::NewGuid().ToString('n')).$Extension")
}

function New-ConstructRelaunchScript {
    <#
        The script the ELEVATED copy runs: decode the parameters baked INTO it,
        splat them at this script, propagate the exit code.

        The payload travels inside the script text as base64 rather than through a
        file, because that file would have to live somewhere the UNELEVATED caller
        can write -- its own %TEMP% -- and anything there can be replaced between
        the write and the elevated read. Another process running as the same user
        only has to watch for the name and swap the contents while the UAC prompt is
        up to choose the ScriptsDir the service will then execute as LocalSystem, or
        to add -SkipAclHardening. A GUID name does not help: it prevents guessing the
        name in advance, not noticing it appear.

        What crosses the boundary is therefore fixed at launch: -EncodedCommand is
        part of the elevated process's command line, which the caller cannot alter
        once Start-Process has been called. Base64 is inert -- [A-Za-z0-9+/=] only --
        so no parameter value can end the literal it sits in.
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

function New-ConstructLocalSystemScript {
    <#
        The script the LOCALSYSTEM task runs: read {FilePath, ArgumentList} back
        from JSON, invoke it with the argument list SPLATTED -- so each element
        stays one argument no matter what it contains -- and capture everything to
        a file. Again, the only interpolated values are two GUID temp paths.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$PayloadFile,
        [Parameter(Mandatory = $true)][string]$OutputFile
    )

    $payload = $PayloadFile.Replace("'", "''")
    $output  = $OutputFile.Replace("'", "''")

    return @"
`$ErrorActionPreference = 'Continue'
`$spec = Get-Content -Raw -LiteralPath '$payload' | ConvertFrom-Json
& `$spec.FilePath @(`$spec.ArgumentList) *> '$output'
exit `$LASTEXITCODE
"@
}

# ── Self-elevate to Administrator ────────────────────────────────────────────
# Hyper-V features, the LocalMachine certificate store, firewall rules and service
# registration all need it. Every bound parameter is carried INSIDE the encoded
# command so the elevated copy makes the same choices -- no value is concatenated
# into a command line, and nothing crosses the privilege boundary through a file the
# unelevated caller could still rewrite afterwards.
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

# ── Helpers ──────────────────────────────────────────────────────────────────

function Split-PortRange {
    <#
        "2201-2299" -> @{ Start = 2201; End = 2299 }. Validated here rather than
        trusted: these numbers end up in firewall rules and in the service's
        allocator, and a reversed range would silently allocate nothing.
    #>
    param([Parameter(Mandatory = $true)][string]$Range, [Parameter(Mandatory = $true)][string]$Name)

    $parts = $Range.Split("-")
    $start = [int]$parts[0]
    $end   = [int]$parts[1]
    if ($start -lt 1 -or $end -gt 65535 -or $end -lt $start) {
        throw "$Name must be a port range like 2201-2299 (1-65535, start <= end); got '$Range'."
    }
    return @{ Start = $start; End = $end }
}

function Get-ListenPort {
    <# The port the API listens on, taken from -ListenUrl. #>
    param([Parameter(Mandatory = $true)][string]$Url)

    $uri = $null
    if (-not [System.Uri]::TryCreate($Url, [System.UriKind]::Absolute, [ref]$uri)) {
        throw "-ListenUrl must be an absolute URL like https://0.0.0.0:7462; got '$Url'."
    }
    if ($uri.Port -le 0) { throw "-ListenUrl must include a port; got '$Url'." }
    return $uri.Port
}

function Get-ConstructdExe {
    <# The published service executable inside -PublishDir. #>
    param([Parameter(Mandatory = $true)][string]$Dir)

    foreach ($name in @("Constructd.Api.exe", "constructd.exe")) {
        $candidate = Join-Path $Dir $name
        if (Test-Path -LiteralPath $candidate) { return $candidate }
    }
    throw "No published service executable in $Dir. Run: dotnet publish service\src\Constructd.Api -c Release -r win-x64 --self-contained true -o `"$Dir`""
}

function Get-ConstructAclPolicy {
    <#
        Who may touch the paths the service depends on, as plain data so it can be
        asserted without a Windows ACL API.

          Code : -ScriptsDir and -PublishDir. The LocalSystem service EXECUTES what
                 it finds there (the published exe, the PowerShell driver, the ISO
                 build script), so write access is equivalent to running code as
                 LocalSystem. Everyone may read and execute; only SYSTEM and
                 Administrators may write.
          Data : -DataDir. It holds the authorization database (users, token
                 hashes, the VM registry, the audit trail). Nobody but SYSTEM and
                 Administrators has any business reading it either.

        Well-known SIDs rather than names, so this is right on a non-English host.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][ValidateSet('Code', 'Data')][string]$Kind)

    $rules = @(
        @{ Sid = 'S-1-5-18';     Who = 'LocalSystem';    Rights = 'FullControl' }   # the service identity
        @{ Sid = 'S-1-5-32-544'; Who = 'Administrators'; Rights = 'FullControl' }   # who installs and updates
    )

    if ($Kind -eq 'Code') {
        $rules += @{ Sid = 'S-1-5-32-545'; Who = 'Users'; Rights = 'ReadAndExecute' }
    }

    return $rules
}

function Get-ConstructTrustedSid {
    <#
        The SIDs allowed to write anything the service executes or trusts:
        LocalSystem (the service identity), Administrators (who installs it), and
        TrustedInstaller (which owns parts of ProgramData and Program Files on a
        stock Windows and cannot be removed from them).

        SIDs, not names, so this is right on a non-English host.
    #>
    [CmdletBinding()]
    param()
    return @(
        'S-1-5-18',                                                                    # LocalSystem
        'S-1-5-32-544',                                                                # Administrators
        'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464'               # TrustedInstaller
    )
}

# Raw file-access mask bits. Written out because the decision functions below are
# pure -- they run on any PowerShell, including the Linux one the tests use, where
# the [FileSystemRights] enum is not something to rely on.
$script:ConstructRight = @{
    WriteData                    = 0x00000002
    AppendData                   = 0x00000004
    WriteExtendedAttributes      = 0x00000010
    DeleteSubdirectoriesAndFiles = 0x00000040
    WriteAttributes              = 0x00000100
    Delete                       = 0x00010000
    WriteDac                     = 0x00040000
    WriteOwner                   = 0x00080000
}

function Get-ConstructAncestorRiskMask {
    <#
        Which rights on a PARENT let somebody tamper with a hardened child, whatever
        the child's own ACL says: deleting or renaming it (Delete, and
        DeleteSubdirectoriesAndFiles = FILE_DELETE_CHILD), or rewriting its
        permissions (WriteDac, WriteOwner). FullControl and Modify are composites
        that include these bits, so testing the mask catches them too.

        Creating NEW entries in a parent is deliberately not on the list: that is
        exactly what C:\ProgramData grants Users on a stock Windows, and refusing it
        would refuse the recommended location.
    #>
    [CmdletBinding()]
    param()
    return ($script:ConstructRight.Delete -bor
            $script:ConstructRight.DeleteSubdirectoriesAndFiles -bor
            $script:ConstructRight.WriteDac -bor
            $script:ConstructRight.WriteOwner)
}

function Get-ConstructWriteRiskMask {
    <#
        Which rights amount to "can change this object": everything above, plus
        actually writing to it. Used on the hardened tree itself, where an untrusted
        SID must hold none of them.
    #>
    [CmdletBinding()]
    param()
    return ((Get-ConstructAncestorRiskMask) -bor
            $script:ConstructRight.WriteData -bor
            $script:ConstructRight.AppendData -bor
            $script:ConstructRight.WriteExtendedAttributes -bor
            $script:ConstructRight.WriteAttributes)
}

function Get-ConstructUnsafeAce {
    <#
        THE decision, and it is pure: given a list of ACEs as plain data
        (@{ Sid; Rights; Type; InheritOnly }) and a risk mask, return the ones that
        let an untrusted SID do something dangerous. Deny ACEs are not a risk, and
        the trusted SIDs are allowed everything.

        INHERIT-ONLY ACEs are not a risk either, and skipping them is not a
        loosening: an (IO) ace grants nothing on the object that carries it -- it is
        a template stamped onto children as they are created. Stock Windows relies on
        this: C:\ProgramData carries CREATOR OWNER:(OI)(CI)(IO)(F), so judging it
        would refuse the DEFAULT -DataDir on every clean host. What that ACE really
        means is "whoever creates a child owns it", and any child we create here has
        its DACL replaced outright by Set-ConstructPathAcl anyway.

        Kept free of any Windows type so it can be exercised directly by
        service/tests/host-installer.test.ps1 against synthetic ACLs -- an
        attacker-writable parent, a protected child with an explicit untrusted ACE --
        which is not something a test can produce on a machine without ACLs.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Aces,
        [Parameter(Mandatory = $true)][int]$RiskMask
    )

    $trusted = Get-ConstructTrustedSid
    $unsafe = @()

    foreach ($ace in $Aces) {
        if ($ace.Type -ne 'Allow') { continue }
        if ($ace.InheritOnly) { continue }
        if ($trusted -contains $ace.Sid) { continue }
        if (([int]$ace.Rights -band $RiskMask) -eq 0) { continue }
        $unsafe += $ace
    }

    return $unsafe
}

function Resolve-ConstructAceSid {
    <#
        The SID string behind an ACE identity, without ever failing.

        A SecurityIdentifier is taken as is. Anything else (an NTAccount) is translated,
        and when Windows cannot map it -- app-package authorities, orphaned or foreign
        SIDs -- the raw value is used: if it already reads as a SID ("S-1-...") that IS
        the SID; otherwise the name itself stands in. Either way the ACE keeps flowing
        into Get-ConstructUnsafeAce, where an unknown identity is simply not trusted,
        so an unmappable ACE can only ever make the check stricter, never skip it.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowNull()]$Identity)

    if ($null -eq $Identity) { return '' }
    if ($Identity -is [System.Security.Principal.SecurityIdentifier]) { return $Identity.Value }

    $raw = [string]$Identity.Value
    try {
        $translated = $Identity.Translate([System.Security.Principal.SecurityIdentifier])
        if ($translated) { return $translated.Value }
    } catch {
        # fall through: unmappable identity
    }
    return $raw
}

function ConvertTo-ConstructAceList {
    <#
        A real Windows ACL reduced to the plain data Get-ConstructUnsafeAce works on.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Acl)

    # Ask for the rules keyed by SID up front: translating the display names
    # Get-Acl shows (NTAccount) back into SIDs fails with IdentityNotMappedException
    # for "APPLICATION PACKAGE AUTHORITY\..." ACEs -- which every stock C:\Windows and
    # C:\ProgramData carries -- and for orphaned SIDs; localized hosts make it worse.
    $rules = $null
    try {
        $rules = $Acl.GetAccessRules($true, $true, [System.Security.Principal.SecurityIdentifier])
    } catch {
        $rules = $Acl.Access
    }

    $aces = @()
    foreach ($ace in $rules) {
        $aces += @{
            Sid    = Resolve-ConstructAceSid -Identity $ace.IdentityReference
            Rights = [int]$ace.FileSystemRights
            Type   = [string]$ace.AccessControlType
            # (IO): applies to children as they are created, not to this object.
            InheritOnly = (([int]$ace.PropagationFlags -band
                            [int][System.Security.AccessControl.PropagationFlags]::InheritOnly) -ne 0)
        }
    }
    return $aces
}

function Assert-ConstructPathTrustworthy {
    <#
        Everything about a path that must be true before the service is allowed to
        execute or trust what is inside it -- checked over the WHOLE ancestor chain,
        because a hardened directory is only as safe as the directories above it:

          * no reparse point (junction/symlink) anywhere up the chain -- the ACL we
            set would apply to the link while the service reads through it to a
            target somebody else owns;
          * nothing under a per-user profile root;
          * no ancestor on which an untrusted SID can delete, rename or re-permission
            our directory (FILE_DELETE_CHILD on the parent beats any ACL we set on
            the child).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Name)

    $full = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path)

    foreach ($root in @($env:PUBLIC, (Join-Path $env:SystemDrive "Users"))) {
        if ($root -and $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "$Name is inside a user profile root ($root): $Path. Put it somewhere only administrators can write -- ProgramData, Program Files, or a directory off the drive root -- because the service runs as LocalSystem."
        }
    }

    $ancestorMask = Get-ConstructAncestorRiskMask
    $current = $full
    $isSelf = $true

    while ($current) {
        $item = Get-Item -LiteralPath $current -Force -ErrorAction SilentlyContinue
        if ($item) {
            if ($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
                throw "$Name resolves through a reparse point (junction/symlink) at ${current}. The service runs as LocalSystem and must not follow a link somebody else controls."
            }

            # The path itself is about to be re-permissioned, so only its ancestors
            # are judged on their current ACL.
            if (-not $isSelf) {
                $unsafe = @(Get-ConstructUnsafeAce -Aces (ConvertTo-ConstructAceList -Acl (Get-Acl -LiteralPath $current)) -RiskMask $ancestorMask)
                if ($unsafe.Count -gt 0) {
                    $who = ($unsafe | ForEach-Object { $_.Sid }) -join ', '
                    throw "$Name sits under ${current}, where $who can delete, rename or re-permission it -- which defeats any ACL set on $Path. Move it under a directory only administrators can change (ProgramData, Program Files, or a directory off the drive root that this installer also hardens, e.g. -ScriptsDir C:\Construct with -PublishDir inside it), or fix that ACL."
                }
            }
        }

        $isSelf = $false
        $parent = Split-Path -Parent $current
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
}

function Sort-ConstructHardeningOrder {
    <#
        Order paths so that every directory comes before anything inside it (shorter
        normalized path first; ties keep their given order). Pure, so the test can pin
        the rule: -PublishDir inside -ScriptsDir is hardened AFTER -ScriptsDir.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowEmptyCollection()][object[]]$Entries)

    $indexed = @()
    for ($i = 0; $i -lt $Entries.Count; $i++) {
        $normalized = ([string]$Entries[$i].Path).TrimEnd('\', '/')
        $indexed += @{ Entry = $Entries[$i]; Depth = $normalized.Length; Index = $i }
    }
    return @($indexed | Sort-Object -Property @{ Expression = 'Depth' }, @{ Expression = 'Index' } | ForEach-Object { $_.Entry })
}

function Set-ConstructPathAcl {
    <#
        Harden a directory AND everything already inside it.

        Replacing the directory's DACL is not enough on its own: a file or folder an
        attacker pre-created with inheritance disabled keeps its own DACL, and the
        service would go on executing (or trusting) it. So every existing descendant
        has its protection cleared and its explicit ACEs removed, which makes it
        inherit exactly the policy set here -- and then the whole tree is re-read and
        verified.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidateSet('Code', 'Data')][string]$Kind,
        [Parameter(Mandatory = $true)][string]$Name
    )

    Assert-ConstructPathTrustworthy -Path $Path -Name $Name

    if (-not $PSCmdlet.ShouldProcess($Path, "Restrict to SYSTEM + Administrators ($Kind), including everything inside it")) { return }

    $acl = Get-Acl -LiteralPath $Path
    $acl.SetAccessRuleProtection($true, $false)      # explicit DACL, inheritance off

    foreach ($existing in @($acl.Access)) {
        $null = $acl.RemoveAccessRule($existing)
    }

    $inherit = [System.Security.AccessControl.InheritanceFlags]"ContainerInherit, ObjectInherit"
    $propagate = [System.Security.AccessControl.PropagationFlags]::None

    foreach ($rule in (Get-ConstructAclPolicy -Kind $Kind)) {
        $sid = New-Object System.Security.Principal.SecurityIdentifier($rule.Sid)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $sid, $rule.Rights, $inherit, $propagate, "Allow")))
    }

    # Administrators own it, so an administrator can always repair the ACL later.
    $acl.SetOwner((New-Object System.Security.Principal.SecurityIdentifier('S-1-5-32-544')))
    Set-Acl -LiteralPath $Path -AclObject $acl

    # Everything already inside: drop its own DACL so it inherits the one above.
    $descendants = @(Get-ChildItem -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue)
    foreach ($child in $descendants) {
        if ($child.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
            throw "$Name contains a reparse point at $($child.FullName). Remove it -- the service runs as LocalSystem and must not follow a link somebody else controls."
        }

        $childAcl = Get-Acl -LiteralPath $child.FullName
        $changed = $false

        if ($childAcl.AreAccessRulesProtected) {
            $childAcl.SetAccessRuleProtection($false, $false)
            $changed = $true
        }

        foreach ($ace in @($childAcl.Access)) {
            if (-not $ace.IsInherited) {
                $null = $childAcl.RemoveAccessRule($ace)
                $changed = $true
            }
        }

        if ($changed) { Set-Acl -LiteralPath $child.FullName -AclObject $childAcl }
    }

    # Verify the result rather than assume it: the whole tree, re-read.
    $writeMask = Get-ConstructWriteRiskMask
    foreach ($target in (@($Path) + @($descendants | ForEach-Object { $_.FullName }))) {
        $unsafe = @(Get-ConstructUnsafeAce -Aces (ConvertTo-ConstructAceList -Acl (Get-Acl -LiteralPath $target)) -RiskMask $writeMask)
        if ($unsafe.Count -gt 0) {
            $who = ($unsafe | ForEach-Object { $_.Sid }) -join ', '
            throw "$Name still grants write access to $who at ${target} after hardening. Fix the ACL by hand, or re-run with -SkipAclHardening once you have."
        }
    }

    Write-Ok "$Name locked to SYSTEM + Administrators ($($descendants.Count) item(s) inside): $Path"
}

# Task Scheduler status values the runner has to recognize (pure data, so the
# decision helpers below run on any PowerShell, including the Linux one the tests use).
$script:ConstructTaskPath  = '\Construct\'
$script:SchedTaskRunning   = 0x41301   # SCHED_S_TASK_RUNNING   (267009) -- LastTaskResult while running
$script:SchedTaskHasNotRun = 0x41303   # SCHED_S_TASK_HAS_NOT_RUN (267011)
$script:TaskStateRunning   = 4         # TASK_STATE_RUNNING (COM); Get-ScheduledTask says "Running"

function Test-ConstructTaskStillRunning {
    <#
        Whether a one-shot task is still executing, judged from BOTH the state and
        the last result: right after Start-ScheduledTask the state can still read
        Ready while the result already says SCHED_S_TASK_RUNNING, and a result of
        0x41301 is never a real exit code -- it means "not finished yet".
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][AllowNull()]$Status)

    if ($null -eq $Status) { return $true }
    if ($Status.State -eq $script:TaskStateRunning -or "$($Status.State)" -eq 'Running') { return $true }
    if ([int]$Status.LastTaskResult -eq $script:SchedTaskRunning) { return $true }
    return $false
}

function Get-ConstructTaskStatus {
    <#
        @{ State; LastTaskResult } for one task in the Construct task folder, read
        through the Schedule.Service COM object (which touches only that task).
        Falls back to the CIM cmdlets scoped to the same folder.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$TaskName)

    try {
        $svc = New-Object -ComObject Schedule.Service
        $svc.Connect()
        $task = $svc.GetFolder($script:ConstructTaskPath.TrimEnd('\')).GetTask($TaskName)
        return @{ State = [int]$task.State; LastTaskResult = [int]$task.LastTaskResult }
    } catch {
        $info = Get-ScheduledTaskInfo -TaskName $TaskName -TaskPath $script:ConstructTaskPath
        $task = Get-ScheduledTask     -TaskName $TaskName -TaskPath $script:ConstructTaskPath
        return @{ State = [string]$task.State; LastTaskResult = [int]$info.LastTaskResult }
    }
}

function Invoke-AsLocalSystem {
    <#
        Run a program as LocalSystem and return @{ ExitCode; Output; Simulated }.

        Needed because several things the service depends on are PER WINDOWS USER,
        WSL distro registration above all: what the elevated administrator running
        this script can see says nothing about what the service will see. A one-shot
        scheduled task is the way to reach that identity without extra tooling.

        The program and its arguments travel as a serialized ARGUMENT LIST, never as
        script text: the generated script reads them back and splats them, so a
        distro name with a space stays one argument and an apostrophe cannot end a
        literal and start another SYSTEM command.

        Simulated = $true means -WhatIf: nothing ran, and the empty output must not
        be read as an answer.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [string[]]$ArgumentList = @(),
        [int]$TimeoutSeconds = 1800
    )

    if (-not $PSCmdlet.ShouldProcess("LocalSystem", "Run $FilePath")) {
        return @{ ExitCode = 0; Output = ""; Simulated = $true }
    }

    $taskName    = "ConstructdSetup-$([guid]::NewGuid().ToString('n'))"
    $payloadFile = New-ConstructTempPath -Extension "json"
    $outFile     = New-ConstructTempPath -Extension "txt"

    $payload = ConvertTo-ConstructPayload -Values @{ FilePath = $FilePath; ArgumentList = @($ArgumentList) }
    [System.IO.File]::WriteAllText($payloadFile, $payload, (New-Object System.Text.UTF8Encoding($false)))

    $script  = New-ConstructLocalSystemScript -PayloadFile $payloadFile -OutputFile $outFile
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))

    $action    = New-ScheduledTaskAction -Execute "powershell.exe" `
                    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand $encoded"
    $principal = New-ScheduledTaskPrincipal -UserId "S-1-5-18" -LogonType ServiceAccount -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -ExecutionTimeLimit (New-TimeSpan -Seconds $TimeoutSeconds)

    # The task lives in its own folder and is polled through the Task Scheduler COM
    # API, not Get-ScheduledTask. Field failure (2026-09-02): Get-ScheduledTask
    # -TaskName enumerates the whole root folder and dies with 0x80041318 as soon as
    # ANY task there has XML the CIM provider cannot parse (a leftover of some
    # uninstalled product is enough); the loop then fell through and reported the
    # scheduler's "still running" status 0x41301 (267009) as the command's exit code.
    try {
        Register-ScheduledTask -TaskName $taskName -TaskPath $script:ConstructTaskPath `
            -Action $action -Principal $principal -Settings $settings -Force | Out-Null
        Start-ScheduledTask -TaskName $taskName -TaskPath $script:ConstructTaskPath

        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        do {
            Start-Sleep -Milliseconds 500
            $status = Get-ConstructTaskStatus -TaskName $taskName
        } while ((Test-ConstructTaskStillRunning -Status $status) -and (Get-Date) -lt $deadline)

        if (Test-ConstructTaskStillRunning -Status $status) { throw "The LocalSystem command did not finish within $TimeoutSeconds seconds." }
        if ($status.LastTaskResult -eq $script:SchedTaskHasNotRun) { throw "The LocalSystem task never started (Task Scheduler reported SCHED_S_TASK_HAS_NOT_RUN)." }

        $output = ""
        if (Test-Path -LiteralPath $outFile) {
            $output = (Get-Content -Raw -LiteralPath $outFile -ErrorAction SilentlyContinue)
            if ($null -eq $output) { $output = "" }
        }

        return @{ ExitCode = [int]$status.LastTaskResult; Output = $output.Trim(); Simulated = $false }
    } finally {
        Unregister-ScheduledTask -TaskName $taskName -TaskPath $script:ConstructTaskPath -Confirm:$false -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $outFile -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $payloadFile -Force -ErrorAction SilentlyContinue
    }
}

function Set-ConstructFirewallRule {
    <#
        One inbound TCP rule, replaced rather than duplicated: New-NetFirewallRule
        happily creates a second rule with the same name, and a reinstall would
        otherwise pile them up.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$DisplayName,
        [Parameter(Mandatory = $true)][string]$LocalPort
    )

    $existing = Get-NetFirewallRule -DisplayName $DisplayName -ErrorAction SilentlyContinue
    if ($existing) {
        if ($PSCmdlet.ShouldProcess($DisplayName, "Remove the previous firewall rule")) {
            $existing | Remove-NetFirewallRule
        }
    }

    if ($PSCmdlet.ShouldProcess($DisplayName, "Allow inbound TCP $LocalPort")) {
        New-NetFirewallRule -DisplayName $DisplayName -Direction Inbound -Action Allow `
            -Protocol TCP -LocalPort $LocalPort -Profile Any | Out-Null
    }
    Write-Ok "$DisplayName ($LocalPort)"
}

function Resolve-ConstructCertificate {
    <#
        The TLS certificate. An explicit thumbprint is used as-is; otherwise a
        self-signed certificate bound to -PublicHost is created once and reused on
        later runs (a fresh certificate every install would break every client's
        pinned thumbprint).
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$DnsName,
        [string]$Thumbprint = ""
    )

    if ($Thumbprint) {
        $cert = Get-ChildItem -Path Cert:\LocalMachine\My |
                Where-Object { $_.Thumbprint -eq $Thumbprint.Replace(" ", "").ToUpper() }
        if (-not $cert) { throw "No certificate with thumbprint '$Thumbprint' in LocalMachine\My." }
        Write-Ok "Using the certificate you supplied"
        return $cert
    }

    $friendly = "Construct constructd ($DnsName)"
    $existing = Get-ChildItem -Path Cert:\LocalMachine\My |
                Where-Object { $_.FriendlyName -eq $friendly -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
                Sort-Object NotAfter -Descending |
                Select-Object -First 1
    if ($existing) {
        Write-Ok "Reusing the existing certificate (clients keep their pinned thumbprint)"
        return $existing
    }

    if (-not $PSCmdlet.ShouldProcess($DnsName, "Create a self-signed TLS certificate")) { return $null }

    return New-SelfSignedCertificate -DnsName $DnsName `
        -CertStoreLocation Cert:\LocalMachine\My `
        -FriendlyName $friendly `
        -KeyExportPolicy NonExportable `
        -NotAfter (Get-Date).AddYears(5)
}

function Invoke-ConstructdAdmin {
    <#
        Runs the service's own admin CLI. Kept to this one place so the installer
        never touches the database itself.
        Returns @{ ExitCode; Output }. Only stdout is captured -- the CLI puts its
        JSON there and its diagnostics on stderr, so merging the two would hand a
        log line to ConvertFrom-Json.
    #>
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $output = & $Exe @Arguments
    return @{ ExitCode = $LASTEXITCODE; Output = ($output | Out-String).Trim() }
}

# ── 0. Validate inputs ───────────────────────────────────────────────────────

Write-Step "Checking the inputs"

if (-not (Test-Path -LiteralPath $ScriptsDir)) { throw "-ScriptsDir does not exist: $ScriptsDir" }
foreach ($rel in @("drivers\Load-ConstructDriver.ps1", "lib\AgentVm.Common.ps1", "bin\build-autoinstall-iso.sh")) {
    $full = Join-Path $ScriptsDir $rel
    if (-not (Test-Path -LiteralPath $full)) {
        throw "-ScriptsDir does not look like a Construct checkout: $full is missing."
    }
}
Write-Ok "Construct checkout: $ScriptsDir"

$exe = Get-ConstructdExe -Dir $PublishDir
Write-Ok "Service executable: $exe"

$listenPort = Get-ListenPort -Url $ListenUrl
$sshRange   = Split-PortRange -Range $SshPortRange -Name "-SshPortRange"
$appRange   = Split-PortRange -Range $AppPortRange -Name "-AppPortRange"
Write-Ok "API port $listenPort, SSH forwards $($sshRange.Start)-$($sshRange.End), app forwards $($appRange.Start)-$($appRange.End)"

# The service allocates from the two ranges independently, so an overlap hands the
# same public port to two VMs and the second netsh rule silently replaces the first.
# The service itself refuses to start on this; catching it here saves a round trip.
if ($sshRange.Start -le $appRange.End -and $appRange.Start -le $sshRange.End) {
    throw "-SshPortRange ($SshPortRange) and -AppPortRange ($AppPortRange) overlap. They are allocated independently, so an overlap would hand the same public port to two VMs."
}

# One protected root above the data directory: the database, the ISO cache and the
# LocalSystem WSL distro all live under it, so hardening it once covers all three and
# no untrusted parent sits between them.
$serviceRoot = Split-Path -Parent $DataDir
if (-not $serviceRoot) { $serviceRoot = $DataDir }
$wslRoot = Join-Path $serviceRoot "wsl"
Write-Ok "Service root: $serviceRoot"

if (-not $AdminUser) {
    $AdminUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name
}
Write-Ok "First admin: $AdminUser"

$bootstrapKey = Join-Path $ScriptsDir "keys\bootstrap_ed25519.pub"
if (-not (Test-Path -LiteralPath $bootstrapKey)) {
    Write-Warning "Bootstrap public key not found at $bootstrapKey -- generate it before creating a VM:"
    Write-Host "    ssh-keygen -t ed25519 -N '' -C bootstrap@construct -f keys\bootstrap_ed25519" -ForegroundColor Yellow
}

# ── 1. Data directory and the protected service root ─────────────────────────

Write-Step "Preparing the data directory"
$isoCacheDir = Join-Path $DataDir "iso"
foreach ($dir in @($serviceRoot, $DataDir, $isoCacheDir, $wslRoot)) {
    if (-not (Test-Path -LiteralPath $dir)) {
        if ($PSCmdlet.ShouldProcess($dir, "Create the directory")) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }
}
Write-Ok "$DataDir (database, ISO cache), $wslRoot (LocalSystem WSL)"

# ── 1b. Lock down what the service executes and trusts ───────────────────────
# The service runs as LocalSystem: anything an unprivileged user can write into the
# publish or scripts directory runs as LocalSystem, and anything they can write under
# the service root IS the authorization database (or the filesystem WSL runs). This
# happens FIRST -- before the WSL distro is imported into it, and long before the
# service is registered.

Write-Step "Locking down the service's executable, script and data paths"
if ($SkipAclHardening) {
    Write-Warning "-SkipAclHardening: not touching the ACLs. Make sure only SYSTEM and Administrators can write to these, or to any directory above them:"
    Write-Note "  $PublishDir (executed as LocalSystem)"
    Write-Note "  $ScriptsDir (executed as LocalSystem)"
    Write-Note "  $serviceRoot (users, token hashes, audit trail, the LocalSystem WSL distro)"
} else {
    # The service root covers the database, the ISO cache and the WSL distro in one
    # hardened tree, and each call verifies the whole ancestor chain above it.
    # Parents before children. The trust check judges a path by its ANCESTORS'
    # ACLs (an ancestor a user can rename is a way to swap the whole tree under the
    # service's feet), so a directory that contains another one on this list must be
    # hardened first: once C:\Construct is SYSTEM + Administrators, the ancestors of
    # C:\Construct\service\publish are too, and a checkout copied onto a stock C:\
    # (where Authenticated Users inherit Modify) installs without a manual ACL fix.
    $hardening = @(
        @{ Path = $serviceRoot; Kind = 'Data'; Name = "the service root" }
        @{ Path = $PublishDir;  Kind = 'Code'; Name = "-PublishDir" }
        @{ Path = $ScriptsDir;  Kind = 'Code'; Name = "-ScriptsDir" }
    )
    foreach ($entry in (Sort-ConstructHardeningOrder -Entries $hardening)) {
        Set-ConstructPathAcl -Path $entry.Path -Kind $entry.Kind -Name $entry.Name
    }
}

# ── 2. Prerequisites ─────────────────────────────────────────────────────────

if ($SkipPrereqs) {
    Write-Step "Skipping the prerequisite checks (-SkipPrereqs)"
} else {
    Write-Step "Checking Hyper-V"
    # The repo's own check, not a copy of it: same features, same guidance, same
    # reboot handling as a local install.
    . (Join-Path $ScriptsDir "lib\AgentVm.Common.ps1")
    if ($PSCmdlet.ShouldProcess($env:COMPUTERNAME, "Ensure the Hyper-V features are enabled")) {
        Ensure-HyperV
    }
    Write-Ok "Hyper-V is available"

    Write-Step "Checking WSL (the ISO build runs xorriso inside it)"
    if (-not (Get-Command wsl.exe -ErrorAction SilentlyContinue)) {
        throw "WSL is not installed. Install it and a distro, then re-run:`n    wsl --install -d Ubuntu"
    }
    $distros = @(& wsl.exe -l -q 2>$null | ForEach-Object { ($_ -replace "`0", "").Trim() } | Where-Object { $_ })
    if ($distros.Count -eq 0) {
        throw "WSL is installed but has no distro. Install one, then re-run:`n    wsl --install -d Ubuntu"
    }
    if ($WslDistro -and ($distros -notcontains $WslDistro)) {
        throw "WSL distro '$WslDistro' is not installed. Present: $($distros -join ', ')."
    }
    Write-Ok "WSL distro: $(if ($WslDistro) { $WslDistro } else { $distros[0] })"

    # ── WSL as the SERVICE sees it ───────────────────────────────────────────
    # WSL distros are registered per Windows user. Everything above was checked as
    # the elevated administrator; the service runs as LocalSystem, which has its own
    # distro registry. Without this step the install reports success and the first
    # VM creation fails with "there is no distribution with the supplied name".
    Write-Step "Checking WSL under the service identity (LocalSystem)"

    $wanted = $WslDistro
    if (-not $wanted) { $wanted = $distros[0] }

    # Every value below travels as an ARGUMENT LIST element, never as script text:
    # a distro name with a space stays one argument, and an apostrophe cannot end a
    # literal and start another SYSTEM command.
    $distroArgs = @()
    if ($WslDistro) { $distroArgs = @("-d", $WslDistro) }

    # This is a prerequisite check like any other, so it FAILS CLOSED. Reporting
    # success and starting a LocalSystem service whose distro was never verified is
    # exactly the failure this step exists to prevent; -SkipPrereqs is the override.
    $listing = Invoke-AsLocalSystem -FilePath "wsl.exe" -ArgumentList @("-l", "-q") -TimeoutSeconds 120

    if ($listing.Simulated) {
        Write-Note "-WhatIf: not running anything as LocalSystem, so the distro check is not performed."
    } else {
        if ($listing.ExitCode -ne 0) {
            throw "Could not list the WSL distros as LocalSystem (exit $($listing.ExitCode)). That is the identity the service runs as, so its distro cannot be verified; fix it, or re-run with -SkipPrereqs to install anyway."
        }

        $systemDistros = @($listing.Output -split "`n" |
                           ForEach-Object { ($_ -replace "`0", "").Trim() } |
                           Where-Object { $_ })

        if ($systemDistros -notcontains $wanted) {
            if (-not $ProvisionWslForService) {
                throw @"
WSL distro '$wanted' is registered for $([Security.Principal.WindowsIdentity]::GetCurrent().Name) but NOT for LocalSystem,
which is the identity the service runs as. The ISO build would fail on the first VM.

Distros LocalSystem can see: $(if ($systemDistros.Count) { $systemDistros -join ', ' } else { '(none)' })

Fix it either way:
  * re-run this installer with -ProvisionWslForService (exports '$wanted' and imports it for LocalSystem; the export can be several GB), or
  * do it by hand, running the import as SYSTEM:
        wsl --export <distro> <tarball>
        wsl --import <distro> $wslRoot\<distro> <tarball>
"@
            }

            Write-Step "Provisioning WSL distro '$wanted' for LocalSystem"
            $tarball  = New-ConstructTempPath -Extension "tar"
            $importTo = Join-Path $wslRoot $wanted

            if ($PSCmdlet.ShouldProcess($wanted, "Export the distro and import it for LocalSystem")) {
                try {
                    & wsl.exe --export $wanted $tarball
                    if ($LASTEXITCODE -ne 0) { throw "wsl --export failed with exit code $LASTEXITCODE." }

                    $import = Invoke-AsLocalSystem -FilePath "wsl.exe" `
                                -ArgumentList @("--import", $wanted, $importTo, $tarball)
                    if ($import.ExitCode -ne 0) { throw "wsl --import as LocalSystem failed with exit code $($import.ExitCode)." }
                } finally {
                    Remove-Item -LiteralPath $tarball -Force -ErrorAction SilentlyContinue
                }
            }

            # The distro's filesystem is code WSL runs as LocalSystem, so it gets the
            # same treatment as everything else the service executes.
            if (-not $SkipAclHardening) {
                Set-ConstructPathAcl -Path $importTo -Kind Data -Name "the LocalSystem WSL distro"
            }
            Write-Ok "'$wanted' is now registered for LocalSystem"
        } else {
            Write-Ok "LocalSystem can see '$wanted'"
        }

        Write-Step "Ensuring xorriso + whois inside WSL (as LocalSystem)"
        # Inside the distro LocalSystem will actually use -- installing them for the
        # administrator's copy proves nothing about the service's. The bash snippet is
        # a constant; nothing configurable is interpolated into it.
        $ensure = Invoke-AsLocalSystem -FilePath "wsl.exe" -ArgumentList (
            $distroArgs + @(
                "-u", "root", "--", "bash", "-lc",
                "command -v xorriso >/dev/null 2>&1 && command -v mkpasswd >/dev/null 2>&1 || { apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y xorriso whois; }"))

        if ($ensure.ExitCode -ne 0) {
            throw "Could not install xorriso/whois inside the LocalSystem WSL distro (exit $($ensure.ExitCode))."
        }
        Write-Ok "xorriso + whois present in the service's WSL distro"
    }

    Write-Step "Checking the OpenSSH client"
    if (Get-Command ssh.exe -ErrorAction SilentlyContinue) {
        Write-Ok "ssh.exe is available"
    } else {
        $capability = Get-WindowsCapability -Online -Name "OpenSSH.Client*" -ErrorAction SilentlyContinue |
                      Select-Object -First 1
        if ($capability -and $capability.State -ne "Installed") {
            if ($PSCmdlet.ShouldProcess("OpenSSH.Client", "Add the Windows capability")) {
                Add-WindowsCapability -Online -Name $capability.Name | Out-Null
            }
        }

        if (-not (Get-Command ssh.exe -ErrorAction SilentlyContinue) -and -not $WhatIfPreference) {
            throw "The OpenSSH client is required and could not be installed. Install it and re-run:`n    Add-WindowsCapability -Online -Name OpenSSH.Client~~~~0.0.1.0"
        }
        Write-Ok "OpenSSH client installed"
    }
}

# ── 3. TLS certificate ───────────────────────────────────────────────────────

Write-Step "TLS certificate"
$certificate = Resolve-ConstructCertificate -DnsName $PublicHost -Thumbprint $CertThumbprint
$thumbprint = ""
if ($certificate) { $thumbprint = $certificate.Thumbprint }

# ── 4. Firewall ──────────────────────────────────────────────────────────────

Write-Step "Firewall rules"
Set-ConstructFirewallRule -DisplayName "Construct constructd API" -LocalPort "$listenPort"
Set-ConstructFirewallRule -DisplayName "Construct constructd SSH forwards" -LocalPort "$($sshRange.Start)-$($sshRange.End)"
Set-ConstructFirewallRule -DisplayName "Construct constructd app forwards" -LocalPort "$($appRange.Start)-$($appRange.End)"
Write-Note "The forward ranges are what makes a VM reachable from the LAN; narrow the rules' scope if that is too broad for this network."

# ── 5. appsettings.Production.json ───────────────────────────────────────────

Write-Step "Writing appsettings.Production.json"

$settings = [ordered]@{
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default                = "Information"
            "Microsoft.AspNetCore" = "Warning"
        }
    }
    Constructd = [ordered]@{
        Persistence      = "Sqlite"
        DatabasePath     = (Join-Path $DataDir "constructd.db")
        ListenUrl        = $ListenUrl
        CertThumbprint   = $thumbprint
        ScriptsDir       = $ScriptsDir
        PublicHost       = $PublicHost
        SwitchName       = $SwitchName
        ListenAddress    = $ListenAddress
        WslDistro        = $WslDistro
        SshForwardPorts  = [ordered]@{ Start = $sshRange.Start; End = $sshRange.End }
        AppForwardPorts  = [ordered]@{ Start = $appRange.Start; End = $appRange.End }
        Iso              = [ordered]@{
            SeedUser              = "construct"
            BootstrapPublicKeyPath = $bootstrapKey
            CacheDir              = $isoCacheDir
            SourcePath            = $IsoSourcePath
            SourceUrl             = $IsoSourceUrl
            Sha256                = $IsoSha256
        }
    }
}

$settingsPath = Join-Path $PublishDir "appsettings.Production.json"
if ($PSCmdlet.ShouldProcess($settingsPath, "Write the service configuration")) {
    # UTF-8 without a BOM: the configuration reader is fine either way, but a BOM
    # makes the file annoying to diff and to edit by hand later.
    $json = $settings | ConvertTo-Json -Depth 6
    [System.IO.File]::WriteAllText($settingsPath, $json, (New-Object System.Text.UTF8Encoding($false)))
}
Write-Ok $settingsPath

# ── 6. First admin + token (before the service starts) ───────────────────────

Write-Step "Creating the first admin"
$token = ""
if ($PSCmdlet.ShouldProcess($AdminUser, "Create the admin user and issue a token")) {
    $env:DOTNET_ENVIRONMENT = "Production"

    $created = $false
    $add = Invoke-ConstructdAdmin -Exe $exe -Arguments @("admin", "users", "add", $AdminUser, "--role", "Admin", "--max-vms", "$AdminMaxVms")

    if ($add.ExitCode -eq 0) {
        $created = $true
        Write-Ok "Added $AdminUser as an admin"
    } elseif ($add.ExitCode -eq 4) {
        # "Already exists" is not "already an admin": the account may have been added
        # as a plain User, and reporting success would leave the host with no admin
        # at all while the installer claimed otherwise.
        $list = Invoke-ConstructdAdmin -Exe $exe -Arguments @("admin", "users", "list", "--json")
        if ($list.ExitCode -ne 0) {
            throw "Could not read the existing users (exit $($list.ExitCode))."
        }

        $existing = @(ConvertFrom-Json $list.Output) | Where-Object { $_.name -eq $AdminUser } | Select-Object -First 1
        if (-not $existing) {
            throw "$AdminUser already exists but could not be read back. Check the database at $DataDir."
        }
        if ($existing.role -ne "Admin") {
            throw "$AdminUser already exists on this host with role '$($existing.role)', not Admin. Pick a different -AdminUser, or promote that account through the API before re-running."
        }

        Write-Ok "$AdminUser is already an admin on this host"
    } else {
        throw "Could not create the admin user (exit $($add.ExitCode))."
    }

    # A token per reinstall would leave a pile of permanent credentials behind, so a
    # re-run issues nothing unless asked.
    if ($created -or $RotateAdminToken) {
        $issue = Invoke-ConstructdAdmin -Exe $exe -Arguments @("admin", "tokens", "issue", $AdminUser, "--label", "install", "--json")
        if ($issue.ExitCode -ne 0) {
            throw "Could not issue an API token (exit $($issue.ExitCode))."
        }
        $token = (ConvertFrom-Json $issue.Output).token
        Write-Ok "Issued an API token (printed once, below)"
    } else {
        Write-Note "No new token issued (the admin already existed). Pass -RotateAdminToken for a fresh one."
    }
}

# ── 7. Windows service ───────────────────────────────────────────────────────

Write-Step "Registering the Windows service"

$binaryPath = "`"$exe`""
$existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue

if ($existingService) {
    if ($existingService.Status -ne "Stopped") {
        if ($PSCmdlet.ShouldProcess($ServiceName, "Stop the running service")) {
            Stop-Service -Name $ServiceName -Force
            $existingService.WaitForStatus("Stopped", (New-TimeSpan -Seconds 60))
        }
    }
    if ($PSCmdlet.ShouldProcess($ServiceName, "Update the service binary path")) {
        # sc.exe wants "binPath= <value>" with the space after the equals sign.
        & sc.exe config $ServiceName binPath= $binaryPath obj= LocalSystem start= auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "sc.exe config failed with exit code $LASTEXITCODE." }
    }
    Write-Ok "Updated the existing service"
} else {
    if ($PSCmdlet.ShouldProcess($ServiceName, "Create the service (LocalSystem, automatic start)")) {
        New-Service -Name $ServiceName `
            -BinaryPathName $binaryPath `
            -DisplayName "Construct host service (constructd)" `
            -Description "Creates and manages Construct agent VMs on this Hyper-V host." `
            -StartupType Automatic | Out-Null
    }
    Write-Ok "Created the service"
}

# LocalSystem: it has to drive Hyper-V, netsh and WSL, none of which a restricted
# service account can do here without further setup.
Write-Note "Running as LocalSystem"

if ($NoStart) {
    Write-Note "Not starting the service (-NoStart). Start it with: Start-Service $ServiceName"
} elseif ($PSCmdlet.ShouldProcess($ServiceName, "Start the service")) {
    Start-Service -Name $ServiceName
    Write-Ok "Service started"
}

# ── 8. Enrollment details ────────────────────────────────────────────────────

$clientUrl = "https://$PublicHost`:$listenPort"

Write-Host ""
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host "  constructd is installed" -ForegroundColor Cyan
Write-Host "============================================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Service URL   : $clientUrl"
Write-Host "  Certificate   : $thumbprint" -ForegroundColor Yellow
Write-Host "                  ^ clients pin this at enrollment; check it matches there."
if ($token) {
    Write-Host ""
    Write-Host "  Admin token for $AdminUser (shown once):"
    Write-Host "  $token" -ForegroundColor Yellow
}
Write-Host ""
Write-Host "  Add another user:"
Write-Host "    & `"$exe`" admin users add DOMAIN\someone --role User --max-vms 2"
Write-Host "    & `"$exe`" admin tokens issue DOMAIN\someone --label laptop"
Write-Host ""
Write-Host "  Check it answers:"
Write-Host "    curl -H `"Authorization: Bearer <token>`" $clientUrl/api/v1/whoami"
Write-Host ""
Write-Host "  Logs: Get-EventLog -LogName Application -Source $ServiceName -Newest 50"
Write-Host ""
