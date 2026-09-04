#Requires -Version 5.1
<#
    Unit tests for B14's "Remove instance" (plan section 4.12 "Cleanup"):
    lib/AgentVm.Cleanup.ps1's pure planner and its pure document editors, plus the
    registry mutator and the Auto-Install.ps1 wiring that drives them. Run:

        pwsh -NoProfile -File test/instance-cleanup.test.ps1

    Self-contained: the planner takes its host paths as parameters, so nothing here
    needs a Windows profile, and the effects are exercised against a temp directory.
#>
$ErrorActionPreference = "Stop"

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent $here

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

. (Join-Path $repoRoot "lib/AgentVm.Common.ps1")
. (Join-Path $repoRoot "lib/AgentVm.Cleanup.ps1")

$HOMEDIR = "/tmp/b14-home"
$LAD     = "/tmp/b14-lad"
$APPDATA = "/tmp/b14-appdata"
$TMPDIR  = "/tmp/b14-temp"

function New-TestIdentity {
    param([string]$Name = 'work-vm', [string]$Backend = 'hyperv-local', [string]$ServiceUrl = "")
    $svc = $null
    if ($ServiceUrl) { $svc = [pscustomobject]@{ Url = $ServiceUrl; Auth = 'negotiate' } }
    return [pscustomobject]@{
        Name      = $Name
        Backend   = $Backend
        VmName    = $Name
        VmHost    = "$Name.mshome.net"
        HostAlias = $Name
        SshPort   = 22
        KeyName   = "construct_${Name}_ed25519"
        Service   = $svc
    }
}

$SCRIPTSDIR = "/tmp/b14-scripts"

function Get-Plan {
    param($Identity, [string]$Name = 'work-vm', [int]$Count = 2, [switch]$IsDefault, [string]$Confirmation = "", [string]$ScriptsDir = $SCRIPTSDIR)
    return Get-ConstructInstanceRemovalPlan -Name $Name -Identity $Identity -InstanceCount $Count `
        -IsDefault:$IsDefault -Confirmation $Confirmation `
        -HomeDir $HOMEDIR -LocalAppData $LAD -AppData $APPDATA -TempDir $TMPDIR -ScriptsDir $ScriptsDir
}

# ── Parser checks ────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Parser checks ===" -ForegroundColor Cyan
foreach ($rel in @("lib/AgentVm.Cleanup.ps1", "lib/AgentVm.Instances.ps1", "lib/AgentVm.InstanceTarget.ps1", "Auto-Install.ps1")) {
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot $rel), [ref]$null, [ref]$errors)
    ok "parse: $rel has zero errors" ($errors.Count -eq 0)
}

# ── The plan: what a local removal does, and what it deliberately keeps ───────
Write-Host ""
Write-Host "=== The removal plan ===" -ForegroundColor Cyan
$local = Get-Plan -Identity (New-TestIdentity)
ok "local: the plan is accepted without any typed confirmation" ($local.Ok -and -not $local.RequiresTypedConfirmation)
ok "local: no VM is deleted" (-not $local.DeletesVm)
$kinds = @($local.Steps | ForEach-Object { $_.Kind })
foreach ($kind in @('ssh-config', 'known-hosts', 'ssh-key', 'vscode-remote-platform', 'opencode-server', 't3-ca', 'instance-state', 't3-endpoint', 'temp-known-hosts', 'registry-entry')) {
    ok "local: the plan covers '$kind'" ($kinds -contains $kind)
}
ok "local: it does NOT ask a host service to delete anything" (-not ($kinds -contains 'remote-vm-delete'))
ok "local: the registry entry goes LAST, after everything it describes" ($kinds[-1] -eq 'registry-entry')
# The OpenCode entry is matched by URL as well as by display name, so the plan has to
# STATE the URLs -- the direct one it derives, plus any this PC recorded for a forward.
ok "local: the plan states the direct OpenCode URL" (@($local.OpenCodeUrls) -contains 'http://work-vm.mshome.net:4096')
$withForward = Get-ConstructInstanceRemovalPlan -Name 'work-vm' -Identity (New-TestIdentity) -InstanceCount 2 `
    -OpenCodeUrls @('https://work-vm.vpn.example:23011') `
    -HomeDir $HOMEDIR -LocalAppData $LAD -AppData $APPDATA -TempDir $TMPDIR
ok "local: a recorded forward URL is carried too" (
    (@($withForward.OpenCodeUrls) -contains 'https://work-vm.vpn.example:23011') -and
    (@($withForward.OpenCodeUrls) -contains 'http://work-vm.mshome.net:4096'))
ok "local: it says the Hyper-V VM is kept" (@($local.Keeps) -join ' ' -match 'NOT deleted')
ok "local: it says the config store is kept" (@($local.Keeps) -join ' ' -match 'config-sync branch')
$targets = @{}
foreach ($s in $local.Steps) { $targets[$s.Kind] = $s.Target }
ok "local: the ssh key target is this instance's own key file" ($targets['ssh-key'] -match 'construct_work-vm_ed25519$')
ok "local: the CA target is this instance's own certificate file" ($targets['t3-ca'] -match 'construct-t3-ca-work-vm\.crt$')
ok "local: the state file target is instances\<name>.json" ($targets['instance-state'] -match 'work-vm\.json$')
ok "local: the temp file target is this instance's known_hosts" ($targets['temp-known-hosts'] -match 'construct-known_hosts-work-vm$')
ok "local: the recorded endpoints file is removed too" ($targets['t3-endpoint'] -match 'remote-work-vm\.json$')

# ── Refusals ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Refusals ===" -ForegroundColor Cyan
$onlyOne = Get-Plan -Identity (New-TestIdentity -Name 'work-vm') -Name 'work-vm' -Count 1 -IsDefault
ok "refusal: the only instance on this PC" ((-not $onlyOne.Ok) -and $onlyOne.Refusal -match 'only instance')
ok "refusal: a refused plan carries no steps" (@($onlyOne.Steps).Count -eq 0)
$onlyOneNotDefault = Get-Plan -Identity (New-TestIdentity -Name 'work-vm') -Name 'work-vm' -Count 1
ok "refusal: ...whether or not it is the default one" (-not $onlyOneNotDefault.Ok)
# THE ONE REFUSAL is "the only instance". Every name is otherwise removable, 'agent-vm'
# included -- its row is synthesized, so the removal is RECORDED (an explicit null entry)
# instead of a key being deleted that would come straight back.
$implicit = Get-Plan -Identity (New-TestIdentity -Name 'agent-vm') -Name 'agent-vm' -Count 3 -IsDefault
ok "role: 'agent-vm' is removable while other instances exist" $implicit.Ok
$implicitKinds = @($implicit.Steps | ForEach-Object { $_.Kind })
ok "role: its registry row is REMOVED like any other" ($implicitKinds -contains 'registry-entry')
ok "role: ...and the step says the removal is written down" (
    @($implicit.Steps | Where-Object { $_.Kind -eq 'registry-entry' })[0].Label -match 'written down')
# The default instance has NO per-instance file: its VM-scoped settings are mirrored into
# the install's .construct-settings.json, so THAT is what a removal has to clear.
ok "role: removing agent-vm clears the default store, not a file that never existed" (
    $implicitKinds -contains 'default-store')
ok "role: it still removes its ssh, CA and state artefacts" (
    ($implicitKinds -contains 'ssh-config') -and ($implicitKinds -contains 't3-ca') -and ($implicitKinds -contains 'instance-state'))
# A NAMED default instance is a normal removal too.
$namedDefault = Get-Plan -Identity (New-TestIdentity -Name 'work-vm') -Name 'work-vm' -Count 3 -IsDefault
ok "role: a NAMED default instance can be removed while others exist" (
    $namedDefault.Ok -and @($namedDefault.Steps | ForEach-Object { $_.Kind }) -contains 'registry-entry')
ok "role: a NAMED instance has no default-store step (it has its own file)" (
    -not (@($namedDefault.Steps | ForEach-Object { $_.Kind }) -contains 'default-store'))

# ── A remote instance: the VM is deleted, so the name must be typed ───────────
Write-Host ""
Write-Host "=== Remote instances ===" -ForegroundColor Cyan
$remoteId = New-TestIdentity -Name 'far-vm' -Backend 'hyperv-remote' -ServiceUrl 'https://buildbox.example.local:7462'
$noConfirm = Get-Plan -Identity $remoteId -Name 'far-vm'
ok "remote: without the typed name the plan is refused" ((-not $noConfirm.Ok) -and $noConfirm.RequiresTypedConfirmation -and -not $noConfirm.ConfirmationOk)
ok "remote: the refusal names the service and says the disk goes" ($noConfirm.Refusal -match 'buildbox\.example\.local' -and $noConfirm.Refusal -match 'disk')
$wrongCase = Get-Plan -Identity $remoteId -Name 'far-vm' -Confirmation 'FAR-VM'
ok "remote: the confirmation is compared case-sensitively" (-not $wrongCase.Ok)
$confirmed = Get-Plan -Identity $remoteId -Name 'far-vm' -Confirmation 'far-vm'
ok "remote: the typed name accepts the plan" ($confirmed.Ok -and $confirmed.DeletesVm)
ok "remote: the VM deletion is the FIRST step (nothing local is touched if it fails)" (@($confirmed.Steps)[0].Kind -eq 'remote-vm-delete')
ok "remote: the deletion step carries the service URL" (@($confirmed.Steps)[0].Target -eq 'https://buildbox.example.local:7462')
ok "remote: nothing claims the Hyper-V VM is kept" (-not (@($confirmed.Keeps) -join ' ' -match 'Hyper-V'))
$noService = Get-Plan -Identity (New-TestIdentity -Name 'far-vm' -Backend 'hyperv-remote') -Name 'far-vm' -Confirmation 'far-vm'
ok "remote: an entry with no service URL plans no deletion step" (
    -not (@($noService.Steps | ForEach-Object { $_.Kind }) -contains 'remote-vm-delete'))

# ── The pure document editors ────────────────────────────────────────────────
Write-Host ""
Write-Host "=== ssh_config editing ===" -ForegroundColor Cyan
$cfgText = @"
Host other
    HostName other.example

Host work-vm
    HostName work-vm.mshome.net
    User root
    IdentitiesOnly yes

Host keep-me
    HostName keep.example
"@
$edited = Remove-ConstructSshConfigBlock -Text $cfgText -Alias 'work-vm'
ok "ssh_config: the instance's block is removed" ($edited.Removed -and $edited.Text -notmatch 'work-vm')
ok "ssh_config: every other block survives verbatim" ($edited.Text -match 'Host other' -and $edited.Text -match 'Host keep-me' -and $edited.Text -match 'keep\.example')
$missing = Remove-ConstructSshConfigBlock -Text $cfgText -Alias 'nope'
ok "ssh_config: an alias that is not there changes nothing" ((-not $missing.Removed) -and $missing.Text.Trim() -eq (($cfgText -split "`r?`n") -join "`r`n").Trim())
$multi = Remove-ConstructSshConfigBlock -Text "Host work-vm other`n    HostName x`n" -Alias 'work-vm'
ok "ssh_config: a multi-pattern Host line is the user's and is never touched" (-not $multi.Removed)
$empty = Remove-ConstructSshConfigBlock -Text "" -Alias 'work-vm'
ok "ssh_config: an empty file is handled" ((-not $empty.Removed) -and $empty.Text -eq "")

Write-Host ""
Write-Host "=== OpenCode server list editing ===" -ForegroundColor Cyan
$list = @(
    [pscustomobject]@{ type = 'http'; displayName = 'work-vm'; http = [pscustomobject]@{ url = 'http://work-vm.mshome.net:4096' } },
    [pscustomobject]@{ type = 'http'; displayName = 'work-vm (old port)'; http = [pscustomobject]@{ url = 'http://buildbox:23011' } },
    [pscustomobject]@{ type = 'http'; displayName = 'my own'; http = [pscustomobject]@{ url = 'http://elsewhere:4096' } }
)
$byUrl = Remove-ConstructOpenCodeServerEntries -List $list -Urls @('http://buildbox:23011/') -DisplayName 'nothing'
ok "opencode: an entry is matched by URL (trailing slash and case ignored)" ($byUrl.Removed -eq 1 -and @($byUrl.List).Count -eq 2)
$byName = Remove-ConstructOpenCodeServerEntries -List $list -Urls @() -DisplayName 'work-vm'
ok "opencode: an entry is matched by display name" ($byName.Removed -eq 1)
ok "opencode: a server the user added is kept" (@($byName.List | Where-Object { $_.displayName -eq 'my own' }).Count -eq 1)
$neither = Remove-ConstructOpenCodeServerEntries -List $list -Urls @() -DisplayName ''
ok "opencode: with nothing to match on, nothing is removed" ($neither.Removed -eq 0 -and @($neither.List).Count -eq 3)

Write-Host ""
Write-Host "=== remote.SSH.remotePlatform editing ===" -ForegroundColor Cyan
$settings = '{"remote.SSH.remotePlatform":{"work-vm":"linux","agent-vm":"linux"},"editor.fontSize":13}' | ConvertFrom-Json
$rp = Remove-ConstructRemotePlatformKey -Settings $settings -Alias 'work-vm'
ok "remotePlatform: the alias is removed" ($rp.Removed -and -not ($rp.Settings.'remote.SSH.remotePlatform'.PSObject.Properties.Name -contains 'work-vm'))
ok "remotePlatform: every other alias stays" ($rp.Settings.'remote.SSH.remotePlatform'.PSObject.Properties.Name -contains 'agent-vm')
ok "remotePlatform: unrelated settings are untouched" ($rp.Settings.'editor.fontSize' -eq 13)
$rpMissing = Remove-ConstructRemotePlatformKey -Settings $settings -Alias 'nope'
ok "remotePlatform: an absent alias changes nothing" (-not $rpMissing.Removed)
$rpNoKey = Remove-ConstructRemotePlatformKey -Settings ('{"editor.fontSize":13}' | ConvertFrom-Json) -Alias 'work-vm'
ok "remotePlatform: a settings file without the map is left alone" (-not $rpNoKey.Removed)

# ── The effects, against a temp directory ────────────────────────────────────
Write-Host ""
Write-Host "=== Effects ===" -ForegroundColor Cyan
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("b14-cleanup-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work -Force | Out-Null
try {
    $cfgPath = Join-Path $work "config"
    [System.IO.File]::WriteAllText($cfgPath, $cfgText)
    $r = Remove-ConstructSshConfigEntry -Path $cfgPath -Alias 'work-vm'
    ok "effect: the ssh_config block is removed from the file" (
        $r.Status -eq 'removed' -and ([System.IO.File]::ReadAllText($cfgPath)) -notmatch 'work-vm')
    $r2 = Remove-ConstructSshConfigEntry -Path $cfgPath -Alias 'work-vm'
    ok "effect: removing it twice is a skip, not a failure" ($r2.Status -eq 'skipped')
    $r3 = Remove-ConstructSshConfigEntry -Path (Join-Path $work "nope") -Alias 'work-vm'
    ok "effect: a missing ssh_config is a skip" ($r3.Status -eq 'skipped')

    $keyPath = Join-Path $work "construct_work-vm_ed25519"
    [System.IO.File]::WriteAllText($keyPath, "key")
    [System.IO.File]::WriteAllText("$keyPath.pub", "pub")
    $r4 = Remove-ConstructInstanceFile -Kind 'ssh-key' -Path $keyPath -WithPublicKey
    ok "effect: the private key and its .pub are both deleted" (
        $r4.Status -eq 'removed' -and -not (Test-Path -LiteralPath $keyPath) -and -not (Test-Path -LiteralPath "$keyPath.pub"))
    ok "effect: deleting what is not there is a skip" ((Remove-ConstructInstanceFile -Kind 'ssh-key' -Path $keyPath).Status -eq 'skipped')

    $ocPath = Join-Path $work "opencode.global.dat"
    $inner = @{ list = @(@{ type = 'http'; displayName = 'work-vm'; http = @{ url = 'http://work-vm.mshome.net:4096' } }) } | ConvertTo-Json -Depth 8 -Compress
    [System.IO.File]::WriteAllText($ocPath, (@{ server = $inner; other = 'keep' } | ConvertTo-Json -Depth 8))
    $r5 = Remove-ConstructOpenCodeServer -Path $ocPath -Urls @() -DisplayName 'work-vm'
    $after = Get-Content -LiteralPath $ocPath -Raw | ConvertFrom-Json
    ok "effect: the OpenCode entry is removed and the file stays valid" (
        $r5.Status -eq 'removed' -and $after.other -eq 'keep' -and @(($after.server | ConvertFrom-Json).list).Count -eq 0)
    [System.IO.File]::WriteAllText($ocPath, "not json at all")
    # Left strictly alone AND reported as a FAILURE: "could not look" is not "nothing to
    # do", and calling it a skip would let the registry entry go while the server entry
    # (which may well still be in there) survives.
    $rOc = Remove-ConstructOpenCodeServer -Path $ocPath -Urls @() -DisplayName 'work-vm'
    ok "effect: an unparsable OpenCode config is left alone and FAILS" (
        $rOc.Status -eq 'failed' -and ([System.IO.File]::ReadAllText($ocPath)) -eq "not json at all")

    $vsPath = Join-Path $work "settings.json"
    [System.IO.File]::WriteAllText($vsPath, '{"remote.SSH.remotePlatform":{"work-vm":"linux"},"editor.fontSize":13}')
    $r6 = Remove-ConstructVsCodeRemotePlatform -Path $vsPath -Alias 'work-vm'
    ok "effect: the remotePlatform key is removed from settings.json" (
        $r6.Status -eq 'removed' -and (Get-Content -LiteralPath $vsPath -Raw) -notmatch 'work-vm')
    # A settings.json this host's PowerShell cannot parse (Windows PowerShell 5.1 also
    # refuses the JSONC comments VS Code allows) must be left byte-for-byte alone and the
    # manual fix named -- rewriting a file we did not understand is how a user's settings
    # get destroyed.
    $broken = '{"remote.SSH.remotePlatform": {"work-vm": '
    [System.IO.File]::WriteAllText($vsPath, $broken)
    $rJsonc = Remove-ConstructVsCodeRemotePlatform -Path $vsPath -Alias 'work-vm'
    ok "effect: an unparsable settings file is not rewritten, and FAILS" (
        $rJsonc.Status -eq 'failed' -and $rJsonc.Message -match 'by hand' -and
        ([System.IO.File]::ReadAllText($vsPath)) -eq $broken)

    # END TO END: what the PROVISIONER writes is what the removal looks for and deletes.
    $endpointRecord = Get-ConstructT3EndpointRecord -InstanceName 'work-vm' `
        -BaseUrl 'https://work-vm.vpn.example:23011' -OpenCodeUrl 'https://work-vm.vpn.example:23012'
    $endpointName = Get-ConstructT3EndpointFileName -InstanceName 'work-vm'
    $endpointPath = Join-Path $work $endpointName
    ($endpointRecord | ConvertTo-Json -Depth 5) | Set-Content -LiteralPath $endpointPath -Encoding UTF8
    ok "effect: the provisioner's endpoint record carries the port AND the OpenCode url" (
        $endpointRecord.port -eq 23011 -and $endpointRecord.openCodeUrl -eq 'https://work-vm.vpn.example:23012')
    $rEnd = Remove-ConstructInstanceFile -Kind 't3-endpoint' -Path $endpointPath
    ok "effect: the removal deletes the file the provisioner wrote" (
        $rEnd.Status -eq 'removed' -and -not (Test-Path -LiteralPath $endpointPath))
    # ...and the url it recorded is what reaches the OpenCode matcher, so an entry whose
    # display name was changed is still found.
    $forwardOc = Join-Path $work "opencode-forward.dat"
    $forwardInner = @{ list = @(@{ type = 'http'; displayName = 'renamed by the user'; http = @{ url = 'https://work-vm.vpn.example:23012' } }) } | ConvertTo-Json -Depth 8 -Compress
    [System.IO.File]::WriteAllText($forwardOc, (@{ server = $forwardInner } | ConvertTo-Json -Depth 8))
    $forwardPlan = Get-ConstructInstanceRemovalPlan -Name 'work-vm' -Identity (New-TestIdentity) -InstanceCount 2 `
        -OpenCodeUrls @([string]$endpointRecord.openCodeUrl) `
        -HomeDir $HOMEDIR -LocalAppData $LAD -AppData $APPDATA -TempDir $TMPDIR
    $rForward = Remove-ConstructOpenCodeServer -Path $forwardOc -Urls @($forwardPlan.OpenCodeUrls) -DisplayName 'work-vm'
    ok "effect: a forwarded entry whose display name changed is still removed, by url" (
        $rForward.Status -eq 'removed')

    ok "effect: a missing CA file AND no ledger is a skip" ((Remove-ConstructT3CaTrust -Path (Join-Path $work "no-ca.crt")).Status -eq 'skipped')
    # LEDGER-ONLY: the certificate file is gone (an earlier run deleted it, or the VM
    # never re-published one) but superseded thumbprints are still recorded. That is
    # exactly what the ledger is for, so it must still be processed and cleared.
    $ledgerOnly = Join-Path $work "ledger-only-ca.crt"
    [System.IO.File]::WriteAllText("$ledgerOnly.orphan", ("A" * 40) + "`nnot-a-thumbprint`n")
    $rLedger = Remove-ConstructT3CaTrust -Path $ledgerOnly
    ok "effect: a ledger with no certificate file is still processed" ($rLedger.Status -eq 'removed')
    ok "effect: ...and the ledger is gone afterwards" (-not (Test-Path -LiteralPath "$ledgerOnly.orphan"))

    # The DEFAULT STORE: the default instance's VM-scoped keys go, the install's own stay.
    $storeDir = Join-Path $work "scripts"
    New-Item -ItemType Directory -Path $storeDir -Force | Out-Null
    $storePath = Join-Path $storeDir ".construct-settings.json"
    # EVERY key the panel and the installer persist for the VM, including the ones that
    # are state rather than form fields (vmAutoCheckpointsApplied), and one this build has
    # never heard of -- which is VM-scoped by default, because that is the safe direction.
    $storeDoc = [ordered]@{
        installedCommit = 'a' * 40; constructRepo = 'permissionBRICK/The-Construct'; constructRef = 'main'
        gitUserName = 'alice'; gitEmail = 'alice@example.com'; gitCredentialStore = $true
        provisionedCommit = 'b' * 40
        micPassthrough = $true; claudePartialStreaming = $true; opencodeBackgroundWatcher = $true
        t3code = $true; t3codeChannel = 'nightly'; t3codeLimitResume = $true
        vsCodeServeWeb = $true; vsCodeTunnel = $false; smbShare = $true
        vmMemoryGB = '16'; vmDiskGB = '120'; ubuntuRelease = '24.04'
        vmAutoCheckpoints = $true; vmAutoCheckpointsApplied = $true
        projects = 'default'; aiTools = 'claude'; t3PairingHint = 'x'
        somethingNewerNobodyKnows = 'a VM setting from a later Construct'
    }
    [System.IO.File]::WriteAllText($storePath, ($storeDoc | ConvertTo-Json -Depth 8))
    $rStore = Remove-ConstructDefaultStoreEntry -Path $storePath
    $storeAfter = Get-Content -LiteralPath $storePath -Raw | ConvertFrom-Json
    $survivors = @($storeAfter.PSObject.Properties.Name | Sort-Object)
    ok "effect: EVERY VM key is cleared, including the state-only and unknown ones" (
        $rStore.Status -eq 'removed' -and
        ($survivors -join ',') -eq 'constructRef,constructRepo,gitCredentialStore,gitEmail,gitUserName,installedCommit')
    ok "effect: ...and the INSTALL-WIDE keys keep their values" (
        $storeAfter.installedCommit -eq ('a' * 40) -and
        $storeAfter.constructRepo -eq 'permissionBRICK/The-Construct' -and
        $storeAfter.constructRef -eq 'main' -and $storeAfter.gitUserName -eq 'alice' -and
        $storeAfter.gitEmail -eq 'alice@example.com' -and $storeAfter.gitCredentialStore -eq $true)
    ok "effect: the applied-checkpoint marker does not survive" (
        -not ($survivors -contains 'vmAutoCheckpointsApplied'))
    ok "effect: a key this build has never seen is treated as the VM's" (
        -not ($survivors -contains 'somethingNewerNobodyKnows'))
    ok "effect: a second run has nothing left to clear" (
        (Remove-ConstructDefaultStoreEntry -Path $storePath).Status -eq 'skipped')
    [System.IO.File]::WriteAllText($storePath, '{"installedCommit": ')
    ok "effect: an unparsable default store FAILS and is left untouched" (
        (Remove-ConstructDefaultStoreEntry -Path $storePath).Status -eq 'failed' -and
        ([System.IO.File]::ReadAllText($storePath)) -eq '{"installedCommit": ')
    ok "effect: a missing default store is a skip" (
        (Remove-ConstructDefaultStoreEntry -Path (Join-Path $work "nope.json")).Status -eq 'skipped')
    # A certificate file this host cannot READ is not "nothing to do": its thumbprint —
    # the only handle on whatever it put in the Root store — is unknown, so it is a
    # FAILURE (which keeps the registry entry) and the file stays for the retry.
    $badCa = Join-Path $work "broken-ca.crt"
    [System.IO.File]::WriteAllText($badCa, "this is not a certificate")
    $rCa = Remove-ConstructT3CaTrust -Path $badCa
    ok "effect: an unreadable CA fails and is LEFT IN PLACE (it is the only record)" (
        $rCa.Status -eq 'failed' -and (Test-Path -LiteralPath $badCa))
    # The orphan ledger the provisioner writes beside it (superseded CAs it could not
    # untrust) is read by the removal too, so those are its last chance to go.
    [System.IO.File]::WriteAllText("$badCa.orphan", ("A" * 40) + "`n" + "not-a-thumbprint`n")
    $rOrphan = Remove-ConstructT3CaTrust -Path $badCa
    ok "effect: the orphan ledger is read beside the certificate" ($rOrphan.Status -eq 'failed')
    Remove-Item -LiteralPath "$badCa.orphan" -Force -ErrorAction SilentlyContinue

    # The walk itself: a refused plan does nothing, and the two callbacks are the
    # caller's (the library never talks to a host service or the registry itself).
    $refused = Invoke-ConstructInstanceRemoval -Plan (Get-Plan -Identity (New-TestIdentity -Name 'work-vm') -Name 'work-vm' -Count 1 -IsDefault)
    ok "walk: a refused plan reports the refusal and does nothing else" (
        @($refused).Count -eq 1 -and @($refused)[0].Status -eq 'failed' -and @($refused)[0].Kind -eq 'refused')
    $seen = @()
    $walked = Invoke-ConstructInstanceRemoval -Plan (Get-Plan -Identity (New-TestIdentity)) `
        -RemoveRegistryEntry { param($n) $script:removedName = $n }
    ok "walk: every planned step produced a result" (@($walked).Count -eq @((Get-Plan -Identity (New-TestIdentity)).Steps).Count)
    ok "walk: the registry writer was called with the instance name" ($script:removedName -eq 'work-vm')
    ok "walk: no step threw (they report instead)" (@($walked | Where-Object { $_.Status -notin @('removed', 'skipped', 'failed') }).Count -eq 0)
    # The registry entry is the handle this action is reached BY, so it is only removed
    # once every other required step worked.
    # A step that genuinely FAILS (not "there was nothing there"): an ssh_config path that
    # is a DIRECTORY -- Test-Path says it exists, reading it throws.
    $failingPlan = Get-Plan -Identity (New-TestIdentity)
    $failingPlan.Steps[0].Target = $work
    $failedWalk = Invoke-ConstructInstanceRemoval -Plan $failingPlan -RemoveRegistryEntry { param($n) throw "must not be called" }
    $registryResult = @($failedWalk | Where-Object { $_.Kind -eq 'registry-entry' })[0]
    ok "walk: a failed step KEEPS the registry entry, so the action can be retried" (
        (@($failedWalk | Where-Object { $_.Status -eq 'failed' }).Count -ge 1) -and
        $registryResult.Status -eq 'failed' -and $registryResult.Message -match 'was KEPT')

    # THE WHOLE WALK with both parse failures: the entry stays, and the writer is never
    # called, so the instance is still there to try again.
    $parsePlan = Get-Plan -Identity (New-TestIdentity)
    $parseWork = Join-Path $work "parse"
    New-Item -ItemType Directory -Path $parseWork -Force | Out-Null
    $vsBroken = Join-Path $parseWork "settings.json"
    $ocBroken = Join-Path $parseWork "opencode.global.dat"
    [System.IO.File]::WriteAllText($vsBroken, '{"remote.SSH.remotePlatform": {')
    [System.IO.File]::WriteAllText($ocBroken, '{"server": ')
    foreach ($st in $parsePlan.Steps) {
        if ($st.Kind -eq 'vscode-remote-platform') { $st.Target = $vsBroken }
        if ($st.Kind -eq 'opencode-server') { $st.Target = $ocBroken }
    }
    $script:writerCalled = $false
    $parseWalk = Invoke-ConstructInstanceRemoval -Plan $parsePlan -RemoveRegistryEntry { param($n) $script:writerCalled = $true }
    ok "walk: both parse failures are reported as failures" (
        @($parseWalk | Where-Object { $_.Kind -in @('vscode-remote-platform', 'opencode-server') -and $_.Status -eq 'failed' }).Count -eq 2)
    ok "walk: ...the registry entry is KEPT and the writer never ran" (
        (@($parseWalk | Where-Object { $_.Kind -eq 'registry-entry' })[0].Message -match 'was KEPT') -and -not $script:writerCalled)

    $noWriter = Invoke-ConstructInstanceRemoval -Plan (Get-Plan -Identity (New-TestIdentity))
    ok "walk: without a registry writer the entry is honestly reported as still there" (
        @($noWriter | Where-Object { $_.Kind -eq 'registry-entry' })[0].Message -match 'still in instances.json')
    $remotePlan = Get-Plan -Identity $remoteId -Name 'far-vm' -Confirmation 'far-vm'
    $noDeleter = Invoke-ConstructInstanceRemoval -Plan $remotePlan
    ok "walk: without a host-service client the VM is honestly reported as NOT deleted" (
        @($noDeleter | Where-Object { $_.Kind -eq 'remote-vm-delete' })[0].Message -match 'NOT deleted')
    $failing = Invoke-ConstructInstanceRemoval -Plan $remotePlan -DeleteVm { param($n, $u) throw "boom" }
    ok "walk: a failed VM deletion stops the walk before anything local is touched" (
        @($failing).Count -eq 1 -and @($failing)[0].Status -eq 'failed' -and @($failing)[0].Message -match 'Nothing else was removed')
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

# ── The registry mutator ─────────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Remove-ConstructInstance ===" -ForegroundColor Cyan
& {
    . (Join-Path $repoRoot "lib/AgentVm.Instances.ps1")
    $reg = ConvertFrom-ConstructInstancesJson -Text (@{
        version = 1; defaultInstance = 'work-vm'
        instances = @{ 'work-vm' = @{ backend = 'hyperv-local' }; 'other-vm' = @{ backend = 'hyperv-local' } }
    } | ConvertTo-Json -Depth 8)
    $next = Remove-ConstructInstance -Registry $reg -Name 'work-vm'
    ok "registry: the entry is gone from the copy" (-not $next.Instances.ContainsKey('work-vm'))
    ok "registry: the input registry is untouched" ($reg.Instances.ContainsKey('work-vm'))
    ok "registry: a default that pointed at it falls back to 'agent-vm'" ($next.Default -eq 'agent-vm')
    ok "registry: every other entry survives" ($next.Instances.ContainsKey('other-vm'))
    # 'agent-vm' is removable, and the removal is RECORDED as an explicit null entry so a
    # reader cannot synthesize it back. Round-tripped through the writer and the reader.
    $withoutDefault = Remove-ConstructInstance -Registry $reg -Name 'agent-vm'
    ok "registry: the default instance's name is removable while another exists" (
        -not $withoutDefault.Instances.ContainsKey('agent-vm'))
    ok "registry: ...and the removal is written down" (@($withoutDefault.Removed) -contains 'agent-vm')
    $roundFile = Join-Path ([System.IO.Path]::GetTempPath()) ("b14-reg-" + [Guid]::NewGuid().ToString("N") + ".json")
    [void](Save-ConstructInstances -Registry $withoutDefault -Path $roundFile)
    $roundJson = Get-Content -LiteralPath $roundFile -Raw
    ok "registry: the file records it as an explicit null entry" ($roundJson -match '"agent-vm":\s*null')
    $roundTrip = ConvertFrom-ConstructInstancesJson -Text $roundJson
    ok "registry: a reader does NOT synthesize it back" (
        (-not $roundTrip.Instances.ContainsKey('agent-vm')) -and @($roundTrip.Problems).Count -eq 0)
    ok "registry: the default moved to a survivor" ($roundTrip.Default -eq 'other-vm' -or $roundTrip.Instances.ContainsKey($roundTrip.Default))
    Remove-Item -LiteralPath $roundFile -Force -ErrorAction SilentlyContinue
    # AT THE DISK LEVEL, which is where the removal has to survive: the reader must carry
    # the record, or the next save of ANY surviving instance resurrects the removed one.
    $diskFile = Join-Path ([System.IO.Path]::GetTempPath()) ("b14-disk-" + [Guid]::NewGuid().ToString("N") + ".json")
    @{ version = 1; defaultInstance = 'work-vm'; instances = @{
        'work-vm' = @{ backend = 'hyperv-local' }; 'other-vm' = @{ backend = 'hyperv-local' } } } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $diskFile
    [void](Save-ConstructInstances -Registry (Remove-ConstructInstance -Registry (Read-ConstructInstances -Path $diskFile) -Name 'agent-vm') -Path $diskFile)
    ok "disk: the removal is there after a save/read cycle" (
        -not (Read-ConstructInstances -Path $diskFile).Instances.ContainsKey('agent-vm'))
    ok "disk: the reader CARRIES the record (or the next save resurrects it)" (
        @((Read-ConstructInstances -Path $diskFile).Removed) -contains 'agent-vm')
    [void](Save-ConstructLocalInstance -Name 'other-vm' -Path $diskFile)
    ok "disk: saving an unrelated instance does NOT bring it back" (
        -not (Read-ConstructInstances -Path $diskFile).Instances.ContainsKey('agent-vm'))
    # ...and installing that VM again is what undoes the removal, deliberately.
    [void](Save-ConstructLocalInstance -Name 'agent-vm' -Path $diskFile)
    $restored = Read-ConstructInstances -Path $diskFile
    ok "disk: registering agent-vm again restores it" (
        $restored.Instances.ContainsKey('agent-vm') -and -not (@($restored.Removed) -contains 'agent-vm'))
    Remove-Item -LiteralPath $diskFile -Force -ErrorAction SilentlyContinue

    $oneLeft = Remove-ConstructInstance -Registry $roundTrip -Name 'other-vm'
    ok "registry: a second removal leaves exactly one instance" ($oneLeft.Instances.Count -eq 1)
    $threwLast = $false
    try { [void](Remove-ConstructInstance -Registry $oneLeft -Name 'work-vm') } catch { $threwLast = $true }
    ok "registry: the LAST instance is refused" $threwLast
    $threwUnknown = $false
    try { [void](Remove-ConstructInstance -Registry $reg -Name 'nope') } catch { $threwUnknown = $true }
    ok "registry: an unknown name is an error, never a silent success" $threwUnknown
}

# ── Auto-Install.ps1 wiring ──────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Auto-Install.ps1 wiring ===" -ForegroundColor Cyan
$ai = [System.IO.File]::ReadAllText((Join-Path $repoRoot "Auto-Install.ps1"), [System.Text.Encoding]::UTF8)
ok "auto-install: -Action accepts remove-instance" ($ai -match '\[ValidateSet\([^)]*"remove-instance"[^)]*\)\]\s*\r?\n\s*\[string\]\$Action')
ok "auto-install: -ConfirmInstanceName exists for the typed confirmation" ($ai -match '\[string\]\$ConfirmInstanceName')
ok "auto-install: the action NEVER self-elevates" ($ai -match "if \(-not \`$SkipCreateVm -and \`$Action -ne 'remove-instance'\) \{")
$aiIdx   = $ai.IndexOf("if (`$Action -eq 'remove-instance') {")
$aiElev  = $ai.IndexOf("Self-elevate to Administrator")
# The TOP-LEVEL install-mode resolution (the same string also appears inside
# Resolve-ConstructInstallMode, far above), matched by the comment that introduces it.
$aiMode  = $ai.IndexOf("Resolve the install mode for the runs that never passed through")
ok "auto-install: the handler exists" ($aiIdx -gt 0)
ok "auto-install: it runs BEFORE any install-mode resolution or VM work" ($aiIdx -gt 0 -and $aiMode -gt $aiIdx)
ok "auto-install: the self-elevation block is skipped for it" ($aiElev -gt 0 -and $aiElev -lt $aiIdx)
ok "auto-install: it exits without entering the installer" ($ai.Substring($aiIdx) -match 'exit \$script:ConstructRemoveInstanceRc')
ok "auto-install: the plan comes from the shared library" ($ai -match 'Get-ConstructInstanceRemovalPlan -Name \$Name')
ok "auto-install: the removal is carried out by the shared library" ($ai -match 'Invoke-ConstructInstanceRemoval -Plan \$riPlan')
ok "auto-install: the registry entry goes through the one adapter" ($ai -match 'Unregister-ConstructVm -Name \$name')
ok "auto-install: a remote VM is deleted through the driver contract" ($ai -match 'Remove-ConstructVm -Name \$name')
ok "auto-install: an unattended run must SUPPLY the confirmation, never be asked" (
    $ai -match 'if \(-not \$Interactive -or \[Console\]::IsInputRedirected\) \{[\s\S]{0,300}-ConfirmInstanceName')
ok "auto-install: the removal is given the URLs it needs to match an OpenCode entry" (
    $ai -match '-OpenCodeUrls \$riExtraUrls')
ok "auto-install: those URLs come from the file the PROVISIONER writes" (
    $ai -match 'Get-ConstructT3EndpointFileName -InstanceName \$Name' -and
    $ai -match 'The-Construct\\instances\\\$Name\.json')
ok "auto-install: the local menu offers it" ($ai -match '"Remove instance  forget this VM on this PC')
ok "auto-install: the remote menu offers it" ($ai -match '"Remove instance  DELETE the VM on the host')

Write-Host ""
Write-Host ("  instance-cleanup unit tests - {0}/{1} passed" -f $script:pass, ($script:pass + $script:fail))
Write-Host ""
if ($script:fail -gt 0) { exit 1 }
exit 0
