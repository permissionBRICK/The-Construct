# Runs a command in the *interactive console session* of the guest and waits
# for it to finish. Needed because UI automation (FlaUI) must run on the
# visible desktop: commands started over SSH land in a non-interactive session
# and cannot drive UI.
#
# Mechanism: one-shot scheduled task registered for the logged-on user with
# "Run only when user is logged on" (= interactive), exit code passed back
# via a marker file.
param(
    [Parameter(Mandatory)] [string]$Command,   # powershell command line to run
    [int]$TimeoutSec = 3600,
    [string]$Name = 'winvm-interactive'
)
$ErrorActionPreference = 'Stop'

$exitFile = "C:\provision\$Name.exit"
$logFile  = "C:\provision\$Name.log"
Remove-Item $exitFile, $logFile -ErrorAction SilentlyContinue

# try/finally: the marker file must be written even if $Command calls `exit`
# (finally blocks run on PowerShell exit).
$wrapped = "`$code = 1; try { & { $Command } *>&1 | Out-File -FilePath '$logFile' -Encoding utf8; " +
           "`$code = `$LASTEXITCODE } finally { Set-Content '$exitFile' `$code }"
$action = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -ExecutionPolicy Bypass -Command `"$wrapped`""
# Interactive token of the currently logged-on user (autologon = Administrator).
$principal = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\Administrator" `
    -LogonType Interactive -RunLevel Highest

Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $Name -Action $action -Principal $principal | Out-Null
Start-ScheduledTask -TaskName $Name

$deadline = (Get-Date).AddSeconds($TimeoutSec)
while (-not (Test-Path $exitFile)) {
    if ((Get-Date) -gt $deadline) {
        Stop-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
        Write-Host "--- captured log (timeout) ---"
        if (Test-Path $logFile) { Get-Content $logFile | Select-Object -Last 100 }
        throw "Interactive command timed out after $TimeoutSec s"
    }
    Start-Sleep -Seconds 5
}
Unregister-ScheduledTask -TaskName $Name -Confirm:$false -ErrorAction SilentlyContinue

Write-Host "--- interactive command log ---"
if (Test-Path $logFile) { Get-Content $logFile }
$code = [int](Get-Content $exitFile | Select-Object -First 1)
Write-Host "--- exit code: $code ---"
exit $code
