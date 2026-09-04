#Requires -Version 5.1
<#
    The-Construct -- PER-INSTANCE STATE STORE (PowerShell reader/writer).

    The PS counterpart of extension/src/instancestate.js. Both read and write the SAME
    files and MUST agree on the split below; this header is the shared contract.

    TWO STORES, ONE SPLIT
      * INSTALL-WIDE facts stay where they have always been: the scripts checkout's
        `.construct-settings.json` (Read-/Save-ConstructSettings in AgentVm.Common.ps1).
        These describe the INSTALLED CONSTRUCT, not a VM: `installedCommit`,
        `constructRepo`, `constructRef`, and the host git identity the installer applies
        to every VM it provisions (`gitUserName`, `gitEmail`, `gitCredentialStore`).
      * Everything else is VM-SCOPED -- `provisionedCommit`, the project selection, the
        mic preference, the VM's RAM/disk/release, the VS Code / SMB / patch toggles, the
        T3 Code toggles and channel, the automatic-checkpoint preference and its applied
        marker -- and belongs to ONE instance.

    WHERE THE VM-SCOPED HALF LIVES
      * The DEFAULT instance (`agent-vm`) keeps reading and writing its VM-scoped keys at
        the LEGACY TOP LEVEL of `.construct-settings.json`, and nothing else: an install
        with one local VM and no registry writes exactly the files it wrote before this
        module existed, and NO `instances\agent-vm.json` is ever created for it.
      * Every OTHER instance uses only its own file:
        `%LOCALAPPDATA%\The-Construct\instances\<name>.json`, next to `instances.json` and
        independent of the scripts checkout (a self-update's Expand-Archive never touches
        it, and two checkouts cannot disagree about one VM).

    Schema of a per-instance file (version 1):

        { "version": 1, "instance": "work-vm", "<vm-scoped key>": <value>, ... }

    `version` and `instance` are METADATA: they are written, and skipped when the file is
    read back as settings. Install-wide keys are refused on the way in and ignored on the
    way out, so a hand-edited per-instance file can never shadow the installed commit.

    Missing file, unreadable file, malformed JSON -> "nothing saved" ($null), exactly the
    tolerance Read-ConstructSettings has always had. Writes of a per-instance file are
    ATOMIC (temp file + move), like the registry's.

    Dot-source it:

        . "$PSScriptRoot\lib\AgentVm.InstanceState.ps1"

    DELIBERATELY NOT Set-StrictMode (same reason as lib\AgentVm.Remote.ps1): this file is
    dot-sourced straight into installer scopes that were not written for strict mode.
    Reading needs nothing else; WRITING needs Save-ConstructSettings from
    lib\AgentVm.Common.ps1 to be loaded too, because that function -- not a second copy of
    it here -- is what keeps the legacy file byte-identical to what it has always been.

    Functions
      Get-ConstructInstanceStateDir                 -> %LOCALAPPDATA%\The-Construct\instances
      Get-ConstructInstanceStatePath -Name          -> that instance's file ($null for the default)
      Test-ConstructDefaultInstanceStore -Name      -> [bool] "this name uses the legacy store"
      Test-ConstructInstanceStateName -Name         -> [bool] THE ONE name rule, read from
                                                       lib\AgentVm.Instances.ps1
      Test-ConstructInstallWideKey -Key             -> [bool]
      Test-ConstructInstanceStateMetaKey -Key       -> [bool]
      Read-ConstructInstanceState -Name [-Dir]      -> [pscustomobject] or $null
      Save-ConstructInstanceState -Name [-Dir] -Values -> merges and writes
#>

# The instance whose VM-scoped keys live at the legacy top level. Mirrors
# $script:ConstructDefaultInstance in lib\AgentVm.Instances.ps1 and DEFAULT_INSTANCE_NAME
# in extension/src/instances.js.
$script:ConstructStateDefaultInstance = 'agent-vm'
$script:ConstructStateSchemaVersion   = 1

# Keys that describe the INSTALLED CONSTRUCT (or the host identity it applies), not a VM.
# They stay in .construct-settings.json for every instance. Mirrored verbatim by
# INSTALL_WIDE_KEYS in extension/src/instancestate.js.
$script:ConstructInstallWideKeys = @(
    'installedCommit',
    'constructRepo',
    'constructRef',
    'gitUserName',
    'gitEmail',
    'gitCredentialStore'
)

# Written into every per-instance file and skipped when it is read back as settings.
$script:ConstructInstanceStateMetaKeys = @('version', 'instance')

# THE ONE INSTANCE-NAME RULE, READ FROM ITS SINGLE DEFINITION -- never restated here.
# The name becomes a FILE NAME in instances\, so it has to be validated; but a second
# regex would be a second rule, and the two would drift. lib\AgentVm.Instances.ps1 is
# loaded in a CHILD SCOPE (it enables Set-StrictMode -Version Latest in whatever scope it
# is dot-sourced into, and the installers that dot-source THIS file were not written for
# that) and only the rule's two values come back. Loaded LAZILY, on the first non-default
# name: a single-VM install never asks.
#
# FAIL CLOSED. Without that library there is no rule to apply, so no per-instance path
# resolves -- which leaves the default instance on its legacy file, i.e. today's
# behaviour, instead of guessing at a file name from an unvalidated string.
$script:ConstructStateNameRule = $null      # @{ Re = <regex>; Reserved = <prefix> } once loaded
$script:ConstructStateNameRuleTried = $false

function Get-ConstructStateNameRule {
    if ($script:ConstructStateNameRuleTried) { return $script:ConstructStateNameRule }
    $script:ConstructStateNameRuleTried = $true
    try {
        $lib = Join-Path $PSScriptRoot "AgentVm.Instances.ps1"
        if (Test-Path -LiteralPath $lib) {
            $script:ConstructStateNameRule = & {
                param($libPath)
                . $libPath
                @{ Re = $script:ConstructInstanceNameRe; Reserved = $script:ConstructReservedNamePrefix }
            } $lib
        }
    } catch { $script:ConstructStateNameRule = $null }
    return $script:ConstructStateNameRule
}

function Get-ConstructInstanceStateDir {
    <#
        %LOCALAPPDATA%\The-Construct\instances -- beside instances.json, which
        Get-ConstructInstancesPath anchors in the same container. Pure path math.
    #>
    $base = $env:LOCALAPPDATA
    if (-not $base) { $base = $env:TEMP }
    if (-not $base) { $base = [System.IO.Path]::GetTempPath() }
    return (Join-Path (Join-Path $base "The-Construct") "instances")
}

function Test-ConstructInstanceStateName {
    <# Is this a usable instance name for a state file? THE ONE NAME RULE
       (Test-ConstructInstanceName in lib\AgentVm.Instances.ps1), applied through
       Get-ConstructStateNameRule so this file holds no copy of it. A lowercase DNS label
       cannot contain a separator or a dot, so passing it is also what makes the name safe
       as a file name. Fails closed when the rule is unavailable. Pure. #>
    param([string]$Name)
    if (-not $Name) { return $false }
    $rule = Get-ConstructStateNameRule
    if (-not $rule) { return $false }
    if ($Name.ToLowerInvariant().StartsWith($rule.Reserved)) { return $false }
    return [bool]([regex]::IsMatch($Name, $rule.Re))
}

function Test-ConstructDefaultInstanceStore {
    <# Does this name read and write the LEGACY top-level keys? True for the default
       instance and for an empty/absent name (a caller that never learned one is, by
       definition, on today's single-VM path).

       CASE-SENSITIVE (-ceq), like instances.isDefaultInstance and the registry itself:
       instance names are lowercase DNS labels, so "Agent-VM" is not the default instance
       -- it is not a valid instance name at all, and silently treating it as the default
       would have this module and the registry disagree about which VM a caller meant.
       Callers that hold an ssh ALIAS lowercase it first (alias = name, lowercased), the
       one derivation rule. Pure. #>
    param([string]$Name)
    $n = "$Name".Trim()
    if (-not $n) { return $true }
    return ($n -ceq $script:ConstructStateDefaultInstance)
}

function Get-ConstructInstanceStatePath {
    <# That instance's state file, or $null when it has none: the default instance (its
       state is the legacy file) and any name that fails the path-safety guard. Pure. #>
    param([string]$Name)
    if (Test-ConstructDefaultInstanceStore $Name) { return $null }
    $n = "$Name".Trim()
    if (-not (Test-ConstructInstanceStateName $n)) { return $null }
    return (Join-Path (Get-ConstructInstanceStateDir) ($n + ".json"))
}

function Test-ConstructInstallWideKey {
    <# Is this key install-wide (stays in .construct-settings.json for every instance)?
       CASE-SENSITIVE (-ceq): JSON object keys are, and the JS twin classifies through a
       Set. PowerShell's own -eq/-contains are case-INSENSITIVE, so an unqualified
       comparison would have "InstalledCommit" split off here and kept as a VM-scoped key
       there -- the two readers must classify every key identically. Pure. #>
    param([string]$Key)
    if (-not $Key) { return $false }
    foreach ($k in $script:ConstructInstallWideKeys) {
        if ($Key -ceq $k) { return $true }
    }
    return $false
}

function Test-ConstructInstanceStateMetaKey {
    <# Is this one of the file's own metadata keys (written, never returned as a setting)?
       Case-sensitive for the same reason as Test-ConstructInstallWideKey. Pure. #>
    param([string]$Key)
    if (-not $Key) { return $false }
    foreach ($k in $script:ConstructInstanceStateMetaKeys) {
        if ($Key -ceq $k) { return $true }
    }
    return $false
}

function Read-ConstructInstanceStateFile {
    <#
        The raw parsed per-instance document, or $null.

        A JSON root that is NOT an object -- `[1,2]`, `"x"`, `42`, `null` -- is a malformed
        file, i.e. "nothing saved", exactly what readJsonObject() in the JS twin returns {}
        for. Without this check ConvertFrom-Json hands back an array (or a scalar) and the
        readers below enumerate ITS PSObject properties, so `[1,2]` would surface
        PowerShell's own array metadata (Length, Count, ...) as if they were settings --
        while the JS reader saw nothing at all. Internal.
    #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    $doc = $null
    try { $doc = (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) } catch { return $null }
    if ($null -eq $doc) { return $null }
    if ($doc -is [System.Array]) { return $null }
    if (-not ($doc -is [System.Management.Automation.PSCustomObject])) { return $null }
    return $doc
}

function Read-ConstructInstanceState {
    <#
        The VM-scoped settings of ONE instance, as a PSCustomObject, or $null when nothing
        is saved for it -- the same shape and the same tolerance Read-ConstructSettings has.

        The DEFAULT instance answers from -Dir's .construct-settings.json, unchanged and
        unfiltered: those keys ARE its state, and an install with one VM must keep seeing
        exactly the object it has always seen. Any other instance answers from its own
        file, with the metadata and install-wide keys stripped.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Dir
    )
    if (Test-ConstructDefaultInstanceStore $Name) {
        if (-not $Dir) { return $null }
        if (Get-Command Read-ConstructSettings -ErrorAction SilentlyContinue) {
            return (Read-ConstructSettings -Dir $Dir)
        }
        $legacy = Join-Path $Dir ".construct-settings.json"
        return (Read-ConstructInstanceStateFile -Path $legacy)
    }
    $path = Get-ConstructInstanceStatePath -Name $Name
    if (-not $path) { return $null }
    $doc = Read-ConstructInstanceStateFile -Path $path
    if (-not $doc) { return $null }
    # ORDINAL-keyed: [ordered]@{} compares keys case-INSENSITIVELY, so a file carrying
    # both "smbShare" and "SmbShare" would collapse them into one setting here and keep
    # two in the JS twin (a JS object's keys are case-sensitive). The two readers must
    # produce the same settings from the same bytes.
    $out = New-Object System.Collections.Specialized.OrderedDictionary([System.StringComparer]::Ordinal)
    foreach ($p in $doc.PSObject.Properties) {
        if (Test-ConstructInstanceStateMetaKey $p.Name) { continue }
        if (Test-ConstructInstallWideKey $p.Name) { continue }
        $out[$p.Name] = $p.Value
    }
    if ($out.Count -eq 0) { return $null }
    return [pscustomobject]$out
}

function Save-ConstructInstanceStateFile {
    <#
        Write one per-instance file ATOMICALLY (temp file + move, atomic on NTFS), so a
        crash or a concurrent reader never sees a half-written document -- which would
        read as "nothing saved" and silently revert a VM's whole configuration.
        BOM-less UTF-8 with a trailing newline, matching Save-ConstructInstances. Internal.
    #>
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)]$Values
    )
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    $doc = New-Object System.Collections.Specialized.OrderedDictionary([System.StringComparer]::Ordinal)
    $doc['version']  = $script:ConstructStateSchemaVersion
    $doc['instance'] = $Name
    # ORDINAL sort, not Sort-Object's culture-aware default: extension/src/instancestate.js
    # sorts with JavaScript's ordinal Array#sort, and the two writers must lay a file out
    # the same way so a hand-diff of one VM's state does not churn per writer.
    $keys = [string[]]@($Values.Keys)
    [Array]::Sort($keys, [System.StringComparer]::Ordinal)
    foreach ($k in $keys) { $doc[$k] = $Values[$k] }
    $json = ($doc | ConvertTo-Json -Depth 10)
    $tmp = "$Path.tmp.$PID.$([DateTime]::UtcNow.Ticks)"
    try {
        $enc = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($tmp, ($json + "`n"), $enc)
        Move-Item -LiteralPath $tmp -Destination $Path -Force
    } catch {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        throw
    }
    return $Path
}

function Save-ConstructInstanceState {
    <#
        Merge -Values into ONE instance's saved state, preserving every key already there.

        The DEFAULT instance goes straight to Save-ConstructSettings -Dir, so its file
        keeps being written by the one function that has always written it (same merge,
        same encoding, same bytes). Any other instance has its VM-scoped keys written to
        its own file and its install-wide keys (if the caller mixed some in) merged into
        the scripts dir's .construct-settings.json, where every instance shares them.

        Best-effort about the legacy half in the same way Save-ConstructSettings is;
        a per-instance write failure throws, because losing it silently would revert that
        VM's configuration on the next read.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$Dir,
        [Parameter(Mandatory)][hashtable]$Values
    )
    if (Test-ConstructDefaultInstanceStore $Name) {
        if (-not $Dir) { throw "Save-ConstructInstanceState needs -Dir for the default instance (its state is the scripts dir's .construct-settings.json)." }
        if (-not (Get-Command Save-ConstructSettings -ErrorAction SilentlyContinue)) {
            throw "Save-ConstructInstanceState needs lib\AgentVm.Common.ps1 (Save-ConstructSettings) to be dot-sourced first."
        }
        Save-ConstructSettings -Dir $Dir -Values $Values
        return
    }
    $path = Get-ConstructInstanceStatePath -Name $Name
    if (-not $path) { throw "'$Name' is not a usable Construct instance name for a state file." }

    $installWide = @{}
    $vmScoped    = New-Object System.Collections.Specialized.OrderedDictionary([System.StringComparer]::Ordinal)
    foreach ($k in $Values.Keys) {
        if (Test-ConstructInstallWideKey $k) { $installWide[$k] = $Values[$k] } else { $vmScoped[$k] = $Values[$k] }
    }
    if ($installWide.Count -gt 0) {
        if (-not $Dir) { throw "Save-ConstructInstanceState needs -Dir to write the install-wide keys ($($installWide.Keys -join ', '))." }
        if (-not (Get-Command Save-ConstructSettings -ErrorAction SilentlyContinue)) {
            throw "Save-ConstructInstanceState needs lib\AgentVm.Common.ps1 (Save-ConstructSettings) to be dot-sourced first."
        }
        Save-ConstructSettings -Dir $Dir -Values $installWide
    }
    if ($vmScoped.Count -eq 0) { return }

    $merged = New-Object System.Collections.Specialized.OrderedDictionary([System.StringComparer]::Ordinal)
    $existing = Read-ConstructInstanceStateFile -Path $path
    if ($existing) {
        foreach ($p in $existing.PSObject.Properties) {
            if (Test-ConstructInstanceStateMetaKey $p.Name) { continue }
            if (Test-ConstructInstallWideKey $p.Name) { continue }
            $merged[$p.Name] = $p.Value
        }
    }
    foreach ($k in $vmScoped.Keys) { $merged[$k] = $vmScoped[$k] }
    Save-ConstructInstanceStateFile -Path $path -Name ("$Name".Trim()) -Values $merged | Out-Null
}
