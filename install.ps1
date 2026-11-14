#Requires -Version 5.1
<#
    One-line web installer for The Construct:
        irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex
    From a branch (it maps to the nightly channel while the branch plays that part):
        & ([scriptblock]::Create((irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/<branch>/install.ps1))) -Ref <branch>

    Installs the Construct device agent for this user, without elevation, and starts it.
    Standalone: it needs no agent and none of the old client's scripts. It reads the
    channel manifest (main = stable, any other ref = nightly), downloads the agent
    package, checks its SHA-256 against the manifest, unpacks it into
    %LOCALAPPDATA%\Construct\agent\versions\<version>\ (one folder whatever the
    channel), installs the VS Code extension it carries where VS Code is installed (a
    failure there fails the install), lets it start with Windows and starts it. The agent's window then runs
    the one-time upgrade of an old install, or the first-run setup.

    An old Construct Companion is asked to quit first, the way its own updates do; if
    anything fails after that, it is started again and the error is thrown, so a retry
    from its Update button (Update-Construct.ps1) or by hand starts over. With an agent
    already running, this hands over to it: the agent updates itself.

    -Repo / -Ref are the old parameters; any further arguments (once passed on to
    Auto-Install.ps1) are ignored with a warning.
#>
param(
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main"
)
$ErrorActionPreference = "Stop"
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }
if ($args.Count -gt 0) { Write-Warning ("Ignored: " + ($args -join ' ') + " (the device agent's window sets everything up now).") }
if ($Repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid Construct repository.' }

$localAppData = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { $env:TEMP }
$agentHome = Join-Path $localAppData 'Construct\agent'
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

function Get-ConstructRunningAgent {
    # The agent's token file and its health route; $null when no agent answers.
    $endpointPath = Join-Path $agentHome 'endpoint.json'
    if (-not (Test-Path -LiteralPath $endpointPath)) { return $null }
    try {
        $endpoint = Get-Content -Raw -LiteralPath $endpointPath | ConvertFrom-Json
        $health = Invoke-RestMethod -UseBasicParsing -Uri ("http://127.0.0.1:{0}/api/health" -f $endpoint.port) -TimeoutSec 5
        if ($health.status -eq 'ok') { return $endpoint }
    } catch { }
    return $null
}

function Invoke-ConstructAgentRoute {
    param($Endpoint, [string]$Path, $Body)
    $json = '{}'
    if ($Body) { $json = $Body | ConvertTo-Json -Compress }
    return Invoke-RestMethod -UseBasicParsing -Method Post -Uri ("http://127.0.0.1:{0}{1}" -f $Endpoint.port, $Path) `
        -Headers @{ Authorization = ('Bearer ' + $Endpoint.token) } -ContentType 'application/json' -Body $json -TimeoutSec 30
}

function Stop-ConstructOldCompanion {
    # The old Companion's own way: POST /v1/quit with its endpoint token, then wait for the process.
    # $true when one was running and has quit; throws when it does not quit. Never kills.
    $stateDir = Join-Path $localAppData 'The-Construct\companion'
    $endpointPath = Join-Path $stateDir 'endpoint.json'
    $stopped = $false
    $endpoint = $null
    if (Test-Path -LiteralPath $endpointPath) {
        try { $endpoint = Get-Content -Raw -LiteralPath $endpointPath | ConvertFrom-Json } catch { $endpoint = $null }
    }
    # Checked before anything quits: a Companion of this Windows session that its endpoint does not name.
    # (Another user's Companion in another session is left alone and does not count.)
    $session = $null
    try { $session = (Get-Process -Id $PID).SessionId } catch { $session = $null }
    $others = @(Get-Process -Name 'ConstructCompanion' -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $session -and (-not $endpoint -or $_.Id -ne [int]$endpoint.pid) })
    if ($others.Count -gt 0) { throw 'The Construct Companion is still running; close it from its tray menu and try again.' }
    if ($endpoint) {
        if ($endpoint.pid -and (Get-Process -Id ([int]$endpoint.pid) -ErrorAction SilentlyContinue)) {
            try {
                Invoke-RestMethod -UseBasicParsing -Method Post -Uri ("http://127.0.0.1:{0}/v1/quit" -f [int]$endpoint.port) `
                    -Headers @{ Authorization = ('Bearer ' + $endpoint.token) } -ContentType 'application/json' -Body '{"reason":"update"}' -TimeoutSec 5 -MaximumRedirection 0 | Out-Null
            } catch { }
            for ($i = 0; $i -lt 60; $i++) {
                if (-not (Get-Process -Id ([int]$endpoint.pid) -ErrorAction SilentlyContinue)) { break }
                Start-Sleep -Milliseconds 250
            }
            if (Get-Process -Id ([int]$endpoint.pid) -ErrorAction SilentlyContinue) { throw 'The Construct Companion did not quit; close it from its tray menu and try again.' }
            $stopped = $true
        }
    }
    return $stopped
}

function Start-ConstructOldCompanion {
    $exe = Join-Path $localAppData 'Programs\ConstructCompanion\ConstructCompanion.exe'
    if (Test-Path -LiteralPath $exe) {
        try { Start-Process -FilePath $exe -ArgumentList '--background' | Out-Null; Write-Host '    the Construct Companion was started again.' } catch { Write-Warning 'Start the Construct Companion again by hand.' }
    }
}

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

function Install-ConstructAgentPackage {
    param([string]$Channel)
    $manifestUrl = "https://github.com/$Repo/releases/latest/download/manifest.json"
    if ($Channel -eq 'nightly') { $manifestUrl = "https://github.com/$Repo/releases/download/channel-nightly/manifest.json" }
    Write-Host "==> Reading the $Channel channel ..." -ForegroundColor Cyan
    $manifest = Invoke-RestMethod -UseBasicParsing -Uri $manifestUrl -TimeoutSec 30
    $entry = $null
    if ($manifest.packages) { $entry = $manifest.packages.agentWindows }
    if (-not $entry -or [string]$entry.sha256 -notmatch '^[0-9a-f]{64}$' -or [string]$entry.asset -match '[\\/]' -or [string]$entry.asset -in @('', '.', '..') -or
        [string]$manifest.releaseTag -notmatch '^[A-Za-z0-9._-]+$' -or [string]$manifest.version -notmatch '^[A-Za-z0-9.+_-]+$') {
        throw "The $Channel channel offers no Construct agent for Windows."
    }
    $version = [string]$manifest.version
    New-Item -ItemType Directory -Force -Path $agentHome | Out-Null
    $target = Join-Path $agentHome ("versions\" + $version)
    if (-not (Test-Path -LiteralPath (Join-Path $target 'construct-agent.exe'))) {
        $work = Join-Path ([IO.Path]::GetTempPath()) ('construct-agent-' + [Guid]::NewGuid().ToString('n'))
        New-Item -ItemType Directory -Path $work | Out-Null
        try {
            $zip = Join-Path $work 'agent.zip'
            $url = "https://github.com/$Repo/releases/download/$($manifest.releaseTag)/" + [Uri]::EscapeDataString([string]$entry.asset)
            Write-Host "==> Downloading the Construct agent $version ..." -ForegroundColor Cyan
            $oldPP = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
            try { Invoke-WebRequest -UseBasicParsing -Uri $url -OutFile $zip -TimeoutSec 300 } finally { $ProgressPreference = $oldPP }
            if ((Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant() -ne [string]$entry.sha256) { throw 'The agent package does not match the SHA-256 in the channel manifest.' }
            $partial = "$target.partial"
            if (Test-Path -LiteralPath $partial) { Remove-Item -Recurse -Force -LiteralPath $partial }
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [IO.Compression.ZipFile]::ExtractToDirectory($zip, $partial)
            if (-not (Test-Path -LiteralPath (Join-Path $partial 'construct-agent.exe'))) { throw 'The agent package has no construct-agent.exe.' }
            if (Test-Path -LiteralPath $target) { Remove-Item -Recurse -Force -LiteralPath $target }
            Move-Item -LiteralPath $partial -Destination $target
        } finally {
            Remove-Item -Recurse -Force -LiteralPath $work -ErrorAction SilentlyContinue
        }
    }
    Install-ConstructVsCodeExtension -Folder $target

    $installPath = Join-Path $agentHome 'install.json'
    $previous = $null
    if (Test-Path -LiteralPath $installPath) { try { $previous = (Get-Content -Raw -LiteralPath $installPath | ConvertFrom-Json).current } catch { $previous = $null } }
    if ($previous -eq $version) { $previous = $null }
    $state = [ordered]@{ current = $version; previous = $previous; pending = [bool]$previous } | ConvertTo-Json
    [IO.File]::WriteAllText($installPath, $state, (New-Object Text.UTF8Encoding($false)))
    # The channel this install came from, unless the agent has its own settings already.
    $deviceJson = Join-Path $agentHome 'device.json'
    if (-not (Test-Path -LiteralPath $deviceJson)) { [IO.File]::WriteAllText($deviceJson, (@{ channel = $Channel } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false))) }

    $exe = Join-Path $target 'construct-agent.exe'
    Set-ItemProperty -Path $runKey -Name 'ConstructAgent' -Value ('"' + $exe + '" --background')
    Start-Process -FilePath $exe -ArgumentList '--background' | Out-Null
    return $version
}

function Wait-ConstructAgent {
    for ($i = 0; $i -lt 120; $i++) {
        $running = Get-ConstructRunningAgent
        if ($running) { return $running }
        Start-Sleep -Milliseconds 500
    }
    throw 'The Construct agent did not start; run construct-agent.exe repair from its folder for details.'
}

$channel = 'stable'
if ($Ref -ne 'main') { $channel = 'nightly'; Write-Host "==> $Ref is not main: following the nightly channel." -ForegroundColor DarkGray }

$running = Get-ConstructRunningAgent
if ($running) {
    # Hand over: the running agent updates itself and opens its window. The extension of the installed version
    # goes into VS Code first, so an old control panel left in VS Code is replaced here too.
    Write-Host '==> The Construct agent is running; it updates itself.' -ForegroundColor Cyan
    $current = $null
    try { $current = [string](Get-Content -Raw -LiteralPath (Join-Path $agentHome 'install.json') | ConvertFrom-Json).current } catch { $current = $null }
    if ($current -match '^[A-Za-z0-9.+_-]+$' -and $current -notin @('.', '..')) {
        $folder = Join-Path $agentHome ('versions\' + $current)
        if (Test-Path -LiteralPath (Join-Path $folder 'construct.vsix')) { Install-ConstructVsCodeExtension -Folder $folder }
    }
    try {
        $offer = Invoke-ConstructAgentRoute -Endpoint $running -Path '/api/update/check'
        if ($offer.available) { Invoke-ConstructAgentRoute -Endpoint $running -Path '/api/update/install' | Out-Null }
    } catch { Write-Warning 'The agent could not check for an update now; it checks by itself every six hours.' }
    try { Invoke-ConstructAgentRoute -Endpoint $running -Path '/api/shell/open' -Body @{ surface = 'win' } | Out-Null } catch { }
    return
}

$stoppedCompanion = Stop-ConstructOldCompanion
try {
    $installed = Install-ConstructAgentPackage -Channel $channel
    $null = Wait-ConstructAgent
} catch {
    # Leave the user with a running Companion: the update can be retried from its button.
    if ($stoppedCompanion) { Start-ConstructOldCompanion }
    throw
}
# The agent holds the Companion's single-instance mutex; the Companion no longer starts with Windows.
Remove-ItemProperty -Path $runKey -Name 'ConstructCompanion' -ErrorAction SilentlyContinue
Write-Host "==> The Construct agent $installed is running. Its window finishes the setup." -ForegroundColor Green
