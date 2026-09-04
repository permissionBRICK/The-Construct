#Requires -Version 5.1
<#
    The-Construct -- NAME-ONLY TARGETING ADAPTER (B11, plan section 4.12).

    lib\AgentVm.Instances.ps1 holds every registry rule, but it enables
    `Set-StrictMode -Version Latest` in whatever scope it is dot-sourced into -- and the
    scripts that need it (Provision-AgentVM.ps1, Update-T3Code.ps1,
    Set-AgentVmCheckpoints.ps1, Get-AgentUsage.ps1, Create-AgentVM.ps1, Auto-Install.ps1)
    are thousands of lines written without it. Loading it directly would change how every
    one of those lines behaves.

    This file is the containment: dot-source IT (it sets no strict mode of its own) and
    the registry module is loaded in a CHILD SCOPE per call, so the rules stay in one
    place and the strictness stays inside it.

        . (Join-Path $PSScriptRoot "lib\AgentVm.InstanceTarget.ps1")
        $t = Resolve-ConstructVmTarget -Name $InstanceName -Explicit $bound
        if (-not $PSBoundParameters.ContainsKey('VmHost')) { $VmHost = $t.VmHost }

    It is ALSO the capability marker the control panel probes for: `-InstanceName` already
    existed on Provision-AgentVM.ps1 (as the REMOTE service identity) and on
    Auto-Install.ps1 (as the remote instance name), so "the script declares the parameter"
    cannot tell name-only targeting apart from the older meaning. The PRESENCE OF THIS
    FILE in a scripts directory can (extension/src/lifecycle.js INSTANCE_TARGET_LIB).

    Functions
      Get-ConstructInstanceNamePattern             -> THE ONE name regex
      Get-ConstructInstanceNameRule                -> THE ONE human-readable statement of it
      Get-ConstructLocalVmIdentity -Name|-VmName   -> the whole derived identity of a local VM
      Resolve-ConstructVmTarget -Name [-Explicit]  -> the instance's identity, or a throw
      Register-ConstructLocalVm -Name              -> record a local VM in the registry
#>

# Captured at DOT-SOURCE time: inside a dot-sourced file $PSScriptRoot is that file's own
# directory, which is exactly where its sibling module sits. Resolving it later from a
# caller's $PSScriptRoot would find the wrong copy when a script is run from elsewhere.
$script:ConstructInstancesModule = Join-Path $PSScriptRoot 'AgentVm.Instances.ps1'

function Get-ConstructInstancesModulePath {
    <# Where the registry module is, or "" when this install does not carry it. #>
    if (Test-Path -LiteralPath $script:ConstructInstancesModule) { return $script:ConstructInstancesModule }
    return ""
}

function Resolve-ConstructVmTarget {
    <#
        The identity of the instance called -Name (see Resolve-ConstructInstanceTarget in
        lib\AgentVm.Instances.ps1 for the rules): the registry answers, explicit values
        must agree with it, an unknown name is an error listing the known ones, and the
        DEFAULT name without a registry file resolves to today's literals.

        -Explicit is the caller's BOUND identity parameters only (never script defaults).
        Throws with a message meant for a console; callers do not catch it, because
        continuing would act on a machine the user did not name.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [hashtable]$Explicit
    )
    $module = Get-ConstructInstancesModulePath
    if (-not $module) {
        throw "Cannot resolve -InstanceName '$Name': lib/AgentVm.Instances.ps1 is missing from this install. Update The Construct."
    }
    return & {
        param($modulePath, $n, $explicit)
        . $modulePath
        Resolve-ConstructInstanceTarget -Name $n -Explicit $explicit
    } $module $Name $Explicit
}

function Get-ConstructInstanceNamePattern {
    <# THE ONE instance-name regex, so no caller writes a second copy of it. #>
    $module = Get-ConstructInstancesModulePath
    if (-not $module) { throw "lib/AgentVm.Instances.ps1 is missing from this install. Update The Construct." }
    return & { param($m) . $m; $script:ConstructInstanceNameRe } $module
}

function Get-ConstructInstanceNameRule {
    <# THE ONE human-readable statement of that rule (shared with the extension's
       instances.NAME_RULE and the service's 400), so no caller writes a second copy. #>
    $module = Get-ConstructInstancesModulePath
    if (-not $module) { throw "lib/AgentVm.Instances.ps1 is missing from this install. Update The Construct." }
    return & { param($m) . $m; $script:ConstructInstanceNameRule } $module
}

function Get-ConstructLocalVmIdentity {
    <#
        EVERYTHING a local Hyper-V VM's identity is derived from its name -- the guest
        hostname / mshome address, the ssh alias, the ~\.ssh key file, the config-sync
        branch and the Hyper-V display name -- with THE ONE NAME RULE applied first. It
        exists so the installers state neither the rule nor any of the formulas a second
        time: they ask for the identity and use what comes back.

        TWO WAYS IN, one answer -- and they are NOT case-equivalent:
          -Name <instance>    the instance name itself, held to the one rule AS SUPPLIED:
                              a LOWERCASE DNS label. 'Work-VM' is refused here exactly as
                              extension/src/instances.js, Resolve-ConstructInstanceTarget
                              and the panel's "Register this VM" box refuse it -- an
                              installer that accepted it would create a VM under a
                              spelling no registry reader can name.
          -VmName <display>   a Hyper-V DISPLAY name of any case ('Work-VM'), which IS
                              lowercased to get the instance name (that is the legacy
                              parameter's documented behaviour). The display name is
                              returned AS GIVEN -- Hyper-V names are case-insensitive and
                              the default VM's historical spelling is 'Agent-VM'.

        It deliberately does NOT touch the registry: Create-AgentVM.ps1 calls it before
        it self-elevates and before the VM exists.

        -ParameterLabel is how the offending argument is named when the rule is broken,
        so one message serves '-VmName' and '-InstanceName' alike.
    #>
    [CmdletBinding()]
    param(
        [string]$Name = "",
        [string]$VmName = "",
        [string]$ParameterLabel = ""
    )
    if ($Name -and $VmName) { throw "Get-ConstructLocalVmIdentity takes -Name or -VmName, not both." }
    if (-not $Name -and -not $VmName) { throw "Get-ConstructLocalVmIdentity needs -Name or -VmName." }
    $label = $ParameterLabel
    if (-not $label) { $label = if ($VmName) { 'VmName' } else { 'InstanceName' } }
    $given = if ($VmName) { $VmName } else { $Name }

    $module = Get-ConstructInstancesModulePath
    if (-not $module) {
        throw "Cannot use -$label '$given': lib/AgentVm.Instances.ps1 is missing from this install. Update The Construct."
    }
    return & {
        param($modulePath, $given, $displayName, $label)
        . $modulePath
        # ONLY the display name is case-normalised -- see the two ways in, above.
        $n = if ($displayName) { $given.ToLowerInvariant() } else { $given }
        $example = if ($displayName) { 'Work-VM' } else { 'work-vm' }
        # The reserved prefix is reported on its own: "rename it" is a different fix from
        # "that is not a host name", and the prefix is the one a valid-looking name trips.
        if (Test-ConstructReservedInstanceName $n) {
            throw "-$label '$given' uses the reserved '$($script:ConstructReservedNamePrefix)' prefix: $($script:ConstructInstanceNameRule) Drop the prefix, e.g. '$example'."
        }
        if (-not (Test-ConstructInstanceName $n)) {
            throw "-$label '$given' is not usable as a hostname (e.g. '$example'): $($script:ConstructInstanceNameRule)"
        }
        # Resolve-ConstructInstanceDefaults, not Get-ConstructCanonicalIdentity, because it
        # is the one that also derives the config-sync branch ('vm' / 'vm-<name>') -- and
        # it is the same derivation the registry reader applies to an entry.
        $d = Resolve-ConstructInstanceDefaults -Name $n -Entry $null
        $vm = if ($displayName) { $displayName } else { [string]$d.VmName }
        [pscustomobject]@{
            Name         = $n
            VmName       = $vm
            VmHost       = [string]$d.VmHost
            HostAlias    = [string]$d.HostAlias
            KeyName      = [string]$d.KeyName
            SshPort      = [int]$d.SshPort
            ConfigBranch = [string]$d.ConfigBranch
            IsDefault    = [bool]($n -ceq $script:ConstructDefaultInstance)
        }
    } $module $given $VmName $label
}

function Register-ConstructLocalVm {
    <#
        Record a LOCAL Hyper-V VM in the instance registry (Save-ConstructLocalInstance).
        Returns the path written, or $null when nothing had to be written -- which is the
        normal answer for a default-only install: 'agent-vm' with no registry file stays
        unrecorded, because a missing file IS the default instance.

        Throws on a refusal (an invalid name, a name already held by an instance on
        somebody else's host) and on an I/O failure. Callers record a VM that has already
        been built, so they report the failure and carry on rather than failing the
        install over bookkeeping.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [string]$ConfigBranch = ""
    )
    $module = Get-ConstructInstancesModulePath
    if (-not $module) {
        throw "Cannot record the instance '$Name': lib/AgentVm.Instances.ps1 is missing from this install. Update The Construct."
    }
    return & {
        param($modulePath, $n, $branch)
        . $modulePath
        Save-ConstructLocalInstance -Name $n -ConfigBranch $branch
    } $module $Name $ConfigBranch
}
