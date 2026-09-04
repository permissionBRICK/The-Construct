#Requires -Version 5.1
<#
    The-Construct -- REMOVING ONE INSTANCE FROM THIS PC (B14, plan section 4.12 "Cleanup").

    Installing a VM writes client-side state in half a dozen places that have nothing to
    do with each other: an ssh_config block, a private key, known_hosts lines, VS Code's
    remote.SSH.remotePlatform map, the OpenCode desktop app's server list, the T3 Code
    certificate authority (file + Root store), the per-instance state file and the
    instance registry. Removing a VM by hand means remembering all of them, and a
    forgotten ssh alias or a stale trusted CA is exactly the kind of leftover that later
    points a tool at a machine that no longer exists.

    THE SHAPE OF THIS FILE, and why:

      * Get-ConstructInstanceRemovalPlan is PURE and decides EVERYTHING -- what will be
        removed, what is deliberately kept, whether the run is refused and whether a
        typed confirmation is required. It touches no disk, so the panel and the
        installer can both show the same list before anything happens, and the tests
        exercise the decisions without a Windows profile.
      * Every EFFECT is one small function that does one removal and reports what it did
        (Removed / Skipped / Failed + a message). Nothing throws: a cleanup that stops
        halfway through would leave a WORSE mess than the one it was asked to clear.
      * Invoke-ConstructInstanceRemoval walks the plan and calls those functions. It is
        the only part that needs a real machine.

    Dot-source it like the other libraries -- AFTER lib\AgentVm.Common.ps1, whose
    Get-ConstructT3CaFileName / Get-ConstructKnownHostsFileName are where the per-instance
    file names are derived (the provisioner reads them from the same two functions, so a
    removal can never look in a place a provision never wrote). It sets no strict mode of
    its own (see lib\AgentVm.InstanceTarget.ps1 for why that matters here).
#>

# The default instance's name. The one NAME RULE lives in lib\AgentVm.Instances.ps1;
# this literal is the same one lib\AgentVm.Common.ps1 already spells for the config
# branch and the legacy key file.
$script:ConstructCleanupDefaultInstance = 'agent-vm'

# ── Pure text/document editors ───────────────────────────────────────────────

function Remove-ConstructSshConfigBlock {
    <#
        .SYNOPSIS
        Drop the `Host <alias>` block from an ssh_config TEXT, keeping every other block
        verbatim. Returns @{ Text = <new text>; Removed = <bool> }. Pure.

        The block walk is the MIRROR IMAGE of Set-HostSshConfig in Provision-AgentVM.ps1
        (a block starts at a `Host` line and runs to the next one; only a block whose
        Host line names exactly this one alias -- the shape Construct writes -- is
        dropped), so what an install wrote is exactly what a removal takes away. A
        multi-pattern `Host a b` line is a user's own and is never touched.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()][AllowNull()][string]$Text,
        [Parameter(Mandatory)][string]$Alias
    )
    $lines = @()
    if ($Text) { $lines = ($Text -split "`r?`n") }
    $kept = New-Object System.Collections.Generic.List[string]
    $skipping = $false
    $removed = $false
    foreach ($line in $lines) {
        if ($line -match '^\s*Host\s+(.+?)\s*$') {
            $patterns = $matches[1] -split '\s+'
            if ($patterns.Count -eq 1 -and $patterns[0] -eq $Alias) {
                $skipping = $true
                $removed = $true
                continue
            }
            $skipping = $false
        }
        if (-not $skipping) { $kept.Add($line) }
    }
    $out = ($kept -join "`r`n").TrimEnd("`r", "`n")
    if ($out) { $out += "`r`n" }
    return @{ Text = $out; Removed = $removed }
}

function Remove-ConstructOpenCodeServerEntries {
    <#
        .SYNOPSIS
        Drop this instance's entries from the OpenCode desktop app's saved server LIST.
        Returns @{ List = <array>; Removed = <int> }. Pure.

        BY URL AND BY DISPLAY NAME, because both identify the same VM and either can be
        the only survivor: the URL changes when the VM's forwarded port is re-allocated
        (so an old entry keeps the display name), and the display name is what a user
        sees in the app. Entries the user added themselves are kept -- an entry matches
        only when it carries one of the URLs this install wrote, or exactly the display
        name Construct gives this instance.
    #>
    [CmdletBinding()]
    param(
        $List,
        [string[]]$Urls,
        # Every name Construct could have registered the entry under: the instance name,
        # and the ssh ALIAS a manual run would have used (Set-OpenCodeRemote takes the
        # alias, which IS the instance name for every registry-resolved instance and can
        # differ only in a hand-assembled BYO run).
        [string[]]$DisplayName
    )
    $urlSet = @{}
    foreach ($u in @($Urls)) {
        if ($u) { $urlSet[([string]$u).Trim().TrimEnd('/').ToLowerInvariant()] = $true }
    }
    $names = @()
    foreach ($n in @($DisplayName)) {
        if ($n -and ([string]$n).Trim()) { $names += ([string]$n).Trim() }
    }
    $kept = @()
    $removed = 0
    foreach ($item in @($List)) {
        if ($null -eq $item) { continue }
        $url = ""
        if (($item.PSObject.Properties.Name -contains 'http') -and $item.http -and
            ($item.http.PSObject.Properties.Name -contains 'url') -and $item.http.url) {
            $url = ([string]$item.http.url).Trim().TrimEnd('/').ToLowerInvariant()
        }
        $display = ""
        if (($item.PSObject.Properties.Name -contains 'displayName') -and $item.displayName) {
            $display = ([string]$item.displayName).Trim()
        }
        if (($url -and $urlSet.ContainsKey($url)) -or ($display -and $names -contains $display)) {
            $removed++
            continue
        }
        $kept += $item
    }
    return @{ List = $kept; Removed = $removed }
}

# `.construct-settings.json` is TWO things in one file: a handful of facts about the
# INSTALL, and the default instance's VM-scoped settings (plan section 4.12, "Per-VM state
# location" -- `agent-vm` mirrors its state into the legacy top-level keys instead of
# having a per-instance file). Removing that instance has to clear the second kind and
# keep the first; deleting `instances\agent-vm.json` would clear nothing, because it never
# existed.
#
# THE LIST BELOW IS THE INSTALL-WIDE ONE, deliberately -- it is the SMALL, CLOSED set, and
# everything else in the file belongs to the VM. A list of VM keys would have to be
# extended every time a setting is added, and the one that was forgotten would silently
# survive a removal; this way a new setting is VM-scoped by default, which is the safe
# direction. (`vmAutoCheckpointsApplied` is exactly that case: it is state, not a form
# field, so a VM-key list would not have had it.)
$script:ConstructInstallWideSettingKeys = @(
    # Which Construct is installed on this PC, and where it came from.
    'installedCommit', 'constructRepo', 'constructRef',
    # The host's git identity: the person, not the machine. It is shared by every VM the
    # installer provisions and is what the installer itself prompts for once.
    'gitUserName', 'gitEmail', 'gitCredentialStore'
)

function Remove-ConstructDefaultStoreVmKeys {
    <#
        .SYNOPSIS
        Drop the DEFAULT INSTANCE's VM-scoped keys from a parsed `.construct-settings.json`.
        Everything in $script:ConstructInstallWideSettingKeys stays; EVERYTHING ELSE goes,
        including a key this build has never heard of. Returns
        @{ Settings = <object>; Removed = <string[]> }. Pure.

        The direction matters: a key a newer Construct added is far more likely to be one
        more VM setting than one more fact about the install, and a removal that left it
        behind would hand the next VM the previous one's state. So the closed list is the
        install-wide one, and the default is "this belongs to the VM".
    #>
    [CmdletBinding()]
    param($Settings)
    $gone = @()
    if ($null -eq $Settings) { return @{ Settings = $Settings; Removed = $gone } }
    foreach ($key in @($Settings.PSObject.Properties.Name)) {
        if ($script:ConstructInstallWideSettingKeys -contains $key) { continue }
        $Settings.PSObject.Properties.Remove($key)
        $gone += $key
    }
    return @{ Settings = $Settings; Removed = $gone }
}

function Remove-ConstructRemotePlatformKey {
    <#
        .SYNOPSIS
        Drop one alias from a parsed VS Code settings object's
        `remote.SSH.remotePlatform` map. Returns @{ Settings = <object>; Removed = <bool> }.
        Pure.

        The map itself is left in place even when it ends up empty: it is a user
        setting, and an install that removes a key it added has no business removing a
        setting the user may have written by hand.
    #>
    [CmdletBinding()]
    param(
        $Settings,
        [Parameter(Mandatory)][string]$Alias
    )
    $key = "remote.SSH.remotePlatform"
    if ($null -eq $Settings) { return @{ Settings = $Settings; Removed = $false } }
    if (-not ($Settings.PSObject.Properties.Name -contains $key)) { return @{ Settings = $Settings; Removed = $false } }
    $platforms = $Settings.$key
    if ($null -eq $platforms -or -not ($platforms.PSObject.Properties.Name -contains $Alias)) {
        return @{ Settings = $Settings; Removed = $false }
    }
    $platforms.PSObject.Properties.Remove($Alias)
    return @{ Settings = $Settings; Removed = $true }
}

# ── The plan ─────────────────────────────────────────────────────────────────

function Get-ConstructInstanceRemovalPlan {
    <#
        .SYNOPSIS
        Everything "Remove instance <name>" would do on this PC, as data. Pure.

        Returns
            Ok           $false when the removal is REFUSED outright (Refusal says why)
            Refusal      the refusal text, or ""
            Name         the instance
            Backend      its backend
            DeletesVm    $true only for a hyperv-remote instance: the host service is
                         asked to DELETE the VM, which destroys its disk
            RequiresTypedConfirmation / ConfirmationOk
                         a remote removal is destructive, so the caller must have the
                         user TYPE the instance name; ConfirmationOk reports whether
                         what was typed matches
            Steps        ordered @{ Kind; Label; Target } records (see below)
            Keeps        what this action deliberately does NOT touch, as text

        REFUSALS
          * the DEFAULT instance while it is the only one -- an install with a single VM
            has no "other" instance to fall back to, and every reader treats a missing
            registry as that VM; removing it would leave the panel describing a machine
            whose client state was just deleted.

        WHAT IS NOT REMOVED
          * a hyperv-LOCAL Hyper-V VM. This action clears what the PC knows about a VM;
            deleting a local VM's disk is Auto-Install.ps1's own reinstall path, and
            Reinstall / Redownload keep working on a VM whose client state was removed
            (they write it again).
          * the shared config store and the VM's config-sync branch: they hold the
            agent configuration, which outlives one VM by design.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        # The resolved identity: Backend, VmName, VmHost, HostAlias, SshPort, KeyName,
        # PublicHost, ServiceUrl. Anything absent is simply not acted on.
        $Identity,
        # How many instances the registry holds right now (a synthesized default = 1).
        [int]$InstanceCount = 1,
        # Whether $Name is the registry's default instance.
        [switch]$IsDefault,
        # Extra OpenCode server URLs known for this VM (a host forward's, from the
        # per-instance state). The direct one is derived below.
        [string[]]$OpenCodeUrls,
        # The port OpenCode serves on (Provision-AgentVM.ps1 -OpencodePort).
        [int]$OpenCodePort = 4096,
        # What the user typed at the confirmation prompt.
        [AllowEmptyString()][AllowNull()][string]$Confirmation,
        # Host paths, so the plan is testable without a Windows profile.
        [string]$HomeDir = "",
        [string]$LocalAppData = "",
        # The installed scripts directory -- where `.construct-settings.json` lives.
        [string]$ScriptsDir = "",
        [string]$AppData = "",
        [string]$TempDir = ""
    )
    $field = {
        param($obj, $prop)
        if ($null -eq $obj) { return "" }
        if (-not ($obj.PSObject.Properties.Name -contains $prop)) { return "" }
        $v = $obj.$prop
        if ($null -eq $v) { return "" }
        return ([string]$v).Trim()
    }
    $backend = & $field $Identity 'Backend'
    if (-not $backend) { $backend = 'hyperv-local' }
    $alias = & $field $Identity 'HostAlias'
    if (-not $alias) { $alias = $Name }
    $vmHost = & $field $Identity 'VmHost'
    $keyName = & $field $Identity 'KeyName'
    $sshPort = & $field $Identity 'SshPort'
    # The registry's resolved identity carries Service.Url; a caller assembling an
    # identity by hand may spell it ServiceUrl. Accept both, invent neither.
    $serviceUrl = & $field $Identity 'ServiceUrl'
    if (-not $serviceUrl -and $null -ne $Identity -and ($Identity.PSObject.Properties.Name -contains 'Service') -and $Identity.Service) {
        $serviceUrl = & $field $Identity.Service 'Url'
    }
    $isRemote = ($backend.ToLowerInvariant() -eq 'hyperv-remote')

    $plan = [ordered]@{
        Ok                        = $true
        Refusal                   = ""
        Name                      = $Name
        Backend                   = $backend
        HostAlias                 = $alias
        DeletesVm                 = $isRemote
        RequiresTypedConfirmation = $isRemote
        ConfirmationOk            = $true
        Steps                     = @()
        Keeps                     = @()
    }
    # THE ONE REFUSAL (plan section 4.12): an install must keep an instance. With one left
    # there is nothing to fall back to -- every reader would synthesize the default again,
    # over the client state that was just deleted. Every other name goes, 'agent-vm'
    # included: its row is synthesized, so Remove-ConstructInstance records the removal
    # EXPLICITLY (a `null` entry) instead of deleting a key that would come straight back.
    if ($InstanceCount -le 1) {
        $plan.Ok = $false
        $plan.Refusal = "'$Name' is the only instance on this PC. Removing it would leave Construct describing a VM whose client state had just been deleted. Add another instance first, or uninstall Construct."
        return [pscustomobject]$plan
    }
    if ($isRemote) {
        $typed = ""
        if ($Confirmation) { $typed = $Confirmation.Trim() }
        $plan.ConfirmationOk = ($typed -ceq $Name)
        if (-not $plan.ConfirmationOk) {
            $plan.Ok = $false
            $plan.Refusal = "Removing '$Name' DELETES the VM on $(if ($serviceUrl) { $serviceUrl } else { 'its host' }), including its disk. Type the instance name exactly ('$Name') to confirm."
            return [pscustomobject]$plan
        }
    }

    $steps = New-Object System.Collections.Generic.List[object]
    $add = {
        param($kind, $label, $target)
        $steps.Add([pscustomobject]@{ Kind = $kind; Label = $label; Target = $target })
    }
    if ($isRemote -and $serviceUrl) {
        & $add 'remote-vm-delete' "Delete the VM '$Name' on $serviceUrl" $serviceUrl
    }
    if ($HomeDir) {
        $sshDir = Join-Path $HomeDir ".ssh"
        & $add 'ssh-config' "Remove the Host '$alias' block from ~/.ssh/config" (Join-Path $sshDir "config")
        & $add 'known-hosts' "Remove ~/.ssh/known_hosts entries for $alias$(if ($vmHost) { " / $vmHost" })" (Join-Path $sshDir "known_hosts")
        if ($keyName) {
            & $add 'ssh-key' "Delete the private key ~/.ssh/$keyName" (Join-Path $sshDir $keyName)
        }
    }
    if ($AppData) {
        & $add 'vscode-remote-platform' "Remove '$alias' from VS Code's remote.SSH.remotePlatform" (Join-Path $AppData "Code\User\settings.json")
        & $add 'opencode-server' "Remove the OpenCode server entry for '$Name'" (Join-Path $AppData "ai.opencode.desktop\opencode.global.dat")
    }
    if ($LocalAppData) {
        # The file names come from lib\AgentVm.Common.ps1, which is where the PROVISIONER
        # gets them: a second copy of either derivation here would let a removal look in
        # the wrong place the moment one of them changed.
        $caName = Get-ConstructT3CaFileName -InstanceName $Name
        & $add 't3-ca' "Untrust and delete this instance's T3 certificate authority ($caName)" `
            (Join-Path $LocalAppData "The-Construct\artifacts\t3code\$caName")
        if ($Name -ceq $script:ConstructCleanupDefaultInstance -and $ScriptsDir) {
            # The default instance has NO per-instance file: its VM-scoped settings are
            # mirrored into the install's `.construct-settings.json` (the default store),
            # so that is where they have to be cleared -- the install-wide keys stay.
            & $add 'default-store' "Clear this instance's settings from .construct-settings.json (the install's own keys stay)" `
                (Join-Path $ScriptsDir ".construct-settings.json")
        }
        & $add 'instance-state' "Delete the per-instance state file instances\$Name.json" `
            (Join-Path $LocalAppData "The-Construct\instances\$Name.json")
        # Where this VM answered, recorded by the provisioner for the T3 Desktop app (its
        # T3 origin and the OpenCode server url it registered). Per-instance client state,
        # so it goes with the rest of it.
        $endpointName = Get-ConstructT3EndpointFileName -InstanceName $Name
        & $add 't3-endpoint' "Delete this instance's recorded endpoints ($endpointName)" `
            (Join-Path $LocalAppData "The-Construct\artifacts\t3code\$endpointName")
    }
    if ($TempDir) {
        $khName = Get-ConstructKnownHostsFileName -HostAlias $alias
        & $add 'temp-known-hosts' "Delete the leftover provisioning file $khName" (Join-Path $TempDir $khName)
    }
    $registryLabel = "Remove '$Name' from instances.json"
    if ($Name -ceq $script:ConstructCleanupDefaultInstance) {
        # Its row is synthesized, so the removal is RECORDED rather than deleted -- said
        # out loud, because "the file will now carry an entry saying it is gone" is a
        # different thing from "a line was deleted".
        $registryLabel = "Record in instances.json that '$Name' is no longer on this PC (its row is synthesized, so the removal is written down)"
    }
    & $add 'registry-entry' $registryLabel $Name

    $keeps = New-Object System.Collections.Generic.List[string]
    if (-not $isRemote) {
        $keeps.Add("The Hyper-V VM itself is NOT deleted -- this clears what this PC knows about it. Reinstall and Redownload keep working and write the client state again.")
    }
    $keeps.Add("The shared config store and this VM's config-sync branch are kept: they hold agent configuration, not client state.")
    # The URLs Set-OpenCodeRemote could have written for this VM. The display name alone
    # is not enough: a re-provision that moved to a host forward wrote a SECOND entry
    # under a URL the alias never appears in, and an entry a user renamed keeps only its
    # URL. Both spellings the provisioner can produce are listed -- the direct one it
    # writes for a local VM, and the host-forward one from the per-instance state when
    # this PC recorded it.
    # NOT $openCodeUrls -- PowerShell variable names are case-INSENSITIVE, so that spelling
    # would be the [string[]] parameter itself (a fixed-size array) rather than a new list.
    $ocUrls = New-Object System.Collections.Generic.List[string]
    if ($vmHost) { $ocUrls.Add("http://${vmHost}:$OpenCodePort") }
    foreach ($u in @($OpenCodeUrls)) { if ($u -and -not $ocUrls.Contains([string]$u)) { $ocUrls.Add([string]$u) } }
    $plan['OpenCodeUrls'] = $ocUrls.ToArray()
    $plan.Steps = $steps.ToArray()
    $plan.Keeps = $keeps.ToArray()
    if ($sshPort) { $plan['SshPort'] = $sshPort }
    if ($vmHost) { $plan['VmHost'] = $vmHost }
    return [pscustomobject]$plan
}

# ── The effects ──────────────────────────────────────────────────────────────
# One function per removal. None of them throws: a cleanup that stops halfway leaves a
# worse mess than the one it was asked to clear, so every failure is REPORTED and the
# walk continues. Each returns @{ Kind; Status ('removed'|'skipped'|'failed'); Message }.

function New-ConstructCleanupResult {
    param([string]$Kind, [string]$Status, [string]$Message)
    return [pscustomobject]@{ Kind = $Kind; Status = $Status; Message = $Message }
}

function Remove-ConstructSshConfigEntry {
    <# Drop the Host block for $Alias from the ssh_config at $Path. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Alias)
    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            return New-ConstructCleanupResult 'ssh-config' 'skipped' "No ssh config at $Path."
        }
        $text = [System.IO.File]::ReadAllText($Path)
        $res = Remove-ConstructSshConfigBlock -Text $text -Alias $Alias
        if (-not $res.Removed) {
            return New-ConstructCleanupResult 'ssh-config' 'skipped' "No Host '$Alias' block in $Path."
        }
        [System.IO.File]::WriteAllText($Path, $res.Text)
        return New-ConstructCleanupResult 'ssh-config' 'removed' "Removed Host '$Alias' from $Path."
    } catch {
        return New-ConstructCleanupResult 'ssh-config' 'failed' "Could not edit $Path ($($_.Exception.Message))."
    }
}

function Remove-ConstructKnownHostsEntries {
    <# ssh-keygen -R for every spelling this VM was ever stored under. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Alias,
        [AllowEmptyString()][string]$VmHost = "",
        [int]$SshPort = 22
    )
    $targets = @($Alias)
    if ($VmHost) { $targets += $VmHost }
    if ($SshPort -and $SshPort -ne 22) {
        $targets += "[$Alias]:$SshPort"
        if ($VmHost) { $targets += "[$VmHost]:$SshPort" }
    }
    $prevEAP = $ErrorActionPreference
    $ErrorActionPreference = "SilentlyContinue"
    try {
        foreach ($t in $targets) { & ssh-keygen -R $t 2>$null | Out-Null }
        return New-ConstructCleanupResult 'known-hosts' 'removed' "Removed known_hosts entries for $($targets -join ', ')."
    } catch {
        return New-ConstructCleanupResult 'known-hosts' 'failed' "ssh-keygen -R failed ($($_.Exception.Message))."
    } finally {
        $ErrorActionPreference = $prevEAP
    }
}

function Remove-ConstructInstanceFile {
    <# Delete one file (and, for a private key, its .pub sibling). #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Kind, [Parameter(Mandatory)][string]$Path, [switch]$WithPublicKey)
    try {
        $gone = @()
        foreach ($p in @($Path) + $(if ($WithPublicKey) { @("$Path.pub") } else { @() })) {
            if (Test-Path -LiteralPath $p) {
                Remove-Item -LiteralPath $p -Force -ErrorAction Stop
                $gone += $p
            }
        }
        if ($gone.Count -eq 0) { return New-ConstructCleanupResult $Kind 'skipped' "Nothing at $Path." }
        return New-ConstructCleanupResult $Kind 'removed' "Deleted $($gone -join ', ')."
    } catch {
        return New-ConstructCleanupResult $Kind 'failed' "Could not delete $Path ($($_.Exception.Message))."
    }
}

function Test-ConstructCertificatePresent {
    <#
        Is this thumbprint in that Root store? The path is built as a STRING rather than
        with Join-Path, and the probe tolerates a missing provider: `Cert:` only exists on
        Windows, and a helper that throws where it does not would turn "no certificate
        store" into a cleanup failure. Pure enough to be called anywhere.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Store, [Parameter(Mandatory)][string]$Thumbprint)
    try { return [bool](Test-Path -LiteralPath (Get-ConstructCertificatePath -Store $Store -Thumbprint $Thumbprint) -ErrorAction SilentlyContinue) }
    catch { return $false }
}

function Get-ConstructCertificatePath {
    <# "Cert:\CurrentUser\Root" + a thumbprint, without asking the provider to resolve. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Store, [Parameter(Mandatory)][string]$Thumbprint)
    return ($Store.TrimEnd('\') + '\' + $Thumbprint)
}

function Remove-ConstructMachineCertificate {
    <#
        Remove ONE certificate from Cert:\LocalMachine\Root through a single elevated
        PowerShell (one UAC prompt, one command, nothing else). Returns
        @{ Removed = <bool>; Message = <string> }; never throws.

        This is the one step of a removal that cannot run as the signed-in user. It is
        kept as narrow as it can be: the thumbprint is validated as 40 hex characters
        before it is put on a command line, the elevated process removes that entry and
        exits, and a declined prompt is reported as "still trusted" rather than swallowed.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Thumbprint)
    $thumb = $Thumbprint.Trim().ToUpperInvariant()
    if ($thumb -notmatch '^[0-9A-F]{40}$') {
        return @{ Removed = $false; Message = "not a usable thumbprint" }
    }
    try {
        $cmd = "Remove-Item -LiteralPath 'Cert:\LocalMachine\Root\$thumb' -Force -ErrorAction Stop"
        $proc = Start-Process -FilePath "powershell.exe" -Verb RunAs -Wait -PassThru -WindowStyle Hidden `
            -ArgumentList @("-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", $cmd)
        if ($proc.ExitCode -eq 0) { return @{ Removed = $true; Message = "removed with elevation" } }
        return @{ Removed = $false; Message = "the elevated removal exited $($proc.ExitCode)" }
    } catch {
        # A declined UAC prompt lands here.
        return @{ Removed = $false; Message = "elevation was declined or failed ($($_.Exception.Message)); remove it from an elevated PowerShell: Remove-Item Cert:\LocalMachine\Root\$thumb" }
    }
}

function Remove-ConstructT3CaTrust {
    <#
        Untrust this instance's certificate authority and delete its file. The thumbprint
        comes from the certificate FILE itself -- the same one-file-per-instance record
        the provisioner keeps -- so nothing else has to be looked up.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    # THE LEDGER IS PROCESSED WHETHER OR NOT THE CERTIFICATE FILE IS STILL THERE. That is
    # the whole point of it: it records superseded thumbprints so old store entries can be
    # found again WITHOUT a certificate to read them from. Returning early on a missing
    # file would leave them trusted forever with the ledger sitting on disk beside nothing.
    $ledgerPresent = Test-Path -LiteralPath "$Path.orphan"
    if (-not (Test-Path -LiteralPath $Path) -and -not $ledgerPresent) {
        return New-ConstructCleanupResult 't3-ca' 'skipped' "No certificate authority file at $Path."
    }
    $thumb = ""
    if (Test-Path -LiteralPath $Path) {
        try {
            $cert = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList $Path
            $thumb = $cert.Thumbprint
        } catch {
            # Unreadable: its thumbprint cannot be looked up, so whatever it put in the
            # Root store cannot be found either. Reported as a FAILURE (which keeps the
            # registry entry) rather than deleted, because deleting it would destroy the
            # only clue.
            return New-ConstructCleanupResult 't3-ca' 'failed' "Could not read the certificate authority at $Path ($($_.Exception.Message)); it was left in place. Remove it, and any certificate it installed, by hand."
        }
    }
    # The orphan ledger the provisioner keeps beside the certificate: superseded CAs that
    # could not be untrusted then. They belong to this instance, so this removal is the
    # last chance to take them out.
    $orphanFile = "$Path.orphan"
    $orphans = @()
    if (Test-Path -LiteralPath $orphanFile) {
        foreach ($line in (Get-Content -LiteralPath $orphanFile -ErrorAction SilentlyContinue)) {
            $o = ([string]$line).Trim().ToUpperInvariant()
            if ($o -match '^[0-9A-F]{40}$' -and $orphans -notcontains $o) { $orphans += $o }
        }
    }
    $untrusted = @()
    $left = @()
    foreach ($orphan in $orphans) {
        $orphanLeft = $false
        foreach ($store in @("Cert:\CurrentUser\Root", "Cert:\LocalMachine\Root")) {
            $orphanPath = Get-ConstructCertificatePath -Store $store -Thumbprint $orphan
            try {
                if (Test-ConstructCertificatePresent -Store $store -Thumbprint $orphan) {
                    Remove-Item -LiteralPath $orphanPath -Force -ErrorAction Stop
                    $untrusted += "$store (superseded $orphan)"
                }
            } catch {
                if ($store -eq "Cert:\LocalMachine\Root") {
                    $elevatedOrphan = Remove-ConstructMachineCertificate -Thumbprint $orphan
                    if ($elevatedOrphan.Removed) { $untrusted += "$store (superseded $orphan, elevated)" }
                    else { $orphanLeft = $true; $left += "$store superseded $orphan ($($elevatedOrphan.Message))" }
                } else {
                    $orphanLeft = $true; $left += "$store superseded $orphan ($($_.Exception.Message))"
                }
            }
        }
        if (-not $orphanLeft) { $orphans = @($orphans | Where-Object { $_ -ne $orphan }) }
    }
    if ($thumb) {
        foreach ($store in @("Cert:\CurrentUser\Root", "Cert:\LocalMachine\Root")) {
            $full = Get-ConstructCertificatePath -Store $store -Thumbprint $thumb
            try {
                if (Test-ConstructCertificatePresent -Store $store -Thumbprint $thumb) {
                    Remove-Item -LiteralPath $full -Force -ErrorAction Stop
                    $untrusted += $store
                }
            } catch {
                # The MACHINE store needs Administrator, and this action deliberately runs
                # as the signed-in user (every other file it touches is in that profile).
                # So raise ONE narrowly-scoped elevated command that removes exactly this
                # thumbprint -- not an elevated re-run of the whole removal, which would
                # then edit ~\.ssh and instances.json in the administrator's profile.
                if ($store -eq "Cert:\LocalMachine\Root") {
                    $elevated = Remove-ConstructMachineCertificate -Thumbprint $thumb
                    if ($elevated.Removed) {
                        $untrusted += "$store (elevated)"
                    } else {
                        $left += "$store ($($elevated.Message))"
                    }
                } else {
                    $left += "$store ($($_.Exception.Message))"
                }
            }
        }
    }
    if ($left.Count -gt 0) {
        # Keep the ledger, narrowed to what is actually still trusted, so a retry (or the
        # next elevated reprovision) works on the real remainder.
        try {
            if ($orphans.Count -gt 0) { Set-Content -LiteralPath $orphanFile -Value ($orphans -join "`n") -Encoding ASCII }
            elseif (Test-Path -LiteralPath $orphanFile) { Remove-Item -LiteralPath $orphanFile -Force -ErrorAction SilentlyContinue }
        } catch { }
        # The FILE IS THE RECORD of which certificate this instance trusts (the
        # provisioner reads its thumbprint back on the next run, and so does a retried
        # removal). Deleting it while a store copy survives would orphan that certificate
        # for good: nothing would be left to identify it. So it stays, the step FAILS --
        # which also keeps the registry entry, so the action can be run again -- and the
        # message says exactly what is still trusted.
        $msg = "Left the superseded-certificate ledger $orphanFile in place: it is the only record of these certificate authorities."
        if (Test-Path -LiteralPath $Path) { $msg = "Left $Path (and its orphan ledger) in place: they are the only record of these certificate authorities." }
        if ($untrusted.Count -gt 0) { $msg = "Untrusted $thumb in $($untrusted -join ', '). " + $msg }
        return New-ConstructCleanupResult 't3-ca' 'failed' ($msg + " Still trusted in $($left -join '; ').")
    }
    # Everything is out of the stores, so the ledger has nothing left to record.
    if (Test-Path -LiteralPath $orphanFile) { Remove-Item -LiteralPath $orphanFile -Force -ErrorAction SilentlyContinue }
    if (-not (Test-Path -LiteralPath $Path)) {
        # Ledger-only: there was no certificate to delete, but its superseded entries are
        # out of the stores and the ledger is gone with them.
        $msg = "Cleared the superseded certificate ledger $orphanFile."
        if ($untrusted.Count -gt 0) { $msg = "Untrusted $($untrusted -join ', '). " + $msg }
        return New-ConstructCleanupResult 't3-ca' 'removed' $msg
    }
    $fileResult = Remove-ConstructInstanceFile -Kind 't3-ca' -Path $Path
    $msg = $fileResult.Message
    if ($untrusted.Count -gt 0) { $msg = "Untrusted the certificate authority $thumb in $($untrusted -join ', '). " + $msg }
    if ($fileResult.Status -eq 'failed') { return New-ConstructCleanupResult 't3-ca' 'failed' $msg }
    return New-ConstructCleanupResult 't3-ca' $fileResult.Status $msg
}

function Remove-ConstructDefaultStoreEntry {
    <# Clear the default instance's VM-scoped keys from `.construct-settings.json`. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            return New-ConstructCleanupResult 'default-store' 'skipped' "No settings file at $Path."
        }
        $raw = Get-Content -LiteralPath $Path -Raw
        if (-not $raw.Trim()) {
            return New-ConstructCleanupResult 'default-store' 'skipped' "$Path is empty."
        }
        $settings = $null
        try { $settings = $raw | ConvertFrom-Json -ErrorAction Stop } catch {
            return New-ConstructCleanupResult 'default-store' 'failed' "Could not parse $Path; it was left untouched. Remove this VM's settings by hand, or fix the file and run Remove instance again."
        }
        $res = Remove-ConstructDefaultStoreVmKeys -Settings $settings
        if (@($res.Removed).Count -eq 0) {
            return New-ConstructCleanupResult 'default-store' 'skipped' "No VM settings for this instance in $Path."
        }
        Copy-Item -LiteralPath $Path "$Path.bak" -Force -ErrorAction SilentlyContinue
        ($res.Settings | ConvertTo-Json -Depth 30) | Set-Content -Path $Path -Encoding UTF8
        return New-ConstructCleanupResult 'default-store' 'removed' "Cleared $(@($res.Removed).Count) VM setting(s) from $Path (the install's own keys stayed)."
    } catch {
        return New-ConstructCleanupResult 'default-store' 'failed' "Could not edit $Path ($($_.Exception.Message))."
    }
}

function Remove-ConstructVsCodeRemotePlatform {
    <# Drop $Alias from remote.SSH.remotePlatform in a VS Code settings.json. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Alias)
    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            return New-ConstructCleanupResult 'vscode-remote-platform' 'skipped' "No VS Code settings at $Path."
        }
        $raw = Get-Content -LiteralPath $Path -Raw
        if (-not $raw.Trim()) {
            return New-ConstructCleanupResult 'vscode-remote-platform' 'skipped' "$Path is empty."
        }
        $settings = $null
        try { $settings = $raw | ConvertFrom-Json -ErrorAction Stop } catch {
            # NOT a skip: the alias may well still be in there, and this run could not
            # look. A FAILURE keeps the registry entry, so the removal can be retried
            # after the file is fixed instead of reporting an instance as gone while its
            # Remote-SSH platform entry survives.
            return New-ConstructCleanupResult 'vscode-remote-platform' 'failed' "Could not parse $Path (comments/JSONC?); it was left untouched. Remove the `"$Alias`" key from remote.SSH.remotePlatform by hand, or fix the file and run Remove instance again."
        }
        $res = Remove-ConstructRemotePlatformKey -Settings $settings -Alias $Alias
        if (-not $res.Removed) {
            return New-ConstructCleanupResult 'vscode-remote-platform' 'skipped' "No '$Alias' entry in remote.SSH.remotePlatform."
        }
        Copy-Item -LiteralPath $Path "$Path.bak" -Force -ErrorAction SilentlyContinue
        ($res.Settings | ConvertTo-Json -Depth 30) | Set-Content -Path $Path -Encoding UTF8
        return New-ConstructCleanupResult 'vscode-remote-platform' 'removed' "Removed '$Alias' from remote.SSH.remotePlatform."
    } catch {
        return New-ConstructCleanupResult 'vscode-remote-platform' 'failed' "Could not edit $Path ($($_.Exception.Message))."
    }
}

function Remove-ConstructOpenCodeServer {
    <#
        Drop this instance's server entries from the OpenCode desktop app's state. The
        file's shape (a JSON object whose "server" value is itself a JSON string) is the
        one Set-OpenCodeRemote in Provision-AgentVM.ps1 writes; anything else is left
        strictly untouched.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [string[]]$Urls,
        [string[]]$DisplayName
    )
    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            return New-ConstructCleanupResult 'opencode-server' 'skipped' "No OpenCode config at $Path."
        }
        $top = $null
        try { $top = (Get-Content -LiteralPath $Path -Raw) | ConvertFrom-Json -ErrorAction Stop } catch {
            # Same rule as the VS Code settings above: "could not look" is a failure, not
            # "nothing to do".
            return New-ConstructCleanupResult 'opencode-server' 'failed' "Could not parse $Path; it was left untouched. Remove the '$(@($DisplayName) -join "' / '")' server in the OpenCode app, or fix the file and run Remove instance again."
        }
        if (-not ($top.PSObject.Properties.Name -contains 'server') -or -not $top.server) {
            return New-ConstructCleanupResult 'opencode-server' 'skipped' "No saved servers in $Path."
        }
        $server = $null
        $serverParsed = $true
        try { $server = $top.server | ConvertFrom-Json -ErrorAction Stop } catch { $server = $null; $serverParsed = $false }
        if (-not $serverParsed) {
            return New-ConstructCleanupResult 'opencode-server' 'failed' "The saved server list in $Path could not be parsed; it was left untouched. Remove the '$(@($DisplayName) -join "' / '")' server in the OpenCode app, or fix the file and run Remove instance again."
        }
        if ($null -eq $server -or -not ($server.PSObject.Properties.Name -contains 'list')) {
            return New-ConstructCleanupResult 'opencode-server' 'skipped' "No saved server list in $Path."
        }
        $res = Remove-ConstructOpenCodeServerEntries -List $server.list -Urls $Urls -DisplayName $DisplayName
        if ($res.Removed -eq 0) {
            return New-ConstructCleanupResult 'opencode-server' 'skipped' "No OpenCode server entry for '$(@($DisplayName) -join "' / '")'."
        }
        $server.list = @($res.List)
        Copy-Item -LiteralPath $Path "$Path.bak" -Force -ErrorAction SilentlyContinue
        $top.server = ($server | ConvertTo-Json -Depth 30 -Compress)
        $json = $top | ConvertTo-Json -Depth 30
        [System.IO.File]::WriteAllText($Path, $json, (New-Object System.Text.UTF8Encoding $false))
        return New-ConstructCleanupResult 'opencode-server' 'removed' "Removed $($res.Removed) OpenCode server entr$(if ($res.Removed -eq 1) { 'y' } else { 'ies' }) for '$(@($DisplayName) -join "' / '")'."
    } catch {
        return New-ConstructCleanupResult 'opencode-server' 'failed' "Could not edit $Path ($($_.Exception.Message))."
    }
}

function Invoke-ConstructInstanceRemoval {
    <#
        .SYNOPSIS
        Carry out a plan from Get-ConstructInstanceRemovalPlan. Returns the list of
        per-step results. Never throws.

        `-DeleteVm` is the caller's callback for the one step this library cannot do
        itself (the host driver owns the API call); it is invoked ONLY for a plan whose
        typed confirmation already passed. `-RemoveRegistryEntry` is the caller's
        registry writer, for the same reason: lib\AgentVm.Instances.ps1 must be loaded
        in its own strict scope.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Plan,
        [scriptblock]$DeleteVm,
        [scriptblock]$RemoveRegistryEntry,
        [string[]]$OpenCodeUrls
    )
    $results = New-Object System.Collections.Generic.List[object]
    if (-not $Plan.Ok) {
        $results.Add((New-ConstructCleanupResult 'refused' 'failed' $Plan.Refusal))
        return $results.ToArray()
    }
    $sshPort = 22
    if (($Plan.PSObject.Properties.Name -contains 'SshPort') -and $Plan.SshPort) { $sshPort = [int]$Plan.SshPort }
    $vmHost = ""
    if (($Plan.PSObject.Properties.Name -contains 'VmHost') -and $Plan.VmHost) { $vmHost = [string]$Plan.VmHost }
    foreach ($step in @($Plan.Steps)) {
        switch ($step.Kind) {
            'remote-vm-delete' {
                if ($null -eq $DeleteVm) {
                    $results.Add((New-ConstructCleanupResult $step.Kind 'skipped' "No host-service client was supplied; the VM on $($step.Target) was NOT deleted."))
                } else {
                    try {
                        & $DeleteVm $Plan.Name $step.Target | Out-Null
                        $results.Add((New-ConstructCleanupResult $step.Kind 'removed' "Deleted the VM '$($Plan.Name)' on $($step.Target)."))
                    } catch {
                        $results.Add((New-ConstructCleanupResult $step.Kind 'failed' "The host service refused to delete '$($Plan.Name)' ($($_.Exception.Message)). Nothing else was removed."))
                        return $results.ToArray()
                    }
                }
            }
            'ssh-config' { $results.Add((Remove-ConstructSshConfigEntry -Path $step.Target -Alias $Plan.HostAlias)) }
            'known-hosts' { $results.Add((Remove-ConstructKnownHostsEntries -Alias $Plan.HostAlias -VmHost $vmHost -SshPort $sshPort)) }
            'ssh-key' { $results.Add((Remove-ConstructInstanceFile -Kind $step.Kind -Path $step.Target -WithPublicKey)) }
            'vscode-remote-platform' { $results.Add((Remove-ConstructVsCodeRemotePlatform -Path $step.Target -Alias $Plan.HostAlias)) }
            'opencode-server' {
                # The plan states the URLs (it is what derived them); an explicit
                # -OpenCodeUrls on the walk only adds to them.
                $urls = @()
                if ($Plan.PSObject.Properties.Name -contains 'OpenCodeUrls') { $urls = @($Plan.OpenCodeUrls) }
                $urls = @($urls + @($OpenCodeUrls) | Where-Object { $_ })
                # The instance name AND the alias: they are the same for every
                # registry-resolved instance, and a BYO run registered the alias.
                $names = @($Plan.Name)
                if ($Plan.HostAlias -and $Plan.HostAlias -ne $Plan.Name) { $names += [string]$Plan.HostAlias }
                $results.Add((Remove-ConstructOpenCodeServer -Path $step.Target -Urls $urls -DisplayName $names))
            }
            't3-ca' { $results.Add((Remove-ConstructT3CaTrust -Path $step.Target)) }
            'default-store' { $results.Add((Remove-ConstructDefaultStoreEntry -Path $step.Target)) }
            'instance-state' { $results.Add((Remove-ConstructInstanceFile -Kind $step.Kind -Path $step.Target)) }
            't3-endpoint' { $results.Add((Remove-ConstructInstanceFile -Kind $step.Kind -Path $step.Target)) }
            'temp-known-hosts' { $results.Add((Remove-ConstructInstanceFile -Kind $step.Kind -Path $step.Target)) }
            'registry-entry' {
                # LAST, and only when everything before it worked. The registry entry is
                # the handle this action is reached BY: dropping it while a required
                # removal failed (an un-untrustable CA in the machine store, a settings
                # file this PowerShell cannot parse) would leave those artefacts behind
                # with no way left to retry.
                $failedSoFar = @($results | Where-Object { $_.Status -eq 'failed' })
                if ($failedSoFar.Count -gt 0) {
                    $results.Add((New-ConstructCleanupResult $step.Kind 'failed' "'$($Plan.Name)' was KEPT in instances.json because $($failedSoFar.Count) step(s) failed -- fix them and run Remove instance again."))
                } elseif ($null -eq $RemoveRegistryEntry) {
                    $results.Add((New-ConstructCleanupResult $step.Kind 'skipped' "No registry writer was supplied; '$($Plan.Name)' is still in instances.json."))
                } else {
                    try {
                        & $RemoveRegistryEntry $Plan.Name | Out-Null
                        $results.Add((New-ConstructCleanupResult $step.Kind 'removed' "Removed '$($Plan.Name)' from instances.json."))
                    } catch {
                        $results.Add((New-ConstructCleanupResult $step.Kind 'failed' "Could not update instances.json ($($_.Exception.Message))."))
                    }
                }
            }
            default { $results.Add((New-ConstructCleanupResult $step.Kind 'skipped' "Unknown step.")) }
        }
    }
    return $results.ToArray()
}
