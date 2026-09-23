#Requires -Version 5.1
# Run the provisioner's real config-restore block without a VM. The remote
# commands execute through bash against a fixture root, so a failed restore's
# retained archive is checked against what an actual reboot does: the image
# empties /tmp on every boot.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'Provision-AgentVM.ps1'), [ref]$null, [ref]$null)
$restoreBranch = $ast.Find({ param($n)
    $n -is [System.Management.Automation.Language.IfStatementAst] -and
    $n.Clauses[0].Item1.Extent.Text -eq '$RestoreDir' -and
    $n.Extent.Text.Contains('Restoring saved agent config onto the VM')
}, $true)
if (-not $restoreBranch) { throw 'Missing production restore branch.' }
$block = [scriptblock]::Create($restoreBranch.Extent.Text)

$fixture = Join-Path ([IO.Path]::GetTempPath()) ('construct-restore-test-' + [guid]::NewGuid().ToString('N'))
$vmRoot = Join-Path $fixture 'vm'
$RestoreDir = Join-Path $fixture 'backup'
$null = New-Item -ItemType Directory -Path (Join-Path $vmRoot 'tmp'), (Join-Path $vmRoot 'var/lib'), $RestoreDir
[IO.File]::WriteAllText((Join-Path $RestoreDir 'backup.tar.gz'), 'fixture archive')
$retained = Join-Path $vmRoot 'var/lib/construct/construct-config-restore.failed.tar.gz'

function Resolve-VmPath([string]$Text) { $Text.Replace('/tmp/', "$vmRoot/tmp/").Replace('/var/lib/', "$vmRoot/var/lib/") }
function Invoke-Ssh {
    param([switch]$Sudo, [string]$Command)
    & bash -c (Resolve-VmPath $Command)
    if ($LASTEXITCODE -ne 0) { throw "ssh command failed: $Command" }
}
function Invoke-Scp {
    param([string]$LocalPath, [string]$RemotePath)
    Copy-Item -LiteralPath $LocalPath -Destination (Resolve-VmPath $RemotePath)
}
function Invoke-SshStream {
    param([switch]$Sudo, [switch]$PassThru, [switch]$NoThrow, [string]$Command)
    @{ ExitCode = $script:restoreExit }
}
function Write-Step($message) { }
function Write-Ok($message) { }
$constructVersion = 'fixture'

try {
    $script:restoreExit = 7
    $errorText = ''
    try { . $block } catch { $errorText = $_.Exception.Message }
    if ($errorText -notlike '*retained archive: /var/lib/construct/construct-config-restore.failed.tar.gz*') {
        throw "Failure message does not name the retained archive: $errorText"
    }
    Write-Host 'PASS: a failed restore names the retained archive'

    # A reboot between the failure and the retry.
    Get-ChildItem -LiteralPath (Join-Path $vmRoot 'tmp') -Force | Remove-Item -Recurse -Force
    if (-not (Test-Path -LiteralPath $retained)) { throw 'Retained archive did not survive a reboot.' }
    if ([IO.File]::ReadAllText($retained) -cne 'fixture archive') { throw 'Retained archive is not the uploaded one.' }
    if (-not $IsWindows -and ((& stat -c '%a' $retained) -ne '600')) { throw 'Retained archive is not mode 600.' }
    Write-Host 'PASS: the retained archive survives a reboot, mode 600'

    $script:restoreExit = 0
    . $block
    if (Test-Path -LiteralPath $retained) { throw 'A successful restore left the retained archive behind.' }
    if (Test-Path -LiteralPath (Join-Path $vmRoot 'tmp/construct-config-restore.tar.gz')) { throw 'A successful restore left the upload behind.' }
    Write-Host 'PASS: the next successful restore removes the retained archive'
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
