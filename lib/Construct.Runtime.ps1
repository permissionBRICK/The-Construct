#Requires -Version 5.1
# Shared release metadata and runtime probing. Dot-sourcing has no side effects.
function Get-ConstructRequiredRuntimes {
    param([string]$RuntimeConfig)
    $config=Get-Content -LiteralPath $RuntimeConfig -Raw | ConvertFrom-Json
    $frameworks=@($config.runtimeOptions.frameworks)
    if ($config.runtimeOptions.framework) { $frameworks+=@($config.runtimeOptions.framework) }
    $requirements=@(foreach ($framework in $frameworks) {
        if ($null -eq $framework) { continue }
        $version=$null
        if ($framework.name -cnotmatch '^Microsoft\.(NETCore|WindowsDesktop|AspNetCore)\.App$' -or
            -not [version]::TryParse([string]$framework.version,[ref]$version)) { throw 'Invalid shared framework metadata.' }
        [ordered]@{name=[string]$framework.name;majorVersion=$version.Major}
    })
    if ($requirements.Count -eq 0) { throw 'Framework-dependent runtimeconfig must list shared frameworks.' }
    return $requirements
}

function Assert-ConstructFrameworkDependentManifest {
    param($Manifest,[string]$Asset)
    $fields=@('frameworkDependentAsset','frameworkDependentSha256','frameworkDependentSizeBytes','frameworkDependentSumsSha256','frameworkDependentUncompressedSizeBytes','runtimes')
    $present=@($fields | Where-Object { $null -ne $Manifest.$_ })
    if ($present.Count -eq 0) { return }
    if ($present.Count -ne $fields.Count -or $Manifest.frameworkDependentAsset -cne $Asset -or
        $Manifest.frameworkDependentSha256 -cnotmatch '^[0-9a-f]{64}$' -or $Manifest.frameworkDependentSumsSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        ($Manifest.frameworkDependentSizeBytes -isnot [int] -and $Manifest.frameworkDependentSizeBytes -isnot [long]) -or
        ($Manifest.frameworkDependentUncompressedSizeBytes -isnot [int] -and $Manifest.frameworkDependentUncompressedSizeBytes -isnot [long]) -or $Manifest.frameworkDependentUncompressedSizeBytes -le 0 -or $Manifest.frameworkDependentUncompressedSizeBytes -gt 1GB -or
        $Manifest.frameworkDependentSizeBytes -le 0 -or $Manifest.frameworkDependentSizeBytes -gt 1GB -or @($Manifest.runtimes).Count -eq 0) { throw 'Invalid framework-dependent manifest.' }
    $seen=@{}
    foreach ($runtime in $Manifest.runtimes) {
        if ($runtime.name -cnotmatch '^Microsoft\.(NETCore|WindowsDesktop|AspNetCore)\.App$' -or $seen.ContainsKey([string]$runtime.name) -or
            ($runtime.majorVersion -isnot [int] -and $runtime.majorVersion -isnot [long]) -or $runtime.majorVersion -le 0) { throw 'Invalid shared framework requirement.' }
        $seen[$runtime.name]=$true
    }
}

function Invoke-ConstructRuntimeNative {
    param([string]$Exe,[string[]]$Arguments)
    if (-not (Get-Command $Exe -ErrorAction SilentlyContinue)) { return @{exitCode=127;output=@()} }
    $prior=$ErrorActionPreference
    try { $ErrorActionPreference='Continue'; $output=@(& $Exe @Arguments 2>&1); return @{exitCode=$LASTEXITCODE;output=$output} }
    catch { return @{exitCode=127;output=@()} }
    finally { $ErrorActionPreference=$prior }
}

function Test-ConstructSharedRuntimes {
    param($Runtimes,[scriptblock]$Native=${function:Invoke-ConstructRuntimeNative})
    if (@($Runtimes).Count -eq 0) { return $false }
    try { $result=& $Native 'dotnet' @('--list-runtimes') } catch { return $false }
    if ($result.exitCode -ne 0) { return $false }
    foreach ($runtime in $Runtimes) {
        $pattern='^'+[regex]::Escape($runtime.name)+' '+[regex]::Escape([string]$runtime.majorVersion)+'\.\d+\.\d+ \[.+\]$'
        if (@($result.output | Where-Object { $_ -is [string] -and $_ -cmatch $pattern }).Count -eq 0) { return $false }
    }
    return $true
}

function Select-ConstructReleasePayload {
    param($Manifest,[scriptblock]$Native=${function:Invoke-ConstructRuntimeNative})
    if ($Manifest.frameworkDependentAsset -and (Test-ConstructSharedRuntimes $Manifest.runtimes $Native)) {
        return @{source='framework-dependent';asset=$Manifest.frameworkDependentAsset;sha256=$Manifest.frameworkDependentSha256;sizeBytes=$Manifest.frameworkDependentSizeBytes;sumsSha256=$Manifest.frameworkDependentSumsSha256;uncompressedSizeBytes=$Manifest.frameworkDependentUncompressedSizeBytes}
    }
    return @{source='self-contained';asset=$Manifest.payloadAsset;sha256=$Manifest.payloadSha256;sizeBytes=$Manifest.payloadSizeBytes;sumsSha256=$Manifest.sumsSha256;uncompressedSizeBytes=$Manifest.payloadUncompressedSizeBytes}
}

# Absolute extraction bounds apply to old manifests too; new totals must match exactly.
function Assert-ConstructArchiveLengths {
    param($Archive,$DeclaredTotal)
    if ($null -ne $DeclaredTotal -and (($DeclaredTotal -isnot [int] -and $DeclaredTotal -isnot [long]) -or $DeclaredTotal -le 0 -or $DeclaredTotal -gt 1GB)) { throw 'Invalid archive uncompressed size.' }
    if ($Archive.Entries.Count -gt 20000) { throw 'Too many archive entries.' }
    $total=0L
    foreach ($entry in $Archive.Entries) {
        if ($entry.FullName -and $entry.FullName.EndsWith('/')) { throw 'Archive directory entries are not supported.' }
        if ($entry.Length -lt 0 -or $entry.Length -gt 256MB) { throw 'Archive entry exceeds extraction limit.' }
        $total+=$entry.Length
        if ($total -gt 1GB -or ($null -ne $DeclaredTotal -and $total -gt $DeclaredTotal)) { throw 'Archive exceeds declared extraction limit.' }
    }
    if ($null -ne $DeclaredTotal -and $total -ne $DeclaredTotal) { throw 'Archive uncompressed size mismatch.' }
}

function Expand-ConstructBoundedEntry {
    param($Entry,[string]$Destination,[switch]$Hash)
    $hasher=$null
    if ($Hash) { $hasher=[Security.Cryptography.SHA256]::Create() }
    $inputStream=$Entry.Open(); $outputStream=$null
    try {
        $outputStream=[IO.Stream]::Null
        if ($Destination) { $outputStream=[IO.File]::Open($Destination,[IO.FileMode]::Create,[IO.FileAccess]::Write) }
        $buffer=New-Object byte[] 81920; $total=0L
        while (($read=$inputStream.Read($buffer,0,$buffer.Length)) -gt 0) {
            $total+=$read
            if ($total -gt $Entry.Length -or $total -gt 256MB) { throw 'Archive entry uncompressed size mismatch.' }
            if ($hasher) { $null=$hasher.TransformBlock($buffer,0,$read,$buffer,0) }
            $outputStream.Write($buffer,0,$read)
        }
        if ($total -ne $Entry.Length) { throw 'Archive entry uncompressed size mismatch.' }
        if ($hasher) { $null=$hasher.TransformFinalBlock((New-Object byte[] 0),0,0); return ([BitConverter]::ToString($hasher.Hash)).Replace('-','').ToLowerInvariant() }
    } finally { if ($hasher) { $hasher.Dispose() }; if ($outputStream) { $outputStream.Dispose() }; $inputStream.Dispose() }
}
