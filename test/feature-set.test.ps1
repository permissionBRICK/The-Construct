#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
. (Join-Path $repo 'lib/AgentVm.Common.ps1')
. (Join-Path $repo 'lib/AgentVm.FeatureSet.ps1')
. (Join-Path $repo 'lib/AgentVm.InstanceState.ps1')
$count = 0
function Assert($Condition, $Message) {
    if (-not $Condition) { throw $Message }
    $script:count++
}
$table = @(Get-ConstructFeatureTable)
Assert ($table.Count -eq 13) 'All thirteen components, in the specified order'
$expectedParams = @('SkipCompanion','ClaudePartialStreaming','GitCredentialStore','VsCodeServeWeb','VsCodeTunnel','SmbShare','MountRepoShare','MicPassthrough','AutomaticCheckpoints','OpenCodeBackgroundWatcher','T3Code','T3CodeLimitResume','T3CodeHttps')
Assert (($table.Parameter -join ',') -eq ($expectedParams -join ',')) 'Prompt order matches the specification'
$minimal = @($true,$true,$true,$false,$false,$false,$false,$false,$false,$false,$false,$false,$true)
$full = @($true,$true,$true,$true,$false,$true,$false,$true,$false,$true,$true,$true,$true)
foreach ($tier in @('minimal','full')) {
    $result = Resolve-ConstructFeatureSet -FeatureSet $tier
    $expected = if ($tier -eq 'minimal') { $minimal } else { $full }
    Assert ($result.Values -is [Collections.Specialized.OrderedDictionary]) "$tier returns ordered values"
    for ($i=0; $i -lt $table.Count; $i++) {
        $row = $table[$i]
        Assert ($result.Values[$row.Name] -eq $expected[$i]) "$tier $($row.Name) value"
        Assert ($result.Settings[$row.Key] -is [bool] -and $result.Settings[$row.Key] -eq $expected[$i]) "$tier $($row.Name) panel boolean"
        $wanted = $expected[$i]
        if ($row.Parameter -eq 'SkipCompanion') { $wanted = -not $wanted }
        Assert ((ConvertTo-ConstructFeatureBoolean $result.Parameters[$row.Parameter]) -eq $wanted) "$tier $($row.Name) parameter"
        $bound = @{}; $bound[$row.Parameter] = "$(-not $wanted)".ToLowerInvariant()
        $override = Resolve-ConstructFeatureSet -FeatureSet $tier -Bound $bound
        Assert ($override.Values[$row.Name] -eq (-not $expected[$i])) "$tier explicit $($row.Parameter) wins"
    }
    Assert (($result.ForwardParameters -join ',') -eq (($expectedParams + 'T3CodeChannel') -join ',')) "$tier complete forwarding list"
    Assert ($result.Parameters.T3CodeChannel -eq 'stable') "$tier stable T3 channel"
    Assert ($result.Parameters.MountRepoShare -eq 'false') "$tier never maps a drive"
}
$answers = @{}; foreach ($p in $expectedParams) { $answers[$p] = $true }
$custom = Resolve-ConstructFeatureSet -FeatureSet custom -CustomAnswers $answers -Bound @{ T3Code='false'; SkipCompanion=[switch]$true; T3CodeChannel='NIGHTLY' }
Assert (-not $custom.Values.Companion -and -not $custom.Values.'T3 Code') 'Explicit values override Custom answers, including inverted Companion'
Assert ($custom.Parameters.MountRepoShare -eq 'true') 'Custom can opt into drive mapping'
Assert ($custom.Parameters.T3CodeChannel -eq 'nightly') 'Explicit T3 channel wins'
$customDefault = Resolve-ConstructFeatureSet -FeatureSet custom
Assert (($customDefault.Values.Values -join ',') -eq ($minimal -join ',')) 'Unattended Custom uses Minimal answers'
$invalid = $false
try { Resolve-ConstructFeatureSet -FeatureSet banana | Out-Null } catch { $invalid=$true }
Assert $invalid 'Unknown tier is rejected'

# Real parameter metadata proves every component reaches its consumer. Older copies
# expose only a subset, and must not receive an unknown parameter in their splat.
foreach ($file in @('Auto-Install.ps1','Create-AgentVM.ps1','Provision-AgentVM.ps1')) {
    $command = Get-Command (Join-Path $repo $file)
    foreach ($p in $expectedParams + 'T3CodeChannel') {
        if ($file -eq 'Provision-AgentVM.ps1' -and $p -eq 'AutomaticCheckpoints') { continue }
        Assert ($command.Parameters.ContainsKey($p)) "$file declares $p"
    }
    $argsToForward = @{}
    Add-ConstructFeatureArguments -Arguments $argsToForward -Values $custom.Parameters -Command $command
    foreach ($p in $argsToForward.Keys) { Assert ($argsToForward[$p] -eq $custom.Parameters[$p]) "$file forwards resolved $p" }
}
$oldArgs = @{ VmHost='example' }
Add-ConstructFeatureArguments -Arguments $oldArgs -Values $custom.Parameters -Command ([pscustomobject]@{Parameters=@{T3Code=$null}})
Assert ($oldArgs.Count -eq 2 -and $oldArgs.T3Code -eq 'false' -and $oldArgs.VmHost -eq 'example') 'Old downstream parameter probing preserves identity and filters features'

function Test-BoundForwarding {
    param([string]$SmbShare)
    $forward=@{}
    Add-ConstructFeatureArguments -Arguments $forward -Values $PSBoundParameters -Command ([pscustomobject]@{Parameters=@{SmbShare=$null}})
    return $forward
}
Assert ((Test-BoundForwarding -SmbShare false).SmbShare -eq 'false') 'Create forwards the real PSBoundParameters dictionary'
$keep=Resolve-ConstructFeatureSet -FeatureSet full -Bound @{T3Code=''}
Assert ($keep.Parameters.T3Code -eq '' -and -not $keep.Settings.Contains('t3code')) 'Explicit empty keeps the guest value without persisting a false panel toggle'

# Lift the actual installer gates, substituting only Console's static redirected-input
# probe. Menus, registry and Hyper-V are fakes; persistence uses the real disk helpers.
$autoSource = Get-Content -Raw (Join-Path $repo 'Auto-Install.ps1')
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($autoSource,[ref]$null,[ref]$errors)
Assert ($errors.Count -eq 0) 'Auto-Install parses'
foreach ($name in @('Test-ConstructFreshFeatureInstall','Initialize-ConstructInstallFeatures','Restore-ConstructInstallFeatures','New-ConstructRemoteProvisionArgs')) {
    $fn = $ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    Assert ($null -ne $fn) "Installer defines $name"
    Invoke-Expression ($fn.Extent.Text.Replace('[Console]::IsInputRedirected', '$script:InputRedirected').Replace('$PSScriptRoot', '$script:FeatureScriptsDir'))
}
function Test-ConstructPriorLocalInstall { param($VmName) return $script:PriorKey }
function Test-ConstructVmPresent { param($Name) return $script:PriorVm }
function Show-Menu { param($Title,$Options,$Default) $script:Menus++; return $script:Pick }
function Invoke-TuiConfirm { param($ScreenTitle,$Question,[switch]$DefaultNo) $script:Questions += $Question; return (-not $DefaultNo) }
function Show-TuiScreen { param($Title,$Body) $script:Screens++; $script:Summary=$Body }
$dir = Join-Path ([IO.Path]::GetTempPath()) ('construct-features-'+[guid]::NewGuid().ToString('N'))
$oldLocal = $env:LOCALAPPDATA
$installerRoot = $PSScriptRoot
try {
    $env:LOCALAPPDATA = $dir
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    # Extracted functions use the same scripts-dir variable as Auto-Install.
    $script:FeatureScriptsDir = $dir
    function Reset-Scenario {
        $script:ConstructFeaturesInitialized=$false
        $script:ConstructFeatureParameters=@{}
        $script:SkipCreateVm=$false; $script:Action=''; $script:FromPanel=$false
        $script:Auto=$false; $script:NonInteractive=$false; $script:FeatureSetResolved=$false
        $script:FeatureSet=''; $script:InputRedirected=$false
        $script:VmName='Agent-VM'; $script:InstanceName=''; $script:SkipCompanion=[switch]$false
        $script:PriorKey=$false; $script:PriorVm=$false
        $script:Menus=0; $script:Questions=@(); $script:Screens=0; $script:Pick=0
    }
    $fresh = [pscustomobject]@{Exists=$false; Entries=@{'agent-vm'=@{}}}
    foreach ($gate in @('fresh','registry','key','vm','skip','reprovision','reinstall','redownload','panel','auto','noninteractive','redirected','remote','explicit','custom','handoff','fresh-add-config')) {
        Reset-Scenario
        $snapshot=$fresh; $bound=@{}
        switch ($gate) {
            registry { $snapshot=[pscustomobject]@{Exists=$true; Entries=@{'work-vm'=@{}}} }
            key { $script:PriorKey=$true }
            vm { $script:PriorVm=$true }
            skip { $script:SkipCreateVm=$true }
            reprovision { $script:Action='reprovision' }
            reinstall { $script:Action='reinstall' }
            redownload { $script:Action='redownload' }
            panel { $script:FromPanel=$true }
            auto { $script:Auto=$true }
            noninteractive { $script:NonInteractive=$true }
            redirected { $script:InputRedirected=$true }
            remote { $script:Backend='hyperv-remote'; $bound.Backend='hyperv-remote' }
            explicit { $script:FeatureSet='full'; $bound.T3Code='false' }
            custom { $script:Pick=2 }
            fresh-add-config { $script:Action='add-config' }
            handoff { $script:FeatureSetResolved=$true; $script:FeatureSet='custom'; $bound.SkipCompanion=[switch]$true }
        }
        Initialize-ConstructInstallFeatures -Bound $bound -Snapshot $snapshot
        $expectedMenus = if ($gate -in @('fresh','remote','custom','fresh-add-config')) { 1 } else { 0 }
        Assert ($script:Menus -eq $expectedMenus) "$gate feature menu gate"
        $expectedQuestions = if ($gate -eq 'custom') { 13 } else { 0 }
        Assert ($script:Questions.Count -eq $expectedQuestions) "$gate Custom question gate"
        if ($gate -in @('fresh','remote','custom','explicit','panel','auto','noninteractive','redirected','fresh-add-config')) {
            Assert ($script:Screens -eq (1 + $expectedMenus) -and $script:Summary.Count -eq 13) "$gate informational summary"
            Assert ($bound.ContainsKey('T3CodeLimitResume') -and $bound.ContainsKey('T3CodeHttps')) "$gate elevation forwards all choices"
            $saved=Read-ConstructSettings -Dir $dir
            Assert ($saved.companion -and $saved.gitCredentialStore) "$gate client preference persistence"
        }
        if ($gate -eq 'explicit') { Assert ($T3Code -eq 'false' -and $SmbShare -eq 'true') 'Explicit T3 off with Full SMB on' }
        if ($gate -eq 'custom') { Assert (($script:Questions -join ',') -eq ($table.Prompt -join ',')) 'Custom asks rows in table order' }
    }
    Reset-Scenario
    $script:FeatureSet='full'
    $bound=@{ GitCredentialStore='false'; SkipCompanion=[switch]$true }
    Initialize-ConstructInstallFeatures -Bound $bound -Snapshot $fresh
    Restore-ConstructInstallFeatures -Name 'agent-vm'
    $saved=Read-ConstructSettings -Dir $dir
    Assert (-not $saved.companion -and -not $saved.gitCredentialStore) 'Explicit client opt-outs persist'
    foreach ($row in $table | Where-Object { $_.Parameter -ne 'SkipCompanion' }) {
        Assert ($saved.PSObject.Properties.Name -contains $row.Key) "Default store writes panel key $($row.Key)"
    }
    Assert ($saved.t3code -and $saved.t3codeLimitResume -and $saved.t3codeChannel -eq 'stable') 'T3 panel keys use actual lowercase spellings'
    # A rebuild must ignore a newly supplied tier and retain saved per-component choices.
    Reset-Scenario
    $script:Action='reinstall'; $script:FeatureSet='minimal'
    $bound=@{MicPassthrough='false'}
    Initialize-ConstructInstallFeatures -Bound $bound -Snapshot $fresh
    Restore-ConstructInstallFeatures -Name 'agent-vm'
    Assert ($SmbShare -eq 'true' -and $T3Code -eq 'true' -and $MicPassthrough -eq 'false') 'Rebuild retains saved values with explicit override'
    Assert ($script:SkipCompanion) 'Saved Companion off suppresses the client hook'
    Reset-Scenario
    $script:Action='reinstall'
    Initialize-ConstructInstallFeatures -Bound @{SkipCompanion=[switch]$false} -Snapshot $fresh
    Assert ((Read-ConstructSettings -Dir $dir).companion -eq $true) 'Explicit Companion enable overrides saved opt-out before the hook'
    # Remote uses the same resolved features through its actual argument builder.
    $script:ConstructFeatureParameters=(Resolve-ConstructFeatureSet -FeatureSet full).Parameters
    $script:RemoteProvCmd=Get-Command (Join-Path $repo 'Provision-AgentVM.ps1')
    $script:RemoteBound=@{}
    $remoteArgs=New-ConstructRemoteProvisionArgs -Name 'work-vm' -Endpoint @{SshHost='example';SshPort=2222} -ServiceUrl 'https://example:7462' -ConfigBranch 'vm-work-vm'
    foreach ($p in $expectedParams | Where-Object { $_ -ne 'AutomaticCheckpoints' }) {
        Assert ($remoteArgs[$p] -eq $script:ConstructFeatureParameters[$p]) "Remote forwards resolved $p"
    }
    # Named VM uses its instance file and retains the install-wide credential preference.
    $script:ConstructFeatureParameters=@{SmbShare='false'; T3Code='false'; GitCredentialStore='false'; T3CodeChannel='nightly'}
    Restore-ConstructInstallFeatures -Name 'work-vm'
    $named=Read-ConstructInstanceState -Name 'work-vm' -Dir $dir
    Assert ($named.smbShare -eq $false -and $named.t3code -eq $false -and $named.t3codeChannel -eq 'nightly') 'Named instance saves its own settings'
    Assert ((Read-ConstructSettings -Dir $dir).smbShare -eq $true) 'Named instance leaves default VM preferences intact'
    Assert ($named.PSObject.Properties.Name -notcontains 'gitCredentialStore') 'Credential preference follows the existing install-wide split'
    # Saved files are tolerant inputs. Invalid caller/custom values still fail.
    Reset-Scenario
    Save-ConstructSettings -Dir $dir -Values @{vmAutoCheckpoints=1; t3code=$null; smbShare='yes'; t3codeChannel='junk'}
    Restore-ConstructInstallFeatures -Name 'agent-vm'
    Assert ($AutomaticCheckpoints -eq 'true') 'Saved numeric 1 retains legacy checkpoint support'
    Assert (-not $script:ConstructFeatureParameters.ContainsKey('T3Code')) 'Saved null is treated as no T3 preference'
    Assert (-not $script:ConstructFeatureParameters.ContainsKey('SmbShare')) 'Saved junk boolean is treated as no SMB preference'
    Assert (-not $script:ConstructFeatureParameters.ContainsKey('T3CodeChannel')) 'Saved junk T3 channel is ignored'
    foreach ($inputKind in @('Bound','CustomAnswers')) {
        $bad=@{FeatureSet='custom'}; $bad[$inputKind]=@{SmbShare='yes'}
        $threw=$false
        try { Resolve-ConstructFeatureSet @bad | Out-Null } catch { $threw=$true }
        Assert $threw "Invalid $inputKind boolean remains an error"
    }
    # Simulate a partial checkout without the optional instance-state library.
    & {
        function Get-Command {
            [CmdletBinding()]param([string]$Name)
            if ($Name -in @('Read-ConstructInstanceState','Save-ConstructInstanceState')) { return $null }
            Microsoft.PowerShell.Core\Get-Command @PSBoundParameters
        }
        $script:ConstructFeatureParameters=@{SmbShare='false'}
        Restore-ConstructInstallFeatures -Name 'agent-vm'
        Assert ((Read-ConstructSettings -Dir $dir).smbShare -eq $false) 'Missing optional library falls back to legacy default store'
        $script:ConstructFeatureParameters=@{}
        Restore-ConstructInstallFeatures -Name 'work-vm'
        Assert (-not $script:ConstructFeatureParameters.ContainsKey('SmbShare')) 'Missing optional library does not borrow default VM settings for a named VM'
        $script:ConstructFeatureParameters=@{SmbShare='true'}
        Restore-ConstructInstallFeatures -Name 'work-vm'
        Assert ((Read-ConstructSettings -Dir $dir).smbShare -eq $false) 'Missing optional library does not write named VM values into default store'
    }
    # Keep the existing disclosure even when the tier already answered the question.
    & {
        function Read-Host { param($Prompt) return '' }
        $script:Questions=@()
        $output=@(Resolve-GitIdentity -Dir $dir -Name 'Test' -Email 'test@example.invalid' -CredentialStore yes 6>&1)
        Assert (($output -join ' ') -match 'PLAINTEXT' -and ($output -join ' ') -match '~/.git-credentials') 'Resolved credential choice still displays the storage warning'
        Assert ($script:Questions.Count -eq 0) 'Resolved credential choice does not ask again'
    }
} finally {
    $PSScriptRoot=$installerRoot
    $env:LOCALAPPDATA=$oldLocal
    Remove-Item -LiteralPath $dir -Recurse -Force
}
# Source-level ordering and coverage of actual call sites, including the direct remote path.
Assert ($autoSource.IndexOf('Initialize-ConstructInstallFeatures -Bound') -lt $autoSource.IndexOf('Invoke-ConstructCompanionInstallHook -ScriptsDir')) 'Feature choice precedes Companion installation'
Assert ($autoSource.IndexOf('Initialize-ConstructInstallFeatures -Bound') -lt $autoSource.IndexOf('-ScreenTitle "VM memory"')) 'Feature choice precedes resource questions'
foreach ($argsName in @('createArgs','provArgs','reprovArgs','acReprovArgs','a')) {
    Assert ($autoSource.Contains('Add-ConstructFeatureArguments -Arguments $'+$argsName)) "Actual $argsName call path forwards features"
}
foreach ($pair in @(@('reprovArgs','reprovCmd'),@('acReprovArgs','acProvCmd'),@('createArgs','createCmd'),@('provArgs','provCmd'))) {
    Assert ($autoSource.Contains('Add-ConstructFeatureArguments -Arguments $'+$pair[0]+' -Values $script:ConstructFeatureParameters -Command $'+$pair[1])) "Forwarding $($pair[0]) reuses its guarded parameter probe"
}
$noProbe=@{VmHost='example'}
Add-ConstructFeatureArguments -Arguments $noProbe -Values $custom.Parameters -Command $null
Assert ($noProbe.Count -eq 1 -and $noProbe.VmHost -eq 'example') 'Failed parameter probe adds no feature arguments'
Assert ($autoSource.Contains("AutomaticCheckpoints = (`$AutomaticCheckpoints -eq 'true')")) 'Remote VM descriptor respects checkpoint choice'
foreach ($file in @('Auto-Install.ps1','Create-AgentVM.ps1','Provision-AgentVM.ps1','lib/AgentVm.Common.ps1','lib/AgentVm.FeatureSet.ps1')) {
    $text=Get-Content -Raw (Join-Path $repo $file)
    $null=[scriptblock]::Create($text)
    $scriptAst=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$null,[ref]$null)
    $modern=@($scriptAst.FindAll({param($n) $n.GetType().Name -in @('TernaryExpressionAst','PipelineChainAst') -or
        ($n.GetType().Name -eq 'BinaryExpressionAst' -and "$($n.Operator)" -eq 'QuestionQuestion') -or
        ($n.GetType().Name -in @('MemberExpressionAst','InvokeMemberExpressionAst','IndexExpressionAst') -and $n.NullConditional)},$true))
    Assert ($modern.Count -eq 0) "$file has no PS7 syntax"
}
Write-Host "Feature set: $count passed"
