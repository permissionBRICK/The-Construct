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
    if ($Arguments[0] -eq '--list-runtimes') { return @{exitCode=$script:runtimeExit;output=$script:runtimes} }
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
$script:runningPolls=0; $script:holders=@()
$seams.Running={ param($Directory) if ($script:runningPolls -gt 0) { $script:runningPolls--; return @(@{name='ConstructCompanion.exe';id=777}) }; @() }
$seams.Holders={ param($Directory) $script:holders }
$script:failMoveTimes=0
$seams.Move={ param($From,$To)
    if ($From -match 'companion-stage-') {
        if ($script:failMove) { throw 'fake move failure: being used by another process' }
        if ($script:failMoveTimes -gt 0) { $script:failMoveTimes--; throw 'fake transient move failure' }
    }
    [IO.Directory]::Move($From,$To)
}
try {
    [IO.Directory]::CreateDirectory((Join-Path $repo 'companion')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $repo 'companion/Construct.Companion.sln'),'fixture')
    [IO.File]::WriteAllText((Join-Path $repo '.git'),'fake worktree pointer')
    $script:manifest=[pscustomobject]@{schemaVersion=1;ipcApiVersion=1;commit=$sha;repository='permissionBRICK/The-Construct';releaseTag=('companion-'+$sha);ref='refs/heads/main';exe='app\ConstructCompanion.exe';payloadAsset=('construct-companion-'+$sha.Substring(0,7)+'-win-x64.zip');payloadSha256=('b'*64);sumsSha256=('c'*64);packageVersion='2026.09.11+aaaaaaa';builtAt='2026-09-11T00:00:00Z'}
    $script:runtimeExit=0; $script:runtimes=@()
    Assert ((Resolve-ConstructCompanionSource $repo auto $seams).source -eq 'self-contained') 'SDK never triggers automatic local build'
    Assert ($script:nativeCalls.Count -eq 0) 'Legacy manifest does not probe dotnet'
    $script:manifest | Add-Member -NotePropertyName frameworkDependentAsset -NotePropertyValue ('construct-companion-'+$sha.Substring(0,7)+'-win-x64-fdd.zip')
    $script:manifest | Add-Member -NotePropertyName frameworkDependentSha256 -NotePropertyValue ('d'*64)
    $script:manifest | Add-Member -NotePropertyName frameworkDependentSumsSha256 -NotePropertyValue ('e'*64)
    $script:manifest | Add-Member -NotePropertyName frameworkDependentSizeBytes -NotePropertyValue 123
    $script:manifest | Add-Member -NotePropertyName frameworkDependentUncompressedSizeBytes -NotePropertyValue 456
    $script:manifest | Add-Member -NotePropertyName runtimes -NotePropertyValue @(@{name='Microsoft.NETCore.App';majorVersion=10},@{name='Microsoft.WindowsDesktop.App';majorVersion=10},@{name='Microsoft.AspNetCore.App';majorVersion=10})
    Assert ((Resolve-ConstructCompanionSource $repo auto $seams).source -eq 'self-contained') 'Missing runtimes choose self-contained'
    $script:runtimes=@('Microsoft.NETCore.App 10.0.1 [/shared]','Microsoft.WindowsDesktop.App 10.0.1 [/shared]')
    Assert ((Resolve-ConstructCompanionSource $repo auto $seams).source -eq 'self-contained') 'Partial runtimes choose self-contained'
    $script:runtimes+=@('Microsoft.AspNetCore.App 11.0.1 [/shared]')
    Assert ((Resolve-ConstructCompanionSource $repo auto $seams).source -eq 'self-contained') 'Different major is insufficient'
    $script:runtimes+=@('Microsoft.AspNetCore.App 10.0.1 [/shared]')
    Assert ((Resolve-ConstructCompanionSource $repo auto $seams).source -eq 'framework-dependent') 'All shared frameworks select FDD'
    Assert (($script:nativeCalls[-1].argv -join '|') -eq '--list-runtimes' -and $script:nativeCalls[-1].exe -eq 'dotnet') 'Exact runtime detection argv'
    $script:runtimeExit=127
    Assert ((Resolve-ConstructCompanionSource $repo auto $seams).source -eq 'self-contained') 'Absent dotnet chooses self-contained'
    $script:runtimeExit=0
    Assert ((Resolve-ConstructCompanionSource $repo local $seams).source -eq 'local-build') 'Explicit local chooses SDK build'
    $script:sdks=@('9.0.1 [/sdk]','11.0.1 [/sdk]')
    Reject { Resolve-ConstructCompanionSource $repo local $seams } 'Explicit local requires SDK 10'
    $script:sdks=@('10.0.100 [/sdk]')
    Assert ((Resolve-ConstructCompanionSource $repo release $seams).source -eq 'framework-dependent') 'Explicit release also selects by runtimes'
    Assert ((Resolve-ConstructCompanionSource $dir auto $seams).source -eq 'framework-dependent') 'No solution needed for FDD'
    foreach ($field in @('frameworkDependentAsset','frameworkDependentSha256','frameworkDependentSumsSha256','frameworkDependentSizeBytes','frameworkDependentUncompressedSizeBytes','runtimes')) {
        $old=$script:manifest.$field; $script:manifest.$field=$null
        Reject { Assert-ConstructCompanionManifest $script:manifest 'permissionBRICK/The-Construct' ('companion-'+$sha) } "Reject incomplete FDD $field"
        $script:manifest.$field=$old
    }
    foreach ($property in @('schemaVersion','ipcApiVersion','commit','repository','releaseTag','ref','exe','payloadAsset','payloadSha256','sumsSha256','packageVersion','builtAt')) {
        $old=$script:manifest.$property; $script:manifest.$property='invalid'
        Reject { Assert-ConstructCompanionManifest $script:manifest 'permissionBRICK/The-Construct' ('companion-'+$sha) } "Reject bad $property"
        $script:manifest.$property=$old
    }
    Assert ((Install-ConstructCompanion $repo -Source local -SkipCompanion -LocalAppData $local -Seams $seams) -eq 'skipped') 'Skip flag'
    $settings=Join-Path $repo '.construct-settings.json'
    [IO.File]::WriteAllText($settings,'{"companion":false}')
    Assert ((Install-ConstructCompanion $repo -Source local -LocalAppData $local -Seams $seams) -eq 'skipped') 'Persistent opt-out'
    [IO.File]::WriteAllText($settings,'{}')
    Assert ((Install-ConstructCompanion $repo -Source local -LocalAppData $local -Seams $seams) -eq 'installed') 'Fresh install'
    $paths=Get-ConstructCompanionPaths $local
    $record=Read-ConstructCompanionJson (Join-Path $paths.install 'install.json')
    Assert ($record.commit -eq $sha -and $record.source -eq 'local-build' -and $record.ipcApiVersion -eq 1 -and $null -eq $record.releaseTag) 'Install record'
    Assert ($script:registry.Count -eq 6) 'Six registry values'
    Assert ($script:registry['HKCU:\Software\Microsoft\Windows\CurrentVersion\Run|ConstructCompanion'] -eq ('"'+(Join-Path $paths.install 'ConstructCompanion.exe')+'" --background')) 'Run key quoting'
    Assert ($script:registry['HKCU:\Software\Classes\construct\shell\open\command|'] -eq ('"'+(Join-Path $paths.install 'ConstructCompanion.exe')+'" --uri "%1"')) 'Protocol quoting'
    Assert ($script:registry['HKCU:\Software\Classes\AppUserModelId\PermissionBrick.TheConstruct|DisplayName'] -eq 'Construct Companion') 'AUMID'
    Assert ($script:starts.Count -eq 1 -and ($script:starts[0].argv -join '|') -eq '--background') 'Detached argv'
    $publish=@($script:nativeCalls | Where-Object { $_.argv[0] -eq 'publish' })[0]
    $expected=@('publish',(Join-Path $repo 'companion/src/Construct.Companion/Construct.Companion.csproj'),'-c','Release','-r','win-x64','--self-contained','true',('-p:InformationalVersion=1.0.0+'+$sha),'-p:IncludeSourceRevisionInInformationalVersion=false','-nodeReuse:false','-p:UseSharedCompilation=false','--artifacts-path',(Join-Path (Split-Path -Parent $publish.argv[-1]) 'artifacts'),'-o',$publish.argv[-1])
    Assert (($publish.argv -join '|') -eq ($expected -join '|')) 'Exact publish argv, including spaced path'
    $before=$script:nativeCalls.Count
    Assert ((Install-ConstructCompanion $repo -Source local -LocalAppData $local -Seams $seams) -eq 'unchanged') 'Identical commit is no-op'
    Assert ($script:starts.Count -eq 1 -and $script:nativeCalls.Count -eq $before+2) 'No publish/start on no-op'
    Assert ((Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams) -eq 'installed') 'Force reinstalls'
    Assert (-not (Test-Path ($paths.install+'.previous'))) 'Previous removed after success'
    $endpoint=Join-Path $paths.state 'endpoint.json'
    [IO.File]::WriteAllText($endpoint,(@{v=1;ipcApiVersion=1;port=12345;pid=23456;token=('d'*64)} | ConvertTo-Json))
    $script:alive=$true
    Stop-ConstructCompanionForInstall $paths.state $seams
    Assert ($script:quits -eq 1 -and -not $script:alive -and $script:lastReason -eq 'update') 'Quit handshake'
    $script:alive=$true; $script:quitWorks=$false
    Reject { Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams } 'Quit timeout aborts installation'
    Assert ($script:sleeps -eq 60 -and -not (Test-Path ($paths.install+'.previous'))) 'Timeout bounded, no swap'
    $script:alive=$false; $script:quitWorks=$true
    $before=$script:quits; Stop-ConstructCompanionForInstall $paths.state $seams
    Assert ($script:quits -eq $before) 'Stale dead PID skips HTTP'
    [IO.File]::WriteAllText($endpoint,'{"v":1,"port":0,"token":"do-not-print"}')
    Reject { Stop-ConstructCompanionForInstall $paths.state $seams } 'Malformed endpoint rejected'
    [IO.File]::WriteAllText($endpoint,(@{v=1;ipcApiVersion=1;port=12345;pid=34567;token=('d'*64)} | ConvertTo-Json))
    $before=$script:quits; $script:alive=$true
    Stop-ConstructCompanionForInstall $paths.state $seams -Reason user
    Assert ($script:quits -eq $before+1 -and -not $script:alive -and $script:lastReason -eq 'user') 'Uninstall quit handshake sends reason user'
    Remove-Item -LiteralPath $endpoint
    $exe=Join-Path $paths.install 'ConstructCompanion.exe'
    [IO.File]::WriteAllText($exe,'original app')
    $registryBefore=$script:registry | ConvertTo-Json -Compress
    $script:failStart=$true
    $startMessage=''; try { Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams | Out-Null } catch { $startMessage=$_.Exception.Message }
    Assert ($startMessage.Contains('while starting the app (fake start failure)') -and $startMessage.Contains('Start the Companion again manually')) 'Start failure triggers rollback and names the step'
    Assert ([IO.File]::ReadAllText($exe) -eq 'original app') 'Restores old files'
    Assert (($script:registry | ConvertTo-Json -Compress) -eq $registryBefore) 'Restores registry'
    Assert (-not (Test-Path ($paths.install+'.previous'))) 'Rollback consumes previous'
    $script:failStart=$false; $script:failMove=$true; $startsBefore=$script:starts.Count; $sleepsBefore=$script:sleeps
    $script:holders=@(@{name='MsMpEng.exe';id=42},@{name='ConstructCompanion.exe';id=777})
    $moveMessage=''; try { Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams | Out-Null } catch { $moveMessage=$_.Exception.Message }
    Assert ($moveMessage.Contains('while moving the new files into place (media: fake move failure: being used by another process; held by MsMpEng.exe (pid 42), ConstructCompanion.exe (pid 777))') -and $moveMessage.Contains('the previous Companion was started again')) 'Move failure triggers rollback, names the step, the item, the holders, and restarts the previous app'
    Assert ($script:starts.Count -eq $startsBefore+1 -and $script:sleeps -eq $sleepsBefore+19) 'Move retried for ten seconds before rollback, previous app restarted'
    # A held installation folder (a console's current directory) cannot be renamed, but its files move.
    $script:holders=@(); $script:failMove=$false
    $heldFolderSeam=$seams.Move
    $seams.Move={ param($From,$To) if ($From -eq $paths.install -or $To -eq $paths.install) { throw 'folder rename refused' }; [IO.Directory]::Move($From,$To) }
    Assert ((Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams) -eq 'installed') 'Held installation folder is swapped file by file'
    Assert ((Test-Path (Join-Path $paths.install 'ConstructCompanion.exe')) -and -not (Test-ConstructCompanionFolderHasContent ($paths.install+'.previous'))) 'Files landed in the held folder and .previous is consumed'
    $seams.Move=$heldFolderSeam; [IO.File]::WriteAllText($exe,'original app')
    $script:holders=@(); $script:failMove=$false
    # A Companion running from the install folder without a reachable endpoint is waited for, then refused.
    $script:runningPolls=3
    Assert ((Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams) -eq 'installed') 'Exiting Companion process is waited for'
    [IO.File]::WriteAllText($exe,'original app')
    $script:runningPolls=1000; $sleepsBefore=$script:sleeps
    $runningMessage=''; try { Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams | Out-Null } catch { $runningMessage=$_.Exception.Message }
    Assert ($runningMessage.Contains('still running (ConstructCompanion.exe (pid 777))') -and $script:sleeps -eq $sleepsBefore+60 -and [IO.File]::ReadAllText($exe) -eq 'original app' -and -not (Test-Path ($paths.install+'.previous'))) 'Unreachable running Companion refused before any swap'
    $script:runningPolls=0
    Assert ([IO.File]::ReadAllText($exe) -eq 'original app') 'Move failure restores files'
    $script:failMoveTimes=2
    Assert ((Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams) -eq 'installed') 'Transient move failures are retried'
    [IO.File]::WriteAllText($exe,'original app')
    $script:failRegistry=$true
    Reject { Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams } 'Registry failure triggers rollback'
    Assert ([IO.File]::ReadAllText($exe) -eq 'original app') 'Registry failure restores files'
    [IO.File]::WriteAllText((Join-Path $paths.state 'settings.json'),'{"autostart":false}')
    Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams | Out-Null
    Assert (-not $script:registry.ContainsKey('HKCU:\Software\Microsoft\Windows\CurrentVersion\Run|ConstructCompanion')) 'Autostart opt-out retained'
    $held=[IO.File]::Open((Join-Path $paths.state 'install.lock'),'OpenOrCreate','ReadWrite','None')
    try { Reject { Install-ConstructCompanion $repo -Source local -Force -LocalAppData $local -Seams $seams } 'Concurrent install refused' } finally { $held.Dispose() }
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
    # Windows PowerShell 5.1 hands back a JSON array as a single object; discovery still works.
    $seams.Json={ param($Uri)
        if ($Uri -match '/releases\?') { return ,@(@{tag_name=('host-'+$sha);published_at='2026-09-12T00:00:00Z'},@{tag_name=('companion-'+$sha);published_at='2026-09-11T00:00:00Z'}) }
        return $script:manifest
    }
    Assert ((Resolve-ConstructCompanionSource $repo release $seams).commit -eq $sha) 'Nested array from Invoke-RestMethod is unwrapped'
    # The installed commit names its Companion release directly: no listing, no API rate limit.
    [IO.File]::WriteAllText($settings,(@{installedCommit=$sha} | ConvertTo-Json))
    $script:releaseCalls=0; $script:directUris=@()
    $seams.Json={ param($Uri)
        if ($Uri -match '/releases\?') { $script:releaseCalls++; return @() }
        $script:directUris+=$Uri; return $script:manifest
    }
    $direct=Resolve-ConstructCompanionSource $repo release $seams
    Assert ($direct.commit -eq $sha -and $direct.releaseTag -eq ('companion-'+$sha) -and $script:releaseCalls -eq 0 -and $script:directUris[0] -eq ('https://github.com/permissionBRICK/The-Construct/releases/download/host-'+$sha+'/manifest.json') -and $script:directUris[1] -eq ('https://github.com/permissionBRICK/The-Construct/releases/download/companion-'+$sha+'/manifest.json')) 'Installed commit resolves its Companion without listing releases'
    # A host release whose manifest points at an older Companion (no Companion change since) installs that one.
    $script:releaseCalls=0; $script:directUris=@()
    $seams.Json={ param($Uri)
        if ($Uri -match '/releases\?') { $script:releaseCalls++; return @() }
        $script:directUris+=$Uri
        if ($Uri -match ('/host-'+('9'*40)+'/manifest.json$')) { return [pscustomobject]@{commit=('9'*40);companionReleaseTag=('companion-'+$sha)} }
        if ($Uri -match ('/companion-'+$sha+'/manifest.json$')) { return $script:manifest }
        throw 'The remote server returned an error: (404) Not Found.'
    }
    [IO.File]::WriteAllText($settings,(@{installedCommit=('9'*40)} | ConvertTo-Json))
    $pointed=Resolve-ConstructCompanionSource $repo release $seams
    Assert ($pointed.commit -eq $sha -and $pointed.releaseTag -eq ('companion-'+$sha) -and $script:releaseCalls -eq 0 -and $script:directUris.Count -eq 2) 'Host manifest pointer selects the Companion release without listing'
    $seams.Json={ param($Uri)
        if ($Uri -match '/releases\?') { $script:releaseCalls++; return @() }
        if ($Uri -match ('/host-'+('9'*40)+'/manifest.json$')) { return [pscustomobject]@{commit=('9'*40);companionReleaseTag='companion-latest'} }
        if ($Uri -match ('/companion-'+('9'*40)+'/manifest.json$')) { throw 'The remote server returned an error: (404) Not Found.' }
        return $script:manifest
    }
    Reject { Resolve-ConstructCompanionSource $repo release $seams } 'Malformed pointer is ignored and a missing own build with an empty listing fails clearly'
    [IO.File]::WriteAllText($settings,(@{installedCommit=$sha} | ConvertTo-Json))
    $seams.Json={ param($Uri)
        if ($Uri -match '/releases\?') { $script:releaseCalls++; return @() }
        $script:directUris+=$Uri; return $script:manifest
    }
    Assert ($direct.payloadUri -eq ('https://github.com/permissionBRICK/The-Construct/releases/download/companion-'+$sha+'/'+$direct.payload.asset)) 'Direct payload URI uses the commit tag'
    [IO.File]::WriteAllText($settings,'{}')
    [IO.File]::WriteAllText((Join-Path $repo '.construct-revision'),$sha+"`n")
    $script:releaseCalls=0
    Assert ((Resolve-ConstructCompanionSource $repo release $seams).commit -eq $sha -and $script:releaseCalls -eq 0) 'Archive revision marker also resolves directly'
    Remove-Item -LiteralPath (Join-Path $repo '.construct-revision') -Force
    # A commit whose Companion release is missing (still building) falls back to the newest listed one.
    [IO.File]::WriteAllText($settings,(@{installedCommit=('f'*40)} | ConvertTo-Json))
    $script:releaseCalls=0
    $seams.Json={ param($Uri)
        if ($Uri -match '/releases\?') { $script:releaseCalls++; return @(@{tag_name=('companion-'+$sha);published_at='2026-09-11T00:00:00Z'}) }
        if ($Uri -match ('companion-'+('f'*40))) { throw 'The remote server returned an error: (404) Not Found.' }
        return $script:manifest
    }
    Assert ((Resolve-ConstructCompanionSource $repo release $seams).commit -eq $sha -and $script:releaseCalls -eq 1) 'Missing direct release falls back to the listing'
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
    try { Install-ConstructCompanion $repo -Source local -LocalAppData $local -Seams $seams | Out-Null } catch { $buildMessage=$_.Exception.Message }
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
