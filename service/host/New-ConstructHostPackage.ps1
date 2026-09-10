#requires -Version 5.1
<# Builds the GitHub Release package and manifest with SHA-256 payload checksums. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [Parameter(Mandatory=$true)][string]$OutputDir,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [DateTimeOffset]$BuiltAt = [DateTimeOffset]::UtcNow
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$utf8 = New-Object System.Text.UTF8Encoding($false)
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $OutputDir) { throw 'Output directory must be new.' }
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null
$payload = Join-Path $OutputDir 'payload'
[IO.Directory]::CreateDirectory($payload) | Out-Null
function Copy-PayloadFile([string]$Source, [string]$Relative) {
    if ($Relative -match '(^|/)(\.\.?|keys|\.git|\.construct-tools|projects|settings\.json|appsettings\.Production\.json)(/|$)' -or $Relative -match '\.db($|[-.])') { throw 'Preserved file in package.' }
    if ((Get-Item -LiteralPath $Source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package cannot contain links.' }
    $target = Join-Path $payload $Relative
    [IO.Directory]::CreateDirectory((Split-Path $target -Parent)) | Out-Null
    [IO.File]::Copy($Source, $target)
}
function Get-ConstructHostMigrationMetadata([string]$Root) {
    $dir = Join-Path $Root 'service/src/Constructd.Sqlite/Migrations'
    $registry = Get-Content -Raw (Join-Path $dir 'SqliteMigrations.cs')
    $ids=@();$breaking=@()
    foreach ($match in [regex]::Matches($registry, 'new (M([0-9]+)_[A-Za-z0-9_]+)\(')) {
        $id=[int]$match.Groups[2].Value
        $source=Get-Content -Raw (Join-Path $dir ($match.Groups[1].Value+'.cs'))
        $declaration=[regex]::Match($source,'public bool Breaking => (true|false);')
        if (-not $declaration.Success) { throw 'Migration breaking metadata must be explicit.' }
        $ids+=$id
        if ($declaration.Groups[1].Value -eq 'true') { $breaking+=$id }
    }
    if ($ids.Count -eq 0) { throw 'No registered migrations.' }
    $minimum=0
    if ($breaking.Count -gt 0) { $minimum=[int]($breaking|Measure-Object -Maximum).Maximum }
    return @{schemaVersion=[int]($ids|Measure-Object -Maximum).Maximum;minReadableBy=$minimum;breakingMigrations=@($breaking|Sort-Object)}
}
try {
    $publishRoot = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
    foreach ($file in Get-ChildItem -LiteralPath $publishRoot -Recurse -File) {
        $relative = $file.FullName.Substring($publishRoot.Length + 1).Replace('\','/')
        Copy-PayloadFile $file.FullName ('service/' + $relative)
    }
    if (-not (Test-Path (Join-Path $payload 'service/Constructd.Api.exe'))) { throw 'Self-contained win-x64 publish output is required.' }
    # Only tracked files from this checkout; never working-tree data, build output or credentials.
    $tracked = @(& git -C $RepositoryRoot ls-files -- drivers lib bin config docs Create-AgentVM.ps1 Provision-AgentVM.ps1 service/host)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate scripts.' }
    foreach ($rel in ($tracked | Sort-Object -Unique)) {
        if ($rel.StartsWith('docs/') -and $rel -notmatch '\.(md|txt|json|yml|yaml)$') { continue }
        if ($rel.StartsWith('service/host/') -and $rel -notmatch '\.ps1$') { continue }
        Copy-PayloadFile (Join-Path $RepositoryRoot $rel) ('scripts/' + $rel)
    }
    Copy-PayloadFile (Join-Path $RepositoryRoot 'service/host/Update-ConstructHost.ps1') 'updater/Update-ConstructHost.ps1'
    $lines = @()
    foreach ($file in (Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName)) {
        $rel = $file.FullName.Substring($payload.Length + 1).Replace('\','/')
        $lines += (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $rel
    }
    [IO.File]::WriteAllText((Join-Path $payload 'SHA256SUMS'), (($lines -join "`n") + "`n"), $utf8)
    $asset = 'construct-host-' + $Commit.Substring(0,7) + '-win-x64.zip'
    # Stored entries keep the contract's 4x extraction bound true even for highly compressible scripts.
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, (Join-Path $OutputDir $asset), [IO.Compression.CompressionLevel]::NoCompression, $false)
    $databaseMetadata = Get-ConstructHostMigrationMetadata $RepositoryRoot
    $manifest = [ordered]@{
        schemaVersion=1; commit=$Commit; ref='refs/heads/main'; packageVersion=($BuiltAt.ToString('yyyy.MM.dd') + '+' + $Commit.Substring(0,7)); builtAt=$BuiltAt.ToString('o')
        features=@('local-vm-adoption-v1')
        repository='permissionBRICK/The-Construct'; releaseTag=('host-' + $Commit); payloadAsset=$asset
        payloadSha256=(Get-FileHash (Join-Path $OutputDir $asset) -Algorithm SHA256).Hash.ToLowerInvariant()
        sumsSha256=(Get-FileHash (Join-Path $payload 'SHA256SUMS') -Algorithm SHA256).Hash.ToLowerInvariant()
        updaterPath='updater/Update-ConstructHost.ps1'; updaterSha256=(Get-FileHash (Join-Path $payload 'updater/Update-ConstructHost.ps1') -Algorithm SHA256).Hash.ToLowerInvariant()
        database=$databaseMetadata
        config=@{settingsSchemaVersion=1;minReadableBy=1;requiredKeys=@();newKeysWithDefaults=@('Constructd:HostAdmin:*')}
        compat=@{minInstalledCommitDate='2026-08-01';minSchemaVersionToUpdateFrom=0}
    }
    $manifestPath = Join-Path $OutputDir 'manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), $utf8)
    Copy-Item -LiteralPath (Join-Path $payload 'SHA256SUMS') -Destination (Join-Path $OutputDir 'SHA256SUMS')
} finally { if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force } }
