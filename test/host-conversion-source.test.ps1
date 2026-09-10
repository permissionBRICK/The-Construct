#Requires -Version 5.1
# Pure metadata fixtures; no network or ISO downloads.
$ErrorActionPreference='Stop'
$t=$null; $e=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path (Split-Path -Parent $PSScriptRoot) 'service/host/ConvertTo-ConstructHost.ps1'),[ref]$t,[ref]$e)
if($e.Count){throw ($e|Out-String)}
$f=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-UbuntuSourceIso'},$true)
Invoke-Expression $f.Extent.Text
function Invoke-WebRequest($Uri, [switch]$UseBasicParsing, $TimeoutSec) {
 if($Uri -ne 'https://releases.ubuntu.com/24.04/SHA256SUMS'){throw 'Unexpected metadata URL.'}
 [pscustomobject]@{Content=$script:content}
}
$hash='a'*64
$lines=@(
 ($hash+' *ubuntu-24.04.9-live-server-amd64.iso'),
 ($hash+'  ubuntu-24.04.10-live-server-amd64.iso'),
 ($hash+' *ubuntu-24.04.11-desktop-amd64.iso'),
 ($hash+' *ubuntu-22.04.12-live-server-amd64.iso'),
 ($hash+' *ubuntu-24.04.99-live-server-arm64.iso'),
 ($hash+' *ubuntu-24.04.99-live-server-amd64.iso.torrent'),
 ($hash+' *../ubuntu-24.04.99-live-server-amd64.iso')
)
foreach($encoding in @('text','bytes','bom')) {
 $script:content=$lines -join "`r`n"
 if($encoding -eq 'bytes'){$script:content=[Text.Encoding]::UTF8.GetBytes($script:content)}
 if($encoding -eq 'bom'){$script:content=[Text.Encoding]::UTF8.GetBytes([char]0xFEFF+$script:content)}
 $iso=Get-UbuntuSourceIso '24.04'
 if($iso.Name -ne 'ubuntu-24.04.10-live-server-amd64.iso' -or $iso.Sha256 -ne $hash -or $iso.Url -ne ('https://releases.ubuntu.com/24.04/'+$iso.Name)){throw "Wrong ISO for $encoding body."}
}
$script:content=($hash.ToUpperInvariant()+' *ubuntu-24.04-live-server-amd64.iso')
if((Get-UbuntuSourceIso '24.04').Sha256 -cne $hash){throw 'Base release or uppercase checksum failed.'}
foreach($bad in @('', '<html>Proxy login</html>', ('z'*64+' *ubuntu-24.04.5-live-server-amd64.iso'),
 (($hash+' *ubuntu-24.04.5-live-server-amd64.iso')+"`n"+('b'*64+' *ubuntu-24.04.5-live-server-amd64.iso')))) {
 $script:content=$bad
 $refused=$false
 try {Get-UbuntuSourceIso '24.04' | Out-Null}catch{$refused=$true}
 if(-not $refused){throw 'Invalid or conflicting checksum metadata was accepted.'}
}
'Ubuntu source metadata checks passed: text/byte/BOM bodies, version ordering, exact release/architecture, and invalid/conflicting checksums.'
