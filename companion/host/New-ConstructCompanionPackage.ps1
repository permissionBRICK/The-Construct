#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDir,
    [Parameter(Mandatory)][string]$OutputDir,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository='permissionBRICK/The-Construct',
    [DateTimeOffset]$BuiltAt=[DateTimeOffset]::UtcNow
)
$ErrorActionPreference='Stop'
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
[IO.Directory]::CreateDirectory((Join-Path $payload 'app')) | Out-Null
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
    [IO.Compression.ZipFile]::CreateFromDirectory($payload,$zip,[IO.Compression.CompressionLevel]::NoCompression,$false)
    $manifest=[ordered]@{
        schemaVersion=1; commit=$Commit; ref='refs/heads/main'; packageVersion=($BuiltAt.ToString('yyyy.MM.dd')+'+'+$Commit.Substring(0,7)); builtAt=$BuiltAt.ToString('o')
        repository=$Repository; releaseTag=('companion-'+$Commit); payloadAsset=$asset
        payloadSha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
        sumsSha256=(Get-FileHash -LiteralPath $sums -Algorithm SHA256).Hash.ToLowerInvariant()
        exe='app\ConstructCompanion.exe'; ipcApiVersion=1
    }
    [IO.File]::WriteAllText((Join-Path $OutputDir 'manifest.json'),($manifest | ConvertTo-Json),$utf8)
    Copy-Item -LiteralPath $sums -Destination (Join-Path $OutputDir 'SHA256SUMS')
} finally { if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force } }
