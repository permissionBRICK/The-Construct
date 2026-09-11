#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$OutputDir,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository='permissionBRICK/The-Construct',
    [DateTimeOffset]$BuiltAt=[DateTimeOffset]::UtcNow,
    [string]$FrameworkDependentPublishDir
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../../lib/Construct.Runtime.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$utf8=New-Object Text.UTF8Encoding($false)
$publishRoot=(Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
$OutputDir=[IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $OutputDir) { throw 'Output directory must be new.' }
if ($OutputDir.StartsWith($publishRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Output must be outside the publish directory.' }
if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'ConstructCompanion.exe')) -or -not (Test-Path -LiteralPath (Join-Path $publishRoot 'media/panel.html'))) { throw 'Publish output must contain ConstructCompanion.exe and media/panel.html.' }
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null
$payload=Join-Path $OutputDir 'payload'
[IO.Directory]::CreateDirectory($payload) | Out-Null
try {
    foreach ($item in Get-ChildItem -LiteralPath $publishRoot -Recurse -Force) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package cannot contain links.' }
        if ($item.PSIsContainer) { continue }
        $relative=$item.FullName.Substring($publishRoot.Length+1).Replace('\','/')
        if ($relative -match '(^|/)(\.git|install\.json|settings\.json|endpoint\.json|logs)(/|$)' -or $relative -match '[\r\n]') { throw 'Package cannot contain user state.' }
        $target=Join-Path $payload ('app/'+$relative)
        [IO.Directory]::CreateDirectory((Split-Path $target -Parent)) | Out-Null
        [IO.File]::Copy($item.FullName,$target)
    }
    $lines=@(foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse | Sort-Object FullName) {
        (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()+'  '+$file.FullName.Substring($payload.Length+1).Replace('\','/')
    })
    $sums=Join-Path $payload 'SHA256SUMS'
    [IO.File]::WriteAllText($sums,($lines -join "`n")+"`n",$utf8)
    $asset='construct-companion-'+$Commit.Substring(0,7)+'-win-x64.zip'
    $zip=Join-Path $OutputDir $asset
    [IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
    $manifest=[ordered]@{
        schemaVersion=1; commit=$Commit; ref='refs/heads/main'; packageVersion=($BuiltAt.ToString('yyyy.MM.dd')+'+'+$Commit.Substring(0,7)); builtAt=$BuiltAt.ToString('o')
        repository=$Repository; releaseTag=('companion-'+$Commit); payloadAsset=$asset
        payloadSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        sumsSha256=(Get-FileHash -LiteralPath $sums -Algorithm SHA256).Hash.ToLowerInvariant()
        exe='app\ConstructCompanion.exe'; ipcApiVersion=1
    }
    $manifest.payloadUncompressedSizeBytes=[long](Get-ChildItem -LiteralPath $payload -File -Recurse | Measure-Object Length -Sum).Sum
    $archive=[IO.Compression.ZipFile]::OpenRead($zip)
    try { Assert-ConstructArchiveLengths $archive $manifest.payloadUncompressedSizeBytes } finally { $archive.Dispose() }
    $manifest.payloadSizeBytes=(Get-Item -LiteralPath $zip).Length
    if ($FrameworkDependentPublishDir) {
        $fddOutput=Join-Path $OutputDir 'fdd-package'
        & $PSCommandPath -PublishDir $FrameworkDependentPublishDir -OutputDir $fddOutput -Commit $Commit -Repository $Repository -BuiltAt $BuiltAt
        $fdd=Get-Content -LiteralPath (Join-Path $fddOutput 'manifest.json') -Raw | ConvertFrom-Json
        $fddPayload=Join-Path $OutputDir 'fdd-payload'
        try {
            [IO.Compression.ZipFile]::ExtractToDirectory((Join-Path $fddOutput $fdd.payloadAsset),$fddPayload)
            $fddAsset='construct-companion-'+$Commit.Substring(0,7)+'-win-x64-fdd.zip'
            $fddZip=Join-Path $OutputDir $fddAsset
            [IO.Compression.ZipFile]::CreateFromDirectory($fddPayload,$fddZip,[IO.Compression.CompressionLevel]::Optimal,$false)
            $manifest.frameworkDependentAsset=$fddAsset
            $manifest.frameworkDependentSha256=(Get-FileHash -LiteralPath $fddZip).Hash.ToLowerInvariant()
            $manifest.frameworkDependentSizeBytes=(Get-Item -LiteralPath $fddZip).Length
            $manifest.frameworkDependentSumsSha256=$fdd.sumsSha256
            $manifest.frameworkDependentUncompressedSizeBytes=$fdd.payloadUncompressedSizeBytes
            $manifest.runtimes=@(Get-ConstructRequiredRuntimes (Join-Path $FrameworkDependentPublishDir 'ConstructCompanion.runtimeconfig.json'))
        } finally {
            if (Test-Path -LiteralPath $fddPayload) { Remove-Item -LiteralPath $fddPayload -Recurse -Force }
            Remove-Item -LiteralPath $fddOutput -Recurse -Force
        }
    }
    [IO.File]::WriteAllText((Join-Path $OutputDir 'manifest.json'),($manifest | ConvertTo-Json -Depth 10),$utf8)
    Copy-Item -LiteralPath $sums -Destination (Join-Path $OutputDir 'SHA256SUMS')
    $archiveSums=$manifest.payloadSha256+'  '+$asset+"`n"
    if ($FrameworkDependentPublishDir) { $archiveSums+=$manifest.frameworkDependentSha256+'  '+$manifest.frameworkDependentAsset+"`n" }
    [IO.File]::AppendAllText((Join-Path $OutputDir 'SHA256SUMS'),$archiveSums,$utf8)
} finally { if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force } }
