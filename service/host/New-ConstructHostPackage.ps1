#requires -Version 5.1
<# Builds the GitHub Release package and manifest with SHA-256 payload checksums. #>
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$PublishDir,
    [Parameter(Mandatory=$true)][string]$OutputDir,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
    [string]$RepositoryRoot = (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent),
    [string]$Repository = 'permissionBRICK/The-Construct',
    [DateTimeOffset]$BuiltAt = [DateTimeOffset]::UtcNow,
    [string]$FrameworkDependentPublishDir,
    # A linux-x64 self-contained publish, with the same scripts layout and its own updater.
    [string]$LinuxPublishDir
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../lib/Construct.Runtime.ps1')
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$utf8 = New-Object System.Text.UTF8Encoding($false)
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if (Test-Path -LiteralPath $OutputDir) { throw 'Output directory must be new.' }
[IO.Directory]::CreateDirectory($OutputDir) | Out-Null
$payload = Join-Path $OutputDir 'payload'
[IO.Directory]::CreateDirectory($payload) | Out-Null
function Copy-PayloadFile([string]$Source, [string]$Relative) {
    if ($Relative -match '(^|/)(\.\.?|keys|\.git|\.construct-tools|projects|data|media|iso|install\.json|settings\.json|appsettings\.Production\.json)(/|$)' -or $Relative -match '\.db') { throw 'Preserved file in package.' }
    for ($current = [IO.Path]::GetFullPath($Source); $current; $current = Split-Path $current -Parent) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package cannot contain links.' }
    }
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
function New-HostPayload([string]$PublishDir, [string]$UpdaterName, [string]$Rid) {
    $payload = Join-Path $OutputDir ('payload-' + $Rid)
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    $publishRoot = (Resolve-Path -LiteralPath $PublishDir).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
    foreach ($file in Get-ChildItem -LiteralPath $publishRoot -Recurse -Force) {
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package cannot contain links.' }
        if ($file.PSIsContainer) { continue }
        $relative = $file.FullName.Substring($publishRoot.Length + 1).Replace('\','/')
        Copy-PayloadFile $file.FullName ('service/' + $relative)
    }
    $executable = 'Constructd.Api'; if ($Rid -eq 'win-x64') { $executable += '.exe' }
    if (-not (Test-Path (Join-Path $payload ('service/' + $executable)))) { throw "$Rid publish output is required." }
    # Only tracked files from this checkout; never working-tree data, build output or credentials.
    $tracked = @(& git -C $RepositoryRoot ls-files -- drivers lib bin config docs Create-AgentVM.ps1 Provision-AgentVM.ps1 service/host)
    if ($LASTEXITCODE -ne 0) { throw 'Cannot enumerate scripts.' }
    foreach ($rel in ($tracked | Sort-Object -Unique)) {
        if ($rel.StartsWith('docs/') -and $rel -notmatch '\.(md|txt|json|yml|yaml)$') { continue }
        if ($rel.StartsWith('service/host/') -and $rel -notmatch '\.(ps1|sh)$') { continue }
        Copy-PayloadFile (Join-Path $RepositoryRoot $rel) ('scripts/' + $rel)
    }
    Copy-PayloadFile (Join-Path $RepositoryRoot ('service/host/' + $UpdaterName)) ('updater/' + $UpdaterName)
    [IO.File]::WriteAllText((Join-Path $payload 'scripts/.construct-revision'), ($Commit + "`n"), $utf8)
    $lines = @()
    foreach ($file in (Get-ChildItem -LiteralPath $payload -Recurse -File -Force | Sort-Object FullName)) {
        $rel = $file.FullName.Substring($payload.Length + 1).Replace('\','/')
        $lines += (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $rel
    }
    [IO.File]::WriteAllText((Join-Path $payload 'SHA256SUMS'), (($lines -join "`n") + "`n"), $utf8)
    $asset = 'construct-host-' + $Commit.Substring(0,7) + '-' + $Rid + '.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($payload, (Join-Path $OutputDir $asset), [IO.Compression.CompressionLevel]::Optimal, $false)
    return @{Path=$payload;Asset=$asset}
}
try {
    $windows = New-HostPayload $PublishDir 'Update-ConstructHost.ps1' 'win-x64'
    $payload = $windows.Path; $asset = $windows.Asset
    $databaseMetadata = Get-ConstructHostMigrationMetadata $RepositoryRoot
    $manifest = [ordered]@{
        schemaVersion=1; commit=$Commit; ref='refs/heads/main'; packageVersion=($BuiltAt.ToString('yyyy.MM.dd') + '+' + $Commit.Substring(0,7)); builtAt=$BuiltAt.ToString('o')
        features=@('local-vm-adoption-v1')
        repository=$Repository; releaseTag=('host-' + $Commit); payloadAsset=$asset
        payloadSha256=(Get-FileHash (Join-Path $OutputDir $asset) -Algorithm SHA256).Hash.ToLowerInvariant()
        sumsSha256=(Get-FileHash (Join-Path $payload 'SHA256SUMS') -Algorithm SHA256).Hash.ToLowerInvariant()
        updaterPath='updater/Update-ConstructHost.ps1'; updaterSha256=(Get-FileHash (Join-Path $payload 'updater/Update-ConstructHost.ps1') -Algorithm SHA256).Hash.ToLowerInvariant()
        database=$databaseMetadata
        config=@{settingsSchemaVersion=1;minReadableBy=1;requiredKeys=@();newKeysWithDefaults=@('Constructd:HostAdmin:*')}
        compat=@{minInstalledCommitDate='2026-08-01';minSchemaVersionToUpdateFrom=0}
    }
    $manifest.payloadSizeBytes=(Get-Item -LiteralPath (Join-Path $OutputDir $asset)).Length
    $manifest.payloadUncompressedSizeBytes=[long](Get-ChildItem -LiteralPath $payload -Recurse -File -Force | Measure-Object Length -Sum).Sum
    $archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $OutputDir $asset))
    try { Assert-ConstructArchiveLengths $archive $manifest.payloadUncompressedSizeBytes } finally { $archive.Dispose() }
    if ($FrameworkDependentPublishDir) {
        $fddOutput=Join-Path $OutputDir 'fdd-package'
        try {
            & $PSCommandPath -PublishDir $FrameworkDependentPublishDir -OutputDir $fddOutput -Commit $Commit -RepositoryRoot $RepositoryRoot -Repository $Repository -BuiltAt $BuiltAt
            $fdd=Get-Content -LiteralPath (Join-Path $fddOutput 'manifest.json') -Raw | ConvertFrom-Json
            $manifest.frameworkDependentAsset='construct-host-'+$Commit.Substring(0,7)+'-win-x64-fdd.zip'
            Move-Item -LiteralPath (Join-Path $fddOutput $fdd.payloadAsset) -Destination (Join-Path $OutputDir $manifest.frameworkDependentAsset)
            $manifest.frameworkDependentSha256=$fdd.payloadSha256
            $manifest.frameworkDependentSizeBytes=$fdd.payloadSizeBytes
            $manifest.frameworkDependentSumsSha256=$fdd.sumsSha256
            $manifest.frameworkDependentUncompressedSizeBytes=$fdd.payloadUncompressedSizeBytes
            $manifest.runtimes=@(Get-ConstructRequiredRuntimes (Join-Path $FrameworkDependentPublishDir 'Constructd.Api.runtimeconfig.json'))
        } finally { if (Test-Path -LiteralPath $fddOutput) { Remove-Item -LiteralPath $fddOutput -Recurse -Force } }
    }
    if ($LinuxPublishDir) {
        $linux = New-HostPayload $LinuxPublishDir 'update-construct-host.sh' 'linux-x64'
        $manifest.linuxAsset=$linux.Asset
        $manifest.linuxSha256=(Get-FileHash (Join-Path $OutputDir $manifest.linuxAsset) -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest.linuxSizeBytes=(Get-Item -LiteralPath (Join-Path $OutputDir $manifest.linuxAsset)).Length
        $manifest.linuxSumsSha256=(Get-FileHash (Join-Path $linux.Path 'SHA256SUMS') -Algorithm SHA256).Hash.ToLowerInvariant()
        $manifest.linuxUncompressedSizeBytes=[long](Get-ChildItem -LiteralPath $linux.Path -Recurse -File -Force | Measure-Object Length -Sum).Sum
        $manifest.linuxUpdaterPath='updater/update-construct-host.sh'
        $manifest.linuxUpdaterSha256=(Get-FileHash (Join-Path $linux.Path $manifest.linuxUpdaterPath) -Algorithm SHA256).Hash.ToLowerInvariant()
        $archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $OutputDir $manifest.linuxAsset))
        try { Assert-ConstructArchiveLengths $archive $manifest.linuxUncompressedSizeBytes } finally { $archive.Dispose() }
    }
    $manifestPath = Join-Path $OutputDir 'manifest.json'
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 10), $utf8)
    Copy-Item -LiteralPath (Join-Path $payload 'SHA256SUMS') -Destination (Join-Path $OutputDir 'SHA256SUMS')
    $archiveSums=$manifest.payloadSha256+'  '+$asset+"`n"
    if ($FrameworkDependentPublishDir) { $archiveSums+=$manifest.frameworkDependentSha256+'  '+$manifest.frameworkDependentAsset+"`n" }
    if ($LinuxPublishDir) { $archiveSums+=$manifest.linuxSha256+'  '+$manifest.linuxAsset+"`n" }
    [IO.File]::AppendAllText((Join-Path $OutputDir 'SHA256SUMS'),$archiveSums,$utf8)
} finally {
    foreach ($dir in @('payload','payload-win-x64','payload-linux-x64')) {
        $path=Join-Path $OutputDir $dir
        if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
    }
}
