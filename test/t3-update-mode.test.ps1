# Update-T3Code.ps1 -Mode: which provisioner run each mode starts, and the exit code the
# Desktop app and the Companion see. Runs the real script beside a fake
# Provision-AgentVM.ps1 that records its -Action; nothing contacts a VM.
$ErrorActionPreference = 'Stop'
function Assert($condition, $message) { if (-not $condition) { throw $message } }
$root = Join-Path ([IO.Path]::GetTempPath()) ('t3-update-mode-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
try {
    Copy-Item (Join-Path $PSScriptRoot '../Update-T3Code.ps1') $root
    Set-Content (Join-Path $root 'Provision-AgentVM.ps1') @'
param([string]$Action, [switch]$FromPanel, [switch]$NonInteractive, [string]$VsCodeServeWeb, [string]$VsCodeTunnel,
      [string]$SmbShare, [string]$ClaudePartialStreaming, [string]$MicPassthrough, [string]$T3Code, [string]$T3CodeChannel,
      [string]$T3CodeLimitResume, [string]$Projects, [string]$InstanceName, [string]$VmHost, [string]$HostAlias,
      [int]$SshPort, [string]$LocalKeyName)
Add-Content -LiteralPath $env:FAKE_LOG -Value $Action
Add-Content -LiteralPath "$env:FAKE_LOG.target" -Value "$InstanceName|$HostAlias"
if ($Action -eq 't3update' -and $env:FAKE_OLD_GUEST -eq '1') { $global:ConstructT3UpdateNeedsReprovision = $true; return }
if ($env:FAKE_OUTCOME -eq 'fail') { $global:ConstructProvisionFailureMessage = 'boom'; $global:ConstructProvisionHadErrors = $true }
if ($env:FAKE_OUTCOME -eq 'errors') { $global:ConstructProvisionHadErrors = $true }
'@
    # A child process, as the Desktop app and the Companion start it: the exit code is the result.
    $powershell = (Get-Process -Id $PID).Path
    $log = Join-Path $root 'calls.log'
    $settings = Join-Path $root '.construct-settings.json'
    function Run([string]$Mode, [string]$Provisioned, [string]$OldGuest = '0', [string]$Outcome = '') {
        Remove-Item $log, "$log.target" -ErrorAction SilentlyContinue
        @{ installedCommit = 'a' * 40; provisionedCommit = $Provisioned; t3code = $true } | ConvertTo-Json | Set-Content $settings
        $env:FAKE_LOG = $log; $env:FAKE_OLD_GUEST = $OldGuest; $env:FAKE_OUTCOME = $Outcome
        $modeArgs = if ($Mode) { @('-Mode', $Mode) } else { @() }
        & $powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'Update-T3Code.ps1') @modeArgs | Out-Null
        [pscustomobject]@{ Exit = $LASTEXITCODE; Calls = (@(Get-Content $log -ErrorAction SilentlyContinue) -join ',')
                           Targets = (@(Get-Content "$log.target" -ErrorAction SilentlyContinue) | Select-Object -Unique) }
    }
    $current = 'A' * 40   # the same commit; the comparison ignores case like the panel's
    $behind = 'b' * 40

    $r = Run '' $current
    Assert ($r.Calls -eq 't3update' -and $r.Exit -eq 0) "auto on a current VM must update T3 Code alone: $($r.Calls)/$($r.Exit)"
    $r = Run '' $behind
    Assert ($r.Calls -eq 'provision') "auto on a VM behind this PC's Construct must reprovision: $($r.Calls)"
    $r = Run '' ''
    Assert ($r.Calls -eq 't3update') "auto without a provisioned commit must not guess a reprovision: $($r.Calls)"
    $r = Run 'quick' $behind
    Assert ($r.Calls -eq 't3update') "quick must not reprovision a VM that is behind: $($r.Calls)"
    $r = Run 'reprovision' $current
    Assert ($r.Calls -eq 'provision') "reprovision must reprovision a current VM: $($r.Calls)"
    $r = Run 'quick' $current '1'
    Assert ($r.Calls -eq 't3update,provision' -and $r.Exit -eq 0) "a VM without the update script must be reprovisioned instead: $($r.Calls)/$($r.Exit)"
    Assert (@($r.Targets).Count -eq 1) "the update and the reprovision instead must target the same VM: $($r.Targets)"
    $r = Run 'quick' $current '0' 'fail'
    Assert ($r.Calls -eq 't3update' -and $r.Exit -eq 1) "a failed T3 update must exit 1: $($r.Calls)/$($r.Exit)"
    $r = Run 'quick' $current '0' 'errors'
    Assert ($r.Exit -eq 3) "a T3 update whose Desktop install failed must exit 3: $($r.Exit)"
    $r = Run 'reprovision' $current '0' 'fail'
    Assert ($r.Exit -eq 1) "a failed reprovision must exit 1: $($r.Exit)"
    Write-Host 'PASS: Update-T3Code.ps1 picks the quick update or a reprovision and reports the outcome'
} finally {
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
}
