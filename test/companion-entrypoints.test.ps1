#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$dir = Join-Path ([IO.Path]::GetTempPath()) ('companion-entry-' + [guid]::NewGuid().ToString('N'))
$script:count = 0
$script:fakeElevated = $false
$script:calls = New-Object Collections.ArrayList
function Assert($Condition, $Message) { if (-not $Condition) { throw $Message }; $script:count++ }
$oldLocal = $env:LOCALAPPDATA
try {
    New-Item -ItemType Directory -Path (Join-Path $dir 'lib') -Force | Out-Null
    # Real hook, fake installer: no download, process, registry or elevation effects.
    $lib = Get-Content -Raw (Join-Path $repo 'lib/Construct.Companion.ps1')
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($lib, [ref]$tokens, [ref]$errors)
    $hook = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Invoke-ConstructCompanionInstallHook' }, $true).Extent.Text
    $fake = @'
function Test-ConstructCompanionElevated { return $script:fakeElevated }
function Install-ConstructCompanion { param($ScriptsDir, $Source, [switch]$Force, [switch]$SkipCompanion)
    $null = $script:calls.Add(@{dir=$ScriptsDir;source=$Source;force=[bool]$Force;skip=[bool]$SkipCompanion})
}
function Uninstall-ConstructCompanion { $null = $script:calls.Add(@{uninstall=$true}) }
'@
    Set-Content (Join-Path $dir 'lib/Construct.Companion.ps1') ($fake + "`n" + $hook)
    Set-Content (Join-Path $dir 'lib/AgentVm.Common.ps1') 'function Install-ControlPanelExtension { param($SourceRoot) return $true }'
    $auto = Get-Content -Raw (Join-Path $repo 'Auto-Install.ps1')
    $begin = $auto.IndexOf("if (-not `$SkipCreateVm -and `$Action -ne 'remove-instance') {")
    $end = $auto.IndexOf("        if ((Resolve-ConstructInstallMode -Bound", $begin)
    Assert ($begin -gt 0 -and $end -gt $begin) 'Locate actual installer pre-step before elevation'
    $pre = $auto.Substring($begin, $end-$begin) + "`n    }`n}"
    # The only Windows-native query in this boundary is replaced by a normal-user result.
    $pre = [regex]::Replace($pre, '(?s)\$isAdmin = \(\[Security.Principal.WindowsPrincipal\].*?\)\.IsInRole\(\[Security.Principal.WindowsBuiltInRole\]::Administrator\)', '$isAdmin = $false')
    $header = @'
param($Backend, [switch]$SkipCompanion)
$SkipCreateVm=$false; $Action=""; $PSBoundParameters=@{}
function Read-ConstructInstanceRegistrySnapshot { return $null }
function Resolve-ConstructInstallMode { param($Bound,$Snapshot) return $Backend }
function Enable-ConstructTui { }
function Initialize-ConstructInstallFeatures { param($Bound,$Snapshot) $script:ConstructFeatureParameters=@{} }
'@ + "`n"
    Set-Content (Join-Path $dir 'Auto-Install.ps1') ($header + $pre)
    foreach ($scenario in @('fresh-local', 'fresh-remote', 'additional-remote')) {
        $script:calls.Clear()
        $env:LOCALAPPDATA = $dir
        if ($scenario -eq 'additional-remote') {
            New-Item -ItemType Directory -Path (Join-Path $dir 'The-Construct') -Force | Out-Null
            Set-Content (Join-Path $dir 'The-Construct/instances.json') '{"version":1,"instances":{"remote-vm":{"backend":"hyperv-remote"}}}'
        }
        $backend = if ($scenario -eq 'fresh-local') { 'hyperv-local' } else { 'hyperv-remote' }
        . (Join-Path $dir 'Auto-Install.ps1') -Backend $backend
        Assert ($script:calls.Count -eq 1) "$scenario reaches installer once before elevation"
        Assert ($script:calls[0].dir -eq $dir) "$scenario installs on the client PC"
    }
    $provision = Get-Content -Raw (Join-Path $repo 'Provision-AgentVM.ps1')
    $provisionAst = [Management.Automation.Language.Parser]::ParseInput($provision, [ref]$tokens, [ref]$errors)
    $hookStatement = $provisionAst.EndBlock.Statements | Where-Object { $_ -is [Management.Automation.Language.IfStatementAst] -and $_.Extent.Text -match 'Invoke-ConstructCompanionInstallHook' } | Select-Object -First 1
    Assert ($null -ne $hookStatement) 'Provision has a top-level non-blocking hook'
    Assert ($provisionAst.ParamBlock.Parameters.Name.VariablePath.UserPath -contains 'SkipCompanion') 'Provision accepts SkipCompanion'
    Set-Content (Join-Path $dir 'Provision-AgentVM.ps1') ('param([switch]$SkipCompanion, [string]$Action="provision", [switch]$ScanReposOnly)' + "`n" + $hookStatement.Extent.Text)
    $script:calls.Clear()
    . (Join-Path $dir 'Provision-AgentVM.ps1')
    Assert ($script:calls.Count -eq 1 -and $script:calls[0].dir -eq $dir) 'Plain reprovision reaches installer on client'
    $script:calls.Clear()
    . (Join-Path $dir 'Provision-AgentVM.ps1') -SkipCompanion
    Assert ($script:calls.Count -eq 0) 'Reprovision opt-out skips installer'
    . (Join-Path $dir 'Provision-AgentVM.ps1') -Action export
    . (Join-Path $dir 'Provision-AgentVM.ps1') -ScanReposOnly
    Assert ($script:calls.Count -eq 0) 'Export and scan never install Companion'
    $script:fakeElevated = $true
    $warnings = @(. (Join-Path $dir 'Provision-AgentVM.ps1') 3>&1)
    Assert ($script:calls.Count -eq 0 -and $warnings.Count -eq 0) 'Elevated reprovision skips silently'
    $script:fakeElevated = $false
    # Execute the real version-guarded forwarding statements at each chain boundary.
    $SkipCompanion = $true
    $createCmd = [pscustomobject]@{ Parameters=@{ SkipCompanion=$null } }
    $provCmd = $createCmd; $reprovCmd = $createCmd; $acProvCmd = $createCmd; $script:RemoteProvCmd = $createCmd
    $provisionScript = Join-Path $dir 'Provision-AgentVM.ps1'
    foreach ($entryFile in @('Auto-Install.ps1', 'Create-AgentVM.ps1')) {
        $entryAst = [Management.Automation.Language.Parser]::ParseInput((Get-Content -Raw (Join-Path $repo $entryFile)), [ref]$tokens, [ref]$errors)
        $forwarding = @($entryAst.FindAll({ param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Extent.Text -match '^if \(\$SkipCompanion -and' -and $n.Extent.Text -match "\['SkipCompanion'\]" }, $true))
        $launches = @($entryAst.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-DeElevatedProvision' }, $true)).Count
        # Auto also owns one shared direct-remote splat and the Create child launch.
        $launches += @($entryAst.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'New-ConstructRemoteProvisionArgs' }, $true)).Count
        $launches += @($entryAst.FindAll({ param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.Extent.Text -eq '& $createScript @createArgs' }, $true)).Count
        Assert ($forwarding.Count -eq $launches) "$entryFile guards every provisioning path (Auto=5, Create=2)"
        foreach ($statement in $forwarding) {
            $a=@{}; $createArgs=@{}; $provArgs=@{}; $reprovArgs=@{}; $acReprovArgs=@{}
            . ([scriptblock]::Create($statement.Extent.Text))
            $forwarded = @($a,$createArgs,$provArgs,$reprovArgs,$acReprovArgs) | Where-Object { $_.ContainsKey('SkipCompanion') } | Select-Object -First 1
            Assert ($null -ne $forwarded -and $forwarded.SkipCompanion) 'Opt-out crosses boundary'
            . $provisionScript @forwarded
            Assert ($script:calls.Count -eq 0) 'Descendant does not call installer after opt-out'
        }
    }
    $script:calls.Clear()
    Copy-Item (Join-Path $repo 'Install-ConstructCompanion.ps1') $dir
    . (Join-Path $dir 'Install-ConstructCompanion.ps1') -Source release -Force
    Assert ($script:calls.Count -eq 1 -and $script:calls[0].source -eq 'release' -and $script:calls[0].force) 'Root entry forwards source and force'
    . (Join-Path $dir 'Install-ConstructCompanion.ps1') -Uninstall
    Assert ($script:calls[1].uninstall) 'Root uninstall reaches library'
    # Execute the complete updater with local archive/network/helper fakes.
    $downloaded = Join-Path $dir 'The-Construct/owner-repo-main/repo-main'
    New-Item -ItemType Directory -Path (Join-Path $downloaded 'lib') -Force | Out-Null
    Copy-Item (Join-Path $dir 'lib/*') (Join-Path $downloaded 'lib')
    $script:sourceBytes = [Text.Encoding]::UTF8.GetBytes('fixture')
    $hasher = [Security.Cryptography.SHA256]::Create()
    try { $sourceHash = ([BitConverter]::ToString($hasher.ComputeHash($script:sourceBytes))).Replace('-', '').ToLowerInvariant() } finally { $hasher.Dispose() }
    $script:fixtureManifest = @{schemaVersion=1;repository='owner/repo';ref='refs/heads/main';commit=('a'*40);releaseTag=('host-'+('a'*40));sourceAsset=('construct-source-'+('a'*40)+'.zip');sourceSha256=$sourceHash;sourceSizeBytes=$script:sourceBytes.Length;payloadAsset='construct-host-aaaaaaa-win-x64.zip';payloadSha256=('b'*64);payloadSizeBytes=10}
    function Invoke-RestMethod { param($Uri, [switch]$UseBasicParsing, $TimeoutSec)
        Assert ($Uri -eq 'https://github.com/owner/repo/releases/latest/download/manifest.json') 'Updater discovers the published main manifest'
        return $script:fixtureManifest
    }
    function Invoke-WebRequest { param($Uri, $OutFile, [switch]$UseBasicParsing, $TimeoutSec)
        Assert ($Uri -eq ('https://github.com/owner/repo/releases/download/host-'+('a'*40)+'/construct-source-'+('a'*40)+'.zip')) 'Updater downloads immutable manifest source'
        [IO.File]::WriteAllBytes($OutFile, $script:sourceBytes)
    }
    function Expand-Archive { param($LiteralPath, $DestinationPath, [switch]$Force) }
    $script:calls.Clear()
    . (Join-Path $repo 'Update-Construct.ps1') -Repo owner/repo -ResultFile (Join-Path $dir 'result')
    Assert ($script:calls.Count -eq 1 -and $script:calls[0].dir -eq $downloaded) 'Full Update-Construct reaches refreshed installer'
    Assert ((Get-Content (Join-Path $dir 'result')).Trim() -eq 'ok') 'Updater completes after hook'
    Write-Host "Companion entrypoints: $script:count passed"
} finally {
    $env:LOCALAPPDATA = $oldLocal
    Remove-Item -LiteralPath $dir -Recurse
}
