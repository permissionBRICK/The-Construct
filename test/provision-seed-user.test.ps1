#Requires -Version 5.1
# Exercise the actual seed-query assignments without running the provisioner.
# On Linux, run their SSH payload through bash against isolated config/user fixtures.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $repoRoot 'Provision-AgentVM.ps1'), [ref]$null, [ref]$null)
$assignments = @{}
foreach ($name in @('seedQuery', 'seedQueryB64', 'guestSeed')) {
    $node = $ast.Find({ param($n)
        $n -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $n.Left.Extent.Text -eq ('$' + $name)
    }, $true)
    if (-not $node) { throw "Missing assignment: $name" }
    $assignments[$name] = $node.Extent.Text
}

$script:wireCommand = ''
function Invoke-Ssh {
    param([string]$Command)
    $script:wireCommand = $Command
    return "construct`n"
}
$SeedUser = 'agent'
foreach ($name in @('seedQuery', 'seedQueryB64', 'guestSeed')) {
    Invoke-Expression $assignments[$name]
}
if ($guestSeed -ne 'construct') { throw 'Guest seed response was not trimmed' }
# PS 5.1 strips double quotes before ssh.exe sees its argument. The new wire
# payload must contain none, and its decoded script must remain byte-for-byte intact.
if ($wireCommand.Contains('"')) { throw 'SSH payload contains vulnerable double quotes' }
if ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($seedQueryB64)) -cne $seedQuery) {
    throw 'Seed query changed during encoding'
}
Write-Host 'PASS: quote-free SSH transport and exact query round trip'

if ($env:OS -eq 'Windows_NT') { exit 0 }
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('construct-seed-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $config = Join-Path $fixture 'config.env'
    $cases = @(
        @{ Value = 'SSH_USER=construct'; Users = 'construct'; Expected = 'construct' },
        @{ Value = "SSH_USER='custom-seed'"; Users = 'custom-seed construct'; Expected = 'custom-seed' },
        @{ Value = 'SSH_USER="custom-seed"'; Users = 'custom-seed construct'; Expected = 'custom-seed' },
        @{ Value = 'SSH_USER=missing'; Users = 'agent construct'; Expected = 'agent' },
        @{ Value = ''; Users = 'construct'; Expected = 'construct' },
        @{ Value = 'SSH_USER=missing'; Users = 'construct'; Expected = 'construct' }
    )
    foreach ($case in $cases) {
        [IO.File]::WriteAllText($config, $case.Value + "`n")
        # Keep the production query; substitute only its config path and account lookup.
        $seedQuery = $seedQuery.Replace('/etc/construct/config.env', $config)
        $seedQueryB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($seedQuery))
        Invoke-Expression $assignments['guestSeed']
        $lookup = 'id() { case " ' + $case.Users + ' " in *" $2 "*) echo 1000;; *) return 1;; esac; }; export -f id; '
        # Reproduce Invoke-Ssh's root login-shell wrapper and PS 5.1 quote removal.
        $wrapped = "bash -lc '" + $wireCommand.Replace("'", "'\''") + "'"
        $result = & bash -c ($lookup + $wrapped.Replace('"', ''))
        if ($LASTEXITCODE -ne 0 -or "$result" -cne $case.Expected) {
            throw "Seed lookup failed for '$($case.Value)': $result (exit $LASTEXITCODE)"
        }
        Write-Host "PASS: '$($case.Value)' selects $result"
    }
} finally {
    Remove-Item -LiteralPath $fixture -Recurse -Force
}
