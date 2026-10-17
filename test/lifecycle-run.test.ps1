# lib/AgentVm.LifecycleRun.ps1: one lifecycle run at a time on this PC. Acquire, release, a run held
# by another live process, a stale marker of a dead one, nested scripts of the same run, and the real
# Update-T3Code.ps1 / Update-Construct.ps1 holding and refusing it. Nothing contacts a VM.
$ErrorActionPreference = 'Stop'
function Assert($condition, $message) { if (-not $condition) { throw $message } }
$root = Join-Path ([IO.Path]::GetTempPath()) ('lifecycle-run-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
$savedLocalAppData = $env:LOCALAPPDATA
$powershell = (Get-Process -Id $PID).Path
$holder = $null
try {
    $env:LOCALAPPDATA = $root
    Remove-Item Env:CONSTRUCT_LIFECYCLE_RUN -ErrorAction SilentlyContinue
    . (Join-Path $PSScriptRoot '../lib/AgentVm.LifecycleRun.ps1')
    $marker = Join-Path $root 'The-Construct/lifecycle-running.json'
    Assert ((Get-ConstructLifecycleRunPath) -eq $marker) "the marker lives under the Construct data directory: $(Get-ConstructLifecycleRunPath)"
    function Marker { Get-Content -Raw -LiteralPath $marker | ConvertFrom-Json }
    function Write-Marker([int]$OwnerPid, [string]$Token = 'other') {
        New-Item -ItemType Directory -Force (Split-Path $marker) | Out-Null
        @{ pid = $OwnerPid; instance = 'work-vm'; action = 'reprovision'; startedAt = '2026-10-17T10:00:00Z'; token = $Token } | ConvertTo-Json -Compress | Set-Content -LiteralPath $marker
    }
    $holder = Start-Process $powershell -ArgumentList '-NoProfile', '-Command', 'Start-Sleep -Seconds 120' -PassThru
    $dead = Start-Process $powershell -ArgumentList '-NoProfile', '-Command', 'exit 0' -PassThru
    $dead.WaitForExit()

    # Acquire and release.
    Assert ((Enter-ConstructLifecycleRun -Action 'reprovision' -Instance 'work-vm') -eq $true) 'a free host is claimed'
    $m = Marker
    Assert ($m.pid -eq $PID -and $m.action -eq 'reprovision' -and $m.instance -eq 'work-vm' -and $m.startedAt -and $m.token -eq $env:CONSTRUCT_LIFECYCLE_RUN) "the marker names this run: $(Get-Content -Raw $marker)"
    Assert (@(Get-ChildItem (Split-Path $marker) -Filter '*.tmp').Count -eq 0) 'no temporary file is left beside the marker'
    # Nested: a script of the same run (same process, or a child inheriting the token) neither claims nor refuses.
    Assert ((Enter-ConstructLifecycleRun -Action 'reprovision' -Instance 'work-vm') -eq $false) 'a nested script of the same run is not refused'
    $child = & $powershell -NoProfile -Command ". '$PSScriptRoot/../lib/AgentVm.LifecycleRun.ps1'; Enter-ConstructLifecycleRun -Action 'updateT3'"
    Assert ("$child" -eq 'False') "a child process of the run inherits its ownership: $child"
    Assert ((Marker).pid -eq $PID) 'nested scripts leave the owner marker alone'
    Exit-ConstructLifecycleRun
    Assert (-not (Test-Path -LiteralPath $marker) -and -not $env:CONSTRUCT_LIFECYCLE_RUN) 'release removes the marker and the ownership token'

    # Another live run holds the host: refused with the run it names, marker untouched.
    Write-Marker $holder.Id
    $before = Get-Content -Raw -LiteralPath $marker
    $refusal = $null
    try { Enter-ConstructLifecycleRun -Action 'reinstall' -Instance 'other-vm' | Out-Null } catch { $refusal = $_.Exception.Message }
    Assert ($refusal -match "Reprovision of 'work-vm'" -and $refusal -match "process $($holder.Id)") "a second run is refused naming the first: $refusal"
    Assert ((Get-Content -Raw -LiteralPath $marker) -eq $before) 'a refused run leaves the other run''s marker alone'
    Assert ((Read-ConstructLifecycleRun).action -eq 'reprovision') 'a live owner is read as a run'
    # A leaked token of ANOTHER run does not make a script nested.
    $env:CONSTRUCT_LIFECYCLE_RUN = 'mine'
    $refusal = $null
    try { Enter-ConstructLifecycleRun -Action 'reinstall' | Out-Null } catch { $refusal = $_.Exception.Message }
    Assert ($refusal) 'a token that is not the holder''s still refuses'
    # Release by a script that does not own the marker never removes it.
    Exit-ConstructLifecycleRun
    Assert ((Get-Content -Raw -LiteralPath $marker) -eq $before) 'only the owner removes the marker'

    # A dead owner's marker, or one nobody can read, is stale and replaced.
    Write-Marker $dead.Id
    Assert ($null -eq (Read-ConstructLifecycleRun)) 'a dead owner is no run'
    Assert ((Enter-ConstructLifecycleRun -Action 'redownload' -Instance 'work-vm') -eq $true -and (Marker).pid -eq $PID) 'a stale marker is replaced'
    Exit-ConstructLifecycleRun
    Set-Content -LiteralPath $marker -Value '{ not json'
    Assert ((Enter-ConstructLifecycleRun -Action 'updateConstruct') -eq $true -and (Marker).action -eq 'updateConstruct') 'an unreadable marker is replaced'
    Exit-ConstructLifecycleRun

    # Two scripts start together on a stale marker: the check/reclaim/create is serialized, so exactly one
    # claims the run and the other cannot remove the winner's marker. Each pauses inside the locked section.
    Write-Marker $dead.Id
    $contender = Join-Path $root 'contender.ps1'
    Set-Content -LiteralPath $contender -Value @'
param($Lib, $Go, $Out)
. $Lib
$global:ConstructLifecycleRunTestPause = { Start-Sleep -Milliseconds 1500 }
Set-Content -LiteralPath "$Out.ready" -Value ready
while (-not (Test-Path -LiteralPath $Go)) { Start-Sleep -Milliseconds 20 }
try { $owned = Enter-ConstructLifecycleRun -Action 'reinstall' -Instance 'work-vm'; Set-Content -LiteralPath $Out -Value "claimed=$owned" }
catch { Set-Content -LiteralPath $Out -Value "refused: $($_.Exception.Message)" }
Start-Sleep -Seconds 6
'@
    $go = Join-Path $root 'go'
    $lib = Join-Path $PSScriptRoot '../lib/AgentVm.LifecycleRun.ps1'
    $racers = foreach ($n in 1, 2) { Start-Process $powershell -ArgumentList '-NoProfile', '-File', $contender, $lib, $go, (Join-Path $root "racer$n.out") -PassThru }
    while (-not ((Test-Path (Join-Path $root 'racer1.out.ready')) -and (Test-Path (Join-Path $root 'racer2.out.ready')))) { Start-Sleep -Milliseconds 50 }
    Set-Content -LiteralPath $go -Value go
    while (-not ((Test-Path (Join-Path $root 'racer1.out')) -and (Test-Path (Join-Path $root 'racer2.out')))) { Start-Sleep -Milliseconds 50 }
    $outcomes = foreach ($n in 1, 2) { "$(Get-Content (Join-Path $root "racer$n.out"))" }
    $winner = @(0, 1 | Where-Object { $outcomes[$_] -eq 'claimed=True' })
    Assert ($winner.Count -eq 1 -and @($outcomes | Where-Object { $_ -match "^refused: .*Reinstall of 'work-vm'" }).Count -eq 1) "exactly one of two simultaneous scripts claims a stale run: $($outcomes -join ' / ')"
    Assert ((Marker).pid -eq $racers[$winner[0]].Id) 'the winner''s marker survives the loser'
    $racers | ForEach-Object { if (-not $_.HasExited) { $_.Kill() } }
    $racers | ForEach-Object { $_.WaitForExit() }
    Remove-Item -LiteralPath $marker -Force

    # Elevation: the owner hands the run to its relaunched copy, which adopts it by token and releases it.
    Assert ((Enter-ConstructLifecycleRun -Action 'reinstall' -Instance 'work-vm') -eq $true) 'the unelevated copy claims the run'
    $token = $env:CONSTRUCT_LIFECYCLE_RUN
    $adopter = Join-Path $root 'adopter.ps1'
    Set-Content -LiteralPath $adopter -Value @'
param($Lib, $Go, $Out, $Token)
. $Lib
while (-not (Test-Path -LiteralPath $Go)) { Start-Sleep -Milliseconds 20 }
$wrong = try { Enter-ConstructLifecycleRun -Action 'reinstall' -AdoptToken 'not-the-token' } catch { 'refused' }
$owned = Enter-ConstructLifecycleRun -Action 'reinstall' -AdoptToken $Token
$run = Read-ConstructLifecycleRun
Exit-ConstructLifecycleRun
Set-Content -LiteralPath $Out -Value "wrong=$wrong|owned=$owned|holder=$($run.pid)|action=$($run.action)|left=$(Test-Path -LiteralPath (Get-ConstructLifecycleRunPath))"
'@
    $go2 = Join-Path $root 'go2'; $adopted = Join-Path $root 'adopted.out'
    # A relaunch through UAC inherits no environment: the child starts without the owner's token.
    $child = Start-Process $powershell -ArgumentList '-NoProfile', '-Command', "Remove-Item Env:CONSTRUCT_LIFECYCLE_RUN; & '$adopter' '$lib' '$go2' '$adopted' '$token'" -PassThru
    Move-ConstructLifecycleRun -ProcessId $child.Id
    Assert ((Marker).pid -eq $child.Id -and (Marker).token -eq $token -and -not $env:CONSTRUCT_LIFECYCLE_RUN) 'the handed-over marker names the relaunched process and keeps the run'
    Set-Content -LiteralPath $go2 -Value go
    $child.WaitForExit()
    Assert ((Get-Content $adopted) -eq "wrong=refused|owned=True|holder=$($child.Id)|action=reinstall|left=False") "the relaunched copy adopts, owns and releases the run: $(Get-Content $adopted)"

    # Auto-Install.ps1 claims a bound reprovision/reinstall/redownload before anything on this PC changes
    # (the Remote-SSH/panel setup, the installed marker, the Companion installer and the elevation);
    # an export or add-config is not a lifecycle run.
    $installer = Join-Path $root 'installer'
    New-Item -ItemType Directory (Join-Path $installer 'lib') -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot '../Auto-Install.ps1') $installer
    foreach ($lib2 in 'AgentVm.FeatureSet.ps1', 'AgentVm.LifecycleRun.ps1') { Copy-Item (Join-Path $PSScriptRoot "../lib/$lib2") (Join-Path $installer 'lib') }
    Set-Content (Join-Path $installer 'lib/AgentVm.Common.ps1') 'Add-Content -LiteralPath $env:FAKE_LOG -Value common'
    Set-Content (Join-Path $installer 'lib/Construct.Companion.ps1') 'Add-Content -LiteralPath $env:FAKE_LOG -Value companion'
    $env:FAKE_LOG = Join-Path $root 'installer.log'
    Write-Marker $holder.Id
    foreach ($action in 'reinstall', 'redownload', 'reprovision') {
        $output = '' | & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installer 'Auto-Install.ps1') -Action $action -FromPanel 2>&1
        Assert ("$output" -match "Another Construct run is in progress: Reprovision of 'work-vm'" -and "$output" -notmatch 'Windows Principal|Relaunching') "Auto-Install -Action $action is refused before its pre-elevation steps: $output"
        Assert (-not (Test-Path -LiteralPath $env:FAKE_LOG)) "the refused $action changed nothing on this PC"
        Assert ((Marker).pid -eq $holder.Id) "the refused $action leaves the other run alone"
    }
    foreach ($action in 'export', 'add-config') {
        $output = '' | & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installer 'Auto-Install.ps1') -Action $action -FromPanel 2>&1
        Assert ("$output" -notmatch 'Another Construct run') "Auto-Install -Action $action is not a lifecycle run: $output"
    }
    Remove-Item -LiteralPath $marker -Force
    $output = '' | & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $installer 'Auto-Install.ps1') -Action reinstall -FromPanel 2>&1
    Assert (-not (Test-Path -LiteralPath $marker)) "a run that stops early (here: no Windows) releases its claim: $output"

    # Update-T3Code.ps1 holds the run for its provisioner, which runs inside it; a held host refuses it
    # before the provisioner starts.
    $scripts = Join-Path $root 'scripts'
    New-Item -ItemType Directory (Join-Path $scripts 'lib') -Force | Out-Null
    Copy-Item (Join-Path $PSScriptRoot '../Update-T3Code.ps1'), (Join-Path $PSScriptRoot '../Update-Construct.ps1') $scripts
    foreach ($lib in 'AgentVm.LifecycleRun.ps1', 'AgentVm.SettingsMigration.ps1', 'AgentVm.InstanceState.ps1') { Copy-Item (Join-Path $PSScriptRoot "../lib/$lib") (Join-Path $scripts 'lib') }
    Set-Content (Join-Path $scripts 'Provision-AgentVM.ps1') @'
param([string]$Action, [switch]$FromPanel, [switch]$NonInteractive, [string]$InstanceName)
. (Join-Path $PSScriptRoot 'lib/AgentVm.LifecycleRun.ps1')
$owned = Enter-ConstructLifecycleRun -Action 'updateT3' -Instance 'agent-vm'
$run = Read-ConstructLifecycleRun
Set-Content -LiteralPath $env:FAKE_LOG -Value "$Action|owned=$owned|holder=$($run.pid)|action=$($run.action)|instance=$($run.instance)"
'@
    $log = Join-Path $root 'provision.log'
    $env:FAKE_LOG = $log
    $output = & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'Update-T3Code.ps1') -Mode quick 2>&1
    $code = $LASTEXITCODE
    $call = Get-Content -LiteralPath $log -ErrorAction SilentlyContinue
    Assert ($code -eq 0 -and $call -match '^t3update\|owned=False\|holder=\d+\|action=updateT3\|instance=agent-vm$') "Update-T3Code holds the run its provisioner runs in: $code $call $output"
    Assert ($call -notmatch "holder=$PID\|") 'the run belongs to the Update-T3Code process'
    Assert (-not (Test-Path -LiteralPath $marker)) 'Update-T3Code releases the run when it ends'
    Remove-Item $log -ErrorAction SilentlyContinue
    Write-Marker $holder.Id
    $output = & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'Update-T3Code.ps1') -Mode quick 2>&1
    $code = $LASTEXITCODE
    Assert ($code -ne 0 -and -not (Test-Path -LiteralPath $log) -and "$output" -match 'Another Construct run is in progress') "a held host refuses Update-T3Code before the provisioner: $code $output"
    Assert ((Marker).pid -eq $holder.Id) 'the refused update leaves the run alone'

    # Update-Construct.ps1 refuses before downloading anything and reports the failure to its launcher.
    $result = Join-Path $root 'update.result'
    $env:CONSTRUCT_UPDATE_RESULT = $result
    $output = '' | & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $scripts 'Update-Construct.ps1') 2>&1
    $code = $LASTEXITCODE
    Assert ($code -eq 1 -and (Get-Content -LiteralPath $result) -eq 'fail' -and "$output" -match 'Another Construct run is in progress') "a held host refuses the Construct update: $code $output"
    Assert ((Marker).pid -eq $holder.Id) 'the refused Construct update leaves the run alone'
    Write-Host 'lifecycle-run tests passed'
}
finally {
    if ($holder -and -not $holder.HasExited) { $holder.Kill() }
    Remove-Item Env:CONSTRUCT_LIFECYCLE_RUN, Env:FAKE_LOG, Env:CONSTRUCT_UPDATE_RESULT -ErrorAction SilentlyContinue
    $env:LOCALAPPDATA = $savedLocalAppData
    Remove-Item -Recurse -Force $root -ErrorAction SilentlyContinue
}
