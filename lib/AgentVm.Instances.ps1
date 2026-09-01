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
      Test-ConstructInstanceName -Name          -> [bool]
      Get-ConstructInstanceIdentityProblem -Instance [-Entry] -> [string[]] (@() = usable)
      Test-ConstructDefaultInstance -Instance   -> [bool] "behaves exactly like today"
      Save-ConstructInstances                   -> atomic write (temp file + move)

    A normalised instance object has these properties (mirroring the JS shape):
      Name, Backend, VmName, VmHost, SshPort, HostAlias, KeyName, ConfigBranch,
      ScriptsDir, Service, Owner
#>

Set-StrictMode -Version Latest

$script:ConstructSchemaVersion   = 1
$script:ConstructDefaultInstance = 'agent-vm'
$script:ConstructBackends        = @('hyperv-local', 'hyperv-remote')
# Names are used verbatim in file names, ssh aliases and git refs.
$script:ConstructInstanceNameRe  = '^[a-z0-9][a-z0-9-]{0,39}$'

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
    return [bool]([regex]::IsMatch($Name, $script:ConstructInstanceNameRe))
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
    }
}

# The string-typed fields of an instance entry (sshPort is an int and is handled on
# its own). Kept in one list so the type check and the JS reader's STRING_FIELDS stay
# in step.
$script:ConstructStringFields = @('backend', 'vmName', 'sshHost', 'vmHost', 'hostAlias', 'keyName', 'configBranch', 'scriptsDir', 'owner')

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
$script:ConstructSafeTokenRe = '\A[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z'
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
        not a path. Mirrors isKeyFileName() in extension/src/instances.js.
    #>
    param([string]$Value)
    if (-not (Test-ConstructInstanceToken $Value)) { return $false }
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
    if ($Value.EndsWith('.lock')) { return $false }
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
        foreach ($f in @('sshHost', 'vmHost')) {
            $v = Get-ConstructInstanceField $Entry $f
            if ($v -and -not (Test-ConstructInstanceHostEndpoint $v)) {
                & $add "`"$f`" '$v' is not a host name or IP address"
            }
        }
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
        & $add ("`"keyName`" '$($Instance.KeyName)' is not a usable key file name (letters, digits, '.', '_' and '-', max 64;" +
            " no trailing dot and not a reserved Windows device name)")
    }
    if (-not (Test-ConstructInstanceBranch ([string]$Instance.ConfigBranch))) {
        & $add "`"configBranch`" '$($Instance.ConfigBranch)' is not a usable config-sync branch name"
    }
    return @($out)
}

function Get-ConstructRawProperty {
    <# The raw property value, or $null when absent. No coercion of any kind. #>
    param($Object, [string]$Name)
    if ($null -eq $Object) { return $null }
    $prop = $null
    try { $prop = $Object.PSObject.Properties[$Name] } catch { return $null }
    if (-not $prop) { return $null }
    # `,` keeps an ARRAY value intact: a bare `return $prop.Value` unrolls it into the
    # pipeline, so a malformed `"configBranch": ["x"]` would arrive at the caller as the
    # plain string "x" and be accepted -- exactly the mis-normalization the type check
    # exists to catch.
    return ,$prop.Value
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
        Pure; $Entry is never modified.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        $Entry
    )

    $isDefault = ($Name -eq $script:ConstructDefaultInstance)

    # -cnotcontains: the enum comparison is CASE-SENSITIVE, exactly like the JS
    # reader's indexOf. Without the `c` prefix PowerShell would silently accept
    # "HYPERV-REMOTE" that JS rejects, and the two would target different backends.
    $backend = Get-ConstructInstanceField $Entry 'backend'
    if (-not $backend -or ($script:ConstructBackends -cnotcontains $backend)) { $backend = 'hyperv-local' }

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
        $verRaw = Get-ConstructRawProperty $doc 'version'
        if ($null -ne $verRaw -and [string]$verRaw -ne [string]$script:ConstructSchemaVersion) {
            $problems.Add("instances.json has version '$verRaw'; this Construct only understands version $($script:ConstructSchemaVersion) -- ignoring the file and using the default instance (update Construct)")
            $doc = $null
        }
    }

    if ($null -ne $doc) {
        $bagProp = $doc.PSObject.Properties['instances']
        $bag = $null
        if ($bagProp -and $null -ne $bagProp.Value) {
            if ($bagProp.Value -is [psobject] -and -not ($bagProp.Value -is [array])) { $bag = $bagProp.Value }
            else { $problems.Add('instances.json: "instances" must be an object') }
        }
        if ($null -ne $bag) {
            foreach ($p in $bag.PSObject.Properties) {
                $name = $p.Name
                if (-not (Test-ConstructInstanceName $name)) {
                    $problems.Add("instance name '$name' is invalid (allowed: a-z, 0-9 and '-', starting with a letter or digit, max 40 chars) -- skipped")
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
                $rawBackend = Get-ConstructInstanceField $entry 'backend'
                if ($rawBackend -and ($script:ConstructBackends -cnotcontains $rawBackend)) {
                    $problems.Add("instance '$name' has an unknown backend '$rawBackend' -- treated as hyperv-local")
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
                if ($rawBackend -ceq 'hyperv-remote' -and -not (Get-ConstructInstanceField $entry 'sshHost')) {
                    $problems.Add("instance '$name' is hyperv-remote but has no sshHost")
                }
                # A field of the right TYPE can still be unusable (or hostile) as a host
                # name, an ssh alias, a key file name or a git ref. Such an entry is
                # skipped WHOLE: using the rest of it would dial, key or sync some other
                # machine. The JS reader skips exactly the same entries.
                $normalized = Resolve-ConstructInstanceDefaults -Name $name -Entry $entry
                # @() around the call: an empty result unrolls to $null on the way out
                # of a function, and Set-StrictMode makes $null.Count a hard error.
                $bad = @(Get-ConstructInstanceIdentityProblem -Instance $normalized -Entry $entry)
                if ($bad.Count -gt 0) {
                    $problems.Add("instance '$name': $($bad -join '; ') -- skipped")
                    continue
                }
                $instances[$name] = $normalized
            }
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
    return [ordered]@{
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
