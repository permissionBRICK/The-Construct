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
         installer runs), YOUR WSL distro with xorriso + whois inside it, and the
         Windows OpenSSH client. No .NET runtime is required: publish the service
         self-contained.
      3. Data directory (database + ISO cache) under ProgramData.
      4. TLS certificate: -CertThumbprint, or a self-signed one bound to
         -PublicHost. The thumbprint is printed prominently -- clients pin it at
         enrollment, so it is the one value that has to leave this machine.
      5. Firewall: inbound TCP for the API port and both forward port ranges.
      6. appsettings.Production.json next to the published executable.
      7. The autoinstall ISO, built AS YOU through your own WSL
         ('constructd admin iso build'). The service runs as LocalSystem, and WSL
         refuses to run as LocalSystem (WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED), so the
         media is built once here and the service only consumes it -- see
         docs/plans/modular-remote-architecture.md section 4.10.
      8. The first admin user plus an API token, created through the service's own
         admin CLI BEFORE the service starts (so nothing contends for the
         database, and so the host is reachable the moment it comes up).
      9. Registers the Windows service as LocalSystem and starts it.
     10. Prints the enrollment details.

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

.PARAMETER SkipIsoBuild
    Do NOT build the autoinstall ISO. The install finishes without install media,
    and VM creation fails until you build it:
        <PublishDir>\Constructd.Api.exe admin iso build

.PARAMETER IsoBuildOnly
    Only (re)build the autoinstall ISO on an existing install and exit. Nothing
    else is touched -- no ACLs, no certificate, no settings, no service
    registration. This is how you pick up a new Ubuntu release or a rotated
    bootstrap key.

.PARAMETER RotateAdminToken
    Issue a fresh API token even when the admin already exists. Without it, a re-run
    creates no new credential -- otherwise every reinstall would leave another
    permanent token behind.

.PARAMETER KeepHostAwake
    Set this host's AC sleep, hibernate and unattended-sleep timeouts to "never" on
    the active power scheme. Without it the installer only PRINTS them and, in an
    interactive run, asks. In an unattended run (no console input, or -WhatIf) an
    absent switch means "leave the power plan alone".

    The service holds a power availability request while any VM is running, which
    already stops the idle timer; this is the belt to that pair of braces, for the
    window before the service starts and for a host that is expected never to sleep.

.PARAMETER SkipPowerSettings
    Do not read or change this host's power plan at all -- no report, no prompt.

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

    # Constrained on purpose: this value is handed to wsl.exe and written into the
    # service's settings, and WSL's own distro names are a narrow set anyway.
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

    [switch]$SkipIsoBuild,

    [switch]$IsoBuildOnly,

    [switch]$RotateAdminToken,

    [switch]$KeepHostAwake,

    [switch]$SkipPowerSettings,

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

# ── May we ask the operator a question? ──────────────────────────────────────
# Decided in ONE place, and it has to be decided BEFORE the self-elevation: the
# elevated copy is started with Start-Process and gets a brand new console, so it
# cannot see that THIS session had its input redirected, was launched
# -NonInteractive, or has no interactive desktop at all. Inferring it after
# elevation would turn every unattended install into a prompt nobody can answer.

function Test-ConstructNonInteractiveArgument {
    <#
        Was this PowerShell started -NonInteractive? It matters more than it looks:
        there Read-Host THROWS rather than returning a default, so a prompt does not
        hang the install, it fails it.

        PowerShell accepts the usual abbreviations (-noni) and both prefix
        characters; -NoProfile, -NoExit and -NoLogo must NOT match. Pure, so every
        spelling is under test.
    #>
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyCollection()][string[]]$Arguments)

    if (-not $Arguments) { return $false }
    foreach ($argument in $Arguments) {
        # -match is case-insensitive, and a file path cannot start with - or /.
        if ($argument -match '^[-/]+noni') { return $true }
    }
    return $false
}

function Test-ConstructPromptAllowed {
    <#
        THE decision, pure, over the four facts that make a run unattended -- any one
        of them is enough:

          * input is redirected: something is driving this script, not somebody;
          * the process was started -NonInteractive;
          * there is no interactive session at all (a scheduled task, a remoting
            session, CI);
          * -WhatIf: a dry run must not change the machine, and must not stop for an
            answer either.
    #>
    [CmdletBinding()]
    param(
        [bool]$InputRedirected,
        [bool]$NonInteractiveHost,
        [bool]$UserInteractive,
        [bool]$WhatIf
    )

    if ($InputRedirected)    { return $false }
    if ($NonInteractiveHost) { return $false }
    if (-not $UserInteractive) { return $false }
    if ($WhatIf)             { return $false }
    return $true
}

function Test-ConstructInteractive {
    <# The facts about THIS session, handed to the pure decision above. #>
    [CmdletBinding()]
    param()

    return (Test-ConstructPromptAllowed `
        -InputRedirected ([Console]::IsInputRedirected) `
        -NonInteractiveHost (Test-ConstructNonInteractiveArgument -Arguments ([Environment]::GetCommandLineArgs())) `
        -UserInteractive ([Environment]::UserInteractive) `
        -WhatIf ([bool]$WhatIfPreference))
}

# ── Value transport ──────────────────────────────────────────────────────────
# One place here starts a process in a MORE privileged context: the self-elevation
# below. It may not build PowerShell source out of a value: a parameter carrying a
# quote, a semicolon or a newline would otherwise become another statement running
# elevated. So values NEVER appear in a generated script as source -- they cross the
# boundary as inert base64 inside the encoded command.

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

# ── Self-elevate to Administrator ────────────────────────────────────────────
# Hyper-V features, the LocalMachine certificate store, firewall rules and service
# registration all need it. Every bound parameter is carried INSIDE the encoded
# command so the elevated copy makes the same choices -- no value is concatenated
# into a command line, and nothing crosses the privilege boundary through a file the
# unelevated caller could still rewrite afterwards.
if (-not ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Relaunching as Administrator..." -ForegroundColor Yellow

    # A copy, because $PSBoundParameters is live, plus the one decision the elevated
    # copy cannot make for itself: whether it may ask about the power plan. Unattended
    # here means "leave the plan alone" there, which is exactly -KeepHostAwake:$false.
    $forward = @{}
    foreach ($bound in $PSBoundParameters.GetEnumerator()) { $forward[$bound.Key] = $bound.Value }
    if (-not $forward.ContainsKey('KeepHostAwake') -and -not (Test-ConstructInteractive)) {
        $forward['KeepHostAwake'] = $false
    }

    $relaunch = New-ConstructRelaunchScript -ScriptPath $PSCommandPath `
                    -PayloadJson (ConvertTo-ConstructPayload -Values $forward)
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

function Format-ConstructCommandOutput {
    <#
        The first lines a failed command printed, ready to append to an error
        message -- because "exit -1" alone says nothing about WHY wsl.exe refused.
        WSL prints UTF-16 with embedded NULs when redirected; those are stripped.
    #>
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyString()][string]$Output, [int]$MaxLines = 8)

    if ([string]::IsNullOrWhiteSpace($Output)) { return " It printed nothing." }
    $lines = @(($Output -replace "`0", "") -split "\r?\n" | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    if ($lines.Count -eq 0) { return " It printed nothing." }
    $shown = @($lines | Select-Object -First $MaxLines)
    $more = if ($lines.Count -gt $shown.Count) { " (+$($lines.Count - $shown.Count) more line(s))" } else { "" }
    return " It said:`n    " + ($shown -join "`n    ") + $more
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

function Get-ConstructPowerSetting {
    <#
        The sleep timeouts this installer reports on, BY GUID.

        powercfg's aliases (SUB_SLEEP, STANDBYIDLE) and every label it prints are
        localized; the GUIDs are not, and the unattended-sleep timeout is hidden and
        has no alias at all. So the GUIDs are what travels.
    #>
    [CmdletBinding()]
    param()

    $sleep = '238c9fa8-0aad-41ed-83f4-97be242c8f20'   # SUB_SLEEP
    return @(
        @{ Key = 'StandbyIdle';     SubGroup = $sleep; Setting = '29f6c1db-86da-48c5-9fdb-f2b67b1f44da'; Label = 'Sleep after (STANDBYIDLE)' }
        @{ Key = 'HibernateIdle';   SubGroup = $sleep; Setting = '9d7815a6-7ee4-497e-8888-515a05f02364'; Label = 'Hibernate after (HIBERNATEIDLE)' }
        @{ Key = 'UnattendedSleep'; SubGroup = $sleep; Setting = '7bc4a2f9-d8fc-4469-b07b-33eb785aaca0'; Label = 'Unattended sleep timeout' }
    )
}

function ConvertFrom-ConstructPowerQuery {
    <#
        The AC and DC values out of "powercfg /q <scheme> <subgroup> <setting>",
        without reading a single WORD of it.

        Every label powercfg prints is localized ("Aktueller Wechselstromwert..."), so
        matching on text is wrong on any host that is not English -- and the failure
        mode is the bad one: reporting "never" on a machine that sleeps. What is NOT
        localized is the SHAPE. The block ends with the current AC value and then the
        current DC value, each a hex number after the last colon on its line. The
        lines above them (minimum, maximum, increment) have that same shape, which is
        exactly why it is the LAST two that matter.

        Returns @{ Ac = <int64>; Dc = <int64> } in seconds, or $null when the output
        holds no such pair (a failed query, or a setting this host does not have).
    #>
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyString()][string]$Output)

    if ([string]::IsNullOrWhiteSpace($Output)) { return $null }

    $values = @()
    foreach ($line in ($Output -split "\r?\n")) {
        $match = [regex]::Match($line, ':\s*0x([0-9a-fA-F]{1,8})\s*$')
        if ($match.Success) { $values += [Convert]::ToInt64($match.Groups[1].Value, 16) }
    }

    if ($values.Count -lt 2) { return $null }
    return @{ Ac = $values[$values.Count - 2]; Dc = $values[$values.Count - 1] }
}

function ConvertFrom-ConstructActiveScheme {
    <#
        The active scheme's GUID out of "powercfg /getactivescheme" -- the GUID, never
        the localized name printed next to it. Only for the report; the commands
        themselves use powercfg's own SCHEME_CURRENT alias, so nothing depends on this
        having worked.
    #>
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyString()][string]$Output)

    if ([string]::IsNullOrWhiteSpace($Output)) { return "" }
    $match = [regex]::Match($Output, '[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')
    if ($match.Success) { return $match.Value }
    return ""
}

function Format-ConstructPowerTimeout {
    <# A timeout in seconds as a person reads it. 0 is powercfg's "never". #>
    [CmdletBinding()]
    param([AllowNull()]$Seconds)

    if ($null -eq $Seconds) { return "unavailable" }
    $value = [int64]$Seconds
    if ($value -le 0) { return "never" }
    if (($value % 60) -eq 0) { return "$($value / 60) min" }
    return "$value s"
}

function Invoke-ConstructPowercfg {
    <#
        powercfg.exe with an argument LIST, as @{ ExitCode; Output }. The ONE place
        that runs it, which is what lets everything above be exercised against canned
        output from a non-English host.

        A host without powercfg (or one that refuses the query) is reported, not
        fatal: the power plan is a comfort setting, and failing an install over it
        would be worse than the nap it prevents.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    try {
        $output = & powercfg.exe @Arguments 2>&1 | Out-String
        return @{ ExitCode = $LASTEXITCODE; Output = $output }
    } catch {
        return @{ ExitCode = -1; Output = $_.Exception.Message }
    }
}

function Get-ConstructPowerReport {
    <#
        What this host's sleep timeouts are right now: one row per setting, AC and DC
        in seconds ($null when the query failed).

        -Query is the seam. The installer passes its own powercfg runner; the tests
        pass canned output -- including a German host's -- so the parsing is under test
        on a machine that has no powercfg at all.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$SchemeGuid,
        [Parameter(Mandatory = $true)][scriptblock]$Query
    )

    $rows = @()
    foreach ($setting in (Get-ConstructPowerSetting)) {
        $result = & $Query -Arguments @("/q", $SchemeGuid, $setting.SubGroup, $setting.Setting)

        $values = $null
        if ($result -and $result.ExitCode -eq 0) {
            $values = ConvertFrom-ConstructPowerQuery -Output $result.Output
        }

        $ac = $null
        $dc = $null
        if ($values) { $ac = $values.Ac; $dc = $values.Dc }

        $rows += @{
            Key      = $setting.Key
            Label    = $setting.Label
            SubGroup = $setting.SubGroup
            Setting  = $setting.Setting
            Ac       = $ac
            Dc       = $dc
        }
    }
    return $rows
}

function Set-ConstructPowerNever {
    <#
        Set one AC timeout to 0 ("never") on the active scheme. Idempotent: a value
        that is already 0 is left alone and reported as unchanged, so a re-run of the
        installer writes nothing. Returns $true only when it actually changed
        something, so the caller knows whether the scheme has to be re-applied.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$SchemeGuid,
        [Parameter(Mandatory = $true)][hashtable]$Row,
        [Parameter(Mandatory = $true)][scriptblock]$Query
    )

    if ($null -ne $Row.Ac -and [int64]$Row.Ac -eq 0) {
        Write-Note "$($Row.Label): already never (unchanged)"
        return $false
    }

    if (-not $PSCmdlet.ShouldProcess($Row.Label, "Set the AC timeout to never on the active power scheme")) { return $false }

    $result = & $Query -Arguments @("/setacvalueindex", $SchemeGuid, $Row.SubGroup, $Row.Setting, "0")
    if (-not $result -or $result.ExitCode -ne 0) {
        $said = ""
        if ($result) { $said = Format-ConstructCommandOutput -Output $result.Output }
        Write-Warning "powercfg could not set $($Row.Label) to never; set it by hand in the power options.$said"
        return $false
    }

    Write-Ok "$($Row.Label): set to never (AC)"
    return $true
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

function Invoke-ConstructIsoBuild {
    <#
        Build the autoinstall ISO through the service's own admin CLI, and report the
        path it published.

        It runs AS THE ADMINISTRATOR RUNNING THIS INSTALLER, not as the service:
        wsl.exe refuses to run as LocalSystem (Wsl/WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED,
        field-verified 2026-09-02) and LocalSystem is the service's identity. The
        service only consumes what is published here (plan section 4.10).

        Idempotent without -Force: the CLI reports media that is already there rather
        than spending twenty minutes rebuilding it, so re-running the installer is
        cheap. Fails closed, showing what the build printed -- an exit code alone says
        nothing about why xorriso or a download gave up.
    #>
    [CmdletBinding(SupportsShouldProcess = $true)]
    param(
        [Parameter(Mandatory = $true)][string]$Exe,
        [switch]$Force
    )

    if (-not $PSCmdlet.ShouldProcess("the autoinstall ISO", "Build it through WSL and publish it")) { return }

    # An argument LIST, never a command string: nothing here is quoted by hand.
    $arguments = @("admin", "iso", "build")
    if ($Force) { $arguments = $arguments + @("--force") }

    # Tee, so a build that takes twenty minutes is visible while it runs AND readable
    # afterwards. 2>&1 keeps the CLI's diagnostics (stderr) in the capture too.
    $isoOutput = $null
    & $Exe @arguments 2>&1 | Tee-Object -Variable isoOutput
    $isoExit = $LASTEXITCODE
    $text = ($isoOutput | Out-String)

    if ($isoExit -ne 0) {
        $said = Format-ConstructCommandOutput -Output $text -MaxLines 20
        throw "Building the autoinstall ISO failed (exit $isoExit). Fix what it reports, then re-run this installer with -IsoBuildOnly.$said"
    }

    # The command's last line is 'ISO: <path>' precisely so this can report it.
    $isoLine = @($text -split "`r?`n" | Where-Object { $_ -like "ISO: *" } | Select-Object -Last 1)
    if ($isoLine.Count -gt 0) { Write-Ok $isoLine[0].Trim() } else { Write-Ok "Autoinstall ISO ready" }
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

# Every invocation of the service executable below -- the ISO build and the admin CLI --
# must read the SAME configuration the service will: appsettings.Production.json, the file
# this installer writes. Set once, here, because the ISO build runs before the admin steps
# and would otherwise build into the default cache directory instead of -DataDir's.
$env:DOTNET_ENVIRONMENT = "Production"

if ($IsoBuildOnly -and $SkipIsoBuild) {
    throw "-IsoBuildOnly and -SkipIsoBuild contradict each other: one runs nothing but the ISO build, the other runs everything except it."
}

# ── 0b. -IsoBuildOnly: rebuild the media and change nothing else ─────────────
# For an existing install picking up a new Ubuntu release or a rotated bootstrap key.
# No ACLs, no certificate, no settings, no service registration -- so it cannot
# disturb a host that is serving VMs right now. The running install keeps the ISO it
# has attached; the pointer swap is what makes the new one current.
if ($IsoBuildOnly) {
    Write-Step "Rebuilding the autoinstall ISO only (-IsoBuildOnly)"

    # It builds from the SERVICE's configuration (cache directory, source ISO, seed user),
    # so there has to be one. Without this the CLI would quietly fall back to defaults and
    # publish media into a directory the service never reads.
    $productionSettings = Join-Path $PublishDir "appsettings.Production.json"
    if (-not (Test-Path -LiteralPath $productionSettings)) {
        throw "-IsoBuildOnly needs an existing install: $productionSettings is missing. Run the installer without it first."
    }

    Invoke-ConstructIsoBuild -Exe $exe -Force

    Write-Host ""
    Write-Host "The autoinstall ISO was rebuilt; nothing else on this host was changed." -ForegroundColor Cyan
    Write-Host "  What is published now:  & `"$exe`" admin iso status"
    Write-Host "  Remove superseded ISOs: & `"$exe`" admin iso prune"
    Write-Host ""
    exit 0
}

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

# One protected root above the data directory: the database and the ISO catalog both
# live under it, so hardening it once covers both and no untrusted parent sits between
# them.
$serviceRoot = Split-Path -Parent $DataDir
if (-not $serviceRoot) { $serviceRoot = $DataDir }
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
foreach ($dir in @($serviceRoot, $DataDir, $isoCacheDir)) {
    if (-not (Test-Path -LiteralPath $dir)) {
        if ($PSCmdlet.ShouldProcess($dir, "Create the directory")) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }
    }
}
Write-Ok "$DataDir (database), $isoCacheDir (autoinstall ISO catalog)"

# ── 1b. Lock down what the service executes and trusts ───────────────────────
# The service runs as LocalSystem: anything an unprivileged user can write into the
# publish or scripts directory runs as LocalSystem, and anything they can write under
# the service root IS the authorization database -- or the ISO every new VM installs
# itself from. This happens FIRST, before anything is put in those directories and long
# before the service is registered.

Write-Step "Locking down the service's executable, script and data paths"
if ($SkipAclHardening) {
    Write-Warning "-SkipAclHardening: not touching the ACLs. Make sure only SYSTEM and Administrators can write to these, or to any directory above them:"
    Write-Note "  $PublishDir (executed as LocalSystem)"
    Write-Note "  $ScriptsDir (executed as LocalSystem)"
    Write-Note "  $serviceRoot (users, token hashes, audit trail, the autoinstall ISO catalog)"
} else {
    # The service root covers the database and the ISO catalog in one hardened tree, and
    # each call verifies the whole ancestor chain above it.
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

    Write-Step "Checking WSL (the ISO build runs xorriso inside it, as YOU)"
    # YOUR WSL, not the service's. wsl.exe refuses to run as LocalSystem
    # (Wsl/WSL_E_LOCAL_SYSTEM_NOT_SUPPORTED, field-verified 2026-09-02), which is the
    # identity the service runs as -- so the media is built here, once, by the
    # administrator running this installer, and the service only consumes it
    # (plan section 4.10).
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

    Write-Step "Ensuring xorriso + whois inside your WSL distro"
    # Every value travels as an ARGUMENT LIST element, never as script text: a distro
    # name with a space stays one argument, and an apostrophe cannot end a literal and
    # start another command. The bash snippet itself is a constant.
    $distroArgs = @()
    if ($WslDistro) { $distroArgs = @("-d", $WslDistro) }

    if ($PSCmdlet.ShouldProcess("WSL", "Install xorriso and whois inside the distro")) {
        $ensureArgs = $distroArgs + @(
            "-u", "root", "--", "bash", "-lc",
            "command -v xorriso >/dev/null 2>&1 && command -v mkpasswd >/dev/null 2>&1 || { apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y xorriso whois; }")

        $ensureOutput = (& wsl.exe @ensureArgs 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 0) {
            $said = Format-ConstructCommandOutput -Output $ensureOutput
            throw "Could not install xorriso/whois inside WSL (exit $LASTEXITCODE). The ISO build needs both.$said"
        }
    }
    Write-Ok "xorriso + whois present in your WSL distro"

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

# ── 4b. The host's own sleep settings ────────────────────────────────────────
# Field failure (2026-09-04): the host dropped into S3 overnight ("System Idle") with
# VMs that were expected to keep serving. The service now holds a power availability
# request while any VM of its is running, which stops the idle timer -- but only while
# the service is up, and only for the sleep the timer causes. So this step REPORTS what
# the machine is set to (so the number is in the install log either way) and, when
# asked, switches the AC timers off.
#
# Nothing here is read by matching localized text: the settings travel as GUIDs and the
# values come out of the hex indices, which is the same on every host language.

Write-Step "The host's sleep settings"
if ($SkipPowerSettings) {
    Write-Note "-SkipPowerSettings: this host's power plan was neither read nor changed."
} else {
    # SCHEME_CURRENT is powercfg's own alias for the active scheme; the GUID is read
    # separately for the report only, so a failure there changes nothing.
    $activeScheme = "SCHEME_CURRENT"
    $schemeResult = Invoke-ConstructPowercfg -Arguments @("/getactivescheme")
    $schemeGuid = ConvertFrom-ConstructActiveScheme -Output $schemeResult.Output
    if ($schemeGuid) { Write-Note "Active power scheme: $schemeGuid" }

    $powerRows = @(Get-ConstructPowerReport -SchemeGuid $activeScheme -Query ${function:Invoke-ConstructPowercfg})
    foreach ($row in $powerRows) {
        Write-Note ("{0,-26} AC {1,-10}  DC {2}" -f $row.Label,
            (Format-ConstructPowerTimeout -Seconds $row.Ac), (Format-ConstructPowerTimeout -Seconds $row.Dc))
    }

    # -KeepHostAwake decides when it is given -- including when the UNELEVATED copy
    # resolved it to $false before relaunching, which is how an unattended run reaches
    # here without a console of its own. Otherwise: ask, or leave the machine alone.
    $setPowerNever = $false
    if ($PSBoundParameters.ContainsKey("KeepHostAwake")) {
        $setPowerNever = [bool]$KeepHostAwake
    } elseif (-not (Test-ConstructInteractive)) {
        Write-Note "Unattended run: leaving the power plan alone. Pass -KeepHostAwake to set the AC timers to never."
    } else {
        $answer = Read-Host "    Set the AC sleep/hibernate timers to never on this host? [Y/n]"
        $setPowerNever = ($answer -notmatch '^\s*[nN]')
    }

    if ($setPowerNever) {
        $powerChanged = $false
        foreach ($row in $powerRows) {
            if (Set-ConstructPowerNever -SchemeGuid $activeScheme -Row $row -Query ${function:Invoke-ConstructPowercfg}) {
                $powerChanged = $true
            }
        }

        # /setacvalueindex writes the scheme; /setactive is what makes the running
        # configuration pick it up.
        if ($powerChanged -and $PSCmdlet.ShouldProcess("the active power scheme", "Apply the changed timeouts")) {
            $applied = Invoke-ConstructPowercfg -Arguments @("/setactive", $activeScheme)
            if ($applied.ExitCode -ne 0) {
                Write-Warning "powercfg could not re-apply the active scheme; the new timeouts take effect at the next sign-in."
            }
        }
    } else {
        Write-Note "Power plan left as it is. The service still holds a power request while VMs run (powercfg /requests)."
    }
}

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
            # Prebuilt: the service consumes the media built below, as you. It cannot
            # build media itself -- WSL refuses to run as LocalSystem. 'PerVm' is the
            # other implemented strategy (see service/README.md, ISO build strategies).
            Mode                  = "Prebuilt"
            HostnameSource        = "hyperv-kvp"
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

# ── 6. The autoinstall ISO (built as YOU, through your own WSL) ──────────────
# The service is LocalSystem and WSL will not run there, so the media is built here,
# by the administrator running this installer, and published into the catalog the
# service reads (plan section 4.10). It is idempotent: without -Force the command
# reports the media that is already there instead of spending twenty minutes
# rebuilding it.

Write-Step "Building the autoinstall ISO (as you, via WSL)"
if ($SkipIsoBuild) {
    Write-Warning "-SkipIsoBuild: no install media was built, so creating a VM will fail until you run:"
    Write-Host "    & `"$exe`" admin iso build" -ForegroundColor Yellow
} else {
    Invoke-ConstructIsoBuild -Exe $exe
}

# ── 7. First admin + token (before the service starts) ───────────────────────

Write-Step "Creating the first admin"
$token = ""
if ($PSCmdlet.ShouldProcess($AdminUser, "Create the admin user and issue a token")) {
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

# ── 8. Windows service ───────────────────────────────────────────────────────

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

# LocalSystem: it has to drive Hyper-V and netsh, neither of which a restricted service
# account can do here without further setup. It does NOT run WSL -- wsl.exe refuses to run
# as LocalSystem, which is why the ISO was built above, as you.
Write-Note "Running as LocalSystem"

if ($NoStart) {
    Write-Note "Not starting the service (-NoStart). Start it with: Start-Service $ServiceName"
} elseif ($PSCmdlet.ShouldProcess($ServiceName, "Start the service")) {
    Start-Service -Name $ServiceName
    Write-Ok "Service started"
}

# ── 9. Enrollment details ────────────────────────────────────────────────────

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
if ($SkipIsoBuild) {
    Write-Host "  NO INSTALL MEDIA: creating a VM will fail until you build it." -ForegroundColor Yellow
    Write-Host "    & `"$exe`" admin iso build"
} else {
    Write-Host "  Autoinstall ISO:"
    Write-Host "    & `"$exe`" admin iso status            # what is published, and from what"
}
Write-Host "    & `"$exe`" admin iso build --force      # new Ubuntu release, or a rotated bootstrap key"
Write-Host "                                             # (or re-run this installer with -IsoBuildOnly)"
Write-Host "    & `"$exe`" admin iso prune              # delete superseded ISOs nothing has attached"
Write-Host ""
Write-Host "  Add another user:"
Write-Host "    & `"$exe`" admin users add DOMAIN\someone --role User --max-vms 2"
Write-Host "    & `"$exe`" admin tokens issue DOMAIN\someone --label laptop"
Write-Host ""
Write-Host "  Check it answers:"
Write-Host "    curl -H `"Authorization: Bearer <token>`" $clientUrl/api/v1/whoami"
Write-Host ""
Write-Host "  Host stays awake while VMs run (Constructd:Power:KeepHostAwake):"
Write-Host "    powercfg /requests                        # the SYSTEM request the service holds"
Write-Host "    powercfg /q SCHEME_CURRENT SUB_SLEEP      # this host's sleep timeouts"
Write-Host ""
Write-Host "  Logs: Get-EventLog -LogName Application -Source $ServiceName -Newest 50"
Write-Host ""
