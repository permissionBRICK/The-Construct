# Execute the actual provisioner's Desktop handoff with fake SSH and installer
# boundaries. This runs on Linux pwsh too; nothing contacts a VM or runs an EXE.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../lib/AgentVm.Common.ps1')
$source = Get-Content (Join-Path $PSScriptRoot '../Provision-AgentVM.ps1') -Raw
$start = $source.IndexOf("if (`$Action -eq 'provision') {", $source.IndexOf('# The guest prepares the shared T3 sources'))
$end = $source.IndexOf('# Get the root private key', $start)
$handoff = [scriptblock]::Create($source.Substring($start, $end - $start))
$root = Join-Path ([IO.Path]::GetTempPath()) ('t3-handoff-' + [guid]::NewGuid().ToString('N'))
$previousLocalAppData = $env:LOCALAPPDATA
function Assert($condition, $message) { if (-not $condition) { throw $message } }
function Write-Ok($message) {}
function Write-Step($message) {}
function Write-Warning($message) { $script:warnings.Add($message) }
function Get-ConstructRunInstanceName { $script:instance }
function Invoke-Ssh { param([switch]$Sudo, $Command) 'T3CODE_SERVER_READY=yes' }
function Invoke-SshStream {
    param([switch]$Sudo, [switch]$PassThru, [switch]$NoThrow, $Command)
    Assert ($Command -match 'T3CODE_BUILD_MODE=desktop') 'Packaging must use the prepared server'
    $script:calls.Add('package')
    [pscustomobject]@{ExitCode=$script:packageExit}
}
function Invoke-ScpFrom {
    param($RemotePath, $LocalPath)
    $script:calls.Add($RemotePath)
    switch -Wildcard ($RemotePath) {
        '*/server-manifest.json' { $script:manifest | ConvertTo-Json | Set-Content $LocalPath }
        '*/manifest.json' {
            $package = $script:manifest.Clone()
            $package.sha256 = $script:sha
            $package.desktopVersion = '1.0.0-construct.fixture'
            if ($script:mismatch) { $package.patchHash = 'different' }
            $package | ConvertTo-Json | Set-Content $LocalPath
        }
        '*.exe' { Copy-Item $script:installer $LocalPath }
        default { throw "Unexpected download $RemotePath" }
    }
}
function Get-Process { @() }
function Invoke-WebRequest {
    param($Uri, $OutFile, [switch]$UseBasicParsing)
    $script:calls.Add('https-download')
    Copy-Item $script:installer $OutFile
}
function Start-Process {
    param($FilePath, $ArgumentList, [switch]$Wait, [switch]$PassThru, $WindowStyle)
    Assert ($ArgumentList -join ' ' -eq '--updated /S') 'Unexpected install command'
    $script:calls.Add('install')
    if ($script:installExit -eq 0) { Set-Content (Join-Path $script:app 'T3 Code.exe') 'app' }
    [pscustomobject]@{ExitCode=$script:installExit}
}
function Run-Handoff {
    $script:calls.Clear(); $script:warnings.Clear()
    & $handoff
}
try {
    $env:LOCALAPPDATA = $root
    $script:app = Join-Path $root 'Programs/t3code'
    New-Item -ItemType Directory $script:app -Force | Out-Null
    $script:installer = Join-Path $root 'source.exe'
    Set-Content $script:installer 'installer'
    $script:sha = (Get-FileHash $script:installer -Algorithm SHA256).Hash.ToLowerInvariant()
    $script:manifest = @{version='1.0.0'; channel='stable'; patchHash='p1'; buildHash='b1'}
    $script:calls = [Collections.Generic.List[string]]::new()
    $script:warnings = [Collections.Generic.List[string]]::new()
    $script:instance = 'local-vm'
    $script:packageExit = 0; $script:installExit = 0; $script:mismatch = $false
    $Action = 'provision'
    $recordPath = Join-Path $root 'The-Construct/artifacts/t3code/installed.json'
    Run-Handoff
    Assert ($script:warnings.Count -eq 0) "Cold install failed: $script:warnings"
    Assert ($script:calls.Contains('package') -and $script:calls.Contains('install')) 'First provision must package and install'
    Assert ((Get-Content $recordPath -Raw | ConvertFrom-Json).sourceInstance -eq 'local-vm') 'First instance was not recorded'
    $savedRecord = Get-Content $recordPath -Raw
    $script:instance = 'remote-vm'
    Run-Handoff
    Assert ($script:warnings.Count -eq 0) "Second provision failed: $script:warnings"
    Assert ($script:calls.Count -eq 1 -and $script:calls[0] -like '*/server-manifest.json') 'Second VM must only download the server identity: no packaging, EXE download or install'
    Assert ((Get-Content $recordPath -Raw) -eq $savedRecord) 'Skipping must preserve the real installer provenance'
    $script:manifest.patchHash = 'p2'; $script:manifest.buildHash = 'b2'
    Run-Handoff
    Assert ($script:calls.Contains('install')) 'Changed recipe must install'
    Assert (-not ($script:calls | Where-Object { $_ -like '*.exe' })) 'Identical cached installer bytes must not be downloaded again'
    Remove-Item (Join-Path $script:app 'T3 Code.exe')
    Set-Content (Join-Path $script:app 'Uninstall T3 Code.exe') 'uninstaller'
    Run-Handoff
    Assert ($script:calls.Contains('install')) 'An uninstaller alone must not count as an installed app'
    $savedRecord = Get-Content $recordPath -Raw
    $script:manifest.patchHash = 'p3'
    $script:packageExit = 1
    Run-Handoff
    Assert ($script:warnings.Count -eq 1 -and -not $script:calls.Contains('install')) 'Packaging failure must stop handoff'
    Assert ((Get-Content $recordPath -Raw) -eq $savedRecord) 'Packaging failure must preserve installed record'
    $script:packageExit = 0; $script:mismatch = $true
    Run-Handoff
    Assert ($script:warnings.Count -eq 1 -and -not $script:calls.Contains('install')) 'Mismatched packaged identity must be rejected'
    $script:mismatch = $false; $script:installExit = 1
    Run-Handoff
    Assert ($script:warnings.Count -eq 1) 'Installer failure must be reported'
    Assert ((Get-Content $recordPath -Raw) -eq $savedRecord) 'Installer failure must preserve installed record'
    $script:installExit = 0
    $script:sha = 'a' * 64
    Run-Handoff
    Assert ($script:warnings.Count -eq 1 -and -not $script:calls.Contains('install')) 'Corrupt download must be rejected before installation'
    $script:manifest.patchHash = 'c' * 64
    $script:manifest.buildHash = 'd' * 64
    $script:manifest.installationMode = 'prebuilt'
    $script:manifest.sha256 = (Get-FileHash $script:installer -Algorithm SHA256).Hash.ToLowerInvariant()
    $script:manifest.desktopVersion = '1.0.0-construct.prebuilt'
    $script:manifest.downloadUrl = "https://github.com/permissionBRICK/construct-t3-builds/releases/download/t3-1.0.0-$('d' * 64)/T3Code-Construct-Setup.exe"
    Remove-Item (Join-Path $root 'The-Construct/artifacts/t3code/T3Code-Construct-Setup.exe') -Force
    Run-Handoff
    Assert ($script:warnings.Count -eq 0) "Prebuilt handoff failed: $script:warnings"
    Assert ($script:calls.Contains('https-download') -and $script:calls.Contains('install')) 'Prebuilt must download from GitHub and install'
    Assert (-not $script:calls.Contains('package')) 'Prebuilt must never invoke local Windows packaging'
    Run-Handoff
    Assert ($script:calls.Count -eq 1) 'Second VM using prebuilt must skip download and install'
    $script:manifest.patchHash = 'e' * 64
    $script:manifest.downloadUrl = 'https://example.com/other.exe'
    Run-Handoff
    Assert ($script:warnings.Count -eq 1 -and -not $script:calls.Contains('https-download')) 'Non-release download URL must be rejected'
    Write-Host 'PASS: shared host tracking, deferred packaging/download, missing app, and failure handling'
} finally {
    $env:LOCALAPPDATA = $previousLocalAppData
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
