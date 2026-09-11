# Pure installer branches evaluated with an ACL recorder. Never runs the installer entry point.
$ErrorActionPreference='Stop';$script:passed=0
function Assert($Condition,[string]$Message){if(-not $Condition){throw $Message};$script:passed++}
$installer=Join-Path $PSScriptRoot '../host/Install-ConstructHost.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($installer,[ref]$tokens,[ref]$errors)
Assert ($errors.Count -eq 0) 'installer parse errors'
foreach($name in @('Sort-ConstructHardeningOrder','Get-ConstructHostPublishSource','Initialize-ConstructHostInstallRecord')) {
 $fn=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
 . ([ScriptBlock]::Create($fn.Extent.Text))
}
$root=Join-Path ([IO.Path]::GetTempPath()) ('host-install-update-test-'+[Guid]::NewGuid().ToString('n'))
[IO.Directory]::CreateDirectory((Join-Path $root 'config'))|Out-Null
try {
 $ScriptsDir=Join-Path $root 'scripts';$PublishDir=Join-Path $ScriptsDir 'service/publish';$DataDir=Join-Path $root 'data/service'
 [IO.Directory]::CreateDirectory($PublishDir)|Out-Null
 $settingsPath=Join-Path $PublishDir 'appsettings.Production.json'
 [IO.File]::WriteAllText($settingsPath,'{"Constructd":{"Iso":{"CacheDir":"custom-iso"},"HostAdmin":{"Media":{"RootDir":"custom-media"}}}}')
 $before=[IO.File]::ReadAllBytes($settingsPath);$script:paths=@()
 function Set-ConstructPathAcl([string]$Path,[string]$Kind,[string]$Name){$script:paths+=@{path=$Path;kind=$Kind}}
 $branch=$ast.Find({param($n) $n -is [Management.Automation.Language.IfStatementAst] -and $n.Clauses[0].Item1.Extent.Text -eq '$AclOnly'},$true)
 Assert ($null -ne $branch) 'AclOnly branch missing'
 $body=$branch.Clauses[0].Item2.Extent.Text
 & ([ScriptBlock]::Create($body.Substring(1,$body.Length-2)))
 Assert ($script:paths.Count -eq 6) 'AclOnly did not cover code/data/custom roots'
 Assert (($script:paths|Where-Object {$_.path -eq $PublishDir}).kind -eq 'Code') 'publish ACL kind'
 Assert (($script:paths|Where-Object {$_.path -eq 'custom-media'}).kind -eq 'Data') 'media ACL kind'
 Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($settingsPath)) -eq [Convert]::ToBase64String($before)) 'AclOnly rewrote settings'
 Assert ($body -notmatch 'Start-Service|Stop-Service|New-Service|WriteAllText|powercfg|sc.exe') 'AclOnly contains unrelated mutations'
 # Pre-published installer and download selector use the same runtime requirements.
 . (Join-Path $PSScriptRoot '../../lib/Construct.Runtime.ps1')
 $runtimeConfig=Join-Path $PublishDir 'Constructd.Api.runtimeconfig.json'
 [IO.File]::WriteAllText($runtimeConfig,'{"runtimeOptions":{"frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}')
 $script:runtimeExit=0; $script:runtimeLines=@('Microsoft.NETCore.App 10.0.1 [/shared]','Microsoft.AspNetCore.App 10.0.1 [/shared]')
 $native={param($Exe,$Arguments) Assert ($Exe -eq 'dotnet' -and ($Arguments -join '|') -eq '--list-runtimes') 'exact runtime argv'; @{exitCode=$script:runtimeExit;output=$script:runtimeLines}}
 Assert ((Get-ConstructHostPublishSource $PublishDir $native) -eq 'framework-dependent') 'FDD publish with all runtimes'
 $manifest=@{payloadAsset='sc.zip';payloadSha256=('a'*64);payloadSizeBytes=100;frameworkDependentAsset='fdd.zip';frameworkDependentSha256=('b'*64);frameworkDependentSizeBytes=10;runtimes=@(Get-ConstructRequiredRuntimes $runtimeConfig)}
 Assert ((Select-ConstructReleasePayload $manifest $native).source -eq 'framework-dependent') 'Download selector prefers FDD'
 foreach ($scenario in @('missing','partial','absent')) {
   $script:runtimeLines=@(); if ($scenario -eq 'partial') { $script:runtimeLines=@('Microsoft.NETCore.App 10.0.1 [/shared]') }; if ($scenario -eq 'absent') { $script:runtimeExit=127 }
   Assert ((Select-ConstructReleasePayload $manifest $native).source -eq 'self-contained') 'Download selector falls back'
   $rejected=$false; try { Get-ConstructHostPublishSource $PublishDir $native | Out-Null } catch { $rejected=$true }
   Assert $rejected 'Prepublished FDD without runtime rejected'
 }
 $manifest.Remove('frameworkDependentAsset')
 Assert ((Select-ConstructReleasePayload $manifest {throw 'legacy must not probe'}).source -eq 'self-contained') 'Legacy download unchanged'
 [IO.File]::WriteAllText($runtimeConfig,'{"runtimeOptions":{"includedFrameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"}]}}')
 Assert ((Get-ConstructHostPublishSource $PublishDir {throw 'SC must not probe'}) -eq 'self-contained') 'Self-contained publish needs no runtime'
 # Existing updater-owned JSON is preserved byte for byte, including ISO timestamps.
 $ledgerPath=Join-Path $PublishDir 'install.json'
 Initialize-ConstructHostInstallRecord $PublishDir 'framework-dependent'
 Assert ((Get-Content -Raw $ledgerPath | ConvertFrom-Json).source -eq 'framework-dependent') 'Fresh install records variant'
 $originalLedger='{"commit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "installedAt":"2026-09-01T00:00:00Z", "source":"self-contained", "files":[{"path":"service/Constructd.Api.exe","sha256":"abc"}], "extra":{"date":"2026-09-02T12:34:56.789Z"}}'
 [IO.File]::WriteAllText($ledgerPath,$originalLedger,(New-Object Text.UTF8Encoding($false)))
 $ledgerBytes=[IO.File]::ReadAllBytes($ledgerPath)
 Initialize-ConstructHostInstallRecord $PublishDir 'framework-dependent'
 Assert ([Convert]::ToBase64String([IO.File]::ReadAllBytes($ledgerPath)) -ceq [Convert]::ToBase64String($ledgerBytes)) 'Existing ledger and ISO date values survive verbatim'
 # Manifest metadata follows the registered migrations, including a future breaking migration.
 $packAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot '../host/New-ConstructHostPackage.ps1'),[ref]$tokens,[ref]$errors)
 Assert ($errors.Count -eq 0) 'packager parse errors'
 $fn=$packAst.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-ConstructHostMigrationMetadata'},$true)
 . ([ScriptBlock]::Create($fn.Extent.Text))
 $dir=Join-Path $root 'service/src/Constructd.Sqlite/Migrations';[IO.Directory]::CreateDirectory($dir)|Out-Null
 [IO.File]::WriteAllText((Join-Path $dir 'SqliteMigrations.cs'),'new M100_First(), new M600_Breaking()')
 [IO.File]::WriteAllText((Join-Path $dir 'M100_First.cs'),'public bool Breaking => false;')
 [IO.File]::WriteAllText((Join-Path $dir 'M600_Breaking.cs'),'public bool Breaking => true;')
 $metadata=Get-ConstructHostMigrationMetadata $root
 Assert ($metadata.schemaVersion -eq 600 -and $metadata.minReadableBy -eq 600 -and $metadata.breakingMigrations.Count -eq 1) 'breaking migration metadata omitted'
 Write-Host "host-release-installer: $script:passed assertions passed (ACL calls recorded)"
}finally{Remove-Item -LiteralPath $root -Recurse -Force}
