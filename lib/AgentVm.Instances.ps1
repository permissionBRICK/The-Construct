#Requires -Version 5.1
<#
    The-Construct -- CLIENT-SIDE INSTANCE REGISTRY (PowerShell reader/writer).

    The PS counterpart of extension/src/instances.js. Both read the SAME file --
    %LOCALAPPDATA%\The-Construct\instances.json, next to the existing config\ dir --
    and MUST agree on the schema and on every derivation rule; the JS module's header
    comment is the shared contract.

    `agent-vm` is the IMPLICIT DEFAULT instance. A missing file, an unreadable file, or
    a missing entry all mean "exactly today's single-VM behaviour": the default is
    SYNTHESIZED in memory and nothing is written. Read-ConstructInstances therefore
    never throws -- problems are collected in .Problems for the caller to surface.

    Dot-source it:

        . "$PSScriptRoot\lib\AgentVm.Instances.ps1"

    Self-contained on purpose (no dependency on AgentVm.Common.ps1): Get-AgentUsage.ps1
    -style consumers dot-source it on its own. Windows PowerShell 5.1 compatible.

    Functions
      Get-ConstructInstancesPath                -> the registry file path
      Read-ConstructInstances                   -> [pscustomobject] @{ Instances; Default; Problems; Path; Exists }
      Get-ConstructInstance -Name               -> one normalised instance (falls back to the default)
      Resolve-ConstructInstanceDefaults -Name   -> the derivation rules, applied
      Test-ConstructInstanceName -Name          -> [bool] (the ONE name rule)
      Test-ConstructReservedInstanceName -Name  -> [bool] (the 'construct-' half of it)
      Get-ConstructInstanceIdentityProblem -Instance [-Entry] -> [string[]] (@() = usable)
      Get-ConstructBackendProblem -Raw           -> [string[]] (the backend field's own rule)
      Get-ConstructCanonicalIdentity -Name      -> the identity a hyperv-local instance MUST have
      Get-ConstructLocalIdentityProblem -Instance -> [string[]] (that rule; @() = usable/non-local)
      Get-ConstructRemoteIdentityProblem -Instance [-Entry] -> [string[]] (the hyperv-remote
                                                 rules: VmName = Name, SshHost stated)
      Get-ConstructInstanceCollision -Instances  -> @{ Problems; Drop } (cross-entry clashes)
      Test-ConstructDefaultInstance -Instance   -> [bool] "behaves exactly like today"
      Save-ConstructInstances                   -> atomic write (temp file + move)
      New-ConstructLocalInstanceEntry -Name     -> the raw entry for a local VM (B11)
      Save-ConstructLocalInstance -Name         -> record a local VM (zero-change default rule)
      Get-ConstructInstanceTargetConflict       -> [string[]] explicit args vs. the entry
      Resolve-ConstructInstanceTarget -Name     -> name-only targeting for the host scripts

    A normalised instance object has these properties (mirroring the JS shape):
      Name, Backend, VmName, VmHost, SshPort, HostAlias, KeyName, ConfigBranch,
      ScriptsDir, Service, Owner, PublicHost
    PublicHost is the OPTIONAL name the VM's WEB endpoints are reachable under
    (plan section 4.12) -- never where SSH is dialled, and never set for a local VM.
#>

Set-StrictMode -Version Latest

$script:ConstructSchemaVersion   = 1
$script:ConstructDefaultInstance = 'agent-vm'
# The backend a missing 'backend' field means -- today's zero-change path. Mirrors
# DEFAULT_BACKEND in extension/src/instances.js and drivers/index.js.
$script:ConstructDefaultBackend  = 'hyperv-local'
$script:ConstructBackends        = @('hyperv-local', 'hyperv-remote')
# THE ONE INSTANCE-NAME RULE -- mirrored verbatim by NAME_RE in
# extension/src/instances.js, by the -VmName DNS-label check in Auto-Install.ps1 /
# Create-AgentVM.ps1 (applied to the lowercased name) and by
# Constructd.Core.Logic.VmNameValidator. Change all four together.
#
# A name is a LOWERCASE DNS LABEL: it becomes the guest hostname's first label, the ssh
# alias, the construct_<name>_ed25519 key file and the vm-<name> git ref, so it must
# start AND END alphanumeric ('work-' derives 'work-.mshome.net', which is not a host
# name at all), and it is 1-63 characters -- the DNS label's own limit. The DERIVED key
# file of a maximum-length name is 'construct_' + 63 + '_ed25519' = 81 characters, which
# is why $script:ConstructKeyFileRe carries its own longer bound (the ssh-alias token
# rule stays at 64; an alias is not a path, and HostAlias is the bare name).
# \A and \z (not ^ and $): .NET's $ ALSO matches just before a trailing newline, so
# "work`n" would be a valid name here and an invalid one in JavaScript.
$script:ConstructInstanceNameRe  = '\A[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\z'
# A RESERVED name prefix. 'construct-<name>' was an abandoned alias convention: nothing
# ever shipped it, but the branch derivation used to STRIP it, so the valid instance
# 'construct-work' derived branch 'vm-construct-work' in the registry and 'vm-work' in
# the provisioner -- the config store of the DIFFERENT, equally valid instance 'work'.
# The strip is gone (derivation is now exactly alias = name, key =
# construct_<name>_ed25519, branch = vm-<name> everywhere) and the prefix is reserved
# instead. Matched case-insensitively, so a display-cased VM name asks the same question.
$script:ConstructReservedNamePrefix = 'construct-'
# The ONE human-readable statement of that rule, shared with the extension
# (instances.NAME_RULE), the installers and the service's 400. ASCII only.
$script:ConstructInstanceNameRule = '1-63 lowercase letters, digits or hyphens, starting and ending with a letter or digit; names starting with "construct-" are reserved.'

function Get-ConstructInstancesPath {
    <#
        %LOCALAPPDATA%\The-Construct\instances.json -- the same base directory
        Get-ConstructConfigDir anchors config\ under, so the registry sits beside it.
        Pure path math, no side effects.
    #>
    $base = $env:LOCALAPPDATA
    if (-not $base) { $base = $env:TEMP }
    if (-not $base) { $base = [System.IO.Path]::GetTempPath() }
    return (Join-Path (Join-Path $base "The-Construct") "instances.json")
}

function Test-ConstructInstanceName {
    param([string]$Name)
    if (-not $Name) { return $false }
    if (Test-ConstructReservedInstanceName $Name) { return $false }
    return [bool]([regex]::IsMatch($Name, $script:ConstructInstanceNameRe))
}

function Test-ConstructReservedInstanceName {
    <# Does this name claim the reserved 'construct-' prefix? Case-insensitive, so the
       callers that validate a display-cased VM name ask the same question. Pure. #>
    param([string]$Name)
    if (-not $Name) { return $false }
    return $Name.ToLowerInvariant().StartsWith($script:ConstructReservedNamePrefix)
}

function New-ConstructDefaultInstance {
    <# Today's literals -- what an install with no registry implicitly runs. #>
    [pscustomobject]@{
        Name         = $script:ConstructDefaultInstance
        Backend      = 'hyperv-local'
        VmName       = 'Agent-VM'
        VmHost       = 'agent-vm.mshome.net'
        SshPort      = 22
        HostAlias    = 'agent-vm'
        KeyName      = 'agent_vm_ed25519'
        ConfigBranch = 'vm'
        ScriptsDir   = $null
        Service      = $null
        Owner        = $null
        PublicHost   = $null
    }
}

# The string-typed fields of an instance entry (sshPort is an int and is handled on
# its own). Kept in one list so the type check and the JS reader's STRING_FIELDS stay
# in step. 'backend' is deliberately NOT here: "report it and use the derived default" is
# the wrong answer for the one field whose derived default is the LOCAL hypervisor, so it
# has its own, stricter check (Get-ConstructBackendProblem) which SKIPS the entry.
$script:ConstructStringFields = @('vmName', 'sshHost', 'vmHost', 'hostAlias', 'keyName', 'configBranch', 'scriptsDir', 'owner', 'publicHost')

# ── Identity-field FORMAT rules (mirror of extension/src/instances.js) ────────
# Being a string is not enough for the fields that end up in a PowerShell command
# line, an ssh argv, a key-file path or a git ref: '-x; Start-Process calc; #' is a
# perfectly good JSON string. An entry that breaks one of these is SKIPPED with a
# problem -- half an identity would silently target some OTHER machine. Every DERIVED
# value satisfies them, so only a hand-written entry can trip them. Change these
# together with the JS reader's identityProblems().
# \A and \z (not ^ and $): .NET's $ ALSO matches just before a trailing newline, while
# JavaScript's does not -- so "buildbox.local`n" would pass here and be skipped there,
# and the two readers would disagree about which instances exist. \z is the true end.
$script:ConstructHostNameRe  = '\A(?=.{1,253}\z)[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\z'
# The SHAPE an IPv6 literal must have BEFORE it is parsed (a character class alone
# would accept '::::' or '1::2::3'): hex, ':' and '.' only -- no zone id (%eth0), no
# brackets. That filter is also what makes the two readers' PARSERS agree: Node's
# net.isIP accepts 'fe80::1%eth0' and .NET's IPAddress.TryParse accepts '[::1]', so
# neither spelling ever reaches a parser on either side.
$script:ConstructIpv6ShapeRe = '\A[0-9A-Fa-f:.]{2,45}\z'
# The only IPv4 tail an IPv6-mapped address may carry. Pinned here because .NET has
# historically been lenient about leading zeros ('::ffff:1.2.3.004') where Node is not.
$script:ConstructIpv4StrictRe = '\A(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])(\.(25[0-5]|2[0-4][0-9]|1[0-9][0-9]|[1-9]?[0-9])){3}\z'
# An ssh_config Host alias: one path-free, shell-free token (HostAlias is the bare
# instance name, so 64 is comfortably above the 63-character name limit).
$script:ConstructSafeTokenRe = '\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z'
# A key FILE name -- the SAME character class (no path or control-character safety is
# loosened), with a longer length bound so the derived key of a maximum-length instance
# name ('construct_' + 63 + '_ed25519' = 81) still fits. 128 is far inside Windows'
# 255-character file-name limit for ~\.ssh\<KeyName>. Mirrors KEY_FILE_NAME_RE in
# extension/src/instances.js.
$script:ConstructKeyFileRe   = '\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z'
$script:ConstructDnsLabelRe  = '\A[A-Za-z0-9][A-Za-z0-9-]{0,62}\z'
$script:ConstructBranchRe    = '\A[A-Za-z0-9][A-Za-z0-9._-]*\z'
# Windows device names. They are NOT ordinary files at any path, so ~\.ssh\CON can
# never be created -- and the reservation applies to the stem before the first dot, so
# 'CON.txt' is the same device. Compared case-insensitively (lowercased first).
$script:ConstructWindowsDeviceNames = @(
    'con', 'prn', 'aux', 'nul',
    'com1', 'com2', 'com3', 'com4', 'com5', 'com6', 'com7', 'com8', 'com9',
    'lpt1', 'lpt2', 'lpt3', 'lpt4', 'lpt5', 'lpt6', 'lpt7', 'lpt8', 'lpt9'
)
# Branch names that are syntactically fine but semantically wrong (the trunk names and
# git's pseudo-refs). Same list as RESERVED_VM_BRANCHES in extension/src/configsync.js
# and $script:CONSTRUCT_RESERVED_VM_BRANCHES in AgentVm.Common.ps1; repeated here
# because this module is deliberately self-contained.
$script:ConstructReservedBranches = @(
    'main', 'master',
    'head', 'fetch_head', 'orig_head', 'merge_head', 'cherry_pick_head',
    'revert_head', 'bisect_head', 'rebase_head', 'auto_merge', 'stash'
)

function Test-ConstructInstanceIpv6 {
    <#
        A real IPv6 literal: shape-filtered first (see the regex above), then handed to
        an ACTUAL parser, because a character class accepts nonsense like '::::',
        '1::2::3' or '1:2:3:4:5:6:7:8:9'. The JS reader does the same with net.isIP;
        both suites run the same accept/reject matrix.
    #>
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return $false }
    if (-not [regex]::IsMatch($Value, $script:ConstructIpv6ShapeRe)) { return $false }
    if (-not $Value.Contains(':')) { return $false }
    if ($Value.Contains('.')) {
        $tail = $Value.Substring($Value.LastIndexOf(':') + 1)
        if (-not [regex]::IsMatch($tail, $script:ConstructIpv4StrictRe)) { return $false }
    }
    $ip = $null
    if (-not [System.Net.IPAddress]::TryParse($Value, [ref]$ip)) { return $false }
    if ($ip.AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetworkV6) { return $false }
    # A scope id can only come from a '%' the shape filter already rejects; belt and
    # braces, since ssh would have to carry it through a command line too.
    return [bool]($ip.ScopeId -eq 0)
}

function Test-ConstructInstanceHostEndpoint {
    <# A DNS host name / FQDN / IPv4 literal, or a bare IPv6 literal. #>
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return $false }
    if ([regex]::IsMatch($Value, $script:ConstructHostNameRe)) { return $true }
    return [bool](Test-ConstructInstanceIpv6 -Value $Value)
}

function Test-ConstructInstanceToken {
    <# One path-free, shell-free token: an ssh alias (and the base rule for a key name). #>
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return $false }
    if ($Value.Contains('..')) { return $false }
    return [bool]([regex]::IsMatch($Value, $script:ConstructSafeTokenRe))
}

function Test-ConstructInstanceKeyFileName {
    <#
        A key file name -- the alias rule PLUS what Windows adds, because this value is
        used as a FILE: ~\.ssh\<KeyName> is written and read by Provision-AgentVM.ps1.
          * a trailing dot is stripped by Win32, so 'agent_vm_ed25519.' and
            'agent_vm_ed25519' are the SAME file -- an entry spelled that way would
            quietly overwrite the DEFAULT instance's key;
          * a reserved device stem (CON, NUL, COM1 ..., with or without an extension) is
            not a creatable file at all, so provisioning would fail after the VM exists.
        HostAlias deliberately keeps the plain token rule: an ssh_config Host alias is
        not a path, and it is the bare instance name. The key file's LENGTH bound is its
        own ($script:ConstructKeyFileRe, 128) so a 63-character instance name's derived
        key still fits; the character class is identical. Mirrors isKeyFileName() in
        extension/src/instances.js.
    #>
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return $false }
    if ($Value.Contains('..')) { return $false }
    if (-not ([regex]::IsMatch($Value, $script:ConstructKeyFileRe))) { return $false }
    if ($Value.EndsWith('.')) { return $false }
    $stem = $Value.Split('.')[0]
    return [bool]($script:ConstructWindowsDeviceNames -notcontains $stem.ToLowerInvariant())
}

function Test-ConstructInstanceBranch {
    <#
        A branch name git will accept as refs/heads/<name> and that is safe as a bare
        ref operand -- the rule isValidVmBranch (extension/src/configsync.js) and
        Test-ConstructVmBranchName (AgentVm.Common.ps1) apply.
    #>
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return $false }
    if (-not [regex]::IsMatch($Value, $script:ConstructBranchRe)) { return $false }
    if ($Value.Contains('..')) { return $false }
    # `git check-ref-format --branch foo.` fails, so a trailing dot is refused here rather
    # than at the first sync tick (same rule, same fixtures, as the two twins above).
    if ($Value.EndsWith('.')) { return $false }
    if ($Value.EndsWith('.lock')) { return $false }
    # A loose ref is a FILE, and the host config repo is a loose-ref repo on Windows:
    # refs/heads/CON (and the CON.lock git writes beside it) cannot be created there,
    # extension or not. The same device rule Test-ConstructInstanceKeyFileName applies to
    # ~\.ssh\<KeyName>; git on Linux accepts these names, so only this check catches them.
    $stem = $Value.Split('.')[0]
    if ($script:ConstructWindowsDeviceNames -contains $stem.ToLowerInvariant()) { return $false }
    $lower = $Value.ToLowerInvariant()
    if ($script:ConstructReservedBranches -contains $lower) { return $false }
    # Any other spelling of the default branch is the SAME loose-ref file on Windows,
    # so it would hijack the default instance's store.
    if ($lower -eq 'vm' -and $Value -cne 'vm') { return $false }
    return $true
}

function Get-ConstructInstanceIdentityProblem {
    <#
        The format problems of one NORMALISED instance, as strings. Empty = usable.
        Pure; mirrors identityProblems() in extension/src/instances.js.

        -Entry (optional) is the entry as WRITTEN in the file. The host has two
        spellings and Resolve-ConstructInstanceDefaults prefers 'sshHost', so
        { sshHost = 'good.local'; vmHost = '-x; calc' } would otherwise normalise to a
        valid endpoint and leave the hostile one on disk for the next reader. Every
        SUPPLIED host field is checked, then the effective endpoint.
    #>
    param($Instance, $Entry)
    $out = New-Object System.Collections.Generic.List[string]
    $add = { param([string]$m) if (-not $out.Contains($m)) { $out.Add($m) } }
    if ($null -ne $Entry) {
        # 'publicHost' is checked HERE, on the RAW entry, and for EVERY backend -- even the
        # ones that ignore it (Resolve-ConstructInstanceDefaults drops it for hyperv-local).
        # It becomes the provisioner's -PublicHost, CONSTRUCT_EXTERNAL_HOST inside the
        # guest's shell command line and a printed URL, so a value that is not a host name
        # is refused where it sits rather than where it lands.
        foreach ($f in @('sshHost', 'vmHost', 'publicHost')) {
            $v = Get-ConstructInstanceField $Entry $f
            if ($v -and -not (Test-ConstructInstanceHostEndpoint $v)) {
                & $add "`"$f`" '$v' is not a host name or IP address"
            }
        }
    }
    # The NAME is an identity field too -- every other one is derived from it.
    # Read-ConstructInstances already refuses a bad key before it gets here, so this is
    # the belt to that braces (mirrors identityProblems() in extension/src/instances.js).
    if (-not (Test-ConstructInstanceName ([string]$Instance.Name))) {
        & $add "`"name`" '$($Instance.Name)' is not a usable instance name ($($script:ConstructInstanceNameRule))"
    }
    # The VM name doubles as the guest hostname, so it must be ONE DNS label
    # (Auto-Install.ps1 enforces the same shape before it creates anything).
    if (-not ([regex]::IsMatch([string]$Instance.VmName, $script:ConstructDnsLabelRe))) {
        & $add "`"vmName`" '$($Instance.VmName)' is not a usable VM/host name (letters, digits and hyphens, starting alphanumeric, max 63)"
    }
    if (-not (Test-ConstructInstanceHostEndpoint ([string]$Instance.VmHost))) {
        & $add "`"sshHost`" '$($Instance.VmHost)' is not a host name or IP address"
    }
    if (-not (Test-ConstructInstanceToken ([string]$Instance.HostAlias))) {
        & $add "`"hostAlias`" '$($Instance.HostAlias)' is not a usable ssh alias (letters, digits, '.', '_' and '-', max 64)"
    }
    if (-not (Test-ConstructInstanceKeyFileName ([string]$Instance.KeyName))) {
        & $add ("`"keyName`" '$($Instance.KeyName)' is not a usable key file name (letters, digits, '.', '_' and '-', max 128;" +
            " no trailing dot and not a reserved Windows device name)")
    }
    if (-not (Test-ConstructInstanceBranch ([string]$Instance.ConfigBranch))) {
        & $add "`"configBranch`" '$($Instance.ConfigBranch)' is not a usable config-sync branch name"
    }
    return @($out)
}

# ── The CANONICAL identity of a local (hyper-v) instance ─────────────────────
# For backend 'hyperv-local' the identity is DERIVED, in two places that must agree:
# here, from the instance name; and by Auto-Install.ps1 during a rebuild, from -VmName
# alone (guest host "<vmname lowercased>.mshome.net", ssh alias = that name, key
# construct_<name>_ed25519 -- agent_vm_ed25519 for the default VM), which is why
# reinstall/redownload emit ONLY -VmName. So an entry that deviates does not describe a
# customised instance, it TARGETS ANOTHER MACHINE than the one it would rebuild:
# { "work-vm": { "vmName": "Agent-VM" } } reinstalls the DEFAULT VM, and a custom
# host/alias/key is replaced by the derived one the moment the VM is rebuilt. Such an
# entry is SKIPPED with a problem. Non-local backends keep free-form (still
# format-checked) identities -- their endpoints are defined on the other side. Not
# rule-FREE, though: 'hyperv-remote' has two of its own (an endpoint it must state, and
# one VM name for the service and the rebuild alike) -- Get-ConstructRemoteIdentityProblem.
# ConfigBranch is deliberately NOT pinned: it is the one field the launched scripts can
# be TOLD (-ConfigBranch), so an explicit branch stays a supported override.
# Mirrors canonicalIdentity()/localIdentityProblems() in extension/src/instances.js.

function Get-ConstructCanonicalIdentity {
    <# The identity a 'hyperv-local' instance of this name MUST have. Pure. #>
    param([Parameter(Mandatory)][string]$Name)
    $isDefault = ($Name -eq $script:ConstructDefaultInstance)
    return [pscustomobject]@{
        VmName    = if ($isDefault) { 'Agent-VM' }             else { $Name }
        VmHost    = if ($isDefault) { 'agent-vm.mshome.net' }  else { "$Name.mshome.net" }
        HostAlias = if ($isDefault) { 'agent-vm' }             else { $Name }
        KeyName   = if ($isDefault) { 'agent_vm_ed25519' }     else { "construct_${Name}_ed25519" }
        SshPort   = 22
    }
}

function Test-ConstructLocalBackend {
    <#
        Is this backend the LOCAL Hyper-V one? Normalised exactly like getDriver() in
        extension/src/drivers/index.js (trimmed, lowercased, empty = the default
        backend), so every entry that WOULD be handed the local driver -- including a
        differently-cased 'HYPERV-LOCAL' -- is recognised as such. Such a two-faced
        spelling does not load at all (Get-ConstructBackendProblem); the exact value is
        held to the canonical identity.
    #>
    param([string]$Backend)
    $v = ''
    if ($null -ne $Backend) { $v = ([string]$Backend).Trim().ToLowerInvariant() }
    return [bool]($v -eq '' -or $v -eq $script:ConstructDefaultBackend)
}

function Get-ConstructBackendProblem {
    <#
        The problems of an entry's RAW 'backend' value (@() = usable). This field owns its
        own type check (it is NOT in $script:ConstructStringFields) because "report it and
        use the derived default" is exactly the wrong answer for the one field whose
        derived default is the LOCAL hypervisor.

        Two kinds of entry are refused whole rather than normalised:
          * PRESENT BUT UNUSABLE -- backend: 42, "", "  ". The file states a backend; it
            just isn't one. Deriving 'hyperv-local' from it would grant destructive local
            access to a value the user never wrote.
          * A SPELLING THE TWO LOOKUPS READ DIFFERENTLY -- 'HYPERV-LOCAL',
            'Hyperv-Remote', i.e. a case-variant of ANY id this build implements. Every
            enum comparison in both readers is case-SENSITIVE (so the value is "unknown"
            to them), but getDriver() in extension/src/drivers/index.js trims and
            lowercases before the lookup (so it hands back the REAL driver for it -- the
            local one with hostLifecycle: true, or the remote one that drives somebody
            else's host service). The two readings disagree about what such an entry IS,
            so nothing may act on it under either: it does not load. Restricting this to
            'hyperv-local' (as it once was) left 'HYPERV-REMOTE' loading while every
            message about it claimed it had no driver -- and it had one.
        A genuinely unknown backend ('proxmox') is NOT a problem here -- it is reported
        separately and kept, because the driver dispatch degrades on it correctly.
        Mirrors backendProblems() in extension/src/instances.js. Pure.
    #>
    param($Raw)
    $out = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Raw) { return @($out) }   # omitted / JSON null -> the derived default
    if (-not ($Raw -is [string]) -or [string]::IsNullOrWhiteSpace($Raw)) {
        $out.Add("`"backend`" '$Raw' is not a usable backend id (omit it for '$($script:ConstructDefaultBackend)', or name one of: $($script:ConstructBackends -join ', '))")
        return @($out)
    }
    $v = $Raw.Trim()
    if ($script:ConstructBackends -ccontains $v) { return @($out) }
    # The canonical id this value differs from only by case -- the one the driver lookup
    # would resolve it to. An empty string never reaches here (refused above), so the
    # local backend's "empty means local" rule plays no part in this comparison.
    # A foreach, not @(...)[0]: Set-StrictMode -Version Latest makes indexing an EMPTY
    # array a terminating error, and "no canonical id" is the common case here.
    $canonical = ''
    $lower = $v.ToLowerInvariant()
    foreach ($b in $script:ConstructBackends) { if ($b -ceq $lower) { $canonical = $b; break } }
    if ($canonical) {
        $out.Add("`"backend`" '$v' is not spelled '$canonical' (the backend id is case-sensitive, but the driver lookup is not -- a value the two read differently must not drive a VM)")
    }
    return @($out)
}

function Get-ConstructLocalIdentityProblem {
    <#
        The problems of a NORMALISED instance whose identity must be canonical (above):
        @() for every non-local backend and for a local instance that matches its
        derivation. Pure; mirrors localIdentityProblems() in extension/src/instances.js.
    #>
    param($Instance)
    $out = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Instance) { return @($out) }
    if (-not (Test-ConstructLocalBackend ([string]$Instance.Backend))) { return @($out) }
    $c    = Get-ConstructCanonicalIdentity -Name ([string]$Instance.Name)
    $why  = " (a `"$($script:ConstructDefaultBackend)`" instance's identity is derived from its name --" +
            " Auto-Install.ps1 rebuilds it that way, so anything else targets another VM)"
    $n    = "`"$($Instance.Name)`""
    # The Hyper-V display name is case-INSENSITIVE (and the default instance's canonical
    # spelling is the display-cased 'Agent-VM'), so only the lowercased form must match.
    if (([string]$Instance.VmName).ToLowerInvariant() -cne $c.VmName.ToLowerInvariant()) {
        $out.Add("`"vmName`" `"$($Instance.VmName)`" must be `"$($c.VmName)`" for instance $n$why")
    }
    # The rest are the lowercase tokens the scripts derive, compared verbatim: a differing
    # spelling is a differing ssh_config Host block / key file / -VmHost argument.
    if ([string]$Instance.VmHost -cne $c.VmHost) {
        $out.Add("`"sshHost`" `"$($Instance.VmHost)`" must be `"$($c.VmHost)`" for instance $n$why")
    }
    if ([string]$Instance.HostAlias -cne $c.HostAlias) {
        $out.Add("`"hostAlias`" `"$($Instance.HostAlias)`" must be `"$($c.HostAlias)`" for instance $n$why")
    }
    if ([string]$Instance.KeyName -cne $c.KeyName) {
        $out.Add("`"keyName`" `"$($Instance.KeyName)`" must be `"$($c.KeyName)`" for instance $n$why")
    }
    if ([int]$Instance.SshPort -ne [int]$c.SshPort) {
        $out.Add("`"sshPort`" $($Instance.SshPort) must be $($c.SshPort) for instance $n$why")
    }
    return @($out)
}

# ── The CANONICAL identity of a REMOTE instance ──────────────────────────────
# Backend 'hyperv-remote' has two identity rules of its own, for two different reasons.
#
# 1. VmName MUST BE THE INSTANCE NAME. A remote VM is addressed BY NAME on the host
#    service, from two directions that have to mean the same machine: the extension's
#    driver queries and starts vmName (extension/src/drivers/hyperv-remote.js), while a
#    rebuild emits -InstanceName <name> and Auto-Install.ps1 then uses the registry
#    ENTRY's name to fetch the endpoint, DELETE the VM and create it again. An entry
#    keyed 'alias-vm' with vmName 'service-vm' therefore splits the identity in half: the
#    power state and Start act on service-vm while Reinstall deletes and recreates
#    alias-vm -- two different VMs on somebody else's machine. Compared EXACTLY (-cne,
#    not lowercased like the local Hyper-V display name): the value goes into a URL path
#    (/vms/{name}) and into a -InstanceName argument, and nothing here may assume the
#    service folds case.
# 2. SshHost IS REQUIRED. A remote endpoint is whatever the service allocated -- no name
#    convention can produce it. An entry that omits it derives '<name>.mshome.net' (the
#    LOCAL Hyper-V convention), and the picker, ssh and every lifecycle action would then
#    target an unrelated machine on this PC's own network. Only the canonical spelling
#    counts: everything that writes the registry writes sshHost
#    (ConvertTo-ConstructInstanceEntry / toFileEntry), so an entry stating its endpoint
#    under the JS-internal 'vmHost' alias is a hand-written file, and refusing one is the
#    fail-closed reading.
# Both are WHOLE-ENTRY rejections. Mirrors isRemoteBackend()/remoteIdentityProblems() in
# extension/src/instances.js -- change both together.

function Test-ConstructRemoteBackend {
    <#
        Is this backend the REMOTE Hyper-V one? Normalised exactly like getDriver() in
        extension/src/drivers/index.js (trimmed, lowercased), so every entry that WOULD be
        handed the remote driver is held to the rules above. A case-variant spelling never
        gets this far -- Get-ConstructBackendProblem refuses the whole entry -- but the
        lookup here matches the driver's rather than assuming that, so this rule can never
        be the looser of the two.
    #>
    param([string]$Backend)
    $v = ''
    if ($null -ne $Backend) { $v = ([string]$Backend).Trim().ToLowerInvariant() }
    return [bool]($v -eq 'hyperv-remote')
}

function Get-ConstructRemoteIdentityProblem {
    <#
        The problems of a NORMALISED instance on the remote backend (above): @() for every
        other backend, and for a remote instance that states an endpoint and names its VM
        after itself. -Entry is the entry as WRITTEN -- the SshHost rule is about what the
        file says, not about what the derivation made of it. Pure; mirrors
        remoteIdentityProblems() in extension/src/instances.js.
    #>
    param($Instance, $Entry)
    $out = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Instance) { return @($out) }
    if (-not (Test-ConstructRemoteBackend ([string]$Instance.Backend))) { return @($out) }
    if (-not (Get-ConstructInstanceField $Entry 'sshHost')) {
        $out.Add('"sshHost" is missing (no sshHost) -- a "hyperv-remote" instance''s endpoint is the' +
                 ' one its host service allocated, so it cannot be derived from the instance name')
    }
    if ([string]$Instance.VmName -cne [string]$Instance.Name) {
        $out.Add("`"vmName`" `"$($Instance.VmName)`" must be `"$($Instance.Name)`" for a `"hyperv-remote`"" +
                 " instance (the host service addresses the VM by that name, so the power state, Start and a" +
                 " rebuild would otherwise act on two different VMs)")
    }
    return @($out)
}

# The identities that must be UNIQUE across the registry, with the schema name each is
# reported under, the comparison KEY it is compared by and how it is SHOWN in a problem.
# Two instances sharing one are two names for ONE machine (or one key file / one
# ssh_config Host block): a rebuild of the second would delete the first's VM.
# ConfigBranch is one of them for the same reason: the config-sync branch IS that
# instance's store inside the single host config repo (docs/config-sync.md, "Multiple
# instances" -- one branch per VM), so two entries on one branch share their VM
# snapshots, deletion history, merge base and write-backs, and one VM's tick merges (or
# deletes) the other's configuration. Rule 1 below therefore also RESERVES the default
# instance's historical branch 'vm' for agent-vm.
# The ENDPOINT is the composite (sshHost, sshPort), NOT the host alone: several
# hyperv-remote instances legitimately live on ONE service host and are told apart by the
# SSH forward the service allocated them (one port per VM out of a configured range), so
# keying on the host alone made every VM on a shared host collide -- and the "drop BOTH"
# rule then lost them all. A hyperv-local instance's port is canonically 22 and its host
# derives from its own name, so local entries still cannot share an endpoint.
# Mirrors UNIQUE_FIELDS in extension/src/instances.js -- change both together.
$script:ConstructUniqueFields = @(
    @{ Label = 'vmName';
       Value = { param($i) ([string]$i.VmName).ToLowerInvariant() }
       Show  = { param($i) [string]$i.VmName } },
    @{ Label = 'sshHost/sshPort';
       Value = { param($i) (([string]$i.VmHost).ToLowerInvariant() + ' port ' + [string][int]$i.SshPort) }
       Show  = { param($i) ([string]$i.VmHost + ':' + [string][int]$i.SshPort) } },
    @{ Label = 'hostAlias';
       Value = { param($i) ([string]$i.HostAlias).ToLowerInvariant() }
       Show  = { param($i) [string]$i.HostAlias } },
    @{ Label = 'keyName';
       Value = { param($i) ([string]$i.KeyName).ToLowerInvariant() }
       Show  = { param($i) [string]$i.KeyName } },
    @{ Label = 'configBranch';
       Value = { param($i) ([string]$i.ConfigBranch).ToLowerInvariant() }
       Show  = { param($i) [string]$i.ConfigBranch } }
)

function Get-ConstructInstanceCollision {
    <#
        Cross-entry identity COLLISIONS over a name->instance hashtable. Returns
        @{ Problems = [string[]]; Drop = [string[]] }. Two rules:
          1. a non-default entry may not claim any of the DEFAULT instance's values -- the
             default is always present (synthesised when absent), so such an entry aims a
             rebuild/re-key at the default VM under another name;
          2. no two entries may share one -- BOTH are dropped, because nothing in the file
             says which is the impostor. Dropping both is also what makes the two readers
             agree without depending on key order.
        Pure; mirrors collisionProblems() in extension/src/instances.js.

        -ExcludeLabel is NOT a relaxation of the rule set (the READER always applies all
        of it, and the JS reader has no such parameter): it is for a caller that has to
        ask the question BEFORE one of the identities exists. Auto-Install.ps1's remote
        path checks the registry before the host service has allocated the VM's SSH
        forward, so at that moment the composite endpoint ('sshHost/sshPort') is not yet
        knowable and only the name-derived identities can be judged; the full check runs
        again on the endpoint the service really returned.
    #>
    param($Instances, [string[]]$ExcludeLabel = @())
    $problems = New-Object System.Collections.Generic.List[string]
    $drop     = New-Object System.Collections.Generic.List[string]
    # The fields this call is allowed to judge. Ordinal, case-sensitive matching on the
    # LABEL, so a typo in a caller's exclusion silently widens nothing.
    $fields = @($script:ConstructUniqueFields | Where-Object { $ExcludeLabel -cnotcontains $_.Label })
    # ORDINAL sort, like the JS reader's Array#sort -- Sort-Object is culture-sensitive
    # and could order '-' differently, which would reorder the problem messages.
    $names = [string[]]@($Instances.Keys)
    [Array]::Sort($names, [System.StringComparer]::Ordinal)
    $default = New-ConstructDefaultInstance
    foreach ($name in $names) {
        if ($name -ceq $script:ConstructDefaultInstance) { continue }
        $inst = $Instances[$name]
        foreach ($f in $fields) {
            if ((& $f.Value $inst) -ceq (& $f.Value $default)) {
                $problems.Add("instance '$name': $($f.Label) `"$(& $f.Show $inst)`" belongs to the default instance '$($script:ConstructDefaultInstance)' -- skipped")
                if (-not $drop.Contains($name)) { $drop.Add($name) }
                break
            }
        }
    }
    for ($i = 0; $i -lt $names.Count; $i++) {
        for ($j = $i + 1; $j -lt $names.Count; $j++) {
            $a = $Instances[$names[$i]]
            $b = $Instances[$names[$j]]
            foreach ($f in $fields) {
                if ((& $f.Value $a) -ceq (& $f.Value $b)) {
                    $problems.Add("instances '$($names[$i])' and '$($names[$j])' share the same $($f.Label) `"$(& $f.Show $a)`" -- both skipped")
                    foreach ($n in @($names[$i], $names[$j])) { if (-not $drop.Contains($n)) { $drop.Add($n) } }
                    break
                }
            }
        }
    }
    return [pscustomobject]@{ Problems = @($problems); Drop = @($drop) }
}

function Get-ConstructRawProperty {
    <#
        The raw value of the schema field spelled EXACTLY $Name, or $null when the object
        has no such field. No coercion of any kind.

        ORDINAL, CASE-SENSITIVE on purpose. $Object.PSObject.Properties[$Name] -- what this
        used to do -- looks up case-INSENSITIVELY, while JavaScript property access does
        not. The same bytes therefore read differently on the two sides: a file spelled
        { "VERSION": 1, "DEFAULTINSTANCE": "x", "INSTANCES": { "x": ... } } was IGNORED by
        extension/src/instances.js (which sees no version, no instances and no default, so
        it uses agent-vm) while this reader loaded 'x' and made it the DEFAULT -- so a host
        script and the extension would drive DIFFERENT VMs from one registry. Wrong-cased
        NESTED fields did the same one entry at a time ('BACKEND'/'SSHHOST' turned a
        derived hyperv-local entry into a remote one here and nowhere else).
        So every top-level and nested schema field is matched byte-for-byte, and a
        wrong-cased key is simply ABSENT -- exactly what the JS reader makes of it.

        (Known, FAIL-CLOSED divergence left in place: a file that spells the SAME key in
        two casings -- "version" AND "VERSION" -- makes ConvertFrom-Json itself throw on
        PowerShell 6+, so the whole file degrades to "not valid JSON" plus the default
        instance rather than to a wrong target. Windows PowerShell 5.1 has no -AsHashtable
        to avoid that, and falling back to the default instance is the safe reading.)
    #>
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $props = $null
    try { $props = @($Object.PSObject.Properties) } catch { return $null }
    foreach ($prop in $props) {
        if ([string]::Equals([string]$prop.Name, $Name, [System.StringComparison]::Ordinal)) {
            # `,` keeps an ARRAY value intact: a bare `return $prop.Value` unrolls it into
            # the pipeline, so a malformed `"configBranch": ["x"]` would arrive at the
            # caller as the plain string "x" and be accepted -- exactly the
            # mis-normalization the type check exists to catch.
            return ,$prop.Value
        }
    }
    return $null
}

function Test-ConstructBadString {
    <# Present, but NOT a usable string -- i.e. a malformed file worth reporting.
       Mirrors the JS reader's badString(). #>
    param($Value)
    if ($null -eq $Value) { return $false }
    if ($Value -is [string]) { return $false }
    return $true
}

function Get-ConstructInstanceField {
    <#
        A schema STRING field: a trimmed non-empty value ONLY when the JSON value really
        is a string. A number/bool/object under a string key is a malformed file, not
        something to stringify -- ConvertFrom-Json would otherwise turn sshHost: 123
        into the host name "123" and we would happily try to SSH to it, while the JS
        reader (which requires typeof === "string") derives <name>.mshome.net instead.
        The two readers MUST normalize a bad file identically; this is that rule.
    #>
    param($Object, [string]$Name)
    $v = Get-ConstructRawProperty $Object $Name
    if ($null -eq $v) { return $null }
    if (-not ($v -is [string])) { return $null }
    if ([string]::IsNullOrWhiteSpace($v)) { return $null }
    return $v.Trim()
}

function ConvertTo-ConstructPort {
    <#
        A TCP port in 1..65535, from an integral NUMBER or a bare-digit STRING -- the
        same two shapes the JS reader accepts (typeof number + Number.isInteger, or
        /^\d{1,5}$/). Deliberately NOT [int]::TryParse on an arbitrary object: that
        would accept "+2201" and $true, neither of which JS accepts. Both readers DO trim
        surrounding whitespace, so " 2201 " is a port on both sides.
    #>
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) {
        # At most 5 digits, so the [int] cast below can never overflow.
        if (-not [regex]::IsMatch($Value.Trim(), '^\d{1,5}$')) { return $null }
        $n = [int]$Value.Trim()
        if ($n -lt 1 -or $n -gt 65535) { return $null }
        return $n
    }
    if ($Value -is [bool] -or -not ($Value -is [ValueType])) { return $null }
    if (-not ($Value -is [int] -or $Value -is [long] -or $Value -is [int16] -or $Value -is [byte] -or
              $Value -is [sbyte] -or $Value -is [uint16] -or $Value -is [uint32] -or $Value -is [uint64] -or
              $Value -is [double] -or $Value -is [single] -or $Value -is [decimal])) {
        return $null
    }
    # Range-check in a WIDE representation BEFORE any Int32 cast. ConvertFrom-Json hands
    # back an Int64 (or a double) for a large JSON number, and `[int]999999999999` throws
    # "Value was either too large or too small for an Int32" -- which would escape this
    # module and break the "never throws" contract the whole zero-change path rests on.
    $d = 0.0
    try { $d = [double]$Value } catch { return $null }
    if ([double]::IsNaN($d) -or [double]::IsInfinity($d)) { return $null }
    if ($d -lt 1 -or $d -gt 65535) { return $null }
    # Only whole numbers are ports (JS: Number.isInteger).
    if ([math]::Floor($d) -ne $d) { return $null }
    return [int]$d
}

function Resolve-ConstructInstanceDefaults {
    <#
        Fill in every field an entry omits. THE DERIVATION RULES (shared with
        extension/src/instances.js -- change both together):

          agent-vm (the default)  -> today's literals verbatim
          any other <name>        -> HostAlias    "<name>"        (the BARE name: every
                                       shared PS helper -- Get-RemoteOpenLink,
                                       Close-VmVsCodeWindow, Invoke-ConstructVmSsh's
                                       alias fallback -- derives the alias as the first
                                       DNS label of the VM host, and Auto-Install.ps1
                                       writes alias = lowercased VM name)
                                     KeyName      "construct_<name>_ed25519"
                                     ConfigBranch "vm-<name>"     (NOT "vm/<name>": git
                                       cannot hold refs/heads/vm and refs/heads/vm/x
                                       at the same time)
                                     VmName       "<name>"
                                     VmHost       "<name>.mshome.net"  (hyperv-local)
                                     SshPort      22
                                     ScriptsDir   $null           (= newest install)

        For 'hyperv-local' these derivations are also the ONLY permitted values -- an
        entry that states something else is SKIPPED by the parser
        (Get-ConstructLocalIdentityProblem), not normalised into it.
        Pure; $Entry is never modified.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        $Entry
    )

    $isDefault = ($Name -eq $script:ConstructDefaultInstance)

    # The backend is NEVER coerced, and the rule is PRESENCE-AWARE:
    #   * ABSENT (or JSON null) -> 'hyperv-local', today's zero-change default;
    #   * a usable string       -> kept EXACTLY as written (trimmed), whatever it says.
    # A wrong or misspelled one ('proxmox', 'hyperv-remtoe') must reach the driver
    # dispatch as ITSELF, where the unknown-driver fallback refuses the hypervisor
    # actions. Rewriting anything to 'hyperv-local' (as this once did) PROMOTED it to
    # destructive local Hyper-V access. A present-but-UNUSABLE value (wrong type, or an
    # empty/whitespace string) is kept as it came so it can never read as local either --
    # the parser skips such an entry outright (Get-ConstructBackendProblem). A local
    # backend is then held to the canonical identity (Get-ConstructLocalIdentityProblem).
    $rawBackendValue = Get-ConstructRawProperty $Entry 'backend'
    if ($null -eq $rawBackendValue) {
        $backend = $script:ConstructDefaultBackend
    } else {
        $backend = Get-ConstructInstanceField $Entry 'backend'
        if (-not $backend) { $backend = $rawBackendValue }
    }

    $vmName = Get-ConstructInstanceField $Entry 'vmName'
    if (-not $vmName) { $vmName = if ($isDefault) { 'Agent-VM' } else { $Name } }

    $vmHost = Get-ConstructInstanceField $Entry 'sshHost'
    if (-not $vmHost) { $vmHost = Get-ConstructInstanceField $Entry 'vmHost' }
    if (-not $vmHost) { $vmHost = if ($isDefault) { 'agent-vm.mshome.net' } else { "$Name.mshome.net" } }

    $sshPort = ConvertTo-ConstructPort (Get-ConstructRawProperty $Entry 'sshPort')
    if (-not $sshPort) { $sshPort = 22 }

    $hostAlias = Get-ConstructInstanceField $Entry 'hostAlias'
    if (-not $hostAlias) { $hostAlias = if ($isDefault) { 'agent-vm' } else { $Name } }

    $keyName = Get-ConstructInstanceField $Entry 'keyName'
    if (-not $keyName) { $keyName = if ($isDefault) { 'agent_vm_ed25519' } else { "construct_${Name}_ed25519" } }

    $branch = Get-ConstructInstanceField $Entry 'configBranch'
    if (-not $branch) { $branch = if ($isDefault) { 'vm' } else { "vm-$Name" } }

    $service = $null
    $svcRaw = Get-ConstructRawProperty $Entry 'service'
    if ($null -ne $svcRaw -and ($svcRaw -is [psobject]) -and -not ($svcRaw -is [array]) -and -not ($svcRaw -is [string])) {
        $svcUrl = Get-ConstructInstanceField $svcRaw 'url'
        if ($svcUrl) {
            # -cne: case-SENSITIVE, matching the JS reader's `=== "token"`.
            $svcAuth = Get-ConstructInstanceField $svcRaw 'auth'
            if ($svcAuth -cne 'token') { $svcAuth = 'negotiate' }
            $service = [pscustomobject]@{ Url = $svcUrl; Auth = $svcAuth }
        }
    }

    return [pscustomobject]@{
        Name         = $Name
        Backend      = $backend
        VmName       = $vmName
        VmHost       = $vmHost
        SshPort      = [int]$sshPort
        HostAlias    = $hostAlias
        KeyName      = $keyName
        ConfigBranch = $branch
        ScriptsDir   = (Get-ConstructInstanceField $Entry 'scriptsDir')
        Service      = $service
        Owner        = (Get-ConstructInstanceField $Entry 'owner')
        # OPTIONAL and NEVER derived (plan section 4.12): the host service states it
        # (GET /vms/{name}/endpoint -> publicHost) and the installer records it. IGNORED
        # for a local backend, where the one address a VM has is its endpoint -- dropping
        # it here rather than carrying it means no consumer has to re-ask which backend it
        # is looking at. Mirrors deriveDefaults() in extension/src/instances.js.
        PublicHost   = $(if (Test-ConstructLocalBackend $backend) { $null } else { Get-ConstructInstanceField $Entry 'publicHost' })
    }
}

function Test-ConstructDefaultInstance {
    <#
        Does this instance behave EXACTLY like today's single-VM install? That is the
        gate every zero-change code path hangs on: true for $null, for the synthesized
        default, and for a registry that spells `agent-vm` out with today's values.
        ScriptsDir/Service/Owner are excluded -- they carry no VM-targeting consequence.

        -ceq throughout: the comparison is CASE-SENSITIVE, matching the JS reader's
        `===`. With PowerShell's default -eq an explicitly cased vmName "agent-vm" would
        read as the default here and as a NON-default instance in JS -- so one side would
        emit -VmName/-VmHost target arguments and the other would not.
    #>
    param($Instance)
    if ($null -eq $Instance) { return $true }
    $d = New-ConstructDefaultInstance
    return (
        $Instance.Name         -ceq $d.Name -and
        $Instance.Backend      -ceq $d.Backend -and
        $Instance.VmName       -ceq $d.VmName -and
        $Instance.VmHost       -ceq $d.VmHost -and
        [int]$Instance.SshPort -eq  $d.SshPort -and
        $Instance.HostAlias    -ceq $d.HostAlias -and
        $Instance.KeyName      -ceq $d.KeyName -and
        $Instance.ConfigBranch -ceq $d.ConfigBranch
    )
}

function ConvertFrom-ConstructInstancesJson {
    <#
        Parse registry TEXT. NEVER throws: every problem is collected as a string and
        the default instance is synthesized so the caller always has something usable.
        Returns @{ Instances = <hashtable name->instance>; Default = <name>; Problems = <string[]> }.
    #>
    [CmdletBinding()]
    param([string]$Text)

    $problems  = New-Object System.Collections.Generic.List[string]
    # An ORDINAL (case-sensitive) hashtable: PowerShell's default @{} looks up
    # case-insensitively, so Get-ConstructInstance -Name 'AGENT-VM' would resolve here
    # and NOT in JS (whose byName is a plain object). Names are lowercase-only by
    # validation, so this only removes a way for the two readers to disagree.
    $instances = New-Object System.Collections.Hashtable ([System.StringComparer]::Ordinal)
    $default   = $script:ConstructDefaultInstance
    $doc       = $null

    $raw = if ($null -eq $Text) { '' } else { [string]$Text }
    # Windows PowerShell writes a UTF-8 BOM with Set-Content -Encoding UTF8.
    $raw = $raw -replace '^﻿', ''

    if (-not [string]::IsNullOrWhiteSpace($raw)) {
        $parsed = $null
        $parsedOk = $false
        try { $parsed = $raw | ConvertFrom-Json; $parsedOk = $true }
        catch {
            $problems.Add("instances.json is not valid JSON ($($_.Exception.Message))")
        }
        # EVERY non-object top level is a malformed file -- arrays AND scalars. A JSON
        # 0/false/null must not slip through as "an empty registry" with no problem for
        # the caller to toast (the JS reader applies the same rule).
        if ($parsedOk) {
            if ($null -ne $parsed -and ($parsed -is [psobject]) -and -not ($parsed -is [array]) -and
                -not ($parsed -is [string]) -and -not ($parsed -is [bool]) -and -not ($parsed -is [ValueType])) {
                $doc = $parsed
            } else {
                $problems.Add("instances.json must contain a JSON object")
            }
        }
    }

    if ($null -ne $doc) {
        # A foreign schema version is REFUSED, not partially read: a later version may
        # redefine what a field MEANS, and acting on a misread entry would target the
        # wrong machine. Report it and fall back to the byte-identical default.
        # The version must be a JSON NUMBER equal to 1: a quoted "1" is a foreign
        # schema, exactly as the JS reader's `doc.version !== 1` treats it. Comparing
        # STRING REPRESENTATIONS here (what this used to do) loaded a file the
        # extension refuses outright, so the two readers could resolve different
        # targets from the same bytes -- the one thing the shared contract forbids.
        # $true is a [ValueType] that PowerShell would compare equal to 1, so the
        # numeric test is by type, not by coercion.
        $verRaw = Get-ConstructRawProperty $doc 'version'
        $verOk = $false
        if ($null -ne $verRaw -and -not ($verRaw -is [bool]) -and
            (($verRaw -is [byte]) -or ($verRaw -is [int16]) -or ($verRaw -is [int32]) -or ($verRaw -is [int64]) -or
             ($verRaw -is [single]) -or ($verRaw -is [double]) -or ($verRaw -is [decimal]))) {
            $verOk = ([double]$verRaw -eq [double]$script:ConstructSchemaVersion)
        }
        if ($null -ne $verRaw -and -not $verOk) {
            $problems.Add("instances.json has version '$verRaw'; this Construct only understands version $($script:ConstructSchemaVersion) -- ignoring the file and using the default instance (update Construct)")
            $doc = $null
        }
    }

    if ($null -ne $doc) {
        # Get-ConstructRawProperty, not PSObject.Properties['instances']: the lookup is
        # ORDINAL, so an "INSTANCES" key is absent here exactly as it is for the JS
        # reader's doc.instances (see Get-ConstructRawProperty).
        $bagRaw = Get-ConstructRawProperty $doc 'instances'
        $bag = $null
        if ($null -ne $bagRaw) {
            if ($bagRaw -is [psobject] -and -not ($bagRaw -is [array])) { $bag = $bagRaw }
            else { $problems.Add('instances.json: "instances" must be an object') }
        }
        if ($null -ne $bag) {
            foreach ($p in $bag.PSObject.Properties) {
                $name = $p.Name
                if (-not (Test-ConstructInstanceName $name)) {
                    $problems.Add("instance name '$name' is invalid ($($script:ConstructInstanceNameRule)) -- skipped")
                    continue
                }
                $entry = $p.Value
                if ($null -eq $entry -or ($entry -is [array]) -or -not ($entry -is [psobject])) {
                    $problems.Add("instance '$name' is not an object -- skipped")
                    continue
                }
                # Type strictness, matched field-for-field by the JS reader: a value of
                # the wrong JSON type is reported and the DERIVED default is used --
                # never stringified into a host name or an alias.
                foreach ($f in $script:ConstructStringFields) {
                    if (Test-ConstructBadString (Get-ConstructRawProperty $entry $f)) {
                        $problems.Add("instance '$name': '$f' must be a string -- using the derived default")
                    }
                }
                # The backend's own rules: an unusable or two-faced spelling makes the
                # entry unloadable, and is collected with the identity problems below.
                $backendBad = @(Get-ConstructBackendProblem -Raw (Get-ConstructRawProperty $entry 'backend'))
                # -cnotcontains: the enum comparison is CASE-SENSITIVE, exactly like the
                # JS reader's indexOf, so 'HYPERV-REMOTE' can never be honoured by one
                # reader and rejected by the other. An unknown backend is REPORTED BUT
                # KEPT VERBATIM (see Resolve-ConstructInstanceDefaults): coercing it would
                # hand a typo destructive local Hyper-V access.
                $rawBackend = Get-ConstructInstanceField $entry 'backend'
                if ($backendBad.Count -eq 0 -and $rawBackend -and ($script:ConstructBackends -cnotcontains $rawBackend)) {
                    $problems.Add("instance '$name' has an unknown backend '$rawBackend' -- this Construct has no driver for it, so rebuild/checkpoint actions are unavailable for it (update Construct if a newer version created it)")
                }
                $rawPort = Get-ConstructRawProperty $entry 'sshPort'
                if ($null -ne $rawPort -and $null -eq (ConvertTo-ConstructPort $rawPort)) {
                    $problems.Add("instance '$name' has an invalid sshPort -- using 22")
                }
                $rawSvc = Get-ConstructRawProperty $entry 'service'
                if ($null -ne $rawSvc -and (($rawSvc -is [array]) -or ($rawSvc -is [string]) -or ($rawSvc -is [ValueType]))) {
                    $problems.Add("instance '$name': 'service' must be an object -- ignored")
                } elseif ($null -ne $rawSvc -and (Test-ConstructBadString (Get-ConstructRawProperty $rawSvc 'url'))) {
                    $problems.Add("instance '$name': 'service.url' must be a string -- the service entry is ignored")
                } elseif ($null -ne $rawSvc) {
                    $rawAuth = Get-ConstructRawProperty $rawSvc 'auth'
                    if ($null -ne $rawAuth -and $rawAuth -cne 'token' -and $rawAuth -cne 'negotiate') {
                        $problems.Add("instance '$name': unknown service auth '$rawAuth' -- using negotiate")
                    }
                }
                # A field of the right TYPE can still be unusable (or hostile) as a host
                # name, an ssh alias, a key file name or a git ref. Such an entry is
                # skipped WHOLE: using the rest of it would dial, key or sync some other
                # machine. A LOCAL instance is held to its canonical identity on top of
                # that -- a deviating one would rebuild (and then be unable to reach) a
                # different VM than it names -- a REMOTE one to its own (an endpoint it
                # must state, and one VM name for the service and the rebuild alike) --
                # and an entry whose BACKEND itself is unusable never loads at all. The
                # JS reader skips the same entries.
                $normalized = Resolve-ConstructInstanceDefaults -Name $name -Entry $entry
                # @() around the call: an empty result unrolls to $null on the way out
                # of a function, and Set-StrictMode makes $null.Count a hard error.
                $bad = $backendBad +
                       @(Get-ConstructInstanceIdentityProblem -Instance $normalized -Entry $entry) +
                       @(Get-ConstructLocalIdentityProblem -Instance $normalized) +
                       @(Get-ConstructRemoteIdentityProblem -Instance $normalized -Entry $entry)
                if ($bad.Count -gt 0) {
                    $problems.Add("instance '$name': $($bad -join '; ') -- skipped")
                    continue
                }
                $instances[$name] = $normalized
            }
            # ...and finally the CROSS-entry rules: two instances that share an identity,
            # or one that claims the default instance's, are dropped here rather than left
            # to retarget each other's rebuilds.
            $collisions = Get-ConstructInstanceCollision -Instances $instances
            foreach ($p in @($collisions.Problems)) { $problems.Add($p) }
            foreach ($n in @($collisions.Drop)) { $instances.Remove($n) }
        }

        $dflt = Get-ConstructInstanceField $doc 'defaultInstance'
        if ($dflt) {
            if (-not (Test-ConstructInstanceName $dflt)) {
                $problems.Add("defaultInstance '$dflt' is not a valid instance name -- using '$($script:ConstructDefaultInstance)'")
            } elseif (-not $instances.ContainsKey($dflt)) {
                $problems.Add("defaultInstance '$dflt' has no entry in 'instances' -- using '$($script:ConstructDefaultInstance)'")
            } else {
                $default = $dflt
            }
        }
    }

    # The default instance is ALWAYS present, synthesized when absent -- the zero-change
    # guarantee: a registry that never had 'agent-vm' still behaves exactly like today.
    if (-not $instances.ContainsKey($script:ConstructDefaultInstance)) {
        $instances[$script:ConstructDefaultInstance] = New-ConstructDefaultInstance
    }
    if (-not $instances.ContainsKey($default)) { $default = $script:ConstructDefaultInstance }

    return [pscustomobject]@{
        Instances = $instances
        Default   = $default
        Problems  = @($problems)
    }
}

function Read-ConstructInstances {
    <#
        Load the registry from disk. NEVER throws and NEVER writes: a missing file is
        indistinguishable from "one default instance", and an unreadable/garbage file
        degrades to the same thing plus a .Problems entry.

        -Path overrides the default location (tests).
        Returns @{ Instances; Default; Problems; Path; Exists }.
    #>
    [CmdletBinding()]
    param([string]$Path)

    if (-not $Path) { $Path = Get-ConstructInstancesPath }
    $text   = ''
    $exists = $false
    $extra  = New-Object System.Collections.Generic.List[string]

    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $exists = $true
        try { $text = [System.IO.File]::ReadAllText($Path) }
        catch {
            $text = ''
            $extra.Add("instances.json could not be read ($($_.Exception.Message)) -- using the default instance")
        }
    }

    $parsed = ConvertFrom-ConstructInstancesJson -Text $text
    $problems = @($extra) + @($parsed.Problems)

    return [pscustomobject]@{
        Instances = $parsed.Instances
        Default   = $parsed.Default
        Problems  = @($problems)
        Path      = $Path
        Exists    = $exists
    }
}

function Get-ConstructInstance {
    <#
        One instance by name. An empty or unknown name falls back to the registry's
        default instance -- so a caller that has no idea about instances still gets
        exactly today's `agent-vm`. -Registry reuses an already-loaded registry.
    #>
    [CmdletBinding()]
    param(
        [string]$Name,
        $Registry,
        [string]$Path
    )

    if ($null -eq $Registry) { $Registry = Read-ConstructInstances -Path $Path }
    if ($Name -and $Registry.Instances.ContainsKey($Name)) { return $Registry.Instances[$Name] }
    if ($Registry.Instances.ContainsKey($Registry.Default)) { return $Registry.Instances[$Registry.Default] }
    return New-ConstructDefaultInstance
}

function ConvertTo-ConstructInstanceEntry {
    <# The on-disk (schema v1) form of one normalised instance. Pure. #>
    param($Instance)
    $svc = $null
    if ($Instance.Service) { $svc = [ordered]@{ url = $Instance.Service.Url; auth = $Instance.Service.Auth } }
    $entry = [ordered]@{
        backend      = $Instance.Backend
        vmName       = $Instance.VmName
        sshHost      = $Instance.VmHost
        sshPort      = [int]$Instance.SshPort
        hostAlias    = $Instance.HostAlias
        keyName      = $Instance.KeyName
        configBranch = $Instance.ConfigBranch
        scriptsDir   = $Instance.ScriptsDir
        service      = $svc
        owner        = $Instance.Owner
    }
    # Optional: written only when there is one, so a local (or pattern-less remote) entry
    # is byte-identical to what earlier builds wrote. Mirrors toFileEntry() in
    # extension/src/instances.js.
    $pub = $null
    if ($Instance.PSObject.Properties['PublicHost']) { $pub = $Instance.PublicHost }
    if ($pub) { $entry['publicHost'] = $pub }
    return $entry
}

function ConvertTo-ConstructInstanceEntryObject {
    <#
        A raw entry as the READER would see it. Callers build entries as hashtables
        (@{ backend = 'hyperv-remote'; sshHost = '...'; service = @{ url = ... } }), but
        every rule in this file reads its input through PSObject.Properties -- which a
        Hashtable does not expose its keys through, and neither does a nested one.

        Round-tripping through JSON is not a trick: it produces EXACTLY the object shape
        the entry will have when it is read back off disk, so what is validated here is
        what the parser will later accept or skip. Anything already object-shaped is
        passed through untouched.
    #>
    param($Entry)
    if ($null -eq $Entry) { return $null }
    if (-not ($Entry -is [System.Collections.IDictionary])) { return $Entry }
    return (($Entry | ConvertTo-Json -Depth 8) | ConvertFrom-Json)
}

function Get-ConstructInstanceEntryProblem {
    <#
        Every rule the READER applies to one entry, as strings (@() = it will load).
        The name rule, the backend rule, the identity FORMAT rules and -- for a local
        backend -- the canonical-identity rule, in the order the parser applies them.

        This exists so an entry is refused where it is CREATED rather than written and
        then silently dropped on the next load: an instance that vanishes from the
        picker with only a toast to explain it is the worst of both worlds. Mirrors
        validatedInstance() in extension/src/instances.js. Pure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        $Entry
    )
    $out = New-Object System.Collections.Generic.List[string]
    if (-not (Test-ConstructInstanceName $Name)) {
        $out.Add("instance name '$Name' is invalid ($($script:ConstructInstanceNameRule))")
        return @($out)
    }
    $obj = ConvertTo-ConstructInstanceEntryObject -Entry $Entry
    $normalized = Resolve-ConstructInstanceDefaults -Name $Name -Entry $obj
    $bad = @(Get-ConstructBackendProblem -Raw (Get-ConstructRawProperty $obj 'backend')) +
           @(Get-ConstructInstanceIdentityProblem -Instance $normalized -Entry $obj) +
           @(Get-ConstructLocalIdentityProblem -Instance $normalized) +
           @(Get-ConstructRemoteIdentityProblem -Instance $normalized -Entry $obj)
    foreach ($b in $bad) { if (-not $out.Contains($b)) { $out.Add($b) } }
    return @($out)
}

function Add-ConstructInstance {
    <#
        Add (or, with -Replace, overwrite) one instance in a registry object and return
        the UPDATED COPY -- the input registry is never modified, so a caller holding it
        keeps seeing the old state. The caller persists the result with
        Save-ConstructInstances.

        Refuses, rather than persisting something the reader would drop:
          * a name the reader would skip, or an entry that breaks any of its rules
            (Get-ConstructInstanceEntryProblem);
          * an existing name without -Replace;
          * the DEFAULT instance's name in any case -- 'agent-vm' is always present
            (synthesized when the file has no entry for it), so "adding" it would
            silently REPLACE the zero-change default with something else;
          * a cross-entry identity COLLISION (two instances that would name one machine,
            one key file or one ssh_config Host block).

        Mirrors addInstance()/updateInstance() in extension/src/instances.js.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Registry,
        [Parameter(Mandatory)][string]$Name,
        $Entry,
        [switch]$Replace
    )

    if ($Name -ceq $script:ConstructDefaultInstance) {
        throw "The default instance '$($script:ConstructDefaultInstance)' cannot be added or replaced: it is always present and is what every zero-change code path falls back on."
    }
    $problems = @(Get-ConstructInstanceEntryProblem -Name $Name -Entry $Entry)
    if ($problems.Count -gt 0) {
        throw "Instance '$Name': $($problems -join '; ')"
    }

    $next = Copy-ConstructInstanceRegistry -Registry $Registry
    if ($next.Instances.ContainsKey($Name) -and -not $Replace) {
        throw "Instance '$Name' already exists in the registry. Pass -Replace to overwrite it."
    }
    $obj = ConvertTo-ConstructInstanceEntryObject -Entry $Entry
    $next.Instances[$Name] = Resolve-ConstructInstanceDefaults -Name $Name -Entry $obj

    $collisions = Get-ConstructInstanceCollision -Instances $next.Instances
    $collisionProblems = @($collisions.Problems)
    if ($collisionProblems.Count -gt 0) {
        throw "Instance '$Name' cannot be added: $($collisionProblems[0])"
    }
    return $next
}

function Copy-ConstructInstanceRegistry {
    <#
        A shallow, mutable copy of a registry object, so the mutators never edit a loaded
        one in place. Every property is read defensively (this module runs under
        Set-StrictMode -Version Latest, and callers may hand in an object built by hand
        rather than by Read-ConstructInstances).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Registry)

    $instances = New-Object System.Collections.Hashtable ([System.StringComparer]::Ordinal)
    if ($Registry.PSObject.Properties['Instances'] -and $Registry.Instances) {
        foreach ($k in @($Registry.Instances.Keys)) { $instances[[string]$k] = $Registry.Instances[$k] }
    }
    if (-not $instances.ContainsKey($script:ConstructDefaultInstance)) {
        $instances[$script:ConstructDefaultInstance] = New-ConstructDefaultInstance
    }

    $default = $script:ConstructDefaultInstance
    if ($Registry.PSObject.Properties['Default'] -and $Registry.Default) { $default = [string]$Registry.Default }
    if (-not $instances.ContainsKey($default)) { $default = $script:ConstructDefaultInstance }

    $path = $null
    if ($Registry.PSObject.Properties['Path'] -and $Registry.Path) { $path = [string]$Registry.Path }
    $problems = @()
    if ($Registry.PSObject.Properties['Problems'] -and $Registry.Problems) { $problems = @($Registry.Problems) }
    $exists = $false
    if ($Registry.PSObject.Properties['Exists']) { $exists = [bool]$Registry.Exists }

    return [pscustomobject]@{
        Instances = $instances
        Default   = $default
        Problems  = $problems
        Path      = $path
        Exists    = $exists
    }
}

function Save-ConstructInstances {
    <#
        Write the registry ATOMICALLY: the full document goes to a sibling temp file
        which is then MOVED over the destination (a move is atomic on NTFS), so a crash
        or a concurrent reader can never see a half-written registry -- which would
        silently drop every window back to the default instance.

        -Registry is the object Read-ConstructInstances returns (or any object with
        .Instances / .Default). Throws on I/O failure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Registry,
        [string]$Path
    )

    if (-not $Path) {
        $Path = if ($Registry.PSObject.Properties['Path'] -and $Registry.Path) { $Registry.Path } else { Get-ConstructInstancesPath }
    }
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }

    $entries = [ordered]@{}
    foreach ($name in ($Registry.Instances.Keys | Sort-Object)) {
        $entries[$name] = ConvertTo-ConstructInstanceEntry $Registry.Instances[$name]
    }
    $doc = [ordered]@{
        version         = $script:ConstructSchemaVersion
        defaultInstance = $Registry.Default
        instances       = $entries
    }
    $json = ($doc | ConvertTo-Json -Depth 8)

    $tmp = "$Path.tmp.$PID.$([DateTime]::UtcNow.Ticks)"
    try {
        # BOM-less UTF-8 with a trailing newline: the JS reader strips a BOM anyway, but
        # writing without one keeps the two writers' output identical.
        $enc = New-Object System.Text.UTF8Encoding($false)
        [System.IO.File]::WriteAllText($tmp, ($json + "`n"), $enc)
        Move-Item -LiteralPath $tmp -Destination $Path -Force
    } catch {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
        throw
    }
    return $Path
}

# ── LOCAL instance entries + NAME-ONLY TARGETING (B11, plan section 4.12) ─────
# Two rules the installers and the four launched host scripts share, kept here for the
# same reason as everything above: the extension writes and reads the SAME file, so a
# second copy of either rule would let the two halves disagree about what a name means.
#
#   * WRITING a local entry -- Auto-Install.ps1 / Create-AgentVM.ps1 record the VM they
#     just built, with the CANONICAL identity derived from its name (nothing invented,
#     nothing hand-rolled into JSON).
#   * READING one by name -- Provision-AgentVM.ps1, Update-T3Code.ps1,
#     Set-AgentVmCheckpoints.ps1 and Get-AgentUsage.ps1 take -InstanceName instead of
#     four identity arguments and resolve the endpoint from here.

function New-ConstructLocalInstanceEntry {
    <#
        The raw (schema v1) entry for a 'hyperv-local' instance of this name: exactly the
        CANONICAL identity (Get-ConstructCanonicalIdentity), because that is the only
        identity such an entry may have -- the parser SKIPS a local entry that states
        anything else (Get-ConstructLocalIdentityProblem), and a rebuild would overwrite
        it with the derivation anyway.

        -ConfigBranch is the one field a caller may state: it is what the launched
        scripts can be TOLD (-ConfigBranch), so an explicit branch is a supported
        override. Empty leaves it to the derivation ('vm' for the default instance,
        'vm-<name>' otherwise). Pure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$ConfigBranch = ""
    )
    $c = Get-ConstructCanonicalIdentity -Name $Name
    $entry = @{
        backend   = $script:ConstructDefaultBackend
        vmName    = $c.VmName
        sshHost   = $c.VmHost
        sshPort   = [int]$c.SshPort
        hostAlias = $c.HostAlias
        keyName   = $c.KeyName
    }
    if ($ConfigBranch) { $entry['configBranch'] = $ConfigBranch }
    return $entry
}

function Save-ConstructLocalInstance {
    <#
        Record a LOCAL VM in the registry. Returns the file path that was written, or
        $null when nothing had to be written.

        THE ZERO-CHANGE RULE IS THE WHOLE POINT OF THE DEFAULT-NAME BRANCH. An install
        that only ever creates 'agent-vm' must still leave NO instances.json behind: a
        missing file IS the default instance, every reader synthesizes it, and writing
        one would turn a byte-for-byte unchanged install into one that now carries state
        it never had. So the default instance is materialised only when the file already
        exists (some other instance put it there) -- and when a SECOND VM is created the
        default is written alongside it in the same document, because
        Copy-ConstructInstanceRegistry always carries 'agent-vm' and
        Save-ConstructInstances writes every instance it holds.

        A named instance that is ALREADY registered keeps its entry (that is what makes
        reinstall/redownload of a named VM non-destructive to the registry): the identity
        is re-derived (it cannot differ -- it is derived from the same name), while an
        explicit branch and a pinned scriptsDir are preserved. An entry under this name
        that is NOT local is refused rather than silently converted: it describes a VM on
        somebody else's host, and overwriting it would point every rebuild at this PC.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$ConfigBranch = "",
        [string]$Path
    )
    if (-not (Test-ConstructInstanceName $Name)) {
        throw "Cannot record the instance '$Name': $($script:ConstructInstanceNameRule)"
    }
    $reg = if ($Path) { Read-ConstructInstances -Path $Path } else { Read-ConstructInstances }

    if ($Name -ceq $script:ConstructDefaultInstance) {
        if (-not $reg.Exists) { return $null }
        # The file exists, so this PC already manages more than the implicit default:
        # spell the default out explicitly rather than leave it synthesized-only.
        $same = Copy-ConstructInstanceRegistry -Registry $reg
        if ($Path) { return (Save-ConstructInstances -Registry $same -Path $Path) }
        return (Save-ConstructInstances -Registry $same)
    }

    $existing = $null
    if ($reg.Instances.ContainsKey($Name)) { $existing = $reg.Instances[$Name] }
    if ($existing -and -not (Test-ConstructLocalBackend ([string]$existing.Backend))) {
        throw ("The instance '$Name' is already registered as a '$($existing.Backend)' instance " +
               "(endpoint $($existing.VmHost):$($existing.SshPort)). Pick another name, or remove that entry first.")
    }
    $branch = $ConfigBranch
    if (-not $branch -and $existing) { $branch = [string]$existing.ConfigBranch }
    $entry = New-ConstructLocalInstanceEntry -Name $Name -ConfigBranch $branch
    if ($existing -and $existing.ScriptsDir) { $entry['scriptsDir'] = [string]$existing.ScriptsDir }

    $next = Add-ConstructInstance -Registry $reg -Name $Name -Entry $entry -Replace
    if ($Path) { return (Save-ConstructInstances -Registry $next -Path $Path) }
    return (Save-ConstructInstances -Registry $next)
}

# The identity a launched script can ALSO be told explicitly, mapped to the normalised
# instance property it must agree with and to the schema field a conflict message names.
# One list so the conflict check and the resolver cannot drift apart.
$script:ConstructTargetFields = @(
    [pscustomobject]@{ Param = 'VmHost';       Field = 'VmHost';       Schema = 'sshHost' },
    [pscustomobject]@{ Param = 'HostAlias';    Field = 'HostAlias';    Schema = 'hostAlias' },
    [pscustomobject]@{ Param = 'SshPort';      Field = 'SshPort';      Schema = 'sshPort' },
    [pscustomobject]@{ Param = 'LocalKeyName'; Field = 'KeyName';      Schema = 'keyName' },
    [pscustomobject]@{ Param = 'ConfigBranch'; Field = 'ConfigBranch'; Schema = 'configBranch' },
    [pscustomobject]@{ Param = 'VmName';       Field = 'VmName';       Schema = 'vmName' },
    [pscustomobject]@{ Param = 'Backend';      Field = 'Backend';      Schema = 'backend' },
    [pscustomobject]@{ Param = 'ServiceUrl';   Field = 'ServiceUrl';   Schema = 'service.url' }
)

function Get-ConstructInstanceTargetConflict {
    <#
        The explicit parameters that DISAGREE with the registry entry -- @() when they
        all agree (or none was given).

        Why this is an error and not "the explicit one wins": -InstanceName says WHICH
        MACHINE, and so does -VmHost. When the two disagree there is no reading that is
        obviously right, and both readings are destructive -- one reprovisions a VM the
        caller did not name, the other writes this instance's ssh block and key onto
        another instance's endpoint. Saying so, with both values, is the only safe answer.

        -SshPort 0 and an empty string mean "not supplied" (the launched scripts spell an
        unset identity that way), so they never conflict. The Hyper-V DISPLAY NAME is
        compared case-insensitively, exactly like Get-ConstructLocalIdentityProblem;
        everything else is a lowercase token compared verbatim. Pure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Instance,
        [hashtable]$Explicit
    )
    $out = New-Object System.Collections.Generic.List[string]
    if ($null -eq $Explicit) { return @($out) }
    foreach ($f in $script:ConstructTargetFields) {
        if (-not $Explicit.ContainsKey($f.Param)) { continue }
        $given = $Explicit[$f.Param]
        if ($null -eq $given) { continue }
        # ServiceUrl is not a top-level property of a normalised instance -- it lives
        # under .Service, and a local instance has none at all (so any explicit value
        # disagrees with it, which is exactly right: the caller is aiming a local VM at
        # somebody's host service).
        $have = if ($f.Param -eq 'ServiceUrl') {
            if ($Instance.Service) { [string]$Instance.Service.Url } else { '' }
        } else {
            $Instance.($f.Field)
        }
        if ($f.Param -eq 'SshPort') {
            $port = 0
            try { $port = [int]$given } catch { $port = 0 }
            if ($port -le 0) { continue }
            if ($port -ne [int]$have) {
                $out.Add("-SshPort $port conflicts with instance '$($Instance.Name)', whose `"sshPort`" is $([int]$have)")
            }
            continue
        }
        $g = [string]$given
        if ($g -eq '') { continue }
        $isMatch = if ($f.Param -eq 'VmName') {
            $g.ToLowerInvariant() -ceq ([string]$have).ToLowerInvariant()
        } else {
            $g -ceq [string]$have
        }
        if (-not $isMatch) {
            $out.Add("-$($f.Param) '$g' conflicts with instance '$($Instance.Name)', whose `"$($f.Schema)`" is '$have'")
        }
    }
    return @($out)
}

function Resolve-ConstructInstanceTarget {
    <#
        NAME-ONLY TARGETING: everything Provision-AgentVM.ps1, Update-T3Code.ps1,
        Set-AgentVmCheckpoints.ps1 and Get-AgentUsage.ps1 need in order to act on the
        instance called $Name, so a caller passes ONE parameter instead of four and only
        one parameter has to be probed for version skew.

        -Explicit is the hashtable of identity parameters the CALLER actually bound
        (probe-before-splat: only keys that were bound, never a script default). Two
        cases, and only two:

          * the registry KNOWS the name -- every explicit value must AGREE with the entry
            (Get-ConstructInstanceTargetConflict), and the entry answers everything else;
          * the registry does NOT know it -- an error listing the names it DOES know.
            ALWAYS, whatever else the caller passed: -InstanceName means "the instance
            called this", and there is no such instance. A BYO or manual setup is still
            served exactly as before -- by passing the explicit identity WITHOUT a name,
            which is what those parameters are for; letting an unknown name through
            because an endpoint happened to be supplied would make the same argument mean
            two different things.

        The result is FLAT (no nested Service object) so a caller can hand it out of the
        child scope this module is loaded in. Never mutates the registry.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [hashtable]$Explicit,
        $Registry,
        [string]$Path
    )
    if (-not (Test-ConstructInstanceName $Name)) {
        throw "'$Name' is not a valid instance name: $($script:ConstructInstanceNameRule)"
    }
    if ($null -eq $Registry) {
        $Registry = if ($Path) { Read-ConstructInstances -Path $Path } else { Read-ConstructInstances }
    }
    if (-not $Registry.Instances.ContainsKey($Name)) {
        $known = @($Registry.Instances.Keys | Sort-Object)
        throw ("Unknown instance '$Name'. This PC's instance registry ($($Registry.Path)) knows: " +
               ($known -join ', ') + ".")
    }
    $inst = $Registry.Instances[$Name]
    $conflicts = @(Get-ConstructInstanceTargetConflict -Instance $inst -Explicit $Explicit)
    if ($conflicts.Count -gt 0) {
        throw ("-InstanceName '$Name' was given together with an identity it does not have: " +
               ($conflicts -join '; ') + ". Pass only the name, or only the identity.")
    }

    $svcUrl = ''; $svcAuth = ''
    if ($inst.Service) { $svcUrl = [string]$inst.Service.Url; $svcAuth = [string]$inst.Service.Auth }
    return [pscustomobject]@{
        Name         = [string]$inst.Name
        Backend      = [string]$inst.Backend
        VmName       = [string]$inst.VmName
        VmHost       = [string]$inst.VmHost
        SshPort      = [int]$inst.SshPort
        HostAlias    = [string]$inst.HostAlias
        KeyName      = [string]$inst.KeyName
        ConfigBranch = [string]$inst.ConfigBranch
        ScriptsDir   = [string]$inst.ScriptsDir
        ServiceUrl   = $svcUrl
        ServiceAuth  = $svcAuth
        Owner        = [string]$inst.Owner
        IsDefault    = [bool](Test-ConstructDefaultInstance -Instance $inst)
        Path         = [string]$Registry.Path
    }
}
