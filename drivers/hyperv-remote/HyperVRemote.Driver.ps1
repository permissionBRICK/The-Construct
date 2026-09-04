#Requires -Version 5.1
<#
.SYNOPSIS
    Construct hypervisor driver for a REMOTE Hyper-V host (backend "hyperv-remote").

.DESCRIPTION
    The same driver contract as drivers\hyperv-local (docs\drivers.md), implemented
    over the `constructd` host service's HTTP API (service\README.md) instead of the
    local Hyper-V cmdlets. Everything downstream -- the installer, the provisioner,
    the control panel -- keeps talking to the contract and never learns that the VM
    is on somebody else's machine.

    This file is DOT-SOURCED, not imported as a module, and is loaded through
    drivers\Load-ConstructDriver.ps1:

        . (Join-Path $PSScriptRoot "drivers\Load-ConstructDriver.ps1") `
              -Backend "hyperv-remote" -ServiceUrl $url -Auth $auth

    WHERE THE SERVICE URL COMES FROM: a driver that talks to a service has to be told
    which one. The loader applies its -ServiceUrl/-Auth by calling
    Set-ConstructDriverContext below; the caller gets both out of the instance registry
    entry (`service: { url, auth }`) or, on a fresh install, out of the enrolment flow.
    A credential PROVIDER object is passed, not a scheme name -- plan section 4.4's
    seam, so a later OIDC/Proxmox provider needs no change here.

    CAPABILITIES: no checkpoints (the service exposes none, and a "checkpoint" action
    that silently hit the LOCAL Hyper-V would be a disaster), no console, suspend YES
    (the idle policy saves VMs and any start resumes them).

    Every operation that can take minutes is a JOB on the service side: POST/DELETE
    answer 202 + jobId, and Wait-ConstructJob follows it, printing the host's progress
    lines through the host script's Write-Note so a remote create logs like a local one.
#>

# ── Progress output ──────────────────────────────────────────────────────────
# Defined ONLY if the host script hasn't already (Auto-Install.ps1 defines its own),
# exactly like the local driver, so dot-sourcing this on its own still works and no
# host script's output changes.
if (-not (Get-Command Write-Step -ErrorAction SilentlyContinue)) {
    function Write-Step($msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
}
if (-not (Get-Command Write-Ok -ErrorAction SilentlyContinue)) {
    function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }
}
if (-not (Get-Command Write-Note -ErrorAction SilentlyContinue)) {
    function Write-Note($msg) { Write-Host "    $msg" -ForegroundColor DarkGray }
}

# ── The API client ───────────────────────────────────────────────────────────
# lib\AgentVm.Remote.ps1 owns the HTTP, the credential providers, the DPAPI token
# store and the certificate pinning. Loaded here only when the caller hasn't already
# dot-sourced it (Auto-Install.ps1 has), so it is never loaded twice.
if (-not (Get-Command Invoke-ConstructApi -ErrorAction SilentlyContinue)) {
    $remoteLibDir = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $remoteLib = Join-Path (Join-Path $remoteLibDir "lib") "AgentVm.Remote.ps1"
    if (-not (Test-Path -LiteralPath $remoteLib)) {
        throw "The hyperv-remote driver needs lib/AgentVm.Remote.ps1, which is missing from this install ($remoteLib). Update The Construct."
    }
    . $remoteLib
}

# ── Driver context (which service, which credentials) ────────────────────────

$script:ConstructRemoteServiceUrl = ""
$script:ConstructRemoteAuth       = $null
$script:ConstructRemotePin        = ""
$script:ConstructRemoteStoreDir   = ""

function Set-ConstructDriverContext {
    <#
        Point this driver at a service. Called by drivers\Load-ConstructDriver.ps1 when
        it is given a -ServiceUrl, and callable directly by a caller that resolves the
        service later (the installer's enrolment flow does).

          -ServiceUrl  the base URL, in any spelling ConvertTo-ConstructServiceUrl takes
          -Auth        a provider from New-ConstructApiAuth; omitted = Negotiate
          -Pin         an explicit expected certificate fingerprint (default: the pin
                       stored for this host at enrolment)
          -StoreDir    override the credential/pin store location (tests)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ServiceUrl,
        $Auth,
        [string]$Pin,
        [string]$StoreDir
    )
    $script:ConstructRemoteServiceUrl = ConvertTo-ConstructServiceUrl -Value $ServiceUrl
    if ($Auth) { $script:ConstructRemoteAuth = $Auth }
    else { $script:ConstructRemoteAuth = New-ConstructApiAuth -Mode negotiate }
    $script:ConstructRemotePin      = [string]$Pin
    $script:ConstructRemoteStoreDir = [string]$StoreDir
    return $script:ConstructRemoteServiceUrl
}

function Get-ConstructDriverContext {
    <# The service this driver is currently pointed at (diagnostics, and the panel's
       "host service" row). Never returns the credential itself. #>
    [CmdletBinding()]
    param()
    return @{
        ServiceUrl = $script:ConstructRemoteServiceUrl
        AuthMode   = $(if ($script:ConstructRemoteAuth) { [string]$script:ConstructRemoteAuth['Mode'] } else { "" })
    }
}

function Assert-ConstructRemoteContext {
    <# Fail with an ACTIONABLE message rather than an obscure "empty URL" one: a driver
       with no service is a caller bug (or an instance registry entry with no
       `service.url`), and there is no safe default to fall back on. #>
    if (-not $script:ConstructRemoteServiceUrl) {
        throw "The hyperv-remote driver has no host service configured. Load it with -ServiceUrl (or call Set-ConstructDriverContext) -- a remote instance's registry entry must carry service.url."
    }
}

function Invoke-ConstructRemoteApi {
    <# One API call against the configured service, with the configured credentials.
       Every function below goes through here so the context is applied in ONE place. #>
    [CmdletBinding()]
    param(
        [string]$Method = 'GET',
        [Parameter(Mandatory)][string]$Path,
        $Body,
        [switch]$NoThrow
    )
    Assert-ConstructRemoteContext
    return (Invoke-ConstructApi -BaseUrl $script:ConstructRemoteServiceUrl -Method $Method -Path $Path `
                -Body $Body -Auth $script:ConstructRemoteAuth -Pin $script:ConstructRemotePin `
                -StoreDir $script:ConstructRemoteStoreDir -NoThrow:$NoThrow)
}

function Wait-ConstructRemoteJob {
    <# Follow one of the service's jobs, printing its progress lines as host output. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$JobId,
        [int]$TimeoutSeconds = 3600
    )
    Assert-ConstructRemoteContext
    return (Wait-ConstructJob -BaseUrl $script:ConstructRemoteServiceUrl -JobId $JobId `
                -Auth $script:ConstructRemoteAuth -Pin $script:ConstructRemotePin `
                -StoreDir $script:ConstructRemoteStoreDir -TimeoutSeconds $TimeoutSeconds `
                -OnProgress { param($line) Write-Note $line })
}

function Get-ConstructRemoteJobId {
    <# The jobId out of a 202 response, or a clear error. The service always answers
       {"jobId":"..."} for an accepted long operation (service/README.md). #>
    [CmdletBinding()]
    param($Response, [string]$What)
    $id = ""
    if ($Response -and $Response.PSObject.Properties['jobId']) { $id = [string]$Response.jobId }
    if (-not $id) { throw "The host service accepted '$What' but returned no job id." }
    return $id
}

# ── Capabilities ─────────────────────────────────────────────────────────────

function Get-ConstructDriverCapabilities {
    <#
        What this backend can do (docs\drivers.md):
          Checkpoints : $false -- the service exposes no checkpoint operations, and
                        Set-AgentVmCheckpoints.ps1 drives the LOCAL Hyper-V, so running
                        it for a remote instance would reconfigure (and delete
                        checkpoints on) a local VM that merely shares the name.
          Console     : 'none'  -- there is no vmconnect to a machine you aren't at.
          Suspend     : $true   -- the service's idle policy saves VMs (state 'saved')
                        and any power start resumes them.
    #>
    [CmdletBinding()]
    param()
    return @{
        Checkpoints = $false
        Console     = 'none'
        Suspend     = $true
        Backend     = 'hyperv-remote'
    }
}

# ── Prerequisites ────────────────────────────────────────────────────────────

function Test-ConstructDriverPrereqs {
    <#
        Can this machine drive the backend right now? For a remote host that is
        "the service answers and accepts our credentials" -- GET /whoami. Cheap,
        never throws, never prompts (the contract's rule), so a missing service or a
        refused credential simply reads as $false.
    #>
    [CmdletBinding()]
    param()
    if (-not $script:ConstructRemoteServiceUrl) { return $false }
    $me = Invoke-ConstructRemoteApi -Method GET -Path '/whoami' -NoThrow
    return [bool]($null -ne $me)
}

function Ensure-ConstructDriverPrereqs {
    <#
        Bring this machine to a state where the backend is usable. There is no host
        feature to enable and nothing to elevate: what can be wrong is the URL, the
        certificate pin, the credentials or the enrolment -- so this call THROWS with
        the reason instead of silently continuing.

        -Scope is accepted for contract compatibility (the local driver's Platform /
        HostAccess / All) and makes no difference here: there is no local host access
        to grant for a VM that is not on this machine.
    #>
    [CmdletBinding()]
    param(
        [ValidateSet('Platform', 'HostAccess', 'All')]
        [string]$Scope = 'Platform'
    )
    Assert-ConstructRemoteContext
    $me = Invoke-ConstructRemoteApi -Method GET -Path '/whoami'
    $known = $false
    if ($me -and $me.PSObject.Properties['known']) { $known = [bool]$me.known }
    if (-not $known) {
        $who = ""
        if ($me -and $me.PSObject.Properties['name']) { $who = [string]$me.name }
        throw "The host service at $script:ConstructRemoteServiceUrl authenticated you as '$who' but you are not enrolled on it. Ask its administrator to add you (POST /api/v1/users)."
    }
    return $me
}

function Get-ConstructRemoteIdentity {
    <# GET /whoami -- who the service thinks we are, our role and our VM quota. Used by
       the enrolment flow to confirm the credentials before anything is created. #>
    [CmdletBinding()]
    param()
    return (Invoke-ConstructRemoteApi -Method GET -Path '/whoami')
}

# ── VM lifecycle ─────────────────────────────────────────────────────────────

function Resolve-ConstructRemoteGb {
    <#
        The service's API speaks whole GB, while the contract descriptor may carry an
        exact byte count (Create-AgentVM.ps1 computes one). Bytes win, rounded to the
        NEAREST GB and never below 1 -- rounding down a 7.9 GB request to 7 would
        quietly hand the user less RAM than they asked for. Pure.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][hashtable]$Descriptor, [string]$BytesKey, [string]$GbKey, [string]$What)
    $gb = 0
    if ($Descriptor.ContainsKey($BytesKey) -and $Descriptor[$BytesKey]) {
        $gb = [int][Math]::Round([double]$Descriptor[$BytesKey] / 1GB)
    } elseif ($Descriptor.ContainsKey($GbKey) -and $Descriptor[$GbKey]) {
        $gb = [int][Math]::Round([double]$Descriptor[$GbKey])
    }
    if ($gb -lt 1) { throw "New-ConstructVm: the descriptor needs $BytesKey or $GbKey ($What)." }
    return $gb
}

function New-ConstructVm {
    <#
        Create a VM on the remote host: POST /vms, then follow the job (build the
        autoinstall ISO in WSL -> create the VM on the service's internal NAT switch ->
        wait for SSH -> detach the media -> allocate the SSH forward -> issue the
        VM-scoped token).

        RETURNS -- a documented deviation from the local driver, which returns nothing:

            @{ Name; Endpoint = @{ SshHost; SshPort }; VmToken }

        There is nowhere else to get either value. The ENDPOINT is allocated by the
        service (there is no name convention to rebuild it from), and the VM TOKEN is a
        ONE-TIME secret handed out with the first authorised retrieval of the finished
        job and never again (service/README.md). The caller passes both to
        Provision-AgentVM.ps1. The token is never printed, logged or echoed here.

        Descriptor fields used: Name, ProcessorCount, MemoryBytes|MemoryGB,
        DiskBytes|DiskGB, Nested, AutomaticCheckpoints. VhdPath/SwitchName/Generation/
        IsoPath/CheckpointType/AutomaticStart|StopAction are the HOST's business and are
        deliberately not forwarded -- the service owns its own switch, storage and ISO.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][hashtable]$Descriptor,
        [int]$TimeoutSeconds = 3600
    )
    Assert-ConstructRemoteContext

    $name = ""
    if ($Descriptor.ContainsKey('Name')) { $name = [string]$Descriptor['Name'] }
    if ([string]::IsNullOrWhiteSpace($name)) { throw "New-ConstructVm: the descriptor needs a Name." }

    $cpu = 0
    if ($Descriptor.ContainsKey('ProcessorCount')) { $cpu = [int]$Descriptor['ProcessorCount'] }
    if ($cpu -lt 1) { throw "New-ConstructVm: the descriptor needs a ProcessorCount of at least 1." }

    $ramGb  = Resolve-ConstructRemoteGb -Descriptor $Descriptor -BytesKey 'MemoryBytes' -GbKey 'MemoryGB' -What 'VM memory'
    $diskGb = Resolve-ConstructRemoteGb -Descriptor $Descriptor -BytesKey 'DiskBytes'   -GbKey 'DiskGB'   -What 'virtual disk size'

    $opts = @{}
    if ($Descriptor.ContainsKey('Nested')) { $opts['nested'] = [bool]$Descriptor['Nested'] }
    if ($Descriptor.ContainsKey('AutomaticCheckpoints')) { $opts['automaticCheckpoints'] = [bool]$Descriptor['AutomaticCheckpoints'] }

    $body = @{ name = $name; cpu = $cpu; ramGb = $ramGb; diskGb = $diskGb }
    if ($opts.Count -gt 0) { $body['opts'] = $opts }

    Write-Step "Asking the host service to create '$name' ($cpu vCPU, $ramGb GB RAM, $diskGb GB disk)"
    $accepted = Invoke-ConstructRemoteApi -Method POST -Path '/vms' -Body $body
    $jobId = Get-ConstructRemoteJobId -Response $accepted -What "create $name"
    Write-Note "job $jobId accepted"

    $result = Wait-ConstructRemoteJob -JobId $jobId -TimeoutSeconds $TimeoutSeconds

    $ep = $null
    if ($result -and $result.PSObject.Properties['endpoint']) { $ep = $result.endpoint }
    if (-not $ep) { throw "The host service created '$name' but reported no endpoint to reach it on." }
    $token = ""
    if ($result.PSObject.Properties['vmToken'] -and $result.vmToken) { $token = [string]$result.vmToken }

    # Read through the ONE endpoint reading (lib/AgentVm.Remote.ps1), so a creation result
    # and GET /vms/{name}/endpoint hand back the same shape -- publicHost included.
    $endpoint = ConvertFrom-ConstructVmEndpoint -Response $ep
    if ($null -eq $endpoint) { throw "The host service created '$name' but reported no endpoint to reach it on." }

    Write-Ok "VM created on the host service"
    return @{
        Name     = $name
        Endpoint = $endpoint
        VmToken  = $token
    }
}

function Remove-ConstructVm {
    <#
        Delete the VM on the remote host, including its disks and its port forwards:
        DELETE /vms/{name} -> job. Accepting it FENCES the VM immediately (its scoped
        token is revoked and every mutation answers 409), so nothing can be attached
        behind the job that is tearing it down.

        No-op when the VM does not exist, matching the local driver: a 404 here means
        the desired end state already holds.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$TimeoutSeconds = 1800
    )
    Assert-ConstructRemoteContext

    Write-Step "Asking the host service to remove '$Name'"
    $accepted = Invoke-ConstructRemoteApi -Method DELETE -Path "/vms/$Name" -NoThrow
    if ($null -eq $accepted) {
        if ((Get-ConstructApiLastStatus) -eq 404) {
            Write-Note "No VM named '$Name' on the host service -- nothing to remove."
            return
        }
        throw "Could not remove '$Name' on the host service: $(Get-ConstructApiLastError)"
    }
    $jobId = Get-ConstructRemoteJobId -Response $accepted -What "remove $Name"
    Write-Note "job $jobId accepted"
    [void](Wait-ConstructRemoteJob -JobId $jobId -TimeoutSeconds $TimeoutSeconds)
    Write-Ok "VM removed on the host service"
}

function Invoke-ConstructRemotePower {
    <# POST /vms/{name}/power -- synchronous on the service side; returns the new state. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][ValidateSet('start', 'stop', 'save')][string]$Action
    )
    $resp = Invoke-ConstructRemoteApi -Method POST -Path "/vms/$Name/power" -Body @{ action = $Action }
    if ($resp -and $resp.PSObject.Properties['state']) { return ([string]$resp.state).ToLowerInvariant() }
    return 'unknown'
}

function Start-ConstructVm {
    <# Power the VM on. Also RESUMES a saved VM -- which is what the idle policy's
       'save' action relies on. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    [void](Invoke-ConstructRemotePower -Name $Name -Action 'start')
}

function Stop-ConstructVm {
    <#
        Shut the VM down. The service exposes ONE stop action, so -TurnOff and -Force
        are accepted for contract compatibility and make no difference: how forcefully
        a shutdown is applied is the host's policy, not a remote client's to override.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [switch]$TurnOff,
        [switch]$Force
    )
    [void](Invoke-ConstructRemotePower -Name $Name -Action 'stop')
}

function Save-ConstructVm {
    <# Suspend to disk (capability: Suspend). Start-ConstructVm resumes it. #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)
    [void](Invoke-ConstructRemotePower -Name $Name -Action 'save')
}

function Get-ConstructVmState {
    <#
        The VM's power state as one of the contract's values:

            running | off | paused | saved | absent | unknown

        'absent' comes from a 404 and NOTHING else. That discipline matters more here
        than locally: an unreachable service, a 401 or a 403 must read as "can't tell",
        never as "not installed" -- the panel offers to CREATE a VM for 'absent'.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $resp = Invoke-ConstructRemoteApi -Method GET -Path "/vms/$Name/state" -NoThrow
    if ($null -eq $resp) {
        if ((Get-ConstructApiLastStatus) -eq 404) { return 'absent' }
        return 'unknown'
    }
    $state = ""
    if ($resp.PSObject.Properties['state']) { $state = ([string]$resp.state).Trim().ToLowerInvariant() }
    switch ($state) {
        'running' { return 'running' }
        'off'     { return 'off' }
        'paused'  { return 'paused' }
        'saved'   { return 'saved' }
        'absent'  { return 'absent' }
        default   { return 'unknown' }
    }
}

function Test-ConstructVmPresent {
    <#
        Does this VM exist on the host service? THREE-VALUED, like the local driver:
            $true   200 -- the service has a record for it
            $false  404 -- the service positively says there is no such VM
            $null   anything else (unreachable, 401/403) -- can't tell
        A VM that belongs to somebody else answers 403, which is deliberately $null and
        not $false: it exists, we just may not see it.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $resp = Invoke-ConstructRemoteApi -Method GET -Path "/vms/$Name" -NoThrow
    if ($null -ne $resp) { return $true }
    if ((Get-ConstructApiLastStatus) -eq 404) { return $false }
    return $null
}

function Get-ConstructVmEndpoint {
    <#
        Where a client dials this VM: @{ SshHost = <the service's PublicHost>;
        SshPort = <the forward allocated for this VM>;
        PublicHost = <the name this VM's WEB endpoints live under> }.

        PublicHost is the service's rendered Constructd:PublicHostPattern (plan section
        4.12) and equals SshHost when the host runs no pattern; SSH is dialled on SshHost
        either way. The shape is read by ConvertFrom-ConstructVmEndpoint in
        lib/AgentVm.Remote.ps1 -- one reading, shared with the creation job's result.

        THE key abstraction of the contract -- and the reason the whole remote backend
        needs no changes downstream: the provisioner, ssh config, VS Code Remote-SSH and
        the probes all dial an endpoint instead of rebuilding "<name>.mshome.net".

        A 409 means the VM has no forward yet (it sits on the service's internal NAT
        switch and is still being created); that is reported as such rather than as a
        missing VM.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name)

    $resp = Invoke-ConstructRemoteApi -Method GET -Path "/vms/$Name/endpoint" -NoThrow
    if ($null -eq $resp) {
        $status = Get-ConstructApiLastStatus
        if ($status -eq 409) {
            throw "The host service has no client-reachable address for '$Name' yet (its SSH forward has not been allocated). If the VM was just requested, wait for the creation job to finish."
        }
        throw "Could not read the endpoint of '$Name' from the host service: $(Get-ConstructApiLastError)"
    }
    $endpoint = ConvertFrom-ConstructVmEndpoint -Response $resp
    if ($null -eq $endpoint) {
        throw "The host service returned an endpoint for '$Name' without an ssh host, so there is nothing to dial."
    }
    return $endpoint
}

function Test-ConstructVmSshPort {
    <#
        Is the endpoint's SSH port open right now? A raw TcpClient probe, for the same
        reason the local driver uses one: Test-NetConnection prints name-resolution and
        ping banners that no preference fully silences. Any failure is "not open".

        Local-only in the contract's sense (docs\drivers.md), but each driver carries
        its own copy because exactly one driver is loaded at a time.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SshHost,
        [int]$SshPort = 22,
        [int]$TimeoutMs = 3000
    )
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $iar = $client.BeginConnect($SshHost, $SshPort, $null, $null)
        if ($iar.AsyncWaitHandle.WaitOne($TimeoutMs)) {
            $client.EndConnect($iar)
            return $true
        }
        return $false
    } catch {
        return $false
    } finally {
        $client.Close()
    }
}

function Wait-ConstructVmReachable {
    <#
        Wait until the VM answers on the endpoint the service allocated, then settle
        briefly. Returns $true if the port opened, $false if the wait expired -- an
        expired wait is NON-FATAL by contract (Provision-AgentVM.ps1 has its own
        reachability wait and retries).

        The service's creation job already waits for SSH inside the host's network, so
        this is the CLIENT-side half: it proves the port FORWARD works from here, which
        the host cannot test for us.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$TimeoutSeconds = 600,
        [int]$PollIntervalSeconds = 15,
        [int]$SettleSeconds = 5,
        [int]$ProbeTimeoutMs = 3000
    )

    $ep      = Get-ConstructVmEndpoint -Name $Name
    $sshHost = [string]$ep.SshHost
    $sshPort = [int]$ep.SshPort
    $reached = $true
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)

    Write-Host "    Waiting for SSH port to open..." -ForegroundColor DarkGray
    while (-not (Test-ConstructVmSshPort -SshHost $sshHost -SshPort $sshPort -TimeoutMs $ProbeTimeoutMs)) {
        if ((Get-Date) -gt $deadline) {
            Write-Note "SSH still not reachable at $sshHost`:$sshPort after $([int]([math]::Round($TimeoutSeconds / 60))) min; handing off to provisioning anyway."
            $reached = $false
            break
        }
        Write-Host "    Not reachable yet -- retrying in $PollIntervalSeconds seconds..." -ForegroundColor DarkGray
        Start-Sleep -Seconds $PollIntervalSeconds
    }

    if ($SettleSeconds -gt 0) { Start-Sleep -Seconds $SettleSeconds }
    return $reached
}

function Detach-ConstructInstallMedia {
    <#
        NO-OP. The service's creation job detaches the install media on the host before
        it reports success (service/README.md, "the hybrid split"), so there is nothing
        left for the client to do -- and nothing it COULD do: the media lives on a
        machine this script cannot touch.

        The function exists because it is part of the contract and callers invoke it
        unconditionally after a create. ("Detach" is not an approved PowerShell verb; it
        is kept because it IS the contract name, and the driver is dot-sourced rather
        than exported, so no verb warning is produced.)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Name,
        [int]$ControllerNumber = 0,
        [int]$ControllerLocation = 1
    )
    Write-Note "Install media is detached by the host service; nothing to do here."
}
