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
    & (Join-Path $root 'companion/host/New-ConstructCompanionPackage.ps1') -PublishDir $publish -OutputDir $out -Commit $sha -BuiltAt '2026-09-11T12:00:00Z' -Repository 'example/fork'
    $manifest=Read-ConstructCompanionJson (Join-Path $out 'manifest.json')
    Assert-ConstructCompanionManifest $manifest 'example/fork' ('companion-'+$sha)
    Assert ($manifest.packageVersion -eq '2026.09.11+aaaaaaa') 'Package version'
    Assert (($manifest.PSObject.Properties.Name -join ',') -eq 'schemaVersion,commit,ref,packageVersion,builtAt,repository,releaseTag,payloadAsset,payloadSha256,sumsSha256,exe,ipcApiVersion') 'Exact manifest keys'
    $zip=Join-Path $out $manifest.payloadAsset
    Assert ((Get-FileHash $zip).Hash -eq $manifest.payloadSha256) 'Payload SHA'
    Assert ((Get-FileHash (Join-Path $out 'SHA256SUMS')).Hash -eq $manifest.sumsSha256) 'Detached sums SHA'
    Assert (-not (Test-Path (Join-Path $out 'payload'))) 'Packager staging removed'
    $archive=[IO.Compression.ZipFile]::OpenRead($zip)
    try {
        Assert ($archive.Entries.Count -eq 5) 'Only four app files plus sums'
        foreach ($entry in $archive.Entries) {
            Assert (-not $entry.FullName.Contains('\')) 'Portable ZIP names'
            Assert ($entry.CompressedLength -eq $entry.Length) 'Stored compression'
        }
    } finally { $archive.Dispose() }
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
    $seams=New-ConstructCompanionSeams; $seams.CheckUser={}; $seams.Start={param($Exe,$Arguments)}; $seams.Registry={param($Op,$Path,$Name,$Value) if ($Op -eq 'read') { return @{exists=$false} } }
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
    Assert ($record.source -eq 'release' -and $record.releaseTag -eq ('companion-'+$sha)) 'Release install metadata'
    Assert ($script:urls[0] -eq 'https://api.github.com/repos/example/fork/releases?per_page=100&page=1') 'Configured repository API'
    Assert ($script:payloadUrl -eq ('https://github.com/example/fork/releases/download/companion-'+$sha+'/'+$manifest.payloadAsset)) 'Pinned payload URL'
    Assert ((Install-ConstructCompanion $scripts -Source release -LocalAppData $local -Seams $seams) -eq 'unchanged' -and $script:downloads -eq 1) 'Release idempotence avoids payload download'
    Reject { & (Join-Path $root 'companion/host/New-ConstructCompanionPackage.ps1') $publish $out $sha } 'Existing output refused'
    [IO.File]::WriteAllText((Join-Path $publish 'endpoint.json'),'state')
    Reject { & (Join-Path $root 'companion/host/New-ConstructCompanionPackage.ps1') $publish (Join-Path $dir 'state-package') $sha } 'State excluded'
    Write-Host "PASS: $script:count Companion package assertions"
} finally { if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force } }
