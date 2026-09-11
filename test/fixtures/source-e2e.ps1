$ErrorActionPreference = 'Stop'
. (Join-Path $env:ROOT 'lib/AgentVm.Remote.ps1')
$auth=New-ConstructApiAuth -Mode token -Token $env:TOKEN
$base=$env:BASE;$commit=$env:SOURCE_COMMIT
function Call($method,$path,$body){Invoke-ConstructApi -BaseUrl $base -Method $method -Path $path -Body $body -Auth $auth}
function Must($condition,$name){if(-not $condition){throw "Source e2e: $name"};Write-Output "PASS $name"}
if($env:SOURCE_PHASE -eq 'ensure'){
    foreach($name in @('source-vm','source-other')){
        $accepted=Call POST /vms @{name=$name;cpu=1;ramGb=1;diskGb=8}
        $created=Wait-ConstructJob -BaseUrl $base -JobId $accepted.jobId -Auth $auth -PollSeconds 0.1 -TimeoutSeconds 30 -OnProgress {}
        [IO.File]::WriteAllText((Join-Path $env:SOURCE_DIR ($name+'.token')),$created.vmToken)
    }
    Must (Test-ConstructApiFeature -BaseUrl $base -Auth $auth) 'feature advertised'
    $first=Request-ConstructSourceEnsure -BaseUrl $base -VmName source-vm -Commit $commit -OperationKey source-e2e-first -Auth $auth
    Must ($first.Outcome -eq 'downloading') 'first ensure queued'
    $job=Wait-ConstructSourceJob -BaseUrl $base -JobId $first.JobId -Auth $auth -Deadline ([datetime]::UtcNow.AddSeconds(30)) -PollSeconds 0.1
    Must ($job.State -eq 'succeeded') ('source job succeeded: '+$job.Code)
    $second=Request-ConstructSourceEnsure -BaseUrl $base -VmName source-vm -Commit $commit -OperationKey source-e2e-second -Auth $auth
    Must ($second.Outcome -eq 'ready') 'new key cache hit'
    $again=Request-ConstructSourceEnsure -BaseUrl $base -VmName source-vm -Commit $commit -OperationKey source-e2e-first -Auth $auth
    Must ($again.Outcome -eq 'downloading' -and $again.JobId -eq $first.JobId -and $again.Replayed) 'queued replay remains parseable'
    $second|ConvertTo-Json|Set-Content (Join-Path $env:SOURCE_DIR 'source-result.json')
    $listing=Call GET /host/source-cache
    Must ($listing.committedBytes -eq $second.SizeBytes -and $listing.items[0].pinnedBy -contains 'source-vm') 'cache accounting and VM pin'
}else{
    [void](Invoke-ConstructApi -BaseUrl $base -Method DELETE -Path "/host/source-cache/$commit" -Auth $auth -NoThrow)
    $problem=Get-ConstructApiLastProblem
    Must ($problem.Status -eq 409 -and $problem.Code -eq 'source-pinned') 'delete refuses pin'
    $delete=Call DELETE "/host/source-cache/${commit}?force=true"
    Must ([bool]$delete.jobId) 'forced delete accepted'
    [void](Wait-ConstructJob -BaseUrl $base -JobId $delete.jobId -Auth $auth -PollSeconds 0.1 -TimeoutSeconds 30 -OnProgress {})
    $after=Request-ConstructSourceEnsure -BaseUrl $base -VmName source-vm -Commit $commit -OperationKey source-e2e-after-delete -Auth $auth
    Must ($after.Outcome -eq 'downloading') 'deleted source downloads again'
    $job=Wait-ConstructSourceJob -BaseUrl $base -JobId $after.JobId -Auth $auth -Deadline ([datetime]::UtcNow.AddSeconds(30)) -PollSeconds 0.1
    Must ($job.State -eq 'succeeded') 'download after delete succeeds'
}
