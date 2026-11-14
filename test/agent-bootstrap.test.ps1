#Requires -Version 7.0
<#
    The move to the device agent from the PowerShell side (design 13.2), on Linux with every Windows
    command stubbed:
      * install.ps1 and Update-Construct.ps1, the standalone bootstraps: channel from the ref, the
        agent package checked against the manifest, the old Companion asked to quit and started again
        when anything fails, a running agent handed over to, the result file.
      * the three functions the old updater calls in the downloaded lib: the marker installs the agent
        and throws on failure, the Companion hook reports unchanged.
      * the old Update-Construct.ps1 bodies (class E and F, test/fixtures/legacy-updaters) run against a
        download of this tree: what they dot-source and call exists, a failure in the hook reaches
        their result file as "fail", a success as "ok".
      * the VS Code extension the package carries (construct.vsix) is installed where VS Code is, before
        the agent starts; VS Code refusing it fails the install and the update, no VS Code is no failure.
      * the shipped scripts parse as Windows PowerShell 5.1 syntax and are ASCII.

        pwsh -NoProfile -File test/agent-bootstrap.test.ps1
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$script:count = 0
function Assert($Condition, [string]$Message) { if (-not $Condition) { throw "FAIL: $Message" }; $script:count++ }
$dir = Join-Path ([IO.Path]::GetTempPath()) ('agent-bootstrap-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $dir | Out-Null

# ---- syntax: Windows PowerShell 5.1 and ASCII ----
function Test-Ps51([string]$Text, [string]$Name) {
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
    Assert (-not $errors) "parse $Name"
    Assert (@($tokens | Where-Object { $_.Kind.ToString() -in @('QuestionQuestion', 'QuestionQuestionEquals', 'QuestionDot', 'QuestionLBracket', 'AndAnd', 'OrOr') }).Count -eq 0) "no PS7 operator in $Name"
    Assert ($null -eq $ast.Find({ param($n) $n.GetType().Name -in @('TernaryExpressionAst', 'PipelineChainAst') }, $true)) "no ternary or pipeline chain in $Name"
    Assert ($Text -notmatch '[^\x00-\x7F]') "$Name is ASCII"
    return $ast
}
foreach ($file in @('install.ps1', 'Update-Construct.ps1', 'Update-T3Code.ps1', 'Get-ConstructT3PairingLink.ps1', 'lib/Construct.AgentShim.ps1', 'agent/install/Install-ConstructAgent.ps1')) {
    $null = Test-Ps51 ([IO.File]::ReadAllText((Join-Path $repo $file))) $file
}
$hookFunctions = @{
    'lib/AgentVm.Common.ps1'      = @('Install-ControlPanelExtension', 'Set-ConstructInstalledMarker', 'Save-ConstructInstalledMarker')
    'lib/Construct.Companion.ps1' = @('Invoke-ConstructCompanionInstallHook')
}
foreach ($file in $hookFunctions.Keys) {
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $file), [ref]$null, [ref]$null)
    foreach ($name in $hookFunctions[$file]) {
        $fn = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
        Assert ($null -ne $fn) "$file defines $name"
        $null = Test-Ps51 $fn.Extent.Text "$name in $file"
    }
}

# ---- the old updaters find what they dot-source and call ----
foreach ($class in @('e', 'f')) {
    $text = [IO.File]::ReadAllText((Join-Path $PSScriptRoot "fixtures/legacy-updaters/Update-Construct.class-$class.ps1"))
    $ast = [Management.Automation.Language.Parser]::ParseInput($text, [ref]$null, [ref]$null)
    $dotted = @($ast.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.InvocationOperator -eq 'Dot' }, $true) |
        ForEach-Object { [regex]::Match($_.Extent.Text, '["''](lib[\\/][^"'']+)["'']').Groups[1].Value } | Where-Object { $_ })
    $defined = @{}
    foreach ($rel in $dotted) {
        $path = Join-Path $repo ($rel -replace '\\', '/')
        Assert (Test-Path -LiteralPath $path) "class $class dot-sources $rel, which this tree has"
        $libAst = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$null, [ref]$null)
        foreach ($fn in $libAst.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)) { $defined[$fn.Name] = $true }
    }
    foreach ($name in @('Install-ControlPanelExtension', 'Set-ConstructInstalledMarker', 'Invoke-ConstructCompanionInstallHook', 'Remove-ConstructStaleSourceFiles', 'Write-ConstructSourceManifest')) {
        Assert ($text.Contains($name)) "class $class calls $name"
        Assert ($defined.ContainsKey($name)) "class $class finds $name in the downloaded lib"
    }
    $marker = $ast.Find({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Set-ConstructInstalledMarker' }, $true)
    Assert ($marker.Extent.Text -match '@markerArgs') "class $class passes Root, Repo, Ref (and Commit) to the marker"
}

# ---- a fake world: the agent package, the channel manifest, the stubs ----
$package = Join-Path $dir 'package'
New-Item -ItemType Directory -Path $package | Out-Null
Set-Content (Join-Path $package 'construct-agent.exe') 'fake agent'
Set-Content (Join-Path $package 'construct.vsix') 'fake extension'
$zip = Join-Path $dir 'agent.zip'
Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip
$sha = (Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()

# One harness per run, in a child process with stdin redirected (the scripts pause only on a console).
$harness = Join-Path $dir 'harness.ps1'
Set-Content $harness @'
param([string]$Entry, [string]$World)
$ErrorActionPreference = 'Stop'
$w = Get-Content -Raw (Join-Path $World 'world.json') | ConvertFrom-Json
function global:Log([string]$Line) { Add-Content -LiteralPath (Join-Path $World 'calls.log') $Line }
$env:LOCALAPPDATA = Join-Path $World 'local'
# VS Code's CLI: only this test's stubs (bin/code of the world logs its arguments), never one of the machine.
$testDir = Split-Path $World -Parent
$env:PATH = @($env:PATH -split [IO.Path]::PathSeparator | Where-Object { $_ -and ($_.StartsWith($testDir) -or -not (Test-Path -LiteralPath (Join-Path $_ 'code'))) }) -join [IO.Path]::PathSeparator
if (Test-Path (Join-Path $World 'bin/code')) { $env:PATH = (Join-Path $World 'bin') + [IO.Path]::PathSeparator + $env:PATH }
$agentHome = Join-Path $env:LOCALAPPDATA 'Construct/agent'
function global:Invoke-RestMethod {
    param($Uri, $Method = 'Get', $Headers, $Body, $ContentType, $TimeoutSec, [switch]$UseBasicParsing, $MaximumRedirection)
    Log "rest $Method $Uri"
    if ($Uri -match '/v1/quit$') { Set-Content (Join-Path $World 'companion-quit') 'yes'; return @{} }
    if ($Uri -match '/api/health$') { if (Test-Path (Join-Path $World 'agent-started')) { return @{ status = 'ok' } }; throw 'connection refused' }
    if ($Uri -match '/api/update/check$') { return @{ available = $true } }
    if ($Uri -match '/api/') { return @{} }
    if ($Uri -match 'manifest\.json$') {
        return [pscustomobject]@{ version = '2.0.0+abc'; releaseTag = 'host-abc'; packages = [pscustomobject]@{ agentWindows = [pscustomobject]@{ asset = 'construct-agent-win-x64.zip'; sha256 = $w.sha } } }
    }
    throw "unexpected REST call $Uri"
}
function global:Invoke-WebRequest {
    param($Uri, $OutFile, $TimeoutSec, [switch]$UseBasicParsing)
    Log "web $Uri"
    if ($Uri -match '^https://codeload\.github\.com/') { Copy-Item $w.source $OutFile; return }
    Copy-Item $w.package $OutFile
}
function global:Start-Process { param($FilePath, $ArgumentList)
    Log "start $FilePath $ArgumentList"
    if ($FilePath -match 'construct-agent\.exe$') {
        if ($w.agentStarts) {
            Set-Content (Join-Path $World 'agent-started') 'yes'
            Set-Content (Join-Path $agentHome 'endpoint.json') '{"port":5555,"token":"agent-token-value"}'
        }
    }
}
function global:Get-Process { param($Id, $Name, $ErrorAction)
    if ($Name -eq 'ConstructCompanion') { return @() }
    if ($Id -eq 4242 -and -not (Test-Path (Join-Path $World 'companion-quit'))) { return [pscustomobject]@{ Id = 4242 } }
    return $null
}
function global:Start-Sleep { param($Milliseconds, $Seconds) }
function global:Set-ItemProperty { param($Path, $Name, $Value) Log "registry set $Name $Value" }
function global:Remove-ItemProperty { param($Path, $Name, $ErrorAction) Log "registry remove $Name" }
$named = @{}; $list = @($w.arguments)
for ($i = 0; $i -lt $list.Count; $i++) {
    $key = ([string]$list[$i]).TrimStart('-')
    if ($i + 1 -lt $list.Count -and -not ([string]$list[$i + 1]).StartsWith('-')) { $named[$key] = $list[$i + 1]; $i++ } else { $named[$key] = $true }
}
$global:LASTEXITCODE = 0
& $Entry @named
exit $LASTEXITCODE
'@

function New-World([string]$Name, [hashtable]$Options) {
    $world = Join-Path $dir $Name
    New-Item -ItemType Directory -Path (Join-Path $world 'local/The-Construct/companion') -Force | Out-Null
    $doc = @{ sha = $sha; package = $zip; agentStarts = $true; arguments = @('-Ref', 'main'); source = '' }
    foreach ($k in $Options.Keys) { $doc[$k] = $Options[$k] }
    if ($Options.companion) {
        Set-Content (Join-Path $world 'local/The-Construct/companion/endpoint.json') '{"v":1,"ipcApiVersion":1,"port":4100,"pid":4242,"token":"companion-token"}'
        New-Item -ItemType Directory -Path (Join-Path $world 'local/Programs/ConstructCompanion') -Force | Out-Null
        Set-Content (Join-Path $world 'local/Programs/ConstructCompanion/ConstructCompanion.exe') 'old'
    }
    if ($Options.code) {
        New-Item -ItemType Directory -Path (Join-Path $world 'bin') -Force | Out-Null
        $exit = 0
        if ($Options.code -eq 'fail') { $exit = 1 }
        Set-Content (Join-Path $world 'bin/code') "#!/bin/sh`necho `"code `$*`" >> '$world/calls.log'`necho 'a warning on stderr' >&2`nexit $exit"
        & chmod +x (Join-Path $world 'bin/code')
    }
    if ($Options.agentRunning) {
        New-Item -ItemType Directory -Path (Join-Path $world 'local/Construct/agent') -Force | Out-Null
        Set-Content (Join-Path $world 'local/Construct/agent/endpoint.json') '{"port":5555,"token":"agent-token-value"}'
        Set-Content (Join-Path $world 'agent-started') 'yes'
    }
    $doc | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $world 'world.json')
    return $world
}
function Invoke-World([string]$World, [string]$Entry, [string[]]$More = @()) {
    $output = '' | & pwsh -NoProfile -NonInteractive -File $harness $Entry $World *>&1
    return @{ Code = $LASTEXITCODE; Output = ($output -join "`n"); Calls = @(Get-Content (Join-Path $World 'calls.log') -ErrorAction SilentlyContinue) }
}

# ---- install.ps1: a fresh move from a branch, with the old Companion running ----
$world = New-World 'fresh' @{ companion = $true; arguments = @('-Ref', 'rework') }
$run = Invoke-World $world (Join-Path $repo 'install.ps1')
Assert ($run.Code -eq 0) "install.ps1 succeeds: $($run.Output)"
Assert ($run.Calls -contains 'rest Get https://github.com/permissionBRICK/The-Construct/releases/download/channel-nightly/manifest.json') 'a branch ref reads the nightly channel'
Assert ($run.Calls -contains 'rest Post http://127.0.0.1:4100/v1/quit') 'the old Companion is asked to quit, its own way'
$quitAt = [array]::IndexOf($run.Calls, 'rest Post http://127.0.0.1:4100/v1/quit')
$downloadAt = [array]::IndexOf($run.Calls, ($run.Calls | Where-Object { $_ -like 'web *' } | Select-Object -First 1))
Assert ($quitAt -lt $downloadAt) 'the Companion quits before the package is installed'
Assert ($run.Calls -contains 'web https://github.com/permissionBRICK/The-Construct/releases/download/host-abc/construct-agent-win-x64.zip') 'the package comes from the manifest release'
$agentHome = Join-Path $world 'local/Construct/agent'
Assert (Test-Path (Join-Path $agentHome 'versions/2.0.0+abc/construct-agent.exe')) 'the package is unpacked into versions/<version>'
Assert ((Get-Content -Raw (Join-Path $agentHome 'install.json') | ConvertFrom-Json).current -eq '2.0.0+abc') 'install.json names it current'
Assert ((Get-Content -Raw (Join-Path $agentHome 'device.json') | ConvertFrom-Json).channel -eq 'nightly') 'the device follows the nightly channel'
Assert (@($run.Calls | Where-Object { $_ -like 'registry set ConstructAgent *--background' }).Count -eq 1) 'the agent starts with Windows'
Assert ($run.Calls -contains 'registry remove ConstructCompanion') 'the Companion no longer starts with Windows'
Assert (-not ($run.Calls | Where-Object { $_ -like 'start *ConstructCompanion.exe*' })) 'the Companion is not started again after a success'
Assert (-not (Test-Path (Join-Path $world 'local/The-Construct/The-Construct-rework'))) 'no install folder per ref'
Assert ($run.Output -notmatch 'agent-token-value|companion-token') 'no token in the output'

Assert ($run.Output -match 'VS Code is not installed') "no VS Code is no failure: $($run.Output)"

# ---- install.ps1: the extension the package carries goes into VS Code, before the agent starts ----
$world = New-World 'vscode' @{ companion = $true; code = 'ok' }
$run = Invoke-World $world (Join-Path $repo 'install.ps1')
Assert ($run.Code -eq 0) "install.ps1 with VS Code succeeds: $($run.Output)"
$vsix = Join-Path $world 'local/Construct/agent/versions/2.0.0+abc/construct.vsix'
$codeAt = [array]::IndexOf($run.Calls, "code --install-extension $vsix --force")
$startAt = [array]::IndexOf($run.Calls, ($run.Calls | Where-Object { $_ -like 'start *construct-agent.exe*' } | Select-Object -First 1))
Assert ($codeAt -ge 0) "VS Code installs the package's extension: $($run.Calls -join ' | ')"
Assert ($codeAt -lt $startAt) 'the extension is installed before the agent starts'
Assert ($run.Output -notmatch 'a warning on stderr') 'what code writes to stderr is not shown and not a failure'

# ---- install.ps1: VS Code refusing the extension fails the install, as the old updater's step did ----
$world = New-World 'vscode-fail' @{ companion = $true; code = 'fail' }
$run = Invoke-World $world (Join-Path $repo 'install.ps1')
Assert ($run.Code -ne 0 -and $run.Output -match 'did not install the Construct extension') "a refused extension fails: $($run.Output)"
Assert (-not ($run.Calls | Where-Object { $_ -like 'start *construct-agent.exe*' })) 'the agent is not started'
Assert (@($run.Calls | Where-Object { $_ -like 'start *ConstructCompanion.exe --background' }).Count -eq 1) 'the Companion is started again'
Assert (-not (Test-Path (Join-Path $world 'local/Construct/agent/install.json'))) 'install.json is not written'

# ---- Install-ConstructAgent.ps1 (the agent's own Windows installer) installs the extension too ----
$world = New-World 'agent-installer' @{ code = 'ok'; arguments = @('-Package', $zip, '-Version', '3.0.0', '-NoStart') }
$run = Invoke-World $world (Join-Path $repo 'agent/install/Install-ConstructAgent.ps1')
Assert ($run.Code -eq 0) "Install-ConstructAgent.ps1 succeeds: $($run.Output)"
Assert (@($run.Calls | Where-Object { $_ -match '^code --install-extension .*3\.0\.0.construct\.vsix --force$' }).Count -eq 1) "it installs the extension: $($run.Calls -join ' | ')"
$world = New-World 'agent-installer-fail' @{ code = 'fail'; arguments = @('-Package', $zip, '-Version', '3.0.0', '-NoStart') }
$run = Invoke-World $world (Join-Path $repo 'agent/install/Install-ConstructAgent.ps1')
Assert ($run.Code -ne 0 -and $run.Output -match 'did not install the Construct extension') "and fails when VS Code refuses it: $($run.Output)"

# ---- install.ps1: a package that does not match the manifest leaves the old Companion running ----
$world = New-World 'mismatch' @{ companion = $true; sha = ('0' * 64) }
$run = Invoke-World $world (Join-Path $repo 'install.ps1')
Assert ($run.Code -ne 0 -and $run.Output -match 'does not match the SHA-256') "a mismatch fails: $($run.Output)"
Assert (@($run.Calls | Where-Object { $_ -like 'start *ConstructCompanion.exe --background' }).Count -eq 1) 'the Companion is started again'
Assert (-not ($run.Calls -contains 'registry remove ConstructCompanion')) 'its autostart stays'
Assert (-not (Test-Path (Join-Path $world 'local/Construct/agent/versions/2.0.0+abc'))) 'nothing is installed'

# ---- install.ps1: an agent that does not come up counts as a failure too ----
$world = New-World 'nostart' @{ companion = $true; agentStarts = $false }
$run = Invoke-World $world (Join-Path $repo 'install.ps1')
Assert ($run.Code -ne 0 -and $run.Output -match 'did not start') "an agent that does not start fails: $($run.Output)"
Assert (@($run.Calls | Where-Object { $_ -like 'start *ConstructCompanion.exe --background' }).Count -eq 1) 'the Companion is started again'

# ---- install.ps1 with an agent running: hand over ----
$world = New-World 'handover' @{ agentRunning = $true; companion = $true }
$run = Invoke-World $world (Join-Path $repo 'install.ps1')
Assert ($run.Code -eq 0) "hand-over succeeds: $($run.Output)"
Assert ($run.Calls -contains 'rest Post http://127.0.0.1:5555/api/update/check' -and $run.Calls -contains 'rest Post http://127.0.0.1:5555/api/update/install') 'the running agent updates itself'
Assert (-not ($run.Calls | Where-Object { $_ -like 'web *' -or $_ -like '*v1/quit' })) 'nothing is downloaded, the Companion is left alone'

# ---- hand-over with VS Code: the installed version's extension replaces an old one; a refusal fails ----
foreach ($case in @(@{ code = 'ok'; ok = $true }, @{ code = 'fail'; ok = $false })) {
    $world = New-World ('handover-vscode-' + $case.code) @{ agentRunning = $true; code = $case.code }
    $folder = Join-Path $world 'local/Construct/agent/versions\1.0.0'
    New-Item -ItemType Directory -Path $folder -Force | Out-Null
    Set-Content (Join-Path $folder 'construct.vsix') 'fake extension'
    Set-Content (Join-Path $world 'local/Construct/agent/install.json') '{"current":"1.0.0","previous":null,"pending":false}'
    $run = Invoke-World $world (Join-Path $repo 'install.ps1')
    Assert (@($run.Calls | Where-Object { $_ -like 'code --install-extension *construct.vsix --force' }).Count -eq 1) "hand-over installs the extension ($($case.code)): $($run.Calls -join ' | ')"
    Assert (($run.Code -eq 0) -eq $case.ok) "hand-over with VS Code $($case.code) exits by the extension step: $($run.Output)"
}
$world = New-World 'handover-vscode-novsix' @{ agentRunning = $true; code = 'ok' }
$run = Invoke-World $world (Join-Path $repo 'install.ps1')
Assert ($run.Code -eq 0 -and -not ($run.Calls | Where-Object { $_ -like 'code *' })) "an installed version without an extension is left to the agent: $($run.Output)"

# ---- Update-Construct.ps1 (the bootstrap): the old parameters and the result file ----
$scripts = Join-Path $dir 'scripts'
New-Item -ItemType Directory -Path $scripts | Out-Null
Copy-Item (Join-Path $repo 'install.ps1'), (Join-Path $repo 'Update-Construct.ps1') $scripts
foreach ($case in @(@{ name = 'update-ok'; sha = $sha; want = 'ok' }, @{ name = 'update-fail'; sha = ('0' * 64); want = 'fail' },
                    @{ name = 'update-vscode-fail'; sha = $sha; want = 'fail'; code = 'fail' }, @{ name = 'update-vscode-ok'; sha = $sha; want = 'ok'; code = 'ok' })) {
    $result = Join-Path $dir ($case.name + '.result')
    $world = New-World $case.name @{ companion = $true; sha = $case.sha; code = $case.code; arguments = @('-Repo', 'permissionBRICK/The-Construct', '-Ref', 'main', '-SkipCompanion', '-ResultFile', $result) }
    $run = Invoke-World $world (Join-Path $scripts 'Update-Construct.ps1')
    Assert ((Get-Content $result).Trim() -eq $case.want) "Update-Construct.ps1 writes $($case.want): $($run.Output)"
    Assert (($run.Code -eq 0) -eq ($case.want -eq 'ok')) "Update-Construct.ps1 exits by its result ($($case.name))"
}

# ---- the three hook functions on their own ----
. (Join-Path $repo 'lib/Construct.Companion.ps1')
Assert ((Invoke-ConstructCompanionInstallHook -ScriptsDir $repo -SkipCompanion 6>$null) -eq 'unchanged') 'the Companion hook reports unchanged'
$markerRoot = Join-Path $dir 'marker-root'
New-Item -ItemType Directory -Path $markerRoot | Out-Null
. (Join-Path $repo 'lib/AgentVm.Common.ps1')
$failed = $false
try { $null = Set-ConstructInstalledMarker -Root $markerRoot -Repo 'permissionBRICK/The-Construct' -Ref 'main' -Commit ('c' * 40) } catch { $failed = $true }
Assert $failed 'the marker throws when the agent cannot be installed (no install.ps1)'
Assert (-not (Test-Path (Join-Path $markerRoot '.construct-settings.json'))) 'and does not advance the marker'
Set-Content (Join-Path $markerRoot 'install.ps1') 'param($Repo, $Ref) Set-Content (Join-Path $PSScriptRoot "installed") "$Repo $Ref"'
$sha1 = Set-ConstructInstalledMarker -Root $markerRoot -Repo 'permissionBRICK/The-Construct' -Ref 'main' -Commit ('c' * 40)
Assert ((Get-Content (Join-Path $markerRoot 'installed')) -eq 'permissionBRICK/The-Construct main') 'the marker runs install.ps1 of the downloaded release with the repo and ref'
Assert ($sha1 -eq ('c' * 40) -and (Get-Content -Raw (Join-Path $markerRoot '.construct-settings.json')) -match ('c' * 40)) 'then advances the marker'
Set-Content (Join-Path $markerRoot 'install.ps1') 'throw "agent install failed"'
$failed = $false
try { $null = Set-ConstructInstalledMarker -Root $markerRoot -Repo 'permissionBRICK/The-Construct' -Ref 'main' -Commit ('d' * 40) } catch { $failed = $true }
Assert ($failed -and (Get-Content -Raw (Join-Path $markerRoot '.construct-settings.json')) -notmatch ('d' * 40)) 'a failed install throws and leaves the marker'

# ---- the old updaters against a download of this tree ----
$tree = Join-Path $dir 'tree/The-Construct-rework'
New-Item -ItemType Directory -Path $tree -Force | Out-Null
foreach ($item in @('lib', 'extension', 'install.ps1', 'Update-Construct.ps1')) { Copy-Item -Recurse (Join-Path $repo $item) $tree }
Remove-Item -Recurse -Force (Join-Path $tree 'extension/test'), (Join-Path $tree 'extension/node_modules') -ErrorAction SilentlyContinue
$source = Join-Path $dir 'source.zip'
Compress-Archive -Path $tree -DestinationPath $source
# VS Code's CLI: always succeeds (the extension install itself is unchanged by this release).
$bin = Join-Path $dir 'bin'
New-Item -ItemType Directory -Path $bin | Out-Null
Set-Content (Join-Path $bin 'code') "#!/bin/sh`nexit 0"
& chmod +x (Join-Path $bin 'code')
$env:PATH = $bin + [IO.Path]::PathSeparator + $env:PATH
foreach ($class in @('e', 'f')) {
    $old = Join-Path $dir "old-$class"
    New-Item -ItemType Directory -Path (Join-Path $old 'lib') -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot "fixtures/legacy-updaters/Update-Construct.class-$class.ps1") (Join-Path $old 'Update-Construct.ps1')
    Copy-Item (Join-Path $PSScriptRoot 'fixtures/legacy-updaters/AgentVm.LifecycleRun.ps1') (Join-Path $old 'lib/AgentVm.LifecycleRun.ps1')
    foreach ($case in @(@{ name = "class-$class-ok"; sha = $sha; want = 'ok' }, @{ name = "class-$class-fail"; sha = ('0' * 64); want = 'fail' })) {
        $result = Join-Path $dir ($case.name + '.result')
        $world = New-World $case.name @{ companion = $true; sha = $case.sha; source = $source; arguments = @('-Ref', 'rework', '-ResultFile', $result) }
        $run = Invoke-World $world (Join-Path $old 'Update-Construct.ps1')
        Assert ((Get-Content $result).Trim() -eq $case.want) "the class $($class.ToUpper()) updater reports $($case.want): $($run.Output)"
        $installed = Join-Path $world 'local/The-Construct/permissionBRICK-The-Construct-rework/The-Construct-rework'
        Assert (Test-Path (Join-Path $installed 'install.ps1')) "the class $($class.ToUpper()) updater replaced the scripts folder with this release"
        if ($case.want -eq 'ok') {
            Assert (Test-Path (Join-Path $world 'local/Construct/agent/versions/2.0.0+abc/construct-agent.exe')) "class $($class.ToUpper()): the agent is installed"
            Assert ((Get-Content -Raw (Join-Path $installed '.construct-settings.json')) -match '"constructRef":\s*"rework"') "class $($class.ToUpper()): the marker advanced after the agent started"
            Assert ($run.Output -match 'Construct Companion: unchanged') "class $($class.ToUpper()): the Companion hook reports unchanged"
        } else {
            Assert (@($run.Calls | Where-Object { $_ -like 'start *ConstructCompanion.exe --background' }).Count -eq 1) "class $($class.ToUpper()): the Companion runs again after the failure"
            Assert (-not (Test-Path (Join-Path $installed '.construct-settings.json'))) "class $($class.ToUpper()): the marker did not advance"
        }
    }
}

# ---- the T3 shims before the conversion ----
$world = New-World 't3' @{ agentRunning = $true; arguments = @('-InstanceName', 'agent-vm') }
Set-Content $harness ((Get-Content -Raw $harness).Replace("if (`$Uri -match '/api/update/check$')", "if (`$Uri -match '/api/upgrade$') { return @{ legacy = `$true; stage = 'converting' } }`n    if (`$Uri -match '/api/update/check$')"))
$shims = Join-Path $dir 'shims'
New-Item -ItemType Directory -Path (Join-Path $shims 'lib') -Force | Out-Null
Copy-Item (Join-Path $repo 'Update-T3Code.ps1'), (Join-Path $repo 'Get-ConstructT3PairingLink.ps1') $shims
Copy-Item (Join-Path $repo 'lib/Construct.AgentShim.ps1') (Join-Path $shims 'lib')
$run = Invoke-World $world (Join-Path $shims 'Get-ConstructT3PairingLink.ps1')
$line = $run.Output.Trim() | ConvertFrom-Json
Assert ($run.Code -eq 1 -and -not $line.ok -and $line.error -match 'Finish the upgrade in the Construct window') "the pairing shim says to finish the upgrade: $($run.Output)"
$run = Invoke-World $world (Join-Path $shims 'Update-T3Code.ps1')
Assert ($run.Code -eq 1 -and $run.Output -match 'Finish the upgrade in the Construct window') "the T3 update shim says to finish the upgrade: $($run.Output)"

# ---- the pairing shim after the conversion: the agent's link in the old output ----
$world = New-World 't3-converted' @{ agentRunning = $true; arguments = @('-InstanceName', 'Work') }
$converted = (Get-Content -Raw $harness).Replace("if (`$Uri -match '/api/upgrade$') { return @{ legacy = `$true; stage = 'converting' } }",
    "if (`$Uri -match '/api/upgrade$') { return @{ legacy = `$true; stage = 'converted' } }`n    if (`$Uri -match '/api/connections$') { return @(@{ engine = 'e1'; vm = 'work' }) }`n    if (`$Uri -match '/api/connections/e1/work/t3-pairing$') { return [pscustomobject]@{ pairUrl = 'https://host.example:29991/pair#token=ONE-TIME'; links = @([pscustomobject]@{ kind = 'forwarded'; pairUrl = 'https://host.example:29991/pair#token=ONE-TIME' }); scopes = 'administrative' } }")
Set-Content $harness $converted
$run = Invoke-World $world (Join-Path $shims 'Get-ConstructT3PairingLink.ps1')
$line = $run.Output.Trim() | ConvertFrom-Json
Assert ($run.Code -eq 0 -and $line.ok -and $line.instance -eq 'work' -and $line.pairUrl -eq 'https://host.example:29991/pair#token=ONE-TIME' -and $line.scopes -eq 'administrative') "the pairing shim prints the agent's link: $($run.Output)"
Assert (@($line.links).Count -eq 1 -and $line.links[0].kind -eq 'forwarded') "the pairing shim keeps the links: $($run.Output)"
Assert ($run.Calls -contains 'rest Post http://127.0.0.1:5555/api/connections/e1/work/t3-pairing') "the pairing shim asks the agent: $($run.Calls)"
$run = Invoke-World (New-World 't3-unknown' @{ agentRunning = $true; arguments = @('-InstanceName', 'other') }) (Join-Path $shims 'Get-ConstructT3PairingLink.ps1')
$line = $run.Output.Trim() | ConvertFrom-Json
Assert ($run.Code -eq 1 -and -not $line.ok -and $line.error -match 'No VM named other') "an unknown VM gets no link: $($run.Output)"

Remove-Item -Recurse -Force $dir
Write-Host "PASS: $script:count agent bootstrap assertions"
