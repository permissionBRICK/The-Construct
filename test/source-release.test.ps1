#requires -Version 7.0
# Run real installer/updater entrypoints with local archives and mocked HTTP/VS Code.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$taskDir = Join-Path ([IO.Path]::GetTempPath()) ('construct-source-test-' + [guid]::NewGuid().ToString('N'))
$priorTestDir = $env:CONSTRUCT_SOURCE_TEST_DIR
try {
    $env:CONSTRUCT_SOURCE_TEST_DIR = $taskDir
    $fixture = Join-Path $taskDir 'fixture/repo-main'
    [IO.Directory]::CreateDirectory((Join-Path $fixture 'lib')) | Out-Null
    Set-Content (Join-Path $fixture '.construct-revision') ('a' * 40)
    Set-Content (Join-Path $fixture 'Auto-Install.ps1') 'Set-Content (Join-Path $env:CONSTRUCT_SOURCE_TEST_DIR "installed") "source"'
    Set-Content (Join-Path $fixture 'lib/AgentVm.Common.ps1') @'
function Install-ControlPanelExtension { param($SourceRoot) return $true }
function Set-ConstructInstalledMarker {
    param($Root, $Repo, $Ref, $Commit)
    if ($Commit -ne ('a' * 40)) { throw 'Installed marker was not pinned to the downloaded release.' }
    Set-Content (Join-Path $env:CONSTRUCT_SOURCE_TEST_DIR 'installed') $Commit
    return $Commit
}
'@
    $commonAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repoRoot 'lib/AgentVm.Common.ps1'),[ref]$null,[ref]$null)
    foreach ($name in @('Test-ConstructSourceRelativePath','Write-ConstructSourceManifest')) {
        $fn = $commonAst.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
        Add-Content (Join-Path $fixture 'lib/AgentVm.Common.ps1') $fn.Extent.Text
    }
    Add-Content (Join-Path $fixture 'lib/AgentVm.Common.ps1') 'function Get-ConstructConfigDir { Join-Path $env:LOCALAPPDATA "The-Construct/config" }'
    Compress-Archive -Path $fixture -DestinationPath (Join-Path $taskDir 'source.zip')
    $harness = Join-Path $taskDir 'run.ps1'
    Set-Content $harness @'
param($Entry, $Corrupt, $ManifestFault)
$ErrorActionPreference = 'Stop'
$env:LOCALAPPDATA = Join-Path $env:CONSTRUCT_SOURCE_TEST_DIR ([guid]::NewGuid().ToString('N'))
$env:CONSTRUCT_UPDATE_RESULT = Join-Path $env:LOCALAPPDATA 'result'
$manifestDir = Join-Path $env:LOCALAPPDATA 'The-Construct/source-manifests'
if ($ManifestFault -eq 'yes') {
    [IO.Directory]::CreateDirectory((Split-Path $manifestDir -Parent)) | Out-Null
    [IO.File]::WriteAllText($manifestDir, 'blocked')
}
$global:manifestCalls = 0
function Invoke-RestMethod {
    param($Uri, [switch]$UseBasicParsing, $TimeoutSec)
    if ($Uri -ne 'https://github.com/owner/repo/releases/latest/download/manifest.json') { throw 'Unexpected discovery URL.' }
    $global:manifestCalls++
    if ($global:manifestCalls -ne 1) { throw 'Must resolve the release only once.' }
    $zip = Join-Path $env:CONSTRUCT_SOURCE_TEST_DIR 'source.zip'
    return @{
        schemaVersion=1;repository='owner/repo';ref='refs/heads/main';commit=('a'*40);releaseTag=('host-'+('a'*40))
        sourceAsset=('construct-source-'+('a'*40)+'.zip');sourceSha256=$(if ($Corrupt -eq 'yes') {'0'*64} else {(Get-FileHash $zip).Hash.ToLowerInvariant()})
        sourceSizeBytes=(Get-Item $zip).Length;payloadAsset='construct-host-aaaaaaa-win-x64.zip';payloadSha256=('b'*64);payloadSizeBytes=123
    }
}
function Invoke-WebRequest {
    param($Uri, $OutFile, [switch]$UseBasicParsing, $TimeoutSec)
    $expected = 'https://github.com/owner/repo/releases/download/host-'+('a'*40)+'/construct-source-'+('a'*40)+'.zip'
    if ($Uri -cne $expected) { throw 'Download must use the pinned release asset.' }
    Copy-Item (Join-Path $env:CONSTRUCT_SOURCE_TEST_DIR 'source.zip') $OutFile
}
& $Entry -Repo 'owner/repo'
if ($LASTEXITCODE) { exit $LASTEXITCODE }
if ($global:manifestCalls -ne 1) { throw 'Missing release lookup.' }
$manifest = Join-Path $manifestDir (('a'*40)+'.sha256')
if ($ManifestFault -ne 'yes') {
    if (-not (Test-Path $manifest -PathType Leaf)) { throw 'External source manifest missing.' }
    if (@(Get-Content $manifest | Where-Object { $_ -cnotmatch '^[a-f0-9]{64}  \S.*$' }).Count) { throw 'Manifest line malformed.' }
    $copies = @(Get-ChildItem (Join-Path $env:LOCALAPPDATA 'The-Construct') -Filter '*.sha256' -Recurse)
    if ($copies.Count -ne 1 -or $copies[0].FullName -ne $manifest) { throw 'Manifest must live outside the checkout.' }
} elseif (-not (Test-Path $manifestDir -PathType Leaf)) { throw 'Manifest fault fixture changed.' }
'@
    foreach ($entry in @('install.ps1', 'Update-Construct.ps1')) {
        foreach ($scenario in @(@('no','no'),@('yes','no'),@('no','yes'))) {
            $corrupt=$scenario[0];$manifestFault=$scenario[1]
            $installed = Join-Path $taskDir 'installed'
            if (Test-Path $installed) { Remove-Item $installed }
            & pwsh -NoProfile -NonInteractive -File $harness (Join-Path $repoRoot $entry) $corrupt $manifestFault *> (Join-Path $taskDir 'output.log')
            $code = $LASTEXITCODE
            if ($corrupt -eq 'no') {
                if ($code -ne 0 -or -not (Test-Path $installed)) { Get-Content (Join-Path $taskDir 'output.log'); throw "$entry failed valid release." }
            } elseif ($code -eq 0 -or (Test-Path $installed)) {
                throw "$entry accepted a corrupt release."
            }
            Write-Host "PASS $entry corrupt=$corrupt manifestFault=$manifestFault"
        }
    }
} finally {
    $env:CONSTRUCT_SOURCE_TEST_DIR = $priorTestDir
    if (Test-Path $taskDir) { Remove-Item $taskDir -Recurse -Force }
}
