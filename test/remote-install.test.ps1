#Requires -Version 5.1
<#
    Unit tests for the REMOTE INSTALL FLOW's decision points (batch B7). Run:

        pwsh -NoProfile -File test/remote-install.test.ps1

    Two things are pinned here, and they are the two that can go wrong quietly:

      1. THE ZERO-CHANGE BAR. Auto-Install.ps1 gained exactly one new question, and an
         existing install must never see it. Resolve-ConstructInstallMode is the whole
         gate, so every way of being "not a fresh machine" is asserted individually --
         including the one that has no Hyper-V rights, since the mode is resolved BEFORE
         the elevation prompt.
      2. THE REMOTE SPLAT + THE REGISTRY WRITE. New-ConstructRemoteProvisionArgs is what
         aims Provision-AgentVM.ps1 at somebody else's machine; get an argument wrong and
         it provisions the LOCAL VM instead. Add-ConstructInstance is what records the
         result, and an entry the reader would refuse must be refused where it is
         created, not dropped on the next load.

    Self-contained: no Hyper-V, no network, no service. The installer's functions are
    extracted from its AST (exactly as test/instance-identity.test.ps1 does with
    Get-ExternalEnvSuffix) so they can be exercised without running a 3000-line script,
    and everything they call is stubbed here.
#>
$ErrorActionPreference = "Stop"

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}
function Test-Throws([scriptblock]$Script) {
    try { & $Script | Out-Null; return $false } catch { return $true }
}
function Get-ThrowMessage([scriptblock]$Script) {
    try { & $Script | Out-Null; return "" } catch { return [string]$_.Exception.Message }
}

$tmpRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("construct-remote-install-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmpRoot -Force | Out-Null

try {

# ── (a) Lift the installer's decision functions out of its AST ──────────────
Write-Host ""
Write-Host "=== Extracting the flow's functions ===" -ForegroundColor Cyan
$autoInstall = Join-Path $repoRoot "Auto-Install.ps1"
$errors = $null
$autoAst = [System.Management.Automation.Language.Parser]::ParseFile($autoInstall, [ref]$null, [ref]$errors)
ok "parse: Auto-Install.ps1 has zero errors" ($errors.Count -eq 0)
foreach ($e in $errors) { Write-Host "    ERROR: $($e.Message) (line $($e.Extent.StartLineNumber))" -ForegroundColor Red }
if ($errors.Count -gt 0) { exit 1 }

function Get-InstallerFunctionText([string]$Name) {
    $fn = $autoAst.FindAll({
        param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name
    }, $true) | Select-Object -First 1
    if (-not $fn) { return "" }
    return [string]$fn.Extent.Text
}
# Invoke-Expression HERE, at script scope, so the functions land where the scenarios
# below (and each other) can see them -- doing it inside a helper would define them in
# that helper's scope and nowhere else.
foreach ($fname in @('Test-ConstructPriorLocalInstall', 'Resolve-ConstructInstallMode',
                     'Test-ConstructRemoteInstanceName', 'Assert-ConstructRemoteRegistrySpace',
                     'New-ConstructRemoteProvisionArgs')) {
    $fnText = Get-InstallerFunctionText $fname
    ok "extract: Auto-Install.ps1 defines $fname" ($fnText -ne "")
    if ($fnText) { Invoke-Expression $fnText }
}

# ── (b) "Has this PC installed a Construct VM before?" ──────────────────────
# The permission-free half of the freshness check: provisioning leaves the VM's private
# key in the USER's profile, which a non-elevated run can always read.
Write-Host ""
Write-Host "=== Prior-install detection (no Hyper-V rights needed) ===" -ForegroundColor Cyan
$profileDir = Join-Path $tmpRoot "profile"
New-Item -ItemType Directory -Path (Join-Path $profileDir ".ssh") -Force | Out-Null
$savedUserProfile = $env:USERPROFILE
$env:USERPROFILE = $profileDir
try {
    ok "prior: an empty profile is not a prior install" ((Test-ConstructPriorLocalInstall -VmName "Agent-VM") -eq $false)
    Set-Content -LiteralPath (Join-Path $profileDir ".ssh/agent_vm_ed25519") -Value "key" -NoNewline
    ok "prior: the DEFAULT VM's key is a prior install" ((Test-ConstructPriorLocalInstall -VmName "Agent-VM") -eq $true)
    ok "prior: ...and the VM name is matched case-insensitively" ((Test-ConstructPriorLocalInstall -VmName "agent-vm") -eq $true)
    # A named VM has an instance-scoped key, so the default key must not answer for it.
    ok "prior: another VM's key is not this one's" ((Test-ConstructPriorLocalInstall -VmName "work-vm") -eq $false)
    Set-Content -LiteralPath (Join-Path $profileDir ".ssh/construct_work-vm_ed25519") -Value "key" -NoNewline
    ok "prior: a named VM is found under construct_<name>_ed25519" ((Test-ConstructPriorLocalInstall -VmName "work-vm") -eq $true)
    $env:USERPROFILE = Join-Path $tmpRoot "no-such-profile"
    ok "prior: a profile that does not exist is `$false, not an error" ((Test-ConstructPriorLocalInstall -VmName "Agent-VM") -eq $false)
} finally {
    $env:USERPROFILE = $savedUserProfile
}

# ── (c) The mode gate ───────────────────────────────────────────────────────
# Everything Resolve-ConstructInstallMode reads lives in the caller's scope, so the test
# sets up one scenario at a time and calls it. The stubs record whether the user was
# asked ANYTHING -- which is the actual zero-change assertion.
Write-Host ""
Write-Host "=== Install-mode gate (the zero-change bar) ===" -ForegroundColor Cyan

$script:menuCalls = 0
$script:menuAnswer = 0
function Show-Menu { param($Title, $Options, $Default) $script:menuCalls++; return $script:menuAnswer }
function Show-TuiScreen { param($Title, $Body) }
$script:vmPresent = $null      # $null = "can't tell" (no Hyper-V rights), $true/$false = a real answer
$script:prereqs = $true
function Test-ConstructDriverPrereqs { return $script:prereqs }
function Test-ConstructVmPresent { param([string]$Name) return $script:vmPresent }

# The installer's own parameters, as the resolver reads them.
$Backend = "hyperv-local"; $ServiceUrl = ""; $InstanceName = ""
$VmName = "Agent-VM"; $VmHost = "agent-vm"
$SkipCreateVm = $false; $FromPanel = $false

function New-Snapshot([hashtable]$Entries, [bool]$Exists) {
    $e = @{}
    foreach ($k in $Entries.Keys) { $e[$k] = $Entries[$k] }
    if (-not $e.ContainsKey('agent-vm')) {
        $e['agent-vm'] = [pscustomobject]@{ Name = 'agent-vm'; Backend = 'hyperv-local'; VmHost = 'agent-vm.mshome.net'; SshPort = 22 }
    }
    return [pscustomobject]@{ Path = "X"; Exists = $Exists; Default = 'agent-vm'; Problems = @(); Entries = $e }
}
# A machine with nothing on it: no registry file, no VM, no key in the profile.
$freshSnapshot = New-Snapshot @{} $false

# Resolve once per scenario -- the real thing caches in $script:ConstructInstallMode.
function Invoke-Mode([hashtable]$Bound, $Snapshot) {
    $script:ConstructInstallMode = ""
    $script:ConstructModePrompted = $false
    $script:menuCalls = 0
    return (Resolve-ConstructInstallMode -Bound $Bound -Snapshot $Snapshot)
}

$env:USERPROFILE = Join-Path $tmpRoot "no-such-profile"   # "fresh" unless a case says otherwise
try {
    # --- explicit parameters always win, and never ask ---
    $Backend = "hyperv-remote"
    ok "mode: -Backend hyperv-remote is taken as given" ((Invoke-Mode @{ Backend = $true } $freshSnapshot) -eq 'hyperv-remote')
    ok "mode: ...without asking anything" ($script:menuCalls -eq 0)
    $Backend = "hyperv-local"
    ok "mode: -Backend hyperv-local is taken as given" ((Invoke-Mode @{ Backend = $true } $freshSnapshot) -eq 'hyperv-local')
    ok "mode: ...and suppresses the prompt (this is what the elevated child is told)" ($script:menuCalls -eq 0)

    $ServiceUrl = "https://buildbox:7462"
    ok "mode: -ServiceUrl alone means remote" ((Invoke-Mode @{ ServiceUrl = $true } $freshSnapshot) -eq 'hyperv-remote')
    ok "mode: ...without asking" ($script:menuCalls -eq 0)
    $ServiceUrl = ""

    # --- an instance NAMED in the registry decides by its own backend ---
    $registered = New-Snapshot @{
        'work-vm' = [pscustomobject]@{ Name = 'work-vm'; Backend = 'hyperv-remote'; VmHost = 'buildbox'; SshPort = 2201 }
        'lab-vm'  = [pscustomobject]@{ Name = 'lab-vm';  Backend = 'hyperv-local';  VmHost = 'lab-vm.mshome.net'; SshPort = 22 }
    } $true
    $InstanceName = "work-vm"
    ok "mode: a registered REMOTE instance resolves to remote" ((Invoke-Mode @{ InstanceName = $true } $registered) -eq 'hyperv-remote')
    $InstanceName = "lab-vm"
    ok "mode: a registered LOCAL instance resolves to local" ((Invoke-Mode @{ InstanceName = $true } $registered) -eq 'hyperv-local')
    $InstanceName = "never-seen"
    ok "mode: an UNKNOWN -InstanceName does not ask (it is a local run's error to report)" `
        ((Invoke-Mode @{ InstanceName = $true } $registered) -eq 'hyperv-local' -and $script:menuCalls -eq 0)
    $InstanceName = ""

    # --- the fresh machine: the ONE case that asks ---
    $script:vmPresent = $false
    $script:menuAnswer = 0
    ok "mode: a genuinely fresh machine asks" ((Invoke-Mode @{} $freshSnapshot) -eq 'hyperv-local' -and $script:menuCalls -eq 1)
    ok "mode: ...and answering 'Local Hyper-V' keeps today's path" ($script:ConstructInstallMode -eq 'hyperv-local')
    ok "mode: ...and the run records that it asked (the elevated child is told)" ($script:ConstructModePrompted -eq $true)
    $script:menuAnswer = 1
    ok "mode: ...answering 'Remote host' selects the remote flow" ((Invoke-Mode @{} $freshSnapshot) -eq 'hyperv-remote')
    $script:menuAnswer = 0

    # --- every way of NOT being a fresh machine ---
    foreach ($p in @('Action', 'VmName', 'VmHost', 'InstanceName')) {
        [void](Invoke-Mode @{ $p = $true } $freshSnapshot)
        ok "mode: -$p suppresses the prompt" ($script:menuCalls -eq 0)
    }
    $FromPanel = $true
    [void](Invoke-Mode @{} $freshSnapshot)
    ok "mode: -FromPanel never asks (the panel has no console to answer in)" ($script:menuCalls -eq 0)
    $FromPanel = $false
    $SkipCreateVm = $true
    [void](Invoke-Mode @{} $freshSnapshot)
    ok "mode: -SkipCreateVm (ISO only) never asks" ($script:menuCalls -eq 0)
    $SkipCreateVm = $false

    [void](Invoke-Mode @{} (New-Snapshot @{} $true))
    ok "mode: an EXISTING instances.json suppresses the prompt" ($script:menuCalls -eq 0)
    [void](Invoke-Mode @{} $registered)
    ok "mode: a registry naming other VMs suppresses the prompt" ($script:menuCalls -eq 0)

    $script:vmPresent = $true
    [void](Invoke-Mode @{} $freshSnapshot)
    ok "mode: an existing LOCAL VM suppresses the prompt" ($script:menuCalls -eq 0)

    # THE REGRESSION THIS FILE EXISTS FOR: the mode is resolved before elevation, so
    # Get-VM can answer "can't tell" on a machine that HAS a VM. The profile key is the
    # permission-free second opinion; without it, an existing install would be asked a
    # brand-new question.
    $script:vmPresent = $null
    [void](Invoke-Mode @{} $freshSnapshot)
    ok "mode: an unreadable Hyper-V alone does NOT suppress the prompt" ($script:menuCalls -eq 1)
    $env:USERPROFILE = $profileDir     # ...has agent_vm_ed25519 from section (b)
    [void](Invoke-Mode @{} $freshSnapshot)
    ok "mode: an unreadable Hyper-V + this user's VM key = already installed, no prompt" ($script:menuCalls -eq 0)
    $script:prereqs = $false
    [void](Invoke-Mode @{} $freshSnapshot)
    ok "mode: ...and the same holds with no Hyper-V cmdlets at all" ($script:menuCalls -eq 0)
    $script:prereqs = $true
    $env:USERPROFILE = Join-Path $tmpRoot "no-such-profile"
    $script:vmPresent = $false

    # --- a degraded install has no TUI to ask with ---
    Remove-Item function:Show-Menu -Force
    ok "mode: with no TUI helper it falls back to local rather than to a broken prompt" `
        ((Invoke-Mode @{} $freshSnapshot) -eq 'hyperv-local')
    function Show-Menu { param($Title, $Options, $Default) $script:menuCalls++; return $script:menuAnswer }
} finally {
    $env:USERPROFILE = $savedUserProfile
}

# ── (d) The instance-name rule ──────────────────────────────────────────────
Write-Host ""
Write-Host "=== Instance name validation ===" -ForegroundColor Cyan
ok "name: a plain label is valid" (Test-ConstructRemoteInstanceName "work-vm")
# 'agent-vm' is the default instance: always present, and what every zero-change path
# falls back on -- so Add-ConstructInstance refuses to replace it. Refusing it HERE is
# what keeps that refusal from arriving after a VM has been built on a shared host.
ok "name: the reserved default instance name is refused" (-not (Test-ConstructRemoteInstanceName "agent-vm"))
ok "name: ...and the refusal happens before anything is created" (
    # The validator gates the name loop, which sits above the POST /vms in the flow; the
    # two guards after it (an existing entry, and the registry collision) also precede it.
    $autoAst.Extent.Text.IndexOf('while (-not (Test-ConstructRemoteInstanceName $instName))') -lt
    $autoAst.Extent.Text.IndexOf('$created = New-ConstructVm'))
ok "name: digits and a leading digit are valid" (Test-ConstructRemoteInstanceName "9vm")
ok "name: 40 characters is the limit" (Test-ConstructRemoteInstanceName ("a" * 40))
ok "name: 41 is too long" (-not (Test-ConstructRemoteInstanceName ("a" * 41)))
ok "name: empty is invalid" (-not (Test-ConstructRemoteInstanceName ""))
ok "name: uppercase is invalid (the SSH alias and the branch are lowercase)" (-not (Test-ConstructRemoteInstanceName "Work-VM"))
ok "name: a leading hyphen is invalid" (-not (Test-ConstructRemoteInstanceName "-vm"))
ok "name: a dot is invalid (it is a DNS LABEL, not a name)" (-not (Test-ConstructRemoteInstanceName "work.vm"))
ok "name: a path separator is invalid (it becomes a key FILE name)" (-not (Test-ConstructRemoteInstanceName "a/b"))
ok "name: whitespace is invalid" (-not (Test-ConstructRemoteInstanceName "work vm"))

# ── (e) The registry-space guard ────────────────────────────────────────────
# The reader treats sshHost as a unique identity field, so a second VM on one host
# service would make BOTH entries unloadable. That has to be found before a VM is
# created, not after.
Write-Host ""
Write-Host "=== Registry-space guard ===" -ForegroundColor Cyan
$occupied = New-Snapshot @{
    'work-vm' = [pscustomobject]@{ Name = 'work-vm'; Backend = 'hyperv-remote'; VmHost = 'buildbox.example.local'; SshPort = 2201 }
} $true
ok "space: a different host is fine" (-not (Test-Throws { Assert-ConstructRemoteRegistrySpace -Registry $occupied -Name 'other-vm' -SshHost 'otherbox.example.local' }))
ok "space: the SAME host is refused" (Test-Throws { Assert-ConstructRemoteRegistrySpace -Registry $occupied -Name 'other-vm' -SshHost 'buildbox.example.local' })
ok "space: ...case-insensitively (a host name is not case-sensitive)" (Test-Throws { Assert-ConstructRemoteRegistrySpace -Registry $occupied -Name 'other-vm' -SshHost 'BUILDBOX.example.local' })
$spaceMsg = Get-ThrowMessage { Assert-ConstructRemoteRegistrySpace -Registry $occupied -Name 'other-vm' -SshHost 'buildbox.example.local' }
ok "space: ...naming the entry that is in the way" ($spaceMsg -match "work-vm")
ok "space: ...and saying both would become unloadable" ($spaceMsg -match 'unloadable')
# A REBUILD re-uses its own address, which is not a collision with itself.
ok "space: an instance never collides with itself (this is how reinstall works)" `
    (-not (Test-Throws { Assert-ConstructRemoteRegistrySpace -Registry $occupied -Name 'work-vm' -SshHost 'buildbox.example.local' }))

# ── (f) The provisioner splat ───────────────────────────────────────────────
# Get one of these wrong and the provisioner aims at the DEFAULT LOCAL VM.
Write-Host ""
Write-Host "=== Remote provisioning arguments ===" -ForegroundColor Cyan
$ClaudePartialStreaming = "on"; $MicPassthrough = "off"; $OpenCodeBackgroundWatcher = "on"
$T3Code = "on"; $T3CodeChannel = "stable"; $T3CodeLimitResume = "off"
$AutoResolve = $false; $Repo = "owner/repo"; $Ref = "main"
$script:RemoteBound  = @{}
# A provisioner that declares everything -- the current one.
$fullParams = @{}
foreach ($p in @('VmHost','SshPort','HostAlias','LocalKeyName','ConfigBranch','ServiceUrl','InstanceName','VmTokenB64',
                 'Projects','GitUserName','GitEmail','GitCloneCredentialsB64','AgentPassword','RestoreDir','AutoResolve',
                 'Auto','Repo','Ref','ClaudePartialStreaming','MicPassthrough','OpenCodeBackgroundWatcher',
                 'T3Code','T3CodeChannel','T3CodeLimitResume')) { $fullParams[$p] = $true }
$script:RemoteProvCmd = [pscustomobject]@{ Parameters = $fullParams }

$ep = @{ SshHost = 'buildbox.example.local'; SshPort = 2201 }
$args1 = New-ConstructRemoteProvisionArgs -Name 'work-vm' -Endpoint $ep -ServiceUrl 'https://buildbox.example.local:7462' `
            -ConfigBranch 'vm-work-vm' -Projects 'default' -GitName 'A B' -GitEmail 'a@b.c'
ok "args: -VmHost is the SERVICE's address, not a mshome name" ($args1['VmHost'] -eq 'buildbox.example.local')
ok "args: -SshPort is the ALLOCATED forward" ($args1['SshPort'] -eq 2201)
ok "args: -HostAlias is the instance name (its own ssh_config block)" ($args1['HostAlias'] -eq 'work-vm')
ok "args: -LocalKeyName is instance-scoped (never overwrites the default VM's key)" ($args1['LocalKeyName'] -eq 'construct_work-vm_ed25519')
ok "args: -ConfigBranch is this VM's own ref" ($args1['ConfigBranch'] -eq 'vm-work-vm')
ok "args: -ServiceUrl reaches the guest" ($args1['ServiceUrl'] -eq 'https://buildbox.example.local:7462')
ok "args: -InstanceName reaches the guest" ($args1['InstanceName'] -eq 'work-vm')
ok "args: it runs unattended" ($args1['Auto'] -eq $true)
ok "args: NO -VmTokenB64 when no token was issued (a rebuild still provisions)" (-not $args1.ContainsKey('VmTokenB64'))
ok "args: nothing local leaks in (-VmName would name a Hyper-V VM here)" (-not $args1.ContainsKey('VmName'))
ok "args: -Repo/-Ref only when the caller bound them" (-not $args1.ContainsKey('Repo'))
$script:RemoteBound = @{ Repo = $true }
$args2 = New-ConstructRemoteProvisionArgs -Name 'work-vm' -Endpoint $ep -ServiceUrl 'https://b:7462' -ConfigBranch 'vm-work-vm'
ok "args: ...and then BOTH are passed as a pair" ($args2['Repo'] -eq 'owner/repo' -and $args2['Ref'] -eq 'main')
$script:RemoteBound = @{}

# The one-time VM token: base64 of the raw secret, and the RAW value never appears.
$args3 = New-ConstructRemoteProvisionArgs -Name 'work-vm' -Endpoint $ep -ServiceUrl 'https://b:7462' `
            -ConfigBranch 'vm-work-vm' -VmToken 's3cr3t-vm-token'
ok "args: -VmTokenB64 is base64 of the issued token" `
    ($args3['VmTokenB64'] -eq [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes('s3cr3t-vm-token')))
ok "args: the RAW token appears in no argument value" `
    (@($args3.Values | Where-Object { "$_" -like "*s3cr3t-vm-token*" }).Count -eq 0)

# Skew: a provisioner that predates the late-added feature flags must not be handed
# them -- an unknown parameter is a BINDING failure, and by then a rebuild has already
# deleted the VM.
$oldParams = @{}
foreach ($p in $fullParams.Keys) { if ($p -notin @('T3CodeChannel', 'T3CodeLimitResume', 'OpenCodeBackgroundWatcher')) { $oldParams[$p] = $true } }
$script:RemoteProvCmd = [pscustomobject]@{ Parameters = $oldParams }
$args4 = New-ConstructRemoteProvisionArgs -Name 'work-vm' -Endpoint $ep -ServiceUrl 'https://b:7462' -ConfigBranch 'vm-work-vm'
foreach ($p in @('T3CodeChannel', 'T3CodeLimitResume', 'OpenCodeBackgroundWatcher')) {
    ok "args: -$p is dropped when the installed provisioner does not declare it" (-not $args4.ContainsKey($p))
}
ok "args: ...while the identity arguments are still there (they are non-negotiable)" `
    ($args4['VmHost'] -eq 'buildbox.example.local' -and $args4['ServiceUrl'] -eq 'https://b:7462')
$script:RemoteProvCmd = [pscustomobject]@{ Parameters = $fullParams }

# ── (f2) The create path's ORDER, and its rollback ──────────────────────────
# These steps are top-level flow rather than functions, so they are pinned at the source
# level (the repo's established way of guarding wiring that cannot be called in
# isolation -- see extension/test/vmpower.test.js). The ORDER is the whole safety story:
# a VM that exists but cannot be recorded is unreachable AND un-recreatable.
Write-Host ""
Write-Host "=== Create-path ordering and rollback ===" -ForegroundColor Cyan
$autoText  = $autoAst.Extent.Text
$iCreate   = $autoText.IndexOf('$created = New-ConstructVm')
$iAssert   = $autoText.IndexOf('Assert-ConstructRemoteRegistrySpace -Registry $registry -Name $instName -SshHost ([string]$endpoint.SshHost)')
$iRecord   = $autoText.IndexOf('Save-ConstructInstanceEntry -Name $instName -Replace')
$iWait     = $autoText.IndexOf('Wait-ConstructVmReachable -Name $instName')
# The FIRST '& $provisionScript' in the file belongs to the reprovision branch, which
# sits above the create path -- search from the create onwards.
$iProvision= $autoText.IndexOf('& $provisionScript @provArgs', $iCreate)
ok "order: the endpoint is checked against the registry after the create" ($iCreate -gt 0 -and $iAssert -gt $iCreate)
ok "order: the instance is RECORDED before provisioning runs" ($iRecord -gt $iAssert -and $iRecord -lt $iProvision)
ok "order: ...and before the reachability wait, which can take ten minutes" ($iRecord -lt $iWait)
# The service advertises its own PublicHost, which can differ from the URL's host, so the
# post-create check is the first moment the real address is known. A failure there must
# not strand a VM on somebody else's machine.
$rollback = $autoText.Substring($iAssert, [Math]::Min(1400, $autoText.Length - $iAssert))
ok "rollback: the post-create check is wrapped so a failure can be handled" ($rollback -match '\}\s*catch\s*\{')
ok "rollback: ...and removes the VM it just created" ($rollback -match 'Remove-ConstructVm -Name \$instName')
ok "rollback: ...saying so, rather than failing silently" ($rollback -match 'was removed from')
ok "rollback: a failed rollback says the VM is STILL THERE" ($rollback -match 'still EXISTS')

# The mode prompt's labels are the ones plan section 4.5 specifies.
ok "prompt: the local option is labelled 'Local Hyper-V install'" ($autoText -match '"Local Hyper-V install\s')
ok "prompt: the remote option is labelled 'Remote host install'" ($autoText -match '"Remote host install\s')

# ── (g) The VM token never reaches an ARGUMENT LIST ─────────────────────────
# Everything in provision.sh's env prefix becomes an argument of ssh.exe, and arguments
# are readable by any process listing on this PC. The one-time VM token therefore takes a
# different route: ssh's STDIN into a 0600 guest file, read back by a command
# substitution inside the remote shell. This section drives the REAL functions with
# ssh.exe shadowed by a recorder, so what is asserted is the actual argv.
Write-Host ""
Write-Host "=== The VM token stays out of every argv ===" -ForegroundColor Cyan
$provisionScriptPath = Join-Path $repoRoot "Provision-AgentVM.ps1"
$provErrors = $null
$provAst = [System.Management.Automation.Language.Parser]::ParseFile($provisionScriptPath, [ref]$null, [ref]$provErrors)
ok "parse: Provision-AgentVM.ps1 has zero errors" ($provErrors.Count -eq 0)

function Get-ProvisionFunctionText([string]$Name) {
    $fn = $provAst.FindAll({
        param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $Name
    }, $true) | Select-Object -First 1
    if (-not $fn) { return "" }
    return [string]$fn.Extent.Text
}
foreach ($fname in @('Send-GuestSecret', 'Invoke-SshStream')) {
    $t = Get-ProvisionFunctionText $fname
    ok "extract: Provision-AgentVM.ps1 defines $fname" ($t -ne "")
    if ($t) { Invoke-Expression $t }
}

# The world those two run in.
$script:SshPortArgs = @('-p', '2201')
$script:SshOpts     = @('-o', 'BatchMode=yes')
$script:ConnectUser = 'root'
$script:UseRootKey  = $true
$VmHost             = 'buildbox.example.local'
$SeedPassword       = 'seed-pw'
# The recorder. A FUNCTION named ssh.exe shadows the executable, so `& ssh.exe ...`
# lands here and every argument -- and everything written to stdin -- is captured.
$script:sshCalls = @()
function ssh.exe {
    $stdin = @($input) -join "`n"
    $script:sshCalls += ,([pscustomobject]@{ Argv = @($args); Stdin = $stdin })
    $global:LASTEXITCODE = 0
}

$TOKEN_RAW = 'sup3r-secret-vm-token'
$TOKEN_B64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($TOKEN_RAW))
$remotePath = "/tmp/.construct-vm-token.deadbeef"

$script:sshCalls = @()
ok "token: the upload reports success" (Send-GuestSecret -Content $TOKEN_B64 -RemotePath $remotePath)
ok "token: ...as exactly one ssh call" ($script:sshCalls.Count -eq 1)
$upload = $script:sshCalls[0]
ok "token: the token is NOT in the ssh arguments" (
    @($upload.Argv | Where-Object { "$_" -like "*$TOKEN_B64*" -or "$_" -like "*$TOKEN_RAW*" }).Count -eq 0)
ok "token: it travels on ssh's STDIN instead" ($upload.Stdin.Trim() -eq $TOKEN_B64)
ok "token: the remote command only writes the file" (
    @($upload.Argv | Where-Object { "$_" -like "*cat > '$remotePath'*" }).Count -eq 1)
ok "token: ...with umask 077, so it is never briefly world-readable" (
    @($upload.Argv | Where-Object { "$_" -like "umask 077;*" }).Count -eq 1)

# The provisioning call itself: the export + cleanup shape the script builds.
$tokenExport  = "export CONSTRUCT_VM_TOKEN_B64=`"`$(cat '$remotePath')`"; "
$tokenCleanup = "; __rc=`$?; rm -f '$remotePath'; exit `$__rc"
$fakeEnvPrefix = "env AI_TOOLS='all' PROJECTS='default' CONSTRUCT_SERVICE_URL='https://b:7462' CONSTRUCT_INSTANCE_NAME='work-vm'"
$script:sshCalls = @()
Invoke-SshStream -Command "$tokenExport$fakeEnvPrefix bash /opt/construct/repo/bin/provision.sh$tokenCleanup" | Out-Null
$prov = $script:sshCalls[0]
$provArgvText = ($prov.Argv | ForEach-Object { "$_" }) -join ' '
ok "token: the provisioning command carries NO token, raw or base64" (
    ($provArgvText -notlike "*$TOKEN_B64*") -and ($provArgvText -notlike "*$TOKEN_RAW*"))
# Invoke-SshStream re-quotes the command for the remote login shell ('  ->  '\''), so
# the assertions look for the pieces rather than a byte-exact string.
ok "token: ...it reads the file in the guest instead" (
    ($provArgvText -like "*`$(cat *") -and ($provArgvText -like "*$remotePath*"))
ok "token: ...and deletes it afterwards, preserving the exit code" (
    ($provArgvText -like "*rm -f *") -and ($provArgvText -like "*exit `$__rc*"))
ok "token: the env prefix itself never mentions the token variable's VALUE" (
    $provArgvText -notlike "*CONSTRUCT_VM_TOKEN_B64='*")

# ZERO-CHANGE: with no token the two pieces are empty, so the command is exactly the
# string this script has always sent.
$script:sshCalls = @()
Invoke-SshStream -Command "$fakeEnvPrefix bash /opt/construct/repo/bin/provision.sh" | Out-Null
$plainArgv = ($script:sshCalls[0].Argv | ForEach-Object { "$_" }) -join ' '
ok "token: a local install's provisioning command has no export and no cleanup" (
    ($plainArgv -notlike "*export CONSTRUCT_VM_TOKEN_B64*") -and ($plainArgv -notlike "*rm -f /tmp/.construct-vm-token*"))

# The script's own wiring: the token must not be handed to the env-prefix renderer.
$provText = $provAst.Extent.Text
ok "token: the provisioning call passes NO token to Get-ServiceEnvSuffix" (
    $provText -match 'Get-ServiceEnvSuffix -ServiceUrl \$ServiceUrl -InstanceName \$InstanceName -VmTokenB64 ""')
ok "token: ...and the command is `$tokenExport + `$envPrefix + ... + `$tokenCleanup" (
    $provText -match '\$tokenExport\$envPrefix bash /opt/construct/repo/bin/provision\.sh\$tokenCleanup')
ok "token: the upload failure is fatal (nothing is provisioned without it)" (
    $provText -match 'if \(-not \(Send-GuestSecret -Content \$VmTokenB64')

# ── (h) Plain http is a loopback-only concession ────────────────────────────
Write-Host ""
Write-Host "=== Transport safety (http is loopback-only) ===" -ForegroundColor Cyan
. (Join-Path $repoRoot "lib/AgentVm.Remote.ps1")
ok "loopback: localhost" (Test-ConstructLoopbackHost -HostName 'localhost')
ok "loopback: 127.0.0.1" (Test-ConstructLoopbackHost -HostName '127.0.0.1')
ok "loopback: anywhere in 127.0.0.0/8" (Test-ConstructLoopbackHost -HostName '127.5.5.5')
ok "loopback: ::1 (bracketed or not)" (
    (Test-ConstructLoopbackHost -HostName '::1') -and (Test-ConstructLoopbackHost -HostName '[::1]'))
ok "loopback: a LAN address is not" (-not (Test-ConstructLoopbackHost -HostName '10.0.0.5'))
ok "loopback: a name that merely CONTAINS localhost is not" (-not (Test-ConstructLoopbackHost -HostName 'localhost.evil.example'))
ok "http: https anywhere is fine" (-not (Test-Throws { Assert-ConstructTransportSafe -BaseUrl 'https://buildbox.example.local:7462' }))
ok "http: http to loopback is fine (this is what the fake service listens on)" (
    -not (Test-Throws { Assert-ConstructTransportSafe -BaseUrl 'http://127.0.0.1:7999' }))
ok "http: http to a LAN host is refused" (Test-Throws { Assert-ConstructTransportSafe -BaseUrl 'http://buildbox.example.local:7462' })
$httpMsg = Get-ThrowMessage { Assert-ConstructTransportSafe -BaseUrl 'http://buildbox.example.local:7462' }
ok "http: ...saying the credentials would be unencrypted" ($httpMsg -match 'unencrypted')
ok "http: ...and that there is no certificate to verify" ($httpMsg -match 'certificate')
# The refusal has to happen BEFORE a credential is attached, which is why it sits at the
# top of Invoke-ConstructApi rather than at the request.
$remoteLibText = [System.IO.File]::ReadAllText((Join-Path $repoRoot "lib/AgentVm.Remote.ps1"))
$invokeIdx = $remoteLibText.IndexOf('function Invoke-ConstructApi')
$assertIdx = $remoteLibText.IndexOf('Assert-ConstructTransportSafe -BaseUrl $base', $invokeIdx)
$authIdx   = $remoteLibText.IndexOf("switch ([string]`$Auth['Mode'])", $invokeIdx)
ok "http: Invoke-ConstructApi refuses the transport before selecting a credential" (
    $assertIdx -gt 0 -and $authIdx -gt 0 -and $assertIdx -lt $authIdx)

# ── (i) The registry write (lib/AgentVm.Instances.ps1's additive helpers) ───
# Run in a CHILD SCOPE: the library turns on Set-StrictMode -Version Latest, which the
# extracted installer code above was never written for. `ok` still counts into this
# script's scope.
Write-Host ""
Write-Host "=== Registry entry writing (Add-ConstructInstance) ===" -ForegroundColor Cyan
& {
    . (Join-Path $repoRoot "lib/AgentVm.Instances.ps1")

    $regPath = Join-Path $tmpRoot "instances.json"
    $reg = Read-ConstructInstances -Path $regPath
    ok "reg: a missing registry still yields the synthesized default" ($reg.Instances.ContainsKey('agent-vm'))

    $remoteEntry = @{
        backend      = 'hyperv-remote'
        vmName       = 'work-vm'
        sshHost      = 'buildbox.example.local'
        sshPort      = 2201
        hostAlias    = 'work-vm'
        keyName      = 'construct_work-vm_ed25519'
        configBranch = 'vm-work-vm'
        service      = @{ url = 'https://buildbox.example.local:7462'; auth = 'negotiate' }
        owner        = 'DOMAIN\alice'
    }
    $next = Add-ConstructInstance -Registry $reg -Name 'work-vm' -Entry $remoteEntry
    ok "reg: the entry is added" ($next.Instances.ContainsKey('work-vm'))
    ok "reg: the INPUT registry is not modified (the caller keeps seeing the old state)" (-not $reg.Instances.ContainsKey('work-vm'))
    ok "reg: the default instance survives untouched" ($next.Instances.ContainsKey('agent-vm'))

    # A hashtable entry with a NESTED hashtable is the shape the installer builds; the
    # round trip is what proves the reader sees the same thing later.
    $next.Default = 'work-vm'
    [void](Save-ConstructInstances -Registry $next -Path $regPath)
    $back = Read-ConstructInstances -Path $regPath
    ok "reg: it survives a save/load round trip" ($back.Instances.ContainsKey('work-vm'))
    ok "reg: ...with no problems reported (i.e. the reader accepted it)" (@($back.Problems).Count -eq 0)
    $w = $back.Instances['work-vm']
    ok "reg: backend round-trips" ($w.Backend -eq 'hyperv-remote')
    ok "reg: the endpoint round-trips" ($w.VmHost -eq 'buildbox.example.local' -and $w.SshPort -eq 2201)
    ok "reg: the key name round-trips" ($w.KeyName -eq 'construct_work-vm_ed25519')
    ok "reg: the config branch round-trips" ($w.ConfigBranch -eq 'vm-work-vm')
    ok "reg: the SERVICE object round-trips (url + auth)" `
        ($w.Service -and $w.Service.Url -eq 'https://buildbox.example.local:7462' -and $w.Service.Auth -eq 'negotiate')
    ok "reg: the owner round-trips" ($w.Owner -eq 'DOMAIN\alice')
    ok "reg: the default instance selection round-trips" ($back.Default -eq 'work-vm')

    # Refusals -- each one is something the READER would otherwise drop silently.
    ok "reg: adding an existing name without -Replace is refused" (Test-Throws { Add-ConstructInstance -Registry $back -Name 'work-vm' -Entry $remoteEntry })
    ok "reg: ...and -Replace overwrites it" (
        (Add-ConstructInstance -Registry $back -Name 'work-vm' -Replace -Entry $remoteEntry).Instances['work-vm'].VmHost -eq 'buildbox.example.local')
    ok "reg: the DEFAULT instance can never be replaced" (Test-Throws { Add-ConstructInstance -Registry $back -Name 'agent-vm' -Entry $remoteEntry })
    ok "reg: an invalid instance name is refused" (Test-Throws { Add-ConstructInstance -Registry $reg -Name 'Work VM' -Entry $remoteEntry })
    # 'hyperv-remote' really is a known backend now -- if it were not, every entry the
    # remote flow writes would be refused here.
    ok "reg: 'hyperv-remote' is a known backend" (@(Get-ConstructInstanceEntryProblem -Name 'x-vm' -Entry $remoteEntry).Count -eq 0)
    # ...and the reader's spelling rule still bites where it matters: a miscased LOCAL id
    # is read as "unknown" by the case-sensitive comparisons and as the LOCAL driver by
    # the lowercasing JS lookup, so it must not load under either reading.
    $miscased = @{} + $remoteEntry; $miscased['backend'] = 'HyperV-Local'
    ok "reg: a miscased local backend id is refused" (Test-Throws { Add-ConstructInstance -Registry $reg -Name 'other-vm' -Entry $miscased })
    $emptyBackend = @{} + $remoteEntry; $emptyBackend['backend'] = '   '
    ok "reg: a present-but-empty backend is refused (it must never derive 'local')" (Test-Throws { Add-ConstructInstance -Registry $reg -Name 'other-vm' -Entry $emptyBackend })
    # The collision the remote flow actually hits: two VMs on one host service.
    $sameHost = @{} + $remoteEntry; $sameHost['vmName'] = 'other-vm'; $sameHost['hostAlias'] = 'other-vm'
    $sameHost['keyName'] = 'construct_other-vm_ed25519'; $sameHost['configBranch'] = 'vm-other-vm'
    $collide = Get-ThrowMessage { Add-ConstructInstance -Registry $back -Name 'other-vm' -Entry $sameHost }
    ok "reg: a second VM at the SAME address is refused, not written" ($collide -ne "")
    ok "reg: ...and the message names the instance being added" ($collide -match 'other-vm')
    ok "reg: nothing was written by the refusal" ((Read-ConstructInstances -Path $regPath).Instances.Keys.Count -eq $back.Instances.Keys.Count)

    # The problem list is the reader's own rule set, reported where the entry is built.
    ok "reg: a good entry reports no problems" (@(Get-ConstructInstanceEntryProblem -Name 'x-vm' -Entry $remoteEntry).Count -eq 0)
    ok "reg: an invalid name reports one" (@(Get-ConstructInstanceEntryProblem -Name 'X VM' -Entry $remoteEntry).Count -gt 0)
}

} finally {
    Remove-Item -LiteralPath $tmpRoot -Recurse -Force -ErrorAction SilentlyContinue
}

# ── Summary ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==============================" -ForegroundColor Cyan
Write-Host "  $($script:pass) passed, $($script:fail) failed" -ForegroundColor $(if ($script:fail -gt 0) { "Red" } else { "Green" })
Write-Host "==============================" -ForegroundColor Cyan
if ($script:fail -gt 0) { exit 1 }
exit 0
