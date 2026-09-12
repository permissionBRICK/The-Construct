#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'lib/AgentVm.Common.ps1')
. (Join-Path $root 'lib/AgentVm.Remote.ps1')
$passed = 0
function Check($name, $condition) { if (-not $condition) { throw "FAIL $name" }; $script:passed++; Write-Host "PASS $name" }
$commit = 'a' * 40; $hash = 'b' * 64
$rows = @(
    @($false,'cache',$false,$false,'dev','unknown','local-install'),
    @($true,'upload',$false,$false,'dev','unknown','mode-upload'),
    @($true,'cache',$true,$false,'dev','unknown','include-git'),
    @($true,'auto',$false,$false,'main','equivalent','service-without-source-cache'),
    @($true,'auto',$false,$true,'dev','equivalent','ref-not-main'),
    @($true,'auto',$false,$true,'main','unknown','commit-unknown'),
    @($true,'auto',$false,$true,'main','unverified','archive-unverified'),
    @($true,'auto',$false,$true,'main','divergent','local-changes'),
    @($true,'auto',$false,$true,'main','equivalent','cache'),
    @($true,'cache',$false,$true,'dev','divergent','cache-forced')
)
foreach ($row in $rows) {
    $plan = Get-ConstructSourceTransportPlan -ServiceManaged $row[0] -Mode $row[1] -IncludeGit $row[2] -FeatureAvailable $row[3] -Ref $row[4] -TreeState $row[5] -Commit $commit -Divergence 2
    Check ('plan '+$row[6]) ($plan.Reason -eq $row[6])
}
foreach ($feature in @($false,$true)) {
    $thrown = $false
    try { Get-ConstructSourceTransportPlan -ServiceManaged $true -Mode cache -FeatureAvailable $feature -Commit '' | Out-Null } catch { $thrown = $true }
    Check ('forced cache throws '+$feature) $thrown
}
Check 'literal unavailable message' ((Get-ConstructSourceMessage -Reason service-without-source-cache) -ceq 'This host service does not offer the source cache (apiFeatures lack "source-cache"); uploading the Construct checkout as before. Update the host service to skip the upload.')
Check 'literal changes message' ((Get-ConstructSourceMessage -Reason local-changes -Commit $commit -Divergence 2) -ceq 'This checkout differs from commit aaaaaaa in 2 file(s) (the differing files could not be listed); uploading it as before so the VM gets them. Commit or ignore them to use the host cache, or pass -SourceMode cache to send commit aaaaaaa without them.')
Check 'capacity guidance' ((Get-ConstructSourceMessage -Reason ensure-failed:source-cache-full -Commit $commit) -match 'ask the host admin to delete unused entries')
$plan = Get-ConstructSourceTransportPlan -ServiceManaged $true -FeatureAvailable $true -Commit $commit -TreeState equivalent
$cases = @(
    @('success','downloading','succeeded',0,'','ensure,wait,stage,fetch'),
    @('ready','ready','succeeded',0,'','ensure,stage,fetch'),
    @('unsupported','unsupported','',0,'service-without-source-cache','ensure,warn,pack,upload'),
    @('denied','denied','',0,'ensure-denied:403','ensure,warn,pack,upload'),
    @('maintenance','maintenance','',0,'ensure-maintenance','ensure,warn,pack,upload'),
    @('refused','refused','',0,'ensure-refused:vm-deleting','ensure,warn,pack,upload'),
    @('error','error','',0,'ensure-failed:tls','ensure,warn,pack,upload'),
    @('malformed','malformed','',0,'ensure-failed:malformed','ensure,warn,pack,upload'),
    @('failed','downloading','failed',0,'ensure-failed:source-unavailable','ensure,wait,warn,pack,upload'),
    @('cancelled','downloading','cancelled',0,'ensure-cancelled','ensure,wait,warn,pack,upload'),
    @('timeout','downloading','timeout',0,'ensure-timeout','ensure,wait,warn,pack,upload'),
    @('poll-denied','downloading','denied',0,'ensure-failed:http','ensure,wait,warn,pack,upload'),
    @('poll-error','downloading','error',0,'ensure-failed:dns','ensure,wait,warn,pack,upload'),
    @('poll-malformed','downloading','malformed',0,'ensure-failed:malformed','ensure,wait,warn,pack,upload'),
    @('stage','ready','',0,'guest-token-staging-failed','ensure,stage,warn,pack,upload'),
    @('ssh','ready','',255,'guest-fetch-failed:ssh','ensure,stage,fetch,warn,pack,upload'),
    @('lost','ready','',7,'guest-fetch-failed:repo-lost','ensure,stage,fetch,warn,pack,upload'),
    @('unverified','ready','',0,'guest-fetch-failed:unverified','ensure,stage,fetch,warn,pack,upload')
)
foreach ($exitCode in 2..6) { $cases += ,@(('fetch'+$exitCode),'ready','',$exitCode,('guest-fetch-failed:'+$exitCode),'ensure,stage,fetch,warn,pack,upload') }
foreach ($case in $cases) {
    $events = [Collections.Generic.List[string]]::new(); $warnings = [Collections.Generic.List[string]]::new()
    $ensure = { param($c,$key)
        Check 'ensure identity and operation key' ($c -eq $commit -and $key -match '^source-[a-f0-9]{32}$')
        $events.Add('ensure'); $code='';$class='none'
        if ($case[0] -eq 'refused') { $code='vm-deleting' }; if ($case[0] -eq 'error') { $class='tls' }
        @{Outcome=$case[1]; Status=403; Code=$code; Class=$class; JobId='job';SizeBytes=123;Sha256=$hash}
    }
    $pack = { $events.Add('pack'); 'archive' }; $warn = {param($text) $events.Add('warn');$warnings.Add($text)}
    $state = Invoke-ConstructSourceTransport -Phase begin -Plan $plan -Ensure $ensure -Pack $pack -Warn $warn -Info {}
    $result = Invoke-ConstructSourceTransport -Phase complete -State $state -Pack $pack -Warn $warn -Info {} -WaitJob {
        $events.Add('wait');$code='';$class='none'
        if ($case[2] -eq 'failed') {$code='source-unavailable'}
        if ($case[2] -eq 'denied') {$class='http'}
        if ($case[2] -eq 'error') {$class='dns'}
        @{State=$case[2];Code=$code;Class=$class;SizeBytes=123;Sha256=$hash}
    } -StageToken {$events.Add('stage');$case[0] -ne 'stage'} -RunGuestFetch {
        $events.Add('fetch');$lines=@('CONSTRUCT_SOURCE_INSTALLED='+$commit)
        if($case[0] -eq 'unverified'){$lines=@()}
        @{ExitCode=$case[3];Lines=$lines}
    } -Upload {param($archive) $events.Add('upload');Check 'archive passed to upload' ($archive -eq 'archive')}
    Check ('sequence '+$case[0]) (($events -join ',') -eq $case[5])
    if($case[4]) {
        Check ('reason '+$case[0]) ($result.Reason -eq $case[4])
        Check ('warning '+$case[0]) ($warnings[0] -eq (Get-ConstructSourceMessage -Reason $case[4] -Commit $commit))
        $forced = @{};foreach($key in $plan.Keys){$forced[$key]=$plan[$key]};$forced.Mode='cache'
        $thrown=$false; $script:forcedUploads=0
        try {
            $state = Invoke-ConstructSourceTransport -Phase begin -Plan $forced -Ensure $ensure -Pack $pack -Warn {} -Info {}
            Invoke-ConstructSourceTransport -Phase complete -State $state -WaitJob {
                $code='';$class='none'
                if($case[2] -eq 'failed'){$code='source-unavailable'}
                if($case[2] -eq 'denied'){$class='http'}
                if($case[2] -eq 'error'){$class='dns'}
                @{State=$case[2];Code=$code;Class=$class;SizeBytes=123;Sha256=$hash}
            } -StageToken { $case[0] -ne 'stage' } -RunGuestFetch {
                $lines=@('CONSTRUCT_SOURCE_INSTALLED='+$commit);if($case[0] -eq 'unverified'){$lines=@()}
                @{ExitCode=$case[3];Lines=$lines}
            } -Upload { $script:forcedUploads++ } -Info {} | Out-Null
        } catch {$thrown=$true}
        Check ('forced cache failure '+$case[0]) ($thrown -and $script:forcedUploads -eq 0)
    }
}
$uploadPlan = Get-ConstructSourceTransportPlan -ServiceManaged $true -Mode upload
$events=[Collections.Generic.List[string]]::new()
$state=Invoke-ConstructSourceTransport -Phase begin -Plan $uploadPlan -Pack {$events.Add('pack');'archive'}
Invoke-ConstructSourceTransport -Phase complete -State $state -Upload {$events.Add('upload')} | Out-Null
Check 'planned upload order' (($events -join ',') -eq 'pack,upload')

$tmp=Join-Path ([IO.Path]::GetTempPath()) ('source-identity-'+[guid]::NewGuid().ToString('N'))
try {
    $tree=Join-Path $tmp 'tree';[IO.Directory]::CreateDirectory((Join-Path $tree '.git'))|Out-Null
    foreach($dirty in @(0,1,2)) {
        $identity=Get-ConstructSourceIdentity -Root $tree -GitRunner {param($r,$argv)
            if($argv[0] -eq 'rev-parse'){return @{ExitCode=0;Lines=@($commit)}}
            Check 'git status flags' (($argv -join ' ') -eq 'status --porcelain --untracked-files=all --ignore-submodules=none')
            $lines=@();if($dirty -ge 1){$lines+=' M tracked'};if($dirty -ge 2){$lines+='?? untracked'}
            @{ExitCode=0;Lines=$lines}
        }
        Check ('git identity '+$dirty) ($identity.Divergence -eq $dirty -and $identity.Commit -eq $commit)
    }
    Check 'failing git is unknown' ((Get-ConstructSourceIdentity -Root $tree -GitRunner {@{ExitCode=1;Lines=@()}}).TreeState -eq 'unknown')
    Remove-Item -LiteralPath (Join-Path $tree '.git') -Recurse -Force
    [IO.File]::WriteAllText((Join-Path $tree '.construct-revision'),$commit)
    [IO.File]::WriteAllText((Join-Path $tree 'file'),'content')
    $manifests=Join-Path $tmp 'manifests';$zip=Join-Path $tmp 'source.zip'
    $archiveRoot=Join-Path $tmp 'repo-main';Copy-Item -LiteralPath $tree -Destination $archiveRoot -Recurse
    Compress-Archive -Path $archiveRoot -DestinationPath $zip
    Check 'manifest written' (Write-ConstructSourceManifest -Zip $zip -Commit $commit -ManifestDir $manifests)
    # Compress-Archive omits dotfiles on Unix; explicitly create a ZIP with every fixture file.
    Remove-Item -LiteralPath $zip
    $z=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
    try { foreach($f in Get-ChildItem -LiteralPath $tree -File -Force){ [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z,$f.FullName,('repo-main/'+$f.Name))|Out-Null } } finally {$z.Dispose()}
    Write-ConstructSourceManifest -Zip $zip -Commit $commit -ManifestDir $manifests | Out-Null
    Check 'archive equivalent' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'equivalent')
    [IO.File]::WriteAllText((Join-Path $tree 'ignored.local'),'ignored')
    Check 'ignored extra still equivalent' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'equivalent')
    foreach ($built in 'companion/src/App/obj/x.dll','companion/src/App/bin/Release/y.dll','extension/test/node_modules/z/index.js') {
        [IO.Directory]::CreateDirectory((Split-Path -Parent (Join-Path $tree $built))) | Out-Null
        [IO.File]::WriteAllText((Join-Path $tree $built),'built')
    }
    Check 'build outputs and node_modules are not local changes' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'equivalent')
    foreach ($devOnly in 'test/old.test.ps1','extension/test/old.test.js','service/src/Old.cs','companion/src/App/Old.cs','docs/plans/old.md','.github/workflows/old.yml') {
        [IO.Directory]::CreateDirectory((Split-Path -Parent (Join-Path $tree $devOnly))) | Out-Null
        [IO.File]::WriteAllText((Join-Path $tree $devOnly),'dev-only')
    }
    Check 'repository-only folders left over from an older archive are not local changes' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'equivalent')
    [IO.Directory]::CreateDirectory((Join-Path $tree 'projects')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $tree 'projects/my-app.json'),'{"name":"my-app"}')
    Check 'user project profiles in the legacy projects folder are not local changes' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'equivalent')
    [IO.File]::WriteAllText((Join-Path $tree 'debug.log'),'dropped by a tool')
    Check 'log files are not local changes' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'equivalent')
    [IO.File]::WriteAllText((Join-Path $tree 'stale.txt'),'from an older release')
    [IO.File]::WriteAllText((Join-Path $tree 'keep.local'),'mine')
    $pruned = Remove-ConstructStaleSourceFiles -Root $tree -Zip $zip
    Check 'pruning removes files the archive no longer ships' ($pruned -eq 1 -and -not (Test-Path (Join-Path $tree 'stale.txt')))
    Check 'pruning keeps archive files and local artifacts' ((Test-Path (Join-Path $tree 'file')) -and (Test-Path (Join-Path $tree 'keep.local')) -and (Test-Path (Join-Path $tree 'companion/src/App/obj/x.dll')))
    Check 'pruning keeps user project profiles' (Test-Path (Join-Path $tree 'projects/my-app.json'))
    [IO.File]::WriteAllText((Join-Path $tree 'extra'),'extra')
    Check 'archive extra divergent' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'divergent')
    Remove-Item -LiteralPath (Join-Path $tree 'extra');[IO.File]::WriteAllText((Join-Path $tree 'file'),'changed')
    Check 'archive edit divergent' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'divergent')
    Remove-Item -LiteralPath (Join-Path $tree 'file')
    Check 'archive missing divergent' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'divergent')
    [IO.File]::WriteAllText((Join-Path $manifests ($commit+'.sha256')),'invalid')
    Check 'malformed manifest unverified' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'unverified')
    Remove-Item -LiteralPath (Join-Path $manifests ($commit+'.sha256'))
    Check 'no manifest unverified' ((Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests).TreeState -eq 'unverified')
    if(Get-Command git -ErrorAction SilentlyContinue) {
        & git -C $tree init -q; & git -C $tree config status.showUntrackedFiles no
        & git -C $tree add .; & git -C $tree -c user.name=test -c user.email=test@example.invalid commit -qm fixture
        [IO.File]::WriteAllText((Join-Path $tree 'untracked'),'untracked')
        Check 'real git overrides hidden untracked configuration' ((Get-ConstructSourceIdentity -Root $tree).TreeState -eq 'divergent')
    }
} finally { if(Test-Path $tmp){Remove-Item -LiteralPath $tmp -Recurse -Force} }
Write-Host "$passed passed"
# Structured transport errors and real source helpers through a shadowed HTTP boundary.
Add-Type -TypeDefinition @'
using System;
public class SourceTestResponse { public int StatusCode {get;set;} }
public class SourceTestHttpException : Exception {
  public SourceTestResponse Response {get;set;}
  public SourceTestHttpException(int status) : base("safe fixture") { Response=new SourceTestResponse {StatusCode=status}; }
}
'@
$script:wireStatus=200;$script:wire=@{};$script:wireException=$null;$script:wireHandler=$null;$script:wireCalls=0
function Invoke-WebRequest {
    [CmdletBinding()]
    param($Uri,$Method,$Headers,$Body,$ContentType,$TimeoutSec,[switch]$UseBasicParsing,[switch]$UseDefaultCredentials,[switch]$SkipCertificateCheck,$Credential)
    $script:wireCalls++;$script:wireRequest=@{Uri=$Uri;Method=$Method;Headers=$Headers;Body=$Body;TimeoutSec=$TimeoutSec}
    if($script:wireException){throw $script:wireException}
    if($script:wireHandler){& $script:wireHandler}
    if($script:wireStatus -ge 400){
        $errorRecord=[System.Management.Automation.ErrorRecord]::new([SourceTestHttpException]::new($script:wireStatus),'SourceTest',[System.Management.Automation.ErrorCategory]::InvalidOperation,$null)
        $errorRecord.ErrorDetails=[System.Management.Automation.ErrorDetails]::new(($script:wire|ConvertTo-Json -Compress))
        throw $errorRecord
    }
    return @{StatusCode=$script:wireStatus;Content=($script:wire|ConvertTo-Json -Depth 8 -Compress)}
}
$auth=New-ConstructApiAuth -Mode token -Token 'owner-secret'
$base='http://127.0.0.1:7999'
foreach($pair in @(
    @('timeout',[TimeoutException]::new('sentinel')),
    @('tls',[Security.Authentication.AuthenticationException]::new('sentinel')),
    @('dns',[Net.WebException]::new('sentinel',[Net.WebExceptionStatus]::NameResolutionFailure)),
    @('connection',[Net.WebException]::new('sentinel',[Net.WebExceptionStatus]::ConnectFailure)),
    @('other',[InvalidOperationException]::new('sentinel')))) {
    $script:wireException=$pair[1]
    Invoke-ConstructApi -BaseUrl $base -Path /health -Auth $auth -NoThrow | Out-Null
    Check ('problem class '+$pair[0]) ((Get-ConstructApiLastProblem).Class -eq $pair[0])
    Check 'legacy status stays zero on transport error' ((Get-ConstructApiLastStatus) -eq 0)
}
$script:wireException=$null
$originalFingerprint=${function:Get-ConstructRemoteFingerprint}
function Get-ConstructRemoteFingerprint {param($BaseUrl,$TimeoutMs) return ('b'*64)}
Invoke-ConstructApi -BaseUrl https://example.invalid -Path /health -Pin ('a'*64) -Auth $auth -NoThrow | Out-Null
Check 'fingerprint refusal has pin class' ((Get-ConstructApiLastProblem).Class -eq 'pin')
Set-Item Function:Get-ConstructRemoteFingerprint $originalFingerprint
$script:wireStatus=409;$script:wire=@{code='source-pinned';title='source-pinned'}
Invoke-ConstructApi -BaseUrl $base -Path /health -Auth $auth -NoThrow | Out-Null
Check 'problem code and legacy fields' ((Get-ConstructApiLastProblem).Code -eq 'source-pinned' -and (Get-ConstructApiLastStatus) -eq 409 -and (Get-ConstructApiLastError) -eq 'source-pinned')
$script:wireStatus=200;$script:wire=@{apiFeatures=@('source-cache')}
Check 'feature available' (Test-ConstructApiFeature -BaseUrl $base -Auth $auth)
$script:wire=@{apiFeatures=@('other')};Check 'feature absent' (-not (Test-ConstructApiFeature -BaseUrl $base -Auth $auth))
$script:wireStatus=404;Check 'feature old service' (-not (Test-ConstructApiFeature -BaseUrl $base -Auth $auth))
$script:wireException=[TimeoutException]::new('timeout');Check 'feature timeout' (-not (Test-ConstructApiFeature -BaseUrl $base -Auth $auth));$script:wireException=$null
foreach($row in @(
    @(200,@{state='ready';sizeBytes=123;sha256=$hash;releaseTag=('host-'+$commit)},'ready'),
    @(202,@{state='downloading';jobId='job'},'downloading'),
    @(200,@{state='downloading';jobId='job';replayed=$true},'downloading'),
    @(200,@{state='ready';sizeBytes=123;sha256='bad'},'malformed'),
    @(404,@{},'unsupported'),@(409,@{code='unsupported-capability'},'unsupported'),
    @(401,@{},'denied'),@(403,@{},'denied'),@(503,@{},'maintenance'),@(409,@{code='vm-deleting'},'refused'),@(502,@{},'error'))) {
    $script:wireStatus=$row[0];$script:wire=$row[1]
    $result=Request-ConstructSourceEnsure -BaseUrl $base -VmName 'vm' -Commit $commit -OperationKey 'source-run' -Auth $auth
    Check ('ensure outcome '+$row[0]+' '+$row[2]) ($result.Outcome -eq $row[2])
    Check 'request body route and operation key' ($script:wireRequest.Method -eq 'POST' -and $script:wireRequest.Uri -eq ($base+'/api/v1/vms/vm/source') -and ($script:wireRequest.Body|ConvertFrom-Json).commit -eq $commit -and $script:wireRequest.Headers['X-Construct-Operation-Key'] -eq 'source-run')
    if($row[1].replayed){Check '200 queued replay recognized' $result.Replayed}
}
Check 'caller auth unmodified' (-not $auth.Headers -or -not $auth.Headers.ContainsKey('X-Construct-Operation-Key'))
$script:wireStatus=200
foreach($state in @('succeeded','failed','cancelled','malformed')) {
    $script:wire=@{state=$state;progress=@();error='source-unavailable';result=@{sizeBytes=123;sha256=$hash}}
    $result=Wait-ConstructSourceJob -BaseUrl $base -JobId 'job' -Auth $auth -Deadline ([datetime]::UtcNow.AddSeconds(5)) -PollSeconds 0
    Check ('job '+$state) ($result.State -eq $state)
    Check 'poll timeout clamped to deadline' ($script:wireRequest.TimeoutSec -ge 1 -and $script:wireRequest.TimeoutSec -le 5)
}
$script:wire=@{state='succeeded';result=@{sizeBytes=0;sha256=$hash}}
Check 'job result validated' ((Wait-ConstructSourceJob -BaseUrl $base -JobId job -Auth $auth -Deadline ([datetime]::UtcNow.AddSeconds(5))).State -eq 'malformed')
$script:wireStatus=403;Check 'poll denied' ((Wait-ConstructSourceJob -BaseUrl $base -JobId job -Auth $auth -Deadline ([datetime]::UtcNow.AddSeconds(5))).State -eq 'denied')
$script:wireStatus=502;$script:wireCalls=0
Check 'three poll failures fall back' ((Wait-ConstructSourceJob -BaseUrl $base -JobId job -Auth $auth -Deadline ([datetime]::UtcNow.AddSeconds(5)) -PollSeconds 0).State -eq 'error' -and $script:wireCalls -eq 3)
$script:wireStatus=200;$script:wireCalls=0;$script:wire=@{state='running';progress=@()}
Check 'deadline ends running job wait' ((Wait-ConstructSourceJob -BaseUrl $base -JobId job -Auth $auth -Deadline ([datetime]::UtcNow.AddMilliseconds(100)) -PollSeconds 0.1).State -eq 'timeout')
$script:wireCalls=0;$script:progressSeen=[Collections.Generic.List[string]]::new()
$script:wireHandler={if($script:wireCalls -eq 1){$script:wire=@{state='queued';progress=@('check')}}else{$script:wire=@{state='succeeded';progress=@('check','ready');result=@{sizeBytes=123;sha256=$hash}}}}
$result=Wait-ConstructSourceJob -BaseUrl $base -JobId job -Auth $auth -Deadline ([datetime]::UtcNow.AddSeconds(5)) -PollSeconds 0 -OnProgress {param($line)$script:progressSeen.Add($line)}
Check 'queued progress consumed once' (($script:progressSeen -join ',') -eq 'check,ready' -and $result.State -eq 'succeeded')
$script:wireCalls=0;$script:progressSeen=[Collections.Generic.List[string]]::new()
$script:wireHandler={$script:wire=@{state='succeeded';progress=@(@{at='2026-09-12T11:03:35Z';text='check'},@{at='2026-09-12T11:03:36Z';text='download'});result=@{sizeBytes=123;sha256=$hash}}}
Wait-ConstructSourceJob -BaseUrl $base -JobId job -Auth $auth -Deadline ([datetime]::UtcNow.AddSeconds(5)) -PollSeconds 0 -OnProgress {param($line)$script:progressSeen.Add($line)} | Out-Null
Check 'structured progress rows print their text' (($script:progressSeen -join ',') -ceq 'check,download')
$script:wireHandler=$null
Write-Host "$passed passed"
# Keep the local transport and everything after repo delivery identical to the reviewed base.
$provision = [IO.File]::ReadAllText((Join-Path $root 'Provision-AgentVM.ps1')) -replace "`r`n", "`n"
$baseline = [IO.File]::ReadAllText((Join-Path $root 'test/fixtures/source-local-baseline.ps1')) -replace "`r`n", "`n"
function FunctionText($source,$name) {
    $ast = [Management.Automation.Language.Parser]::ParseInput($source,[ref]$null,[ref]$null)
    return $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true).Extent.Text
}
Check 'local archive function byte-identical' ((FunctionText $provision 'New-RepoArchive') -ceq (FunctionText $baseline 'New-RepoArchive'))
$marker = '# Upload the archive via SCP (remove any stale copy'
$end = '# ── -Action export:'
$oldBlock = $baseline.Substring($baseline.IndexOf($marker),$baseline.IndexOf($end)-$baseline.IndexOf($marker))
Check 'original upload block appears verbatim twice' ([regex]::Matches($provision,[regex]::Escape($oldBlock)).Count -eq 2)
$sha=[Security.Cryptography.SHA256]::Create()
try {$downstreamHash=[BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($provision.Substring($provision.IndexOf($end)).TrimEnd()))).Replace('-','').ToLowerInvariant()} finally {$sha.Dispose()}
Check 'downstream commands and environment byte-identical to main 4c9409a' ($downstreamHash -ceq 'f8729213b64103ca4226c4848c2062e771e98fe8b7e4c911b14785b6a6f98a59')
$local = $provision.IndexOf('if (-not $ServiceUrl) { $archivePath = New-RepoArchive }')
$reachable = $provision.IndexOf('Ensure-VmReachable', $local)
$begin = $provision.IndexOf('Invoke-ConstructSourceTransport -Phase begin', $reachable)
$key = $provision.IndexOf('Accepting VM host key', $begin)
$complete = $provision.IndexOf('Invoke-ConstructSourceTransport -Phase complete', $key)
Check 'local packing precedes reachability' ($local -ge 0 -and $reachable -gt $local)
Check 'remote begin precedes host key and complete follows' ($begin -gt $reachable -and $key -gt $begin -and $complete -gt $key -and $complete -lt $provision.IndexOf($marker))
$ast = [Management.Automation.Language.Parser]::ParseInput($provision,[ref]$null,[ref]$null)
foreach ($command in $ast.FindAll({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -in @('Get-ConstructSourceIdentity','Get-ConstructSourceTransportPlan','Test-ConstructApiFeature','Invoke-ConstructSourceTransport')},$true)) {
    $guarded=$false;$parent=$command.Parent
    while($parent){
        if($parent -is [Management.Automation.Language.IfStatementAst]) {
            foreach($clause in $parent.Clauses){if($clause.Item1.Extent.Text -eq '$ServiceUrl'){$guarded=$true}}
            if($parent.Clauses[0].Item1.Extent.Text -eq '-not $ServiceUrl' -and $parent.ElseClause -and $command.Extent.StartOffset -ge $parent.ElseClause.Extent.StartOffset){$guarded=$true}
        };$parent=$parent.Parent
    }
    Check ('remote guard '+$command.GetCommandName()) $guarded
}
# Exercise the local archive body against one fixture with both versions: same entries and transcript.
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('source-local-'+[guid]::NewGuid().ToString('N'))
$priorTemp=$env:TEMP;$archiveTemp=$fixture+'.archive'
try {
    [IO.Directory]::CreateDirectory($fixture)|Out-Null
    foreach($name in @('README.md','guest.sh','.construct-settings.json','test.iso')){[IO.File]::WriteAllText((Join-Path $fixture $name),'fixture')}
    foreach($name in @('.git','.construct-backup')){[IO.Directory]::CreateDirectory((Join-Path $fixture $name))|Out-Null;[IO.File]::WriteAllText((Join-Path $fixture "$name/secret"),'excluded')}
    $env:TEMP=Join-Path $fixture 'temp';[IO.Directory]::CreateDirectory($env:TEMP)|Out-Null
    $transcripts=@();$entries=@()
    foreach($source in @($baseline,$provision)){
        $body=(FunctionText $source 'New-RepoArchive').Replace('$repoDir = $PSScriptRoot','$repoDir = $fixture')
        . ([scriptblock]::Create($body))
        $script:transcript=[Collections.Generic.List[string]]::new()
        function Write-Step($s){$script:transcript.Add($s)}
        function Write-Ok($s){$script:transcript.Add($s)}
        function tar.exe { & tar @args; $global:LASTEXITCODE=$LASTEXITCODE }
        $IncludeGit=$false
        # Keep the tar output outside the fixture whose entries are compared.
        $env:TEMP=$archiveTemp;[IO.Directory]::CreateDirectory($archiveTemp)|Out-Null
        $archive=New-RepoArchive
        $entries+=,(@(& tar -tzf $archive) -join "`n")
        $transcripts+=,($script:transcript -join "`n")
    }
    Check 'local archive entries unchanged on identical input' ($entries[0] -ceq $entries[1])
    Check 'local packing transcript unchanged' ($transcripts[0] -ceq $transcripts[1])
    Check 'local archive keeps secret exclusions' ($entries[1] -notmatch 'secret|settings|test.iso')
    Remove-Item -LiteralPath $archive
} finally {$env:TEMP=$priorTemp;Remove-Item -LiteralPath $fixture -Recurse -Force;if(Test-Path $archiveTemp){Remove-Item -LiteralPath $archiveTemp -Recurse -Force}}
if(Get-Command git -ErrorAction SilentlyContinue){
    $work=Join-Path ([IO.Path]::GetTempPath()) ('source-manifest-git-'+[guid]::NewGuid().ToString('N'))
    try{
        [IO.Directory]::CreateDirectory($work)|Out-Null
        $zip=Join-Path $work 'source.zip';$manifestDir=Join-Path $work 'manifests'
        & git -C $root archive --format=zip --prefix=The-Construct-main/ HEAD -o $zip
        Check 'real git archive manifest written' (Write-ConstructSourceManifest -Zip $zip -Commit $commit -ManifestDir $manifestDir)
        $lines=[IO.File]::ReadAllLines((Join-Path $manifestDir ($commit+'.sha256')))
        Check 'manifest lines formatted and prefix stripped' (@($lines|Where-Object{$_ -cnotmatch '^[0-9a-f]{64}  \S.*$' -or $_ -match '  The-Construct-main/'}).Count -eq 0)
        $paths=@($lines|ForEach-Object{$_.Substring(66)});$sorted=[string[]]$paths.Clone();[Array]::Sort($sorted,[StringComparer]::Ordinal)
        Check 'manifest paths ordinal sorted' (($paths -join "`n") -ceq ($sorted -join "`n"))
        Check 'manifest rename leaves no temporary' (@(Get-ChildItem $manifestDir -Force).Count -eq 1)
    }finally{Remove-Item $work -Recurse -Force}
}else{Write-Host 'SKIP real git archive: git unavailable'}
Write-Host "$passed passed"

$messages = @{
 'ref-not-main' = "Construct source ref 'dev' is not main, so no host release exists for it; uploading the checkout as before."
 'commit-unknown' = "Could not determine this checkout's commit (no git or no .construct-revision); uploading the checkout as before."
 'archive-unverified' = 'This Construct install has no source manifest (it was installed before the host cache existed); uploading the checkout as before. Run Update-Construct.ps1 once to enable the host cache.'
 'ensure-denied:403' = 'The host service refused the source request (HTTP 403); uploading the checkout as before.'
 'ensure-maintenance' = 'The host service is in maintenance; uploading the checkout as before.'
 'ensure-failed:tls' = 'The host service could not cache commit aaaaaaa (tls); uploading the checkout as before.'
 'ensure-refused:vm-deleting' = 'The host service could not cache commit aaaaaaa (vm-deleting); uploading the checkout as before.'
 'ensure-cancelled' = "The host service's source download was cancelled; uploading the checkout as before."
 'ensure-timeout' = 'The host service did not finish caching commit aaaaaaa within 900 s; uploading the checkout as before (the host keeps downloading for the next run).'
 'guest-token-staging-failed' = 'Could not hand the VM its service token for the source fetch; uploading the checkout as before.'
 'guest-fetch-failed:ssh' = 'The VM could not fetch commit aaaaaaa from the host service (ssh); uploading the checkout as before.'
 'guest-fetch-failed:repo-lost' = "The VM's Construct repo was lost while swapping in commit aaaaaaa; uploading the checkout to restore it."
 'include-git' = ''
}
foreach($reason in $messages.Keys){Check ('literal message '+$reason) ((Get-ConstructSourceMessage -Reason $reason -Commit $commit -Ref dev) -ceq $messages[$reason])}
Write-Host "$passed passed"
# A peer that accepts TCP but never answers TLS must not consume an unbounded preflight.
. (Join-Path $root 'lib/AgentVm.Remote.ps1')
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start()
try {
    $watch=[Diagnostics.Stopwatch]::StartNew();$threw=$false
    try { Get-ConstructRemoteFingerprint -BaseUrl ('https://127.0.0.1:'+$listener.LocalEndpoint.Port) -TimeoutMs 200 | Out-Null } catch {$threw=$true}
    Check 'silent TLS peer preflight is bounded' ($threw -and $watch.Elapsed.TotalSeconds -lt 3)
} finally {$listener.Stop()}
Write-Host "$passed passed"

# Overlay identity, planning, packing and transport keep the existing local-path pins above.
$changes = @{ Modified = @('edited'); Added = @('new'); Deleted = @('gone') }
$overlayPlan = Get-ConstructSourceTransportPlan -ServiceManaged $true -FeatureAvailable $true -Commit $commit -TreeState divergent -Divergence 3 -Changes $changes
Check 'overlay plan' ($overlayPlan.Transport -eq 'cache' -and $overlayPlan.Reason -eq 'cache-overlay' -and $overlayPlan.Overlay -eq $changes)
foreach ($mode in @('cache','upload')) {
    $p = Get-ConstructSourceTransportPlan -ServiceManaged $true -FeatureAvailable $true -Commit $commit -TreeState divergent -Divergence 3 -Changes $changes -Mode $mode
    Check ('explicit mode ignores overlay '+$mode) ($null -eq $p.Overlay -and $p.Transport -eq $mode)
}
$largeChanges = @{ Modified = @(1..5001 | ForEach-Object { 'f'+$_ }); Added = @(); Deleted = @() }
$p = Get-ConstructSourceTransportPlan -ServiceManaged $true -FeatureAvailable $true -Commit $commit -TreeState divergent -Divergence 5001 -Changes $largeChanges
Check 'overlay count fallback plan' ($p.Transport -eq 'upload' -and $p.Reason -eq 'local-changes-too-large')
$overlayMessages = @{
    'cache-overlay' = 'This checkout differs from commit aaaaaaa in 3 file(s); the VM fetches commit aaaaaaa from the host cache and only those file(s) are uploaded from this PC.'
    'local-changes-too-large' = 'This checkout differs from commit aaaaaaa in 3 file(s), too many or too large for an overlay; uploading it as before…'
    'overlay-pack-failed:overlay-not-file' = 'Could not pack the 3 differing file(s) (overlay-not-file); uploading the checkout as before.'
    'overlay-pack-failed:IOException' = 'Could not pack the 3 differing file(s) (IOException); uploading the checkout as before.'
    'overlay-pack-failed:overlay-file-unreadable/IOException at lib/x.ps1' = 'Could not pack the 3 differing file(s) (overlay-file-unreadable/IOException at lib/x.ps1); uploading the checkout as before.'
    'overlay-upload-failed' = 'Could not upload the source overlay to the VM; uploading the checkout as before.'
}
foreach ($reason in $overlayMessages.Keys) {
    Check ('literal overlay message '+$reason) ((Get-ConstructSourceMessage -Reason $reason -Commit $commit -Divergence 3) -ceq $overlayMessages[$reason])
}
foreach ($failure in @('','size','pack','pack-io','pack-path','pack-empty','upload','empty-upload','stage','fetch')) {
    $events = [Collections.Generic.List[string]]::new(); $infos = [Collections.Generic.List[string]]::new(); $warnings = [Collections.Generic.List[string]]::new()
    $state = Invoke-ConstructSourceTransport -Phase begin -Plan $overlayPlan -Ensure { @{Outcome='ready';SizeBytes=1024;Sha256=$hash} } -Info {param($s) $infos.Add($s)} -Warn {param($s) $warnings.Add($s)}
    $state = Invoke-ConstructSourceTransport -Phase complete -State $state -PackOverlay {
        param($c) $events.Add('pack-overlay'); Check 'pack receives change set' ($c -eq $changes)
        if ($failure -eq 'size') { $e=[InvalidOperationException]::new('limit');$e.Data['ConstructSourceOverlayTooLarge']=$true;throw $e }
        if ($failure -eq 'pack') { throw 'overlay-not-file' }
        if ($failure -eq 'pack-io') { throw [IO.IOException]::new('do not log secret fixture contents') }
        if ($failure -eq 'pack-path') { $e=[Exception]::new('overlay-file-unreadable'); $e.Data['ConstructSourceOverlayError']='IOException'; $e.Data['ConstructSourceOverlayPath']='lib/x.ps1'; throw $e }
        if ($failure -eq 'pack-empty') { return $null }
        @{Path='patch.zip';Files=2;Deleted=1;SizeBytes=20;CompressedSizeBytes=2048;Sha256=$hash}
    } -UploadOverlay {
        param($o) $events.Add('upload-overlay');Check 'upload receives archive' ($o.Path -eq 'patch.zip')
        if ($failure -eq 'upload') {throw 'upload failed'}
        if ($failure -eq 'empty-upload') {return $null}
        '/tmp/patch.zip'
    } -StageToken { $events.Add('stage-token');$failure -ne 'stage' } -RunGuestFetch {
        param($s) $events.Add('guest-fetch');Check 'fetch receives overlay metadata' ($s.OverlayPath -eq '/tmp/patch.zip' -and $s.OverlayArchive.Sha256 -eq $hash)
        if ($failure -eq 'fetch') {return @{ExitCode=5;Lines=@()}}
        @{ExitCode=0;Lines=@('CONSTRUCT_SOURCE_INSTALLED='+$commit)}
    } -Pack {$events.Add('pack');'full'} -Upload {$events.Add('upload')} -Info {param($s)$infos.Add($s)} -Warn {param($s)$warnings.Add($s)}
    Check ('overlay announced as info '+$failure) ($infos[0] -ceq $overlayMessages['cache-overlay'])
    if (-not $failure) {
        Check 'overlay transport order' (($events -join ',') -ceq 'pack-overlay,upload-overlay,stage-token,guest-fetch')
        Check 'overlay final info' ($infos[$infos.Count-1] -ceq '    Construct source: host cache (commit aaaaaaa, 1 KB) + 3 differing file(s) (2 KB) uploaded from this PC.')
        Check 'overlay success no warning' ($warnings.Count -eq 0)
    } else {
        $expected = @{size='local-changes-too-large';pack='overlay-pack-failed:overlay-not-file';'pack-io'='overlay-pack-failed:IOException';'pack-path'='overlay-pack-failed:overlay-file-unreadable/IOException at lib/x.ps1';'pack-empty'='overlay-pack-failed:empty-overlay-result';upload='overlay-upload-failed';'empty-upload'='overlay-upload-failed';stage='guest-token-staging-failed';fetch='guest-fetch-failed:5'}
        Check ('overlay fallback '+$failure) ($state.Transport -eq 'upload' -and $state.Reason -ceq $expected[$failure] -and ($events -join ',').EndsWith('pack,upload'))
        Check ('overlay fallback warning '+$failure) ($warnings.Count -eq 1 -and $warnings[0] -ceq (Get-ConstructSourceMessage -Reason $expected[$failure] -Commit $commit -Divergence 3))
        if ($failure -in @('pack','pack-io','pack-path','pack-empty','size','upload','empty-upload')) { Check ('failed overlay stops before token '+$failure) (-not $events.Contains('stage-token')) }
    }
}
$tmp=Join-Path ([IO.Path]::GetTempPath()) ('source-overlay-'+[guid]::NewGuid().ToString('N'))
try {
    $tree=Join-Path $tmp 'tree';[IO.Directory]::CreateDirectory((Join-Path $tree '.git'))|Out-Null
    $identity=Get-ConstructSourceIdentity -Root $tree -GitRunner {
        param($r,$argv)
        if ($argv[0] -eq 'rev-parse') {return @{ExitCode=0;Lines=@($commit)}}
        @{ExitCode=0;Lines=@(' M z','M  A','?? projects/app.json','A  added',' D deleted','D  removed','R  old name -> New name')}
    }
    Check 'git changes sorted with preserved case and rename' (($identity.Changes.Modified -join ',') -ceq 'A,z' -and ($identity.Changes.Added -join ',') -ceq 'New name,added,projects/app.json' -and ($identity.Changes.Deleted -join ',') -ceq 'deleted,old name,removed' -and $identity.Divergence -eq 8)
    foreach ($line in @('?? "quoted"','R  old -> "quoted"','bad','R  no arrow','?? nested/','?? ../outside','C  old -> new')) {
        $id=Get-ConstructSourceIdentity -Root $tree -GitRunner {param($r,$argv) if($argv[0] -eq 'rev-parse'){return @{ExitCode=0;Lines=@($commit)}};@{ExitCode=0;Lines=@(' M valid',$line)}}
        Check ('unavailable git changes '+$line) ($null -eq $id.Changes -and $id.Divergence -eq 2 -and $id.TreeState -eq 'divergent')
    }
    $id=Get-ConstructSourceIdentity -Root $tree -GitRunner {param($r,$argv)
        if($argv[0] -eq 'rev-parse'){return @{ExitCode=0;Lines=@($commit)}}
        @{ExitCode=0;Lines=@('D  x','?? x','D  y',' M y')}
    }
    Check 'git on-disk copies win over deletions' ($id.Divergence -eq 2 -and $id.Changes.Deleted.Count -eq 0 -and ($id.Changes.Added -join ',') -ceq 'x' -and ($id.Changes.Modified -join ',') -ceq 'y')
    & git -C $tree init -q
    [IO.File]::WriteAllText((Join-Path $tree 'untrack'),'keep on disk')
    & git -C $tree add untrack
    & git -C $tree -c user.name=test -c user.email=test@example.invalid commit -qm fixture
    & git -C $tree rm --cached --quiet untrack
    $id=Get-ConstructSourceIdentity -Root $tree
    Check 'real git rm cached preserves on-disk file in overlay' ($id.Divergence -eq 1 -and $id.Changes.Deleted.Count -eq 0 -and ($id.Changes.Added -join ',') -ceq 'untrack')
    Remove-Item -LiteralPath (Join-Path $tree 'untrack')
    Remove-Item -LiteralPath (Join-Path $tree '.git') -Recurse -Force
    [IO.File]::WriteAllText((Join-Path $tree '.construct-revision'),$commit)
    [IO.File]::WriteAllText((Join-Path $tree 'edited'),'old')
    [IO.File]::WriteAllText((Join-Path $tree 'gone'),'old')
    $baseZip=Join-Path $tmp 'base.zip';$z=[IO.Compression.ZipFile]::Open($baseZip,[IO.Compression.ZipArchiveMode]::Create)
    try {foreach($f in Get-ChildItem -LiteralPath $tree -File -Force){[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($z,$f.FullName,('repo-main/'+$f.Name))|Out-Null}}finally{$z.Dispose()}
    $manifests=Join-Path $tmp 'manifests';Write-ConstructSourceManifest -Zip $baseZip -Commit $commit -ManifestDir $manifests | Out-Null
    [IO.File]::WriteAllText((Join-Path $tree 'edited'),'replacement')
    [IO.Directory]::CreateDirectory((Join-Path $tree 'sub'))|Out-Null
    [IO.File]::WriteAllText((Join-Path $tree 'sub/new'),'added')
    [IO.File]::WriteAllText((Join-Path $tree 'ignored.local'),'excluded')
    Remove-Item -LiteralPath (Join-Path $tree 'gone')
    $id=Get-ConstructSourceIdentity -Root $tree -ManifestDir $manifests
    Check 'archive changes lists' ($id.Divergence -eq 3 -and ($id.Changes.Modified -join ',') -ceq 'edited' -and ($id.Changes.Added -join ',') -ceq 'sub/new' -and ($id.Changes.Deleted -join ',') -ceq 'gone')
    $zip=Join-Path $tmp 'overlay.zip';$o=New-ConstructSourceOverlay -Root $tree -Changes $id.Changes -Path $zip
    Check 'overlay metadata' ($o.Files -eq 2 -and $o.Deleted -eq 1 -and $o.SizeBytes -eq 21 -and $o.CompressedSizeBytes -eq (Get-Item $zip).Length -and $o.Sha256 -ceq (Get-FileHash $zip).Hash.ToLowerInvariant())
    $z=[IO.Compression.ZipFile]::OpenRead($zip)
    try {
        Check 'overlay entry names' ((@($z.Entries.FullName) -join ',') -ceq 'construct-overlay/edited,construct-overlay/sub/new,construct-overlay.deleted')
        foreach ($pair in @(@('construct-overlay/edited','replacement'),@('construct-overlay/sub/new','added'),@('construct-overlay.deleted',"gone`n"))) {
            $reader=[IO.StreamReader]::new($z.GetEntry($pair[0]).Open());try{$text=$reader.ReadToEnd()}finally{$reader.Dispose()}
            Check ('overlay content '+$pair[0]) ($text -ceq $pair[1])
        }
    } finally {$z.Dispose()}
    Remove-Item $zip
    $o=New-ConstructSourceOverlay -Root $tree -Changes @{Modified=@('edited');Added=@();Deleted=@()} -Path $zip
    $z=[IO.Compression.ZipFile]::OpenRead($zip);try{Check 'no deletion entry when empty' ($null -eq $z.GetEntry('construct-overlay.deleted'))}finally{$z.Dispose()};Remove-Item $zip
    foreach ($bad in @('../escape','/absolute','dir\file','sub//file','sub/../file',"line`nbreak")) {
        $thrown=$false;try{New-ConstructSourceOverlay -Root $tree -Changes @{Modified=@();Added=@();Deleted=@($bad)} -Path $zip|Out-Null}catch{$thrown=$true}
        Check ('overlay refuses path '+$bad) ($thrown -and -not (Test-Path $zip))
    }
    foreach ($c in @($largeChanges,@{Modified=@('missing');Added=@();Deleted=@()},@{Modified=@('sub');Added=@();Deleted=@()},@{Modified=@('edited');Added=@();Deleted=@('edited')},@{Modified=@();Added=@('edited');Deleted=@('edited')})) {
        $thrown=$false;try{New-ConstructSourceOverlay -Root $tree -Changes $c -Path $zip|Out-Null}catch{$thrown=$true}
        Check 'overlay refuses count missing file or directory' ($thrown -and -not (Test-Path $zip))
    }
    # The failure names the file (never its contents) so the console says which one could not be packed.
    foreach ($pair in @(@(@{Modified=@('missing');Added=@();Deleted=@()},'overlay-file-missing','missing'),@(@{Modified=@('sub');Added=@();Deleted=@()},'overlay-not-file','sub'))) {
        $code='';$at='';try{New-ConstructSourceOverlay -Root $tree -Changes $pair[0] -Path $zip|Out-Null}catch{$code=$_.Exception.Message;$at=[string]$_.Exception.Data['ConstructSourceOverlayPath']}
        Check ('overlay failure names the file '+$pair[1]) ($code -ceq $pair[1] -and $at -ceq $pair[2])
    }
    $big=Join-Path $tree 'big';$stream=[IO.File]::Create($big);try{$stream.SetLength(64MB+1)}finally{$stream.Dispose()}
    $thrown=$false;try{New-ConstructSourceOverlay -Root $tree -Changes @{Modified=@('big');Added=@();Deleted=@()} -Path $zip|Out-Null}catch{$thrown=[bool]$_.Exception.Data['ConstructSourceOverlayTooLarge']}
    Check 'overlay size uses typed limit failure' ($thrown -and -not (Test-Path $zip))
    if ($env:OS -ne 'Windows_NT') {
        $linkedRoot=Join-Path $tmp 'linked-root'
        New-Item -ItemType SymbolicLink -Path $linkedRoot -Target $tree | Out-Null
        $o=New-ConstructSourceOverlay -Root $linkedRoot -Changes @{Modified=@('edited');Added=@();Deleted=@()} -Path $zip
        Check 'overlay accepts symlinked checkout root' ($o.Files -eq 1 -and (Test-Path $zip))
        Remove-Item -LiteralPath $zip
        New-Item -ItemType SymbolicLink -Path (Join-Path $tree 'link') -Target (Join-Path $tree 'edited')|Out-Null
        New-Item -ItemType SymbolicLink -Path (Join-Path $tree 'linked-dir') -Target (Join-Path $tree 'sub')|Out-Null
        foreach ($path in @('link','linked-dir/new')) {
            $thrown=$false;try{New-ConstructSourceOverlay -Root $tree -Changes @{Modified=@($path);Added=@();Deleted=@()} -Path $zip|Out-Null}catch{$thrown=$true}
            Check ('overlay refuses reparse '+$path) ($thrown -and -not (Test-Path $zip))
        }
    }
} finally {Remove-Item -LiteralPath $tmp -Recurse -Force}
Write-Host "$passed passed"

# Execute the provisioner's real overlay closures behind SSH/SCP fakes.
$parseErrors=$null;$ast=[Management.Automation.Language.Parser]::ParseInput($provision,[ref]$null,[ref]$parseErrors)
Check 'provision parses with overlay closures' ($parseErrors.Count -eq 0)
$completeAst=$ast.Find({param($n) $n -is [Management.Automation.Language.CommandAst] -and $n.GetCommandName() -eq 'Invoke-ConstructSourceTransport' -and $n.Extent.Text.StartsWith('Invoke-ConstructSourceTransport -Phase complete')},$true)
$closures=@{}
for($i=0;$i -lt $completeAst.CommandElements.Count;$i++) {
    $element=$completeAst.CommandElements[$i]
    if($element -is [Management.Automation.Language.CommandParameterAst] -and $element.ParameterName -in @('PackOverlay','UploadOverlay','RunGuestFetch')) {
        $body=$completeAst.CommandElements[$i+1].ScriptBlock.Extent.Text
        $closures[$element.ParameterName]=[scriptblock]::Create($body.Substring(1,$body.Length-2).Replace('$PSScriptRoot','$root'))
    }
}
Check 'provision wires all overlay callbacks' ($closures.Count -eq 3 -and $provision.Contains('-Changes $sourceIdentity.Changes'))
$script:commands=[Collections.Generic.List[string]]::new();$script:ConnectUser='root'
function Invoke-Ssh {param([switch]$Sudo,$Command) $script:commands.Add($Command)}
function Invoke-Scp {param($LocalPath,$RemotePath) $script:commands.Add('scp '+$RemotePath);if($script:failOverlayScp){throw 'scp fixture failure'}}
$tmp=Join-Path ([IO.Path]::GetTempPath()) ('overlay-wire-'+[guid]::NewGuid().ToString('N'))
try {
    foreach($failure in @($false,$true)) {
        [IO.File]::WriteAllText($tmp,'fixture');$script:failOverlayScp=$failure;$script:commands.Clear();$thrown=$false
        try {$remote=& $closures.UploadOverlay @{Path=$tmp}} catch {$thrown=$true}
        Check ('real upload closure removes local zip '+$failure) (-not (Test-Path $tmp) -and $thrown -eq $failure)
        Check ('real upload closure precreates private file '+$failure) ($script:commands[0].StartsWith('umask 077;') -and $script:commands[0].Contains('chown root') -and $script:commands[1].StartsWith('scp /tmp/construct-source-overlay.'))
        if(-not $failure){Check 'real upload closure returns private guest path' ($remote -match '^/tmp/construct-source-overlay\.[a-f0-9]{32}\.zip$' -and $script:commands[2] -ceq ('chmod 0600 -- '+$remote))}
    }
    function Send-GuestSecret {param($Content,$RemotePath) return $true}
    function Get-ConstructRemoteCertificatePem {param($BaseUrl,$TimeoutMs) return ''}
    function Invoke-SshStream {param([switch]$Sudo,[switch]$PassThru,[switch]$NoThrow,$Command) $script:fetchCommand=$Command;return @{ExitCode=0;Lines=@()}}
    $script:SourceOverlayPath='/tmp/construct-source-overlay.fixture.zip';$script:SourceFetchTokenPath='';$ServiceUrl='https://host.invalid';$InstanceName='vm';$SeedUser='root'
    $wireState=@{Commit=$commit;Sha256=$hash;SizeBytes=100;OverlayPath=$script:SourceOverlayPath;OverlayArchive=@{Sha256=('c'*64);CompressedSizeBytes=321}}
    & $closures.RunGuestFetch $wireState | Out-Null
    Check 'real fetch closure passes overlay metadata' ($script:fetchCommand.Contains("CONSTRUCT_SOURCE_OVERLAY='/tmp/construct-source-overlay.fixture.zip'") -and $script:fetchCommand.Contains("CONSTRUCT_SOURCE_OVERLAY_SIZE='321'") -and $script:fetchCommand.Contains("CONSTRUCT_SOURCE_OVERLAY_SHA256='"+('c'*64)+"'"))
    Check 'real fetch closure cleans overlay' ($script:commands[$script:commands.Count-1].Contains($script:SourceOverlayPath) -and $script:fetchCommand.Contains("rm -r -- $($script:SourceOverlayPath); fi' EXIT"))
    $trapScript=Join-Path ([IO.Path]::GetTempPath()) ('overlay-trap-'+[guid]::NewGuid().ToString('N')+'.sh')
    try {
        $trapBody=[regex]::Match($script:fetchCommand, "trap '([^']+)' EXIT").Groups[1].Value
        $trapBody=$trapBody.Substring($trapBody.IndexOf('; if [ -f ')+2)
        [IO.File]::WriteAllText($trapScript,"trap '$trapBody' EXIT`nexit 0`n")
        $output=@(& bash $trapScript 2>&1)
        Check 'outer trap tolerates already-removed overlay' ($LASTEXITCODE -eq 0 -and $output.Count -eq 0)
    } finally {Remove-Item -LiteralPath $trapScript}

} finally {if(Test-Path $tmp){Remove-Item -LiteralPath $tmp -Force}}
Write-Host "$passed passed"
