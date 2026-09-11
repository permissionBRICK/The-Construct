#Requires -Version 5.1
$ErrorActionPreference='Stop'
. (Join-Path (Split-Path $PSScriptRoot -Parent) 'lib/Construct.Companion.ps1')
$script:count=0
function Assert($Condition,[string]$Message) { if (-not $Condition) { throw $Message }; $script:count++ }
function Reject([scriptblock]$Action,[string]$Message) { $failed=$false; try { & $Action | Out-Null } catch { $failed=$true }; Assert $failed $Message }
$dir=Join-Path ([IO.Path]::GetTempPath()) ('companion-test-'+[guid]::NewGuid().ToString('N'))
$repo=Join-Path $dir 'repo with spaces'
$local=Join-Path $dir 'user'
$sha='a'*40
$script:nativeCalls=New-Object Collections.ArrayList
$script:registry=@{}; $script:starts=New-Object Collections.ArrayList; $script:quits=0; $script:sleeps=0
$script:sdks=@('10.0.100 [/sdk]'); $script:alive=$false; $script:quitWorks=$true; $script:failStart=$false; $script:failMove=$false; $script:failRegistry=$false
$seams=New-ConstructCompanionSeams
$seams.CheckUser={}
$seams.Native={ param($Exe,[string[]]$Arguments)
    $null=$script:nativeCalls.Add(@{exe=$Exe; argv=$Arguments})
    if ($Exe -eq 'git') { return @{exitCode=0;output=@($sha)} }
    if ($Arguments[0] -eq '--list-sdks') { return @{exitCode=0;output=$script:sdks} }
    if ($Arguments[0] -eq 'publish') {
        $out=$Arguments[-1]; [IO.Directory]::CreateDirectory((Join-Path $out 'media')) | Out-Null
        [IO.File]::WriteAllText((Join-Path $out 'ConstructCompanion.exe'),'new app')
        [IO.File]::WriteAllText((Join-Path $out 'media/panel.html'),'panel')
        return @{exitCode=0;output=@()}
    }
    throw 'Unexpected native invocation'
}
$seams.Json={ param($Uri)
    if ($Uri -match '/releases\?') { return @(@{tag_name=('companion-'+$sha);published_at='2026-09-11T00:00:00Z';draft=$false;prerelease=$false},@{tag_name=('host-'+$sha);published_at='2026-09-12T00:00:00Z'}) }
    return $script:manifest
}
$seams.Registry={ param($Operation,$Path,$Name,$Value)
    $key=$Path+'|'+$Name
    switch ($Operation) {
        'read' { if ($script:registry.ContainsKey($key)) { return @{exists=$true;value=$script:registry[$key];kind='String'} }; return @{exists=$false} }
        'write' {
            if ($script:failRegistry) { $script:failRegistry=$false; throw 'fake registry failure' }
            if ($Value -is [hashtable]) { $script:registry[$key]=$Value.value } else { $script:registry[$key]=$Value }
        }
        'remove' { $script:registry.Remove($key) }
        'removeTree' { foreach ($k in @($script:registry.Keys)) { if ($k.StartsWith($Path+'|') -or $k.StartsWith($Path+'\')) { $script:registry.Remove($k) } } }
    }
}
$seams.Start={ param($Exe,[string[]]$Arguments) if ($script:failStart) { throw 'fake start failure' }; $null=$script:starts.Add(@{exe=$Exe;argv=$Arguments}) }
$seams.Alive={ param($ProcessId) $script:alive }
$seams.Quit={ param($Endpoint,$Reason) $script:quits++; $script:lastReason=$Reason; if ($script:quitWorks) { $script:alive=$false } }
$seams.Sleep={ param($Milliseconds) $script:sleeps++ }
$seams.Move={ param($From,$To) if ($script:failMove -and $From -match 'companion-stage-') { throw 'fake move failure' }; [IO.Directory]::Move($From,$To) }
try {
    [IO.Directory]::CreateDirectory((Join-Path $repo 'companion')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $repo 'companion/Construct.Companion.sln'),'fixture')
    [IO.File]::WriteAllText((Join-Path $repo '.git'),'fake worktree pointer')
    $script:manifest=[pscustomobject]@{schemaVersion=1;ipcApiVersion=1;commit=$sha;repository='permissionBRICK/The-Construct';releaseTag=('companion-'+$sha);ref='refs/heads/main';exe='app\ConstructCompanion.exe';payloadAsset=('construct-companion-'+$sha.Substring(0,7)+'-win-x64.zip');payloadSha256=('b'*64);sumsSha256=('c'*64);packageVersion='2026.09.11+aaaaaaa';builtAt='2026-09-11T00:00:00Z'}
    Assert ((Resolve-ConstructCompanionSource $repo auto $seams).source -eq 'local-build') 'SDK 10 + solution chooses local'
    $script:sdks=@('9.0.1 [/sdk]','11.0.1 [/sdk]')
    Assert ((Resolve-ConstructCompanionSource $repo auto $seams).source -eq 'release') 'Other SDKs choose release'
    Reject { Resolve-ConstructCompanionSource $repo local $seams } 'Explicit local requires SDK 10'
    $script:sdks=@('10.0.100 [/sdk]')
    Assert ((Resolve-ConstructCompanionSource $repo release $seams).source -eq 'release') 'Explicit release bypasses SDK'
    Assert ((Resolve-ConstructCompanionSource $dir auto $seams).source -eq 'release') 'No solution chooses release'
    foreach ($property in @('schemaVersion','ipcApiVersion','commit','repository','releaseTag','ref','exe','payloadAsset','payloadSha256','sumsSha256','packageVersion','builtAt')) {
        $old=$script:manifest.$property; $script:manifest.$property='invalid'
        Reject { Assert-ConstructCompanionManifest $script:manifest 'permissionBRICK/The-Construct' ('companion-'+$sha) } "Reject bad $property"
        $script:manifest.$property=$old
    }
    Assert ((Install-ConstructCompanion $repo -SkipCompanion -LocalAppData $local -Seams $seams) -eq 'skipped') 'Skip flag'
    $settings=Join-Path $repo '.construct-settings.json'
    [IO.File]::WriteAllText($settings,'{"companion":false}')
    Assert ((Install-ConstructCompanion $repo -LocalAppData $local -Seams $seams) -eq 'skipped') 'Persistent opt-out'
    [IO.File]::WriteAllText($settings,'{}')
    Assert ((Install-ConstructCompanion $repo -LocalAppData $local -Seams $seams) -eq 'installed') 'Fresh install'
    $paths=Get-ConstructCompanionPaths $local
    $record=Read-ConstructCompanionJson (Join-Path $paths.install 'install.json')
    Assert ($record.commit -eq $sha -and $record.source -eq 'local-build' -and $record.ipcApiVersion -eq 1 -and $null -eq $record.releaseTag) 'Install record'
    Assert ($script:registry.Count -eq 6) 'Six registry values'
    Assert ($script:registry['HKCU:\Software\Microsoft\Windows\CurrentVersion\Run|ConstructCompanion'] -eq ('"'+(Join-Path $paths.install 'ConstructCompanion.exe')+'" --background')) 'Run key quoting'
    Assert ($script:registry['HKCU:\Software\Classes\construct\shell\open\command|'] -eq ('"'+(Join-Path $paths.install 'ConstructCompanion.exe')+'" --uri "%1"')) 'Protocol quoting'
    Assert ($script:registry['HKCU:\Software\Classes\AppUserModelId\PermissionBrick.TheConstruct|DisplayName'] -eq 'Construct Companion') 'AUMID'
    Assert ($script:starts.Count -eq 1 -and ($script:starts[0].argv -join '|') -eq '--background') 'Detached argv'
    $publish=@($script:nativeCalls | Where-Object { $_.argv[0] -eq 'publish' })[0]
    $expected=@('publish',(Join-Path $repo 'companion/src/Construct.Companion/Construct.Companion.csproj'),'-c','Release','-r','win-x64','--self-contained','true',('-p:InformationalVersion=1.0.0+'+$sha),'-p:IncludeSourceRevisionInInformationalVersion=false','-o',$publish.argv[-1])
    Assert (($publish.argv -join '|') -eq ($expected -join '|')) 'Exact publish argv, including spaced path'
    $before=$script:nativeCalls.Count
    Assert ((Install-ConstructCompanion $repo -LocalAppData $local -Seams $seams) -eq 'unchanged') 'Identical commit is no-op'
    Assert ($script:starts.Count -eq 1 -and $script:nativeCalls.Count -eq $before+2) 'No publish/start on no-op'
    Assert ((Install-ConstructCompanion $repo -Force -LocalAppData $local -Seams $seams) -eq 'installed') 'Force reinstalls'
    Assert (-not (Test-Path ($paths.install+'.previous'))) 'Previous removed after success'
    $endpoint=Join-Path $paths.state 'endpoint.json'
    [IO.File]::WriteAllText($endpoint,(@{v=1;ipcApiVersion=1;port=12345;pid=23456;token=('d'*64)} | ConvertTo-Json))
    $script:alive=$true
    Stop-ConstructCompanionForInstall $paths.state $seams
    Assert ($script:quits -eq 1 -and -not $script:alive -and $script:lastReason -eq 'update') 'Quit handshake'
    $script:alive=$true; $script:quitWorks=$false
    Reject { Install-ConstructCompanion $repo -Force -LocalAppData $local -Seams $seams } 'Quit timeout aborts installation'
    Assert ($script:sleeps -eq 60 -and -not (Test-Path ($paths.install+'.previous'))) 'Timeout bounded, no swap'
    $script:alive=$false; $script:quitWorks=$true
    $before=$script:quits; Stop-ConstructCompanionForInstall $paths.state $seams
    Assert ($script:quits -eq $before) 'Stale dead PID skips HTTP'
    [IO.File]::WriteAllText($endpoint,'{"v":1,"port":0,"token":"do-not-print"}')
    Reject { Stop-ConstructCompanionForInstall $paths.state $seams } 'Malformed endpoint rejected'
    Remove-Item -LiteralPath $endpoint
    $exe=Join-Path $paths.install 'ConstructCompanion.exe'
    [IO.File]::WriteAllText($exe,'original app')
    $registryBefore=$script:registry | ConvertTo-Json -Compress
    $script:failStart=$true
    Reject { Install-ConstructCompanion $repo -Force -LocalAppData $local -Seams $seams } 'Start failure triggers rollback'
    Assert ([IO.File]::ReadAllText($exe) -eq 'original app') 'Restores old files'
    Assert (($script:registry | ConvertTo-Json -Compress) -eq $registryBefore) 'Restores registry'
    Assert (-not (Test-Path ($paths.install+'.previous'))) 'Rollback consumes previous'
    $script:failStart=$false; $script:failMove=$true
    Reject { Install-ConstructCompanion $repo -Force -LocalAppData $local -Seams $seams } 'Move failure triggers rollback'
    Assert ([IO.File]::ReadAllText($exe) -eq 'original app') 'Move failure restores files'
    $script:failMove=$false; $script:failRegistry=$true
    Reject { Install-ConstructCompanion $repo -Force -LocalAppData $local -Seams $seams } 'Registry failure triggers rollback'
    Assert ([IO.File]::ReadAllText($exe) -eq 'original app') 'Registry failure restores files'
    [IO.File]::WriteAllText((Join-Path $paths.state 'settings.json'),'{"autostart":false}')
    Install-ConstructCompanion $repo -Force -LocalAppData $local -Seams $seams | Out-Null
    Assert (-not $script:registry.ContainsKey('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run|ConstructCompanion')) 'Autostart opt-out retained'
    $held=[IO.File]::Open((Join-Path $paths.state 'install.lock'),'OpenOrCreate','ReadWrite','None')
    try { Reject { Install-ConstructCompanion $repo -Force -LocalAppData $local -Seams $seams } 'Concurrent install refused' } finally { $held.Dispose() }
    Uninstall-ConstructCompanion -LocalAppData $local -Seams $seams
    Assert (-not (Test-Path $paths.install) -and $script:registry.Count -eq 0) 'Uninstall removes app and registrations'
    Assert (Test-Path (Join-Path $paths.state 'settings.json')) 'Uninstall keeps state'
    Uninstall-ConstructCompanion -LocalAppData $local -Seams $seams
    Assert (-not (Test-Path $paths.install)) 'Uninstall idempotent'
    Assert (@(Get-ChildItem -LiteralPath (Split-Path $paths.install -Parent) -Filter 'companion-stage-*').Count -eq 0) 'Staging cleaned on every outcome'
    # Parse all changed PS scripts and reject PS7-only AST nodes/operators.
    foreach ($relative in @('lib/Construct.Companion.ps1','companion/host/New-ConstructCompanionPackage.ps1','Auto-Install.ps1','Update-Construct.ps1')) {
        $errors=$null; $tokens=$null
        $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path (Split-Path $PSScriptRoot -Parent) $relative),[ref]$tokens,[ref]$errors)
        Assert (-not $errors) "Parse $relative"
        Assert (@($tokens | Where-Object { $_.Kind.ToString() -in @('QuestionQuestion','QuestionQuestionEquals','QuestionDot') }).Count -eq 0 -and $null -eq $ast.Find({param($n) $n.GetType().Name -eq 'TernaryExpressionAst'},$true)) "PS 5.1 syntax $relative"
    }
    # Hook stays non-fatal and passes the opt-out even if installation fails.
    & {
        function Install-ConstructCompanion { param($ScriptsDir,[switch]$SkipCompanion) $script:hookSkip=[bool]$SkipCompanion; Throw-ConstructCompanionError 'A previous Companion installation needs recovery; see docs/companion.md.' }
        $warnings=@()
        Invoke-ConstructCompanionInstallHook -ScriptsDir $repo -SkipCompanion -WarningVariable warnings -WarningAction SilentlyContinue
        Assert ($script:hookSkip -and $warnings.Count -eq 1 -and $warnings[0].Message.Contains('A previous Companion installation needs recovery; see docs/companion.md.')) 'Hook forwards skip and preserves the safe failure reason'
        function Install-ConstructCompanion { param($ScriptsDir,[switch]$SkipCompanion) throw 'dependency-credential-sentinel' }
        $warnings=@()
        Invoke-ConstructCompanionInstallHook -ScriptsDir $repo -WarningVariable warnings -WarningAction SilentlyContinue
        Assert ($warnings.Count -eq 1 -and -not $warnings[0].Message.Contains('dependency-credential-sentinel')) 'Hook does not disclose arbitrary dependency errors'
    }
    # Release selection excludes drafts/prereleases, sorts publish time, and paginates.
    $originalJson=$seams.Json
    $script:releaseCalls=0
    $seams.Json={ param($Uri)
        if ($Uri -match '/releases\?') {
            $script:releaseCalls++
            if ($Uri -match 'page=1$') { return @(1..100 | ForEach-Object { @{tag_name=('host-'+$sha);published_at='2026-09-12T00:00:00Z'} }) }
            return @(
                @{tag_name=('companion-'+('b'*40));published_at='2026-09-12T00:00:00Z';draft=$true},
                @{tag_name=('companion-'+('c'*40));published_at='2026-09-13T00:00:00Z';prerelease=$true},
                @{tag_name=('companion-'+('e'*40));published_at='2026-09-01T00:00:00Z'},
                @{tag_name=('companion-'+$sha);published_at='2026-09-11T00:00:00Z'})
        }
        Assert ($Uri -match ('companion-'+$sha+'/manifest.json$')) 'Newest eligible tag selected'
        return $script:manifest
    }
    Assert ((Resolve-ConstructCompanionSource $repo release $seams).commit -eq $sha -and $script:releaseCalls -eq 2) 'Pagination finds Companion after 100 host releases'
    $seams.Json={ param($Uri) @() }
    Reject { Resolve-ConstructCompanionSource $repo release $seams } 'Missing release fails clearly'
    $script:releaseCalls=0
    $seams.Json={param($Uri) $script:releaseCalls++; return @(1..100 | ForEach-Object { @{tag_name=('host-'+$sha)} }) }
    $capMessage=''
    try { Resolve-ConstructCompanionSource $repo release $seams | Out-Null } catch { $capMessage=$_.Exception.Message }
    Assert ($script:releaseCalls -eq 20 -and $capMessage.Contains('20-page limit')) 'Release pagination fails clearly at the cap'
    $seams.Json=$originalJson
    [IO.File]::WriteAllText($settings,'{"constructRepo":"https://invalid/repo"}')
    Reject { Resolve-ConstructCompanionSource $repo release $seams } 'Invalid repository refused'
    [IO.File]::WriteAllText($settings,'{}')
    Remove-Item -LiteralPath (Join-Path $repo '.git') -Force
    $originalNative=$seams.Native
    $seams.Native={param($Exe,$Arguments) if ($Exe -eq 'git') { return @{exitCode=127;output=@()} }; return @{exitCode=0;output=@('10.0.100 [/sdk]')} }
    Reject { Resolve-ConstructCompanionSource $repo local $seams } 'Archive local build needs commit marker'
    [IO.File]::WriteAllText($settings,(@{installedCommit=$sha} | ConvertTo-Json))
    Assert ((Resolve-ConstructCompanionSource $repo local $seams).commit -eq $sha) 'Archive local build uses installed marker'
    $seams.Native=$originalNative
    # Build failures retain actionable native status without printing arbitrary output.
    $seams.Native={param($Exe,$Arguments)
        if ($Arguments[0] -eq '--list-sdks') { return @{exitCode=0;output=@('10.0.100 [/sdk]')} }
        return @{exitCode=1;output=@('error NU1301: https://user:credential-sentinel@feed.invalid/index.json','error NETSDK1045: credential-sentinel','error NU1301: again')}
    }
    $buildMessage=''
    try { Install-ConstructCompanion $repo -LocalAppData $local -Seams $seams | Out-Null } catch { $buildMessage=$_.Exception.Message }
    Assert ($buildMessage.Contains('dotnet exit 1') -and $buildMessage.Contains('NETSDK1045, NU1301')) 'Native build error codes and exit status preserved'
    Assert (-not $buildMessage.Contains('credential-sentinel') -and -not (Test-Path (Get-ConstructCompanionPaths $local).install)) 'Build output secrets suppressed and no installation created'
    $seams.Native=$originalNative
    [IO.File]::WriteAllText((Join-Path $repo '.git'),'fake worktree pointer')
    $nativeWarning=[Management.Automation.ErrorRecord]::new([Exception]::new('warning: harmless Git advice'),'fixture',[Management.Automation.ErrorCategory]::NotSpecified,$null)
    $seams.Native={param($Exe,$Arguments)
        if ($Exe -eq 'git') { return @{exitCode=0;output=@($sha,$nativeWarning)} }
        return @{exitCode=0;output=@('10.0.100 [/sdk]')}
    }
    Assert ((Resolve-ConstructCompanionSource $repo local $seams).commit -eq $sha) 'Successful Git stderr does not corrupt commit parsing'
    $sdkWarning=[Management.Automation.ErrorRecord]::new([Exception]::new('10.0 is mentioned in a warning'),'fixture',[Management.Automation.ErrorCategory]::NotSpecified,$null)
    $seams.Native={param($Exe,$Arguments) return @{exitCode=0;output=@($sdkWarning)} }
    Reject { Resolve-ConstructCompanionSource $repo local $seams } 'SDK stderr is not SDK discovery data'
    $seams.Native=$originalNative
    Write-Host "PASS: $script:count Companion installer assertions"
} finally { if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force } }
