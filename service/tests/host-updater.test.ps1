# Linux fakes; no SCM, scheduled task, certificate store or Hyper-V calls.
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot '../host/Update-ConstructHost.ps1') -LibraryOnly
$script:passed=0
function Assert($Condition,[string]$Message) { if(-not $Condition){throw $Message};$script:passed++ }
foreach($path in @('../bad','/bad','C:/bad','service/CON.txt','service/foo.','service/foo ','service/a:ads','scripts/a\b')) {
    Assert (-not (Test-UpdatePath $path)) ('unsafe path accepted: '+$path)
}
Assert (Test-UpdatePath 'scripts/service/host/Update-ConstructHost.ps1') 'normal path refused'
foreach($path in @('service/appsettings.Production.json','scripts/keys/key','scripts/projects/a','service/constructd.db-wal')) { Assert (Test-UpdatePreserved $path) 'preserved path accepted' }
$r=[pscustomobject]@{outcome='succeeded'}
foreach($disposition in @('closed','rollbackAuthorized','commitOnly')) { Assert ((Get-UpdateAuthority $r ([pscustomobject]@{updateId='u';disposition=$disposition}) 'u') -eq 'already-terminal') 'terminal must win' }
Assert ((Get-UpdateAuthority $null ([pscustomobject]@{updateId='u';disposition='closed'}) 'u') -eq 'superseded') 'closed must refuse'
Assert ((Get-UpdateAuthority $null ([pscustomobject]@{updateId='other';disposition='closed'}) 'u') -eq 'apply') 'fence must be scoped'
Assert ((Get-UpdateRollbackMode ([pscustomobject]@{database=@{minReadableBy=0;breakingMigrations=@()}}) 100) -eq 'binary') 'additive rollback'
Assert ((Get-UpdateRollbackMode ([pscustomobject]@{database=@{minReadableBy=600;breakingMigrations=@(600)}}) 100) -eq 'database') 'breaking rollback'
$script:stops=0;$script:starts=0;$script:healthCalls=0;$script:failNew=$false;$script:failAll=$false
function Stop-UpdateService([string]$Name) {$script:stops++}
function Start-UpdateService([string]$Name) {$script:starts++}
function Test-UpdateHealth($H,[string]$Commit,[int]$Schema,[int]$TimeoutSeconds) {
    $script:healthCalls++
    return (-not $script:failAll -and (-not $script:failNew -or $Commit -eq $H.previousCommit))
}
function schtasks.exe { }
$root=Join-Path ([IO.Path]::GetTempPath()) ('updater-test-'+[Guid]::NewGuid().ToString('n'))
[IO.Directory]::CreateDirectory($root)|Out-Null
function New-Fixture([string]$Name) {
    $base=Join-Path $root $Name;$scripts=Join-Path $base 'scripts';$publish=Join-Path $scripts 'service/publish';$data=Join-Path $base 'data';$id=[Guid]::NewGuid().ToString('n')
    $stage=Join-Path (Join-Path $data 'updates') $id;$extracted=Join-Path $stage 'extracted'
    foreach($dir in @($publish,$data,$extracted,(Join-Path $scripts 'keys'))) {[IO.Directory]::CreateDirectory($dir)|Out-Null}
    [IO.File]::WriteAllText((Join-Path $publish 'Constructd.Api.exe'),'old')
    [IO.File]::WriteAllText((Join-Path $publish 'removed.dll'),'old-owned')
    [IO.File]::WriteAllText((Join-Path $publish 'unowned.txt'),'unowned')
    [IO.File]::WriteAllText((Join-Path $scripts 'keys/key'),'preserved-key')
    [IO.File]::WriteAllText((Join-Path $data 'constructd.db'),'preserved-db-tokens-users')
    Write-UpdateJson (Join-Path $publish 'appsettings.Production.json') @{Constructd=@{DatabasePath=(Join-Path $data 'constructd.db');Iso=@{CacheDir=(Join-Path $data 'iso')};HostAdmin=@{Media=@{RootDir=(Join-Path $data 'media')}}}}
    $oldFiles=@('service/Constructd.Api.exe','service/removed.dll') | ForEach-Object {
        @{path=$_;sha256=(Get-FileHash (Join-Path $publish $_.Substring(8)) -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    Write-UpdateJson (Join-Path $publish 'install.json') @{commit=('b'*40);files=@($oldFiles)}
    $files=@()
    foreach($pair in @(@('service/Constructd.Api.exe','new'),@('service/new.dll','added'),@('scripts/service/host/Install-ConstructHost.ps1','param($ScriptsDir,$PublishDir,$DataDir,[switch]$AclOnly)'),@('updater/Update-ConstructHost.ps1','verified-updater'))) {
        $path=Join-Path $extracted $pair[0];[IO.Directory]::CreateDirectory((Split-Path $path -Parent))|Out-Null;[IO.File]::WriteAllText($path,$pair[1])
        $files+=@{path=$pair[0];sha256=(Get-FileHash $path -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    $sums=Join-Path $extracted 'SHA256SUMS';[IO.File]::WriteAllText($sums,(($files|ForEach-Object {$_.sha256+'  '+$_.path}) -join "`n")+"`n")
    [IO.File]::WriteAllText((Join-Path $stage 'package.zip'),'verified-package-fixture')
    Write-UpdateJson (Join-Path $stage 'manifest.json') @{schemaVersion=1;commit=('a'*40);ref='refs/heads/main';releaseTag=('host-'+('a'*40));packageVersion='test';payloadSha256=(Get-FileHash (Join-Path $stage 'package.zip') -Algorithm SHA256).Hash.ToLowerInvariant();sumsSha256=(Get-FileHash $sums -Algorithm SHA256).Hash.ToLowerInvariant();updaterPath='updater/Update-ConstructHost.ps1';updaterSha256=$files[3].sha256;database=@{schemaVersion=600;minReadableBy=0;breakingMigrations=@()}}
    Write-UpdateJson (Join-Path $stage 'verified.json') @{updateId=$id;commit=('a'*40);files=$files}
    $h=@{updateId=$id;commit=('a'*40);previousCommit=('b'*40);stagedPath=$stage;publishDir=$publish;scriptsDir=$scripts;dataDir=$data;serviceName='test-only';previousSchemaVersion=100;healthTimeoutSeconds=30}
    $hp=Join-Path (Join-Path $data 'updates') 'handoff.json';Write-UpdateJson $hp $h
    return @{h=$h;path=$hp;record=(Join-Path (Join-Path $data 'updates') 'last-update.json')}
}
try {
    $single=Join-Path $root 'single.json';Write-UpdateJson $single @(@{path='service/one.dll';sha256=('a'*64)})
    Assert ([IO.File]::ReadAllText($single).TrimStart().StartsWith('[')) 'single-file ledger lost array shape'
    $f=New-Fixture 'success';$settingsBefore=[IO.File]::ReadAllText((Join-Path $f.h.publishDir 'appsettings.Production.json'))
    Assert ((Invoke-ConstructHostUpdate $f.path $false $false) -eq 0) 'apply failed'
    $record=Read-UpdateJson $f.record
    Assert ($record.outcome -eq 'succeeded' -and $record.backupComplete -and $record.replaceStarted) 'success not durable'
    Assert ([IO.File]::ReadAllText((Join-Path $f.h.publishDir 'Constructd.Api.exe')) -eq 'new') 'new binary missing'
    Assert (-not (Test-Path (Join-Path $f.h.publishDir 'removed.dll'))) 'stale owned file remained'
    Assert ([IO.File]::ReadAllText((Join-Path $f.h.publishDir 'unowned.txt')) -eq 'unowned') 'unowned file altered'
    Assert ([IO.File]::ReadAllText((Join-Path $f.h.publishDir 'appsettings.Production.json')) -eq $settingsBefore) 'settings changed'
    Assert ([IO.File]::ReadAllText((Join-Path $f.h.scriptsDir 'keys/key')) -eq 'preserved-key') 'keys changed'
    Assert ([IO.File]::ReadAllText((Join-Path $f.h.dataDir 'constructd.db')) -eq 'preserved-db-tokens-users') 'database changed'
    $before=$script:stops;Assert ((Invoke-ConstructHostUpdate $f.path $true $true) -eq 0) 'terminal resume failed';Assert ($script:stops -eq $before) 'terminal resume stopped service'
    $previousRecord=$record
    $f=New-Fixture 'next-update';Write-UpdateJson $f.record $previousRecord
    Assert ((Invoke-ConstructHostUpdate $f.path $false $false) -eq 0) 'prior terminal record blocked next update'
    Assert ((Read-UpdateJson $f.record).updateId -eq $f.h.updateId) 'next update did not replace stale record'
    $f=New-Fixture 'backup-failure'
    $ledger=Read-UpdateJson (Join-Path $f.h.publishDir 'install.json');$ledger.files+=@([pscustomobject]@{path='service/missing.dll';sha256=('0'*64)});Write-UpdateJson (Join-Path $f.h.publishDir 'install.json') $ledger
    $startsBefore=$script:starts
    Assert ((Invoke-ConstructHostUpdate $f.path $false $false) -eq 1) 'backup failure reported success'
    Assert ((Read-UpdateJson $f.record).outcome -eq 'applyFailed') 'pre-replace failure unnecessarily froze recovery'
    Assert ($script:starts -eq ($startsBefore+1)) 'old service not restarted after failed backup'
    Assert ([IO.File]::ReadAllText((Join-Path $f.h.publishDir 'Constructd.Api.exe')) -eq 'old') 'pre-replace failure changed binary'
    $f=New-Fixture 'rollback';$script:failNew=$true
    Assert ((Invoke-ConstructHostUpdate $f.path $false $false) -eq 0) 'rollback failed'
    Assert ((Read-UpdateJson $f.record).outcome -eq 'rolledBack') 'rollback outcome missing'
    Assert ([IO.File]::ReadAllText((Join-Path $f.h.publishDir 'Constructd.Api.exe')) -eq 'old') 'old binary not restored'
    Assert (-not (Test-Path (Join-Path $f.h.publishDir 'new.dll'))) 'added DLL not removed'
    Assert (Test-Path (Join-Path $f.h.publishDir 'removed.dll')) 'old owned DLL missing'
    $f=New-Fixture 'recovery-failure';$script:failAll=$true
    Assert ((Invoke-ConstructHostUpdate $f.path $false $false) -eq 1) 'bad rollback reported success'
    Assert ((Read-UpdateJson $f.record).outcome -eq 'recoveryFailed') 'recovery record missing'
    Assert ((Read-UpdateJson $f.record).manualSteps.Count -gt 0) 'manual recovery instructions missing'
    # Exercise the actual authorized recovery entry, not just its authority selector.
    Write-UpdateJson (Join-Path (Split-Path $f.path -Parent) 'fence.json') @{updateId=$f.h.updateId;disposition='rollbackAuthorized'}
    $script:failAll=$false
    Assert ((Invoke-ConstructHostUpdate $f.path $true $true) -eq 0) 'authorized rollback resume failed'
    Assert ((Read-UpdateJson $f.record).outcome -eq 'rolledBack') 'authorized rollback did not reach durable terminal state'
    Assert ([IO.File]::ReadAllText((Join-Path $f.h.publishDir 'Constructd.Api.exe')) -eq 'old') 'authorized rollback did not retain old binary'
    $f=New-Fixture 'commit-only';$script:failNew=$false
    Assert ((Invoke-ConstructHostUpdate $f.path $false $false) -eq 0) 'commit-only fixture apply failed'
    $r=Read-UpdateJson $f.record;$r.outcome=$null;Write-UpdateJson $f.record $r
    Write-UpdateJson (Join-Path (Split-Path $f.path -Parent) 'fence.json') @{updateId=$f.h.updateId;disposition='commitOnly'}
    $before=$script:stops
    Assert ((Invoke-ConstructHostUpdate $f.path $true $false) -eq 0) 'commit-only resume failed'
    Assert ($script:stops -eq $before) 'commit-only recovery stopped service'
    Assert ((Read-UpdateJson $f.record).outcome -eq 'succeeded') 'commit-only recovery did not commit'
    $f=New-Fixture 'bad-manifest';[IO.File]::WriteAllText((Join-Path $f.h.stagedPath 'package.zip'),'tampered');$before=$script:stops
    Assert ((Invoke-ConstructHostUpdate $f.path $false $false) -eq 1) 'bad manifest accepted'
    Assert ($script:stops -eq $before) 'bad manifest stopped service'
    $f=New-Fixture 'incomplete-backup';$r=@{updateId=$f.h.updateId;commit=$f.h.commit;previousCommit=$f.h.previousCommit;phase='replace';phaseAt=[DateTimeOffset]::UtcNow.ToString('o');outcome=$null;error=$null;backupPath=(Join-Path (Split-Path $f.path -Parent) ('backup-'+$f.h.updateId));backupComplete=$false;replaceStarted=$true;stagedPath=$f.h.stagedPath;healthAttempts=0;manualSteps=@()};Write-UpdateJson $f.record $r
    Assert ((Invoke-ConstructHostUpdate $f.path $true $false) -eq 1) 'incomplete mixed backup rebuilt'
    Assert ((Read-UpdateJson $f.record).outcome -eq 'recoveryFailed') 'incomplete backup needs manual recovery'
    Write-Host "host-updater: $script:passed assertions passed (service control and health faked)"
} finally {Remove-Item -LiteralPath $root -Recurse -Force}
