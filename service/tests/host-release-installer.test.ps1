# Pure installer branches evaluated with an ACL recorder. Never runs the installer entry point.
$ErrorActionPreference='Stop';$script:passed=0
function Assert($Condition,[string]$Message){if(-not $Condition){throw $Message};$script:passed++}
$installer=Join-Path $PSScriptRoot '../host/Install-ConstructHost.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($installer,[ref]$tokens,[ref]$errors)
Assert ($errors.Count -eq 0) 'installer parse errors'
foreach($name in @('Sort-ConstructHardeningOrder')) {
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
