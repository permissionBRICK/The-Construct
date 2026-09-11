#Requires -Version 5.1
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'lib/Construct.Companion.ps1')
$script:count=0
function Assert($Condition,[string]$Message) { if (-not $Condition) { throw $Message }; $script:count++ }
function Reject([scriptblock]$Action,[string]$Message) { $failed=$false; try { & $Action | Out-Null } catch { $failed=$true }; Assert $failed $Message }
$dir=Join-Path ([IO.Path]::GetTempPath()) ('companion-package-'+[guid]::NewGuid().ToString('N'))
try {
    $workflow = Get-Content -Raw (Join-Path $root '.github/workflows/companion-release.yml')
    Assert ($workflow -notmatch 'test-linux:|needs:|dotnet test|Headless no-instances selftest|actions/setup-node@') 'Release workflow only produces deliverables'
    Assert ($workflow -match 'dotnet publish.*-r win-x64 --self-contained true') 'Self-contained Windows publish retained'
    Assert ($workflow -match 'gh release create "companion-\$env:GITHUB_SHA" --latest=false') 'Companion cannot displace the complete Construct latest manifest'
    $publish=Join-Path $dir 'publish'; $out=Join-Path $dir 'package'; $sha='a'*40
    [IO.Directory]::CreateDirectory((Join-Path $publish 'media/themes')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $publish 'ConstructCompanion.exe'),'fixture exe, not runnable')
    [IO.File]::WriteAllText((Join-Path $publish 'runtime.dll'),'fixture dll')
    [IO.File]::WriteAllText((Join-Path $publish 'media/panel.html'),('x'*10000))
    [IO.File]::WriteAllText((Join-Path $publish 'media/themes/terminal.css'),'body {}')
    $fdd=Join-Path $dir 'fdd'
    [IO.Directory]::CreateDirectory((Join-Path $fdd 'media')) | Out-Null
    Copy-Item (Join-Path $publish 'ConstructCompanion.exe') $fdd
    Copy-Item (Join-Path $publish 'media/panel.html') (Join-Path $fdd 'media/panel.html')
    [IO.File]::WriteAllText((Join-Path $fdd 'ConstructCompanion.runtimeconfig.json'),'{"runtimeOptions":{"frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.WindowsDesktop.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}')
    & (Join-Path $root 'companion/host/New-ConstructCompanionPackage.ps1') -FrameworkDependentPublishDir $fdd -PublishDir $publish -OutputDir $out -Commit $sha -BuiltAt '2026-09-11T12:00:00Z' -Repository 'example/fork'
    $manifest=Read-ConstructCompanionJson (Join-Path $out 'manifest.json')
    Assert-ConstructCompanionManifest $manifest 'example/fork' ('companion-'+$sha)
    Assert ($manifest.packageVersion -eq '2026.09.11+aaaaaaa') 'Package version'
    Assert (($manifest.PSObject.Properties.Name -join ',') -eq 'schemaVersion,commit,ref,packageVersion,builtAt,repository,releaseTag,payloadAsset,payloadSha256,sumsSha256,exe,ipcApiVersion,payloadUncompressedSizeBytes,payloadSizeBytes,frameworkDependentAsset,frameworkDependentSha256,frameworkDependentSizeBytes,frameworkDependentSumsSha256,frameworkDependentUncompressedSizeBytes,runtimes') 'Exact manifest keys'
    $zip=Join-Path $out $manifest.payloadAsset
    Assert ((Get-FileHash $zip).Hash -eq $manifest.payloadSha256) 'Payload SHA'
    $detached=Get-Content -Raw (Join-Path $out 'SHA256SUMS')
    Assert ($detached.Contains($manifest.payloadSha256+'  '+$manifest.payloadAsset) -and $detached.Contains($manifest.frameworkDependentSha256+'  '+$manifest.frameworkDependentAsset)) 'Both archive hashes in detached sums'
    Assert ($manifest.runtimes.Count -eq 3 -and @($manifest.runtimes | Where-Object { $_.majorVersion -ne 10 }).Count -eq 0) 'Derived shared frameworks'
    $fddZip=Join-Path $out $manifest.frameworkDependentAsset
    Assert ((Get-FileHash $fddZip).Hash -eq $manifest.frameworkDependentSha256 -and (Get-Item $fddZip).Length -eq $manifest.frameworkDependentSizeBytes) 'FDD hash and size'
    $archive=[IO.Compression.ZipFile]::OpenRead($fddZip)
    try { Assert ($archive.GetEntry('app/media/panel.html').CompressedLength -lt 10000) 'FDD uses Optimal compression' } finally { $archive.Dispose() }
    $fddPayload=Select-ConstructReleasePayload $manifest {param($Exe,$Arguments) @{exitCode=0;output=@('Microsoft.NETCore.App 10.0.1 [/shared]','Microsoft.WindowsDesktop.App 10.0.1 [/shared]','Microsoft.AspNetCore.App 10.0.1 [/shared]')}}
    Expand-ConstructCompanionPayload $fddZip (Join-Path $dir 'fdd-unpacked') $manifest $fddPayload
    $fddPayload.sizeBytes++
    Reject { Expand-ConstructCompanionPayload $fddZip (Join-Path $dir 'wrong-size') $manifest $fddPayload } 'FDD size checked before extraction'
    $fddPayload.sizeBytes--
    Assert (-not (Test-Path (Join-Path $out 'payload'))) 'Packager staging removed'
    $archive=[IO.Compression.ZipFile]::OpenRead($zip)
    try {
        Assert ($archive.Entries.Count -eq 5) 'Only four app files plus sums'
        foreach ($entry in $archive.Entries) {
            Assert (-not $entry.FullName.Contains('\')) 'Portable ZIP names'
            Assert ($entry.CompressedLength -gt 0) 'Compressed entry present'
        }
    } finally { $archive.Dispose() }
    $oldTotal=$manifest.payloadUncompressedSizeBytes
    $manifest.payloadUncompressedSizeBytes=1
    Reject { Expand-ConstructCompanionPayload $zip (Join-Path $dir 'bad-total') $manifest } 'Total exceeds manifest rejected'
    Assert (-not (Test-Path (Join-Path $dir 'bad-total'))) 'Total checked before extraction'
    $manifest.payloadUncompressedSizeBytes=$oldTotal
    # A central directory declaring a huge or dishonest length must fail despite a valid ZIP hash.
    $originalBytes=[IO.File]::ReadAllBytes($zip)
    foreach ($declared in @(268435457,1)) {
        $bytes=$originalBytes.Clone()
        for ($i=0; $i -lt $bytes.Length-46; $i++) {
            if ([BitConverter]::ToUInt32($bytes,$i) -eq 0x02014b50) { [BitConverter]::GetBytes([uint32]$declared).CopyTo($bytes,$i+24); break }
        }
        $lying=Join-Path $dir ('lying-'+$declared+'.zip'); [IO.File]::WriteAllBytes($lying,$bytes)
        $lyingPayload=@{sha256=(Get-FileHash $lying).Hash;sizeBytes=$bytes.Length;sumsSha256=$manifest.sumsSha256}
        Reject { Expand-ConstructCompanionPayload $lying (Join-Path $dir ('lying-'+$declared)) $manifest $lyingPayload } 'Oversized or dishonest entry rejected'
        Assert (-not (Test-Path (Join-Path $dir ('lying-'+$declared)))) 'Dishonest length creates no destination'
    }
    Assert-ConstructArchiveLengths ([pscustomobject]@{Entries=@([pscustomobject]@{Length=1})}) $null
    Reject { Assert-ConstructArchiveLengths ([pscustomobject]@{Entries=@(1..5 | ForEach-Object { [pscustomobject]@{Length=256MB} })}) $null } 'Absolute ceiling enforced for legacy manifests'
    $legacy=$manifest | ConvertTo-Json -Depth 10 | ConvertFrom-Json
    $legacy.PSObject.Properties.Remove('payloadUncompressedSizeBytes')
    Expand-ConstructCompanionPayload $zip (Join-Path $dir 'legacy-unpacked') $legacy
    Assert (Test-Path (Join-Path $dir 'legacy-unpacked/app/ConstructCompanion.exe')) 'Legacy manifest extracts with absolute limits'
    $directoryZip=Join-Path $dir 'directory.zip'
    $archive=[IO.Compression.ZipFile]::Open($directoryZip,[IO.Compression.ZipArchiveMode]::Create)
    try { $null=$archive.CreateEntry('app/media/') } finally { $archive.Dispose() }
    Reject { Expand-ConstructCompanionPayload $directoryZip (Join-Path $dir 'directory') $legacy @{sha256=(Get-FileHash $directoryZip).Hash} } 'Explicit directory entries refused'
    $unpacked=Join-Path $dir 'unpacked'
    Expand-ConstructCompanionPayload $zip $unpacked $manifest
    foreach ($file in Get-ChildItem $publish -File -Recurse) {
        $relative=$file.FullName.Substring($publish.Length+1)
        Assert ((Get-FileHash $file.FullName).Hash -eq (Get-FileHash (Join-Path $unpacked ('app/'+$relative))).Hash) 'Extracted bytes identical'
    }
    $old=$manifest.payloadSha256; $manifest.payloadSha256='0'*64
    Reject { Expand-ConstructCompanionPayload $zip (Join-Path $dir 'bad-hash') $manifest } 'Corrupt zip rejected before extraction'
    Assert (-not (Test-Path (Join-Path $dir 'bad-hash'))) 'Hash failure makes no extraction directory'
    $manifest.payloadSha256=$old; $old=$manifest.sumsSha256; $manifest.sumsSha256='0'*64
    Reject { Expand-ConstructCompanionPayload $zip (Join-Path $dir 'bad-sums') $manifest } 'Corrupt sums rejected'
    $manifest.sumsSha256=$old
    # Valid outer hash cannot authorize traversal or a file missing from SHA256SUMS.
    $evil=Join-Path $dir 'evil.zip'
    $archive=[IO.Compression.ZipFile]::Open($evil,[IO.Compression.ZipArchiveMode]::Create)
    try { $null=$archive.CreateEntry('app/../../escape') } finally { $archive.Dispose() }
    $manifest.payloadSha256=(Get-FileHash $evil).Hash.ToLowerInvariant()
    Reject { Expand-ConstructCompanionPayload $evil (Join-Path $dir 'unsafe') $manifest } 'Traversal rejected'
    Assert (-not (Test-Path (Join-Path $dir 'unsafe'))) 'Traversal rejected before extraction'
    [IO.File]::WriteAllText((Join-Path $unpacked 'app/runtime.dll'),'tampered')
    $tampered=Join-Path $dir 'tampered.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($unpacked,$tampered)
    $manifest.payloadSha256=(Get-FileHash $tampered).Hash.ToLowerInvariant()
    Reject { Expand-ConstructCompanionPayload $tampered (Join-Path $dir 'bad-file') $manifest } 'Per-file corruption rejected'
    [IO.File]::WriteAllText((Join-Path $unpacked 'app/runtime.dll'),'fixture dll')
    [IO.File]::WriteAllText((Join-Path $unpacked 'app/unlisted.dll'),'extra')
    $extra=Join-Path $dir 'extra.zip'; [IO.Compression.ZipFile]::CreateFromDirectory($unpacked,$extra)
    $manifest.payloadSha256=(Get-FileHash $extra).Hash.ToLowerInvariant()
    Reject { Expand-ConstructCompanionPayload $extra (Join-Path $dir 'bad-extra') $manifest } 'Unlisted file rejected'
    # Exercise the complete release install with the real package and fake external I/O.
    $manifest=Read-ConstructCompanionJson (Join-Path $out 'manifest.json')
    $scripts=Join-Path $dir 'scripts'; [IO.Directory]::CreateDirectory($scripts) | Out-Null
    [IO.File]::WriteAllText((Join-Path $scripts '.construct-settings.json'),'{"constructRepo":"example/fork"}')
    $seams=New-ConstructCompanionSeams; $seams.Native={param($Exe,$Arguments) @{exitCode=127;output=@()}}; $seams.CheckUser={}; $seams.Start={param($Exe,$Arguments)}; $seams.Registry={param($Op,$Path,$Name,$Value) if ($Op -eq 'read') { return @{exists=$false} } }
    $script:urls=New-Object Collections.ArrayList; $script:downloads=0
    $seams.Json={ param($Uri)
        $null=$script:urls.Add($Uri)
        if ($Uri -match '/releases\?') { return @(@{tag_name=('companion-'+$sha);published_at='2026-09-11T00:00:00Z'}) }
        return $manifest
    }
    $script:fixtureZip=$zip
    $seams.Download={ param($Uri,$Destination,$ScriptsDir) $script:downloads++; $script:payloadUrl=$Uri; Copy-Item -LiteralPath $script:fixtureZip -Destination $Destination }
    $local=Join-Path $dir 'user'
    Assert ((Install-ConstructCompanion $scripts -Source release -LocalAppData $local -Seams $seams) -eq 'installed') 'Real package release install with fakes'
    $paths=Get-ConstructCompanionPaths $local
    $record=Read-ConstructCompanionJson (Join-Path $paths.install 'install.json')
    Assert ($record.source -eq 'self-contained' -and $record.releaseTag -eq ('companion-'+$sha)) 'Release install metadata'
    Assert ($script:urls[0] -eq 'https://api.github.com/repos/example/fork/releases?per_page=100&page=1') 'Configured repository API'
    Assert ($script:payloadUrl -eq ('https://github.com/example/fork/releases/download/companion-'+$sha+'/'+$manifest.payloadAsset)) 'Pinned payload URL'
    Assert ((Install-ConstructCompanion $scripts -Source release -LocalAppData $local -Seams $seams) -eq 'unchanged' -and $script:downloads -eq 1) 'Release idempotence avoids payload download'
    $seams.Native={param($Exe,$Arguments) @{exitCode=0;output=@('Microsoft.NETCore.App 10.0.1 [/shared]','Microsoft.WindowsDesktop.App 10.0.1 [/shared]','Microsoft.AspNetCore.App 10.0.1 [/shared]')}}
    $script:fixtureZip=$fddZip
    Assert ((Install-ConstructCompanion $scripts -Force -LocalAppData $local -Seams $seams) -eq 'installed') 'Real FDD release install'
    $record=Read-ConstructCompanionJson (Join-Path $paths.install 'install.json')
    Assert ($record.source -eq 'framework-dependent' -and $script:payloadUrl.EndsWith($manifest.frameworkDependentAsset)) 'FDD source and URL recorded'
    Reject { & (Join-Path $root 'companion/host/New-ConstructCompanionPackage.ps1') $publish $out $sha } 'Existing output refused'
    [IO.File]::WriteAllText((Join-Path $publish 'endpoint.json'),'state')
    Reject { & (Join-Path $root 'companion/host/New-ConstructCompanionPackage.ps1') $publish (Join-Path $dir 'state-package') $sha } 'State excluded'
    Write-Host "PASS: $script:count Companion package assertions"
} finally { if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force } }
