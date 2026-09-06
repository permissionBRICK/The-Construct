# Windows PowerShell 5.1-compatible resolver for the independently released ISO tool.
# Dot-sourcing this file performs no I/O or installation.

function Get-ConstructIsoBuilderPath {
    param([Parameter(Mandatory = $true)][string]$ScriptsDir)
    return Join-Path $ScriptsDir '.construct-tools\iso\Construct.Iso.exe'
}

function Get-ConstructIsoRelease {
    param([Parameter(Mandatory = $true)][string]$ScriptsDir)
    $release = Get-Content -Raw -LiteralPath (Join-Path $ScriptsDir 'config\iso-builder.json') | ConvertFrom-Json
    if ($release.repository -ne 'permissionBRICK/construct-iso' -or
        $release.tag -notmatch '^build-[a-f0-9]{40}$' -or
        $release.zipSha256 -notmatch '^[a-f0-9]{64}$' -or $release.exeSha256 -notmatch '^[a-f0-9]{64}$') {
        throw 'Invalid pinned ISO builder release in config/iso-builder.json.'
    }
    return $release
}

function Publish-ConstructIsoExecutable {
    param([string]$Source, [string]$Destination)
    $staged = "$Destination.$([guid]::NewGuid().ToString('N')).partial"
    try {
        [System.IO.File]::Copy($Source, $staged, $false)
        if (Test-Path -LiteralPath $Destination) {
            [System.IO.File]::Replace($staged, $Destination, [NullString]::Value)
        } else {
            [System.IO.File]::Move($staged, $Destination)
        }
    } finally {
        if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force }
    }
}

function Resolve-ConstructIsoBuilder {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ScriptsDir,
        [string]$SourceDir = $env:CONSTRUCT_ISO_SOURCE_DIR
    )
    $destination = Get-ConstructIsoBuilderPath -ScriptsDir $ScriptsDir
    $cache = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $cache)) { New-Item -ItemType Directory -Path $cache -Force | Out-Null }
    if (-not $SourceDir) { $SourceDir = Join-Path (Split-Path -Parent $ScriptsDir) 'construct-iso' }
    $project = Join-Path $SourceDir 'src\Construct.Iso\Construct.Iso.csproj'
    $hasSdk = $false
    if ((Test-Path -LiteralPath $project) -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        $sdks = & dotnet --list-sdks 2>$null
        $hasSdk = $LASTEXITCODE -eq 0 -and @($sdks | Where-Object { $_ -match '^10\.' }).Count -gt 0
    }
    if ($hasSdk) {
        Write-Host 'Building the ISO tool from the local construct-iso checkout...'
        $localBuild = Join-Path $cache ('build-' + [guid]::NewGuid().ToString('N'))
        try {
            & dotnet publish $project -c Release -r win-x64 --self-contained true `
                '-p:PublishSingleFile=true' '-p:EnableCompressionInSingleFile=true' -o $localBuild | Out-Host
            if ($LASTEXITCODE -ne 0) { throw 'Building the local ISO tool failed.' }
            $built = Join-Path $localBuild 'Construct.Iso.exe'
            if (-not (Test-Path -LiteralPath $built)) { throw 'Local ISO tool build produced no executable.' }
            Publish-ConstructIsoExecutable -Source $built -Destination $destination
        } finally {
            if (Test-Path -LiteralPath $localBuild) { Remove-Item -LiteralPath $localBuild -Recurse -Force }
        }
        return $destination
    }

    $release = Get-ConstructIsoRelease -ScriptsDir $ScriptsDir
    if ((Test-Path -LiteralPath $destination) -and
        (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq $release.exeSha256) {
        return $destination
    }

    Write-Host "Downloading the self-contained ISO tool ($($release.tag))..."
    $work = Join-Path $cache ('download-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $work | Out-Null
    try {
        $zip = Join-Path $work 'tool.zip'
        $uri = "https://github.com/$($release.repository)/releases/download/$($release.tag)/Construct.Iso-win-x64.zip"
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -UseBasicParsing -Uri $uri -OutFile $zip
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $release.zipSha256) {
            throw 'ISO tool archive checksum mismatch; the existing tool was retained.'
        }
        $unpacked = Join-Path $work 'unpacked'
        Expand-Archive -LiteralPath $zip -DestinationPath $unpacked
        $exe = Join-Path $unpacked 'Construct.Iso.exe'
        if (-not (Test-Path -LiteralPath $exe) -or
            (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $release.exeSha256) {
            throw 'ISO tool executable checksum mismatch; the existing tool was retained.'
        }
        Publish-ConstructIsoExecutable -Source $exe -Destination $destination
    } finally {
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    }
    return $destination
}

function Invoke-ConstructNativeIsoBuild {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$ToolPath,
        [Parameter(Mandatory = $true)][string]$SourceIso,
        [Parameter(Mandatory = $true)][string]$OutputIso,
        [Parameter(Mandatory = $true)][string]$BootstrapPublicKeyPath,
        [string]$User = 'agent',
        [string]$Password = 'agent',
        [string]$Hostname = 'agent-vm',
        [string]$HostnameSource = 'static',
        [string]$SourceId = 'ubuntu-server-minimal',
        [switch]$Overwrite
    )
    $request = @{
        SourceIso = [System.IO.Path]::GetFullPath($SourceIso)
        OutputIso = [System.IO.Path]::GetFullPath($OutputIso)
        BootstrapPublicKeyPath = [System.IO.Path]::GetFullPath($BootstrapPublicKeyPath)
        User = $User; Password = $Password; Hostname = $Hostname
        HostnameSource = $HostnameSource; SourceId = $SourceId; Overwrite = [bool]$Overwrite
    } | ConvertTo-Json -Compress
    # PS 5.1 defaults to ASCII for native stdin. Preserve non-ASCII credentials as UTF-8.
    $previousEncoding = $OutputEncoding
    try {
        $OutputEncoding = New-Object System.Text.UTF8Encoding($false)
        $request | & $ToolPath --request-stdin | Out-Host
        $buildExit = $LASTEXITCODE
    } finally {
        $OutputEncoding = $previousEncoding
        $request = $null
    }
    if ($buildExit -ne 0) { throw "Native autoinstall ISO build failed (exit $buildExit)." }
    if (-not (Test-Path -LiteralPath $OutputIso) -or (Get-Item -LiteralPath $OutputIso).Length -le 0) {
        throw 'ISO build reported success but the output is missing or empty.'
    }
}
