#requires -Version 5.1
<#
.SYNOPSIS
    Installs the Construct device agent for the current user on Windows x64, without elevation, and starts it.

.DESCRIPTION
    Reads the channel manifest, downloads the agentWindows package, checks its SHA-256 against the
    manifest, unpacks it into %LOCALAPPDATA%\Construct\agent\versions\<version>\, records it as current
    in install.json, installs the VS Code extension it carries (construct.vsix) where VS Code is installed,
    lets it start with Windows and starts it in the tray. With -Package a local zip is
    installed instead (the hash check is then the caller's: the Windows installer of the whole product
    passes the package it verified). The agent updates itself from then on (plan 12.1).

.EXAMPLE
    .\Install-ConstructAgent.ps1
    .\Install-ConstructAgent.ps1 -Channel nightly
    .\Install-ConstructAgent.ps1 -Package .\construct-agent-1234567-win-x64.zip -Version 1.2.0+<sha> -NoStart
#>
[CmdletBinding()]
param(
    [string]$Channel = 'stable',
    [string]$Package,
    [string]$Version,
    [switch]$NoStart,
    [string]$Repository = 'permissionBRICK/The-Construct'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Install-ConstructVsCodeExtension {
    # The thin VS Code extension from the agent package (construct.vsix), over the old one: same id, higher
    # version. VS Code not installed is not a failure; VS Code refusing the extension is (13.2).
    param([string]$Folder)
    $code = $null
    $cmd = Get-Command code -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cmd) { $code = $cmd.Source }
    if (-not $code) {
        foreach ($pair in @(@($env:LOCALAPPDATA, 'Programs\Microsoft VS Code\bin\code.cmd'), @($env:ProgramFiles, 'Microsoft VS Code\bin\code.cmd'))) {
            if (-not $pair[0]) { continue }
            $candidate = Join-Path $pair[0] $pair[1]
            if (Test-Path -LiteralPath $candidate) { $code = $candidate; break }
        }
    }
    if (-not $code) { Write-Host '    VS Code is not installed; no extension to update.'; return }
    $vsix = Join-Path $Folder 'construct.vsix'
    if (-not (Test-Path -LiteralPath $vsix)) { throw 'The agent package has no construct.vsix.' }
    # code writes warnings to stderr even when it succeeds: only its exit code counts.
    $previousPreference = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $global:LASTEXITCODE = 1
    try { & $code --install-extension $vsix --force 2>$null | Out-Null } finally { $ErrorActionPreference = $previousPreference }
    if ($LASTEXITCODE -ne 0) { throw "VS Code did not install the Construct extension (code --install-extension exit $LASTEXITCODE)." }
    Write-Host '    the Construct extension is installed in VS Code (it starts with the next VS Code window).'
}

$home_ = if ($env:CONSTRUCT_AGENT_HOME) { $env:CONSTRUCT_AGENT_HOME } else { Join-Path $env:LOCALAPPDATA 'Construct\agent' }
New-Item -ItemType Directory -Force -Path $home_ | Out-Null
$work = Join-Path ([IO.Path]::GetTempPath()) ('construct-agent-' + [Guid]::NewGuid().ToString('n'))
New-Item -ItemType Directory -Path $work | Out-Null
try {
    if (-not $Package) {
        $manifestUrl = switch -Regex ($Channel) {
            '^stable$' { "https://github.com/$Repository/releases/latest/download/manifest.json" }
            '^nightly$' { "https://github.com/$Repository/releases/download/channel-nightly/manifest.json" }
            '^https://' { $Channel }
            default { throw 'A channel is stable, nightly or an https manifest URL.' }
        }
        $manifest = Invoke-RestMethod -UseBasicParsing -Uri $manifestUrl
        $entry = $manifest.packages.agentWindows
        if (-not $entry -or $entry.sha256 -notmatch '^[0-9a-f]{64}$' -or $entry.asset -match '[\\/]' -or $entry.asset -in @('.', '..')) { throw 'The channel manifest is invalid.' }
        $asset = [Uri]::EscapeDataString($entry.asset)
        $url = if ($Channel -in @('stable', 'nightly')) { "https://github.com/$Repository/releases/download/$($manifest.releaseTag)/$asset" } else { [Uri]::new([Uri]$manifestUrl, $asset).AbsoluteUri }
        $Package = Join-Path $work 'agent.zip'
        Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $Package
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $Package).Hash.ToLowerInvariant() -ne $entry.sha256) { throw 'The package does not match the SHA-256 in the channel manifest.' }
        $Version = [string]$manifest.version
    }
    if (-not $Version) { throw '-Package needs -Version.' }
    if ($Version -notmatch '^[A-Za-z0-9.+_-]+$') { throw "Unusable version: $Version" }

    # A running agent stops first (its token file names its port and token).
    $endpoint = Join-Path $home_ 'endpoint.json'
    if (Test-Path -LiteralPath $endpoint) {
        try {
            $e = Get-Content -Raw -LiteralPath $endpoint | ConvertFrom-Json
            Invoke-RestMethod -UseBasicParsing -Method Post -Uri "http://127.0.0.1:$($e.port)/api/shutdown" -Headers @{ Authorization = "Bearer $($e.token)" } -TimeoutSec 5 | Out-Null
            Start-Sleep -Seconds 3
        } catch { }
    }

    $target = Join-Path $home_ "versions\$Version"
    if (-not (Test-Path -LiteralPath (Join-Path $target 'construct-agent.exe'))) {
        $partial = "$target.partial"
        if (Test-Path -LiteralPath $partial) { Remove-Item -Recurse -Force -LiteralPath $partial }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($Package, $partial)
        if (-not (Test-Path -LiteralPath (Join-Path $partial 'construct-agent.exe'))) { throw 'The package has no construct-agent.exe.' }
        if (Test-Path -LiteralPath $target) { Remove-Item -Recurse -Force -LiteralPath $target }
        Move-Item -LiteralPath $partial -Destination $target
    }
    Install-ConstructVsCodeExtension -Folder $target

    $installPath = Join-Path $home_ 'install.json'
    $previous = $null
    if (Test-Path -LiteralPath $installPath) { try { $previous = (Get-Content -Raw -LiteralPath $installPath | ConvertFrom-Json).current } catch { } }
    if ($previous -eq $Version) { $previous = $null }
    # With a previous version the new one is on trial: its first start self-tests and goes back when it fails (12.2).
    $state = [ordered]@{ current = $Version; previous = $previous; pending = [bool]$previous } | ConvertTo-Json
    [IO.File]::WriteAllText($installPath, $state, (New-Object Text.UTF8Encoding($false)))

    $exe = Join-Path $target 'construct-agent.exe'
    # Start with Windows: the current user's Run value, as the agent's own setting writes it.
    Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'ConstructAgent' -Value "`"$exe`" --background"
    if (-not $NoStart) { Start-Process -FilePath $exe -ArgumentList '--background' }
    Write-Host "Installed the Construct agent $Version in $target."
} finally {
    Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
}
