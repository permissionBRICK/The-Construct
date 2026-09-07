#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$script:passed = 0
function Check($name, $value) { if (-not $value) { throw "FAIL: $name" }; $script:passed++; Write-Host "PASS: $name" }
foreach ($name in @('Provision-AgentVM.ps1','Auto-Install.ps1','lib/AgentVm.Remote.ps1')) {
    $errors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $root $name),[ref]$null,[ref]$errors)
    Check "parse $name" ($errors.Count -eq 0)
}
. (Join-Path $root 'lib/AgentVm.Remote.ps1')
$script:calls = @()
$script:failRequest = $false
function Invoke-WebRequest {
    param($Uri,$Method,$Headers,$Body,$TimeoutSec,$UseBasicParsing,$ContentType,$ErrorAction)
    $script:calls += @{ Uri=$Uri; Method=$Method; Body=$Body; TimeoutSec=$TimeoutSec }
    if ($script:failRequest) { throw 'transport failure sentinel' }
    return @{ StatusCode=200; Content='{"vmToken":"fixture-credential","kind":"primary"}' }
}
$auth = New-ConstructApiAuth -Mode token -Token 'fixture-user-credential'
Check 'local report makes no call' (-not (Send-ConstructGuestReport -BaseUrl '' -VmName parent -Event provisioned -Auth $auth))
Check 'no network in local mode' ($script:calls.Count -eq 0)
Check 'successful report' (Send-ConstructGuestReport -BaseUrl 'http://127.0.0.1:7999' -VmName parent -Event provisioned -ConstructCommit abcdef0 -Auth $auth)
$body = $script:calls[-1].Body | ConvertFrom-Json
Check 'report route' ($script:calls[-1].Uri -eq 'http://127.0.0.1:7999/api/v1/vms/parent/guest-report')
Check 'report provenance and commit' ($body.event -eq 'provisioned' -and $body.reporter -eq 'Provision-AgentVM.ps1' -and $body.constructCommit -eq 'abcdef0')
Check 'bounded report request' ($script:calls[-1].TimeoutSec -eq 5)
Check 'reinstall event' (Send-ConstructGuestReport -BaseUrl 'http://127.0.0.1:7999' -VmName parent -Event reinstalled -Auth $auth)
Check 'reinstall stays separate' (($script:calls[-1].Body | ConvertFrom-Json).event -eq 'reinstalled')
Check 'attempt report' (Send-ConstructGuestReport -BaseUrl 'http://127.0.0.1:7999' -VmName parent -Event attempt -Outcome failed -ConstructCommit abcdef0 -Auth $auth)
$body = $script:calls[-1].Body | ConvertFrom-Json
Check 'attempt carries no success commit' ($body.outcome -eq 'failed' -and -not $body.PSObject.Properties['constructCommit'])
$script:failRequest = $true
Check 'transport error is advisory' (-not (Send-ConstructGuestReport -BaseUrl 'http://127.0.0.1:7999' -VmName parent -Event provisioned -Auth $auth))
$script:failRequest = $false
$result = Request-ConstructVmTokenRotation -BaseUrl 'http://127.0.0.1:7999' -VmName parent -Auth $auth
Check 'rotation result is returned once to caller' ($result.kind -eq 'primary' -and $result.vmToken.Length -gt 0)
Check 'rotation route and default kind' ($script:calls[-1].Uri -like '*/vms/parent/token' -and ($script:calls[-1].Body | ConvertFrom-Json).kind -eq 'primary')
$script:failRequest = $true
$rotationError = ''
try { $null = Request-ConstructVmTokenRotation -BaseUrl 'http://127.0.0.1:7999' -VmName parent -Auth $auth } catch { $rotationError = $_.Exception.Message }
Check 'rotation failure omits transport details' ($rotationError -eq 'The host service could not rotate this VM credential.')
$source = [IO.File]::ReadAllText((Join-Path $root 'Provision-AgentVM.ps1'))
Check 'rotation keeps stdin-only delivery' ($source.Contains('Request-ConstructVmTokenRotation') -and $source.Contains('Send-GuestSecret -Content $VmTokenB64'))
Check 'clean guest result gates success reporting' ($source.Contains('$provisionStream.ExitCode -eq 0 -and $script:ProvisionResult.ErrorCount -eq 0'))
Write-Host "$script:passed passed, 0 failed"
