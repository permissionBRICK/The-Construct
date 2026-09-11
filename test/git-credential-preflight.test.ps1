#Requires -Version 5.1
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../lib/AgentVm.Common.ps1')
$script:count = 0
function Assert($condition, $name) {
    if (-not $condition) { throw "FAIL: $name" }
    $script:count++; Write-Host "PASS: $name"
}
function Write-Note($message) { $script:notes.Add([string]$message) }
function Write-Ok($message) { $script:notes.Add([string]$message) }
function Show-TuiScreen { param($Title, $Body) $script:screens++ }
function Reset-Fixture {
    $script:notes = New-Object 'System.Collections.Generic.List[string]'
    $script:screens = 0; $script:reads = 0
    $script:calls = New-Object 'System.Collections.Generic.List[object]'
    $script:answers = New-Object 'System.Collections.Generic.Queue[object]'
    $script:ConstructGitCredentialSession = $null
}
$reader = { param($key) $script:reads++; $script:answers.Dequeue() }
$script:verifyRunner = {
    param($arguments, $credential, $timeout)
    $script:calls.Add(@{ Arguments = $arguments; Credential = $credential; Timeout = $timeout })
    $public = ($arguments[-1] -like '*public*')
    $accepted = $credential -and $credential.Token -ceq 'fixture-pat:/@ with space'
    $message = 'remote: HTTP Basic: Access denied'
    if ($credential -and -not $accepted) { $message += " $($credential.Token) $([uri]::EscapeDataString($credential.Token))" }
    $code = 128
    if ($public -or $accepted) { $code = 0 }
    @{ ExitCode = $code; Stdout = ''; Stderr = $message }
}
$good = @{ User = 'fixture-user'; Token = 'fixture-pat:/@ with space' }
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('construct-preflight-test-' + [guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory $tmp
try {
    [IO.File]::WriteAllText((Join-Path $tmp 'private.json'), '{"repos":[{"url":"https://git.example/private.git"}]}')
    Reset-Fixture
    $script:answers.Enqueue($good)
    $session = New-ConstructGitCredentialSession -GitRunner $script:verifyRunner -ReadCredential $reader
    $b64 = Resolve-GitCloneCredential -ProjectsDir $tmp -Names private -Session $session
    $decoded = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64))
    Assert ($script:reads -eq 1 -and $session.Verified.Count -eq 1) 'pass on first credential'
    Assert ($decoded -ceq 'https://fixture-user:fixture-pat%3A%2F%40%20with%20space@git.example') 'existing guest credential-store wire format'
    Assert ($script:calls.Count -eq 2 -and -not $script:calls[0].Credential) 'anonymous check precedes exact credential verification'
    Assert (($script:calls[1].Arguments -join ' ') -notlike '*fixture-pat*') 'secret absent from git argv'
    Assert ($script:calls[1].Arguments[1] -ceq 'credential.helper=') 'system helper reset is first git configuration'
    $null = Resolve-GitCloneCredential -ProjectsDir $tmp -Names private -Session $session
    Assert ($script:reads -eq 1 -and $script:calls.Count -eq 2) 'same session avoids duplicate prompt and probe'

    Reset-Fixture
    $script:answers.Enqueue(@{ User = 'fixture-user'; Token = 'wrong-secret' })
    $script:answers.Enqueue($good)
    $session = New-ConstructGitCredentialSession -GitRunner $script:verifyRunner -ReadCredential $reader
    $output = Resolve-GitCloneCredential -ProjectsDir $tmp -Names private -Session $session 6>&1
    $text = $output -join "`n"
    Assert ($script:reads -eq 2 -and $session.Verified.Count -eq 1) 'wrong password then PAT retries immediately'
    Assert ($text -like '*HTTP Basic: Access denied*' -and $text -notlike '*wrong-secret*') 'git stderr surfaced with secret removed'
    Assert (($script:notes -join ' ') -like '*Possibly a wrong password. GitLab and GitHub with two-factor or SSO require a personal access token instead of the password.*') 'two-factor and SSO hint'
    Assert ($script:screens -eq 1) 'one credential screen across retry'

    Reset-Fixture
    $script:answers.Enqueue($null)
    $session = New-ConstructGitCredentialSession -GitRunner $script:verifyRunner -ReadCredential $reader
    $b64 = Resolve-GitCloneCredential -ProjectsDir $tmp -Names private -Session $session
    Assert (-not $b64 -and $session.Verified.Count -eq 0) 'skip produces no credential'
    Assert ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:CONSTRUCT_GIT_SKIP_HOSTS_B64)) -eq 'https://git.example') 'skip travels separately to checkout'
    Assert (($script:notes -join ' ') -like '*Skipping https://git.example and its clones*') 'skip clearly reported'

    Reset-Fixture
    $session = New-ConstructGitCredentialSession -GitRunner $script:verifyRunner -ReadCredential $reader
    Resolve-ConstructGitUrls -Urls @('https://git.example/public.git', 'git@git.example:ssh.git') -Session $session
    Assert ($script:reads -eq 0 -and $script:screens -eq 0 -and $script:calls.Count -eq 1) 'public and SSH repos never trigger credential prompt'

    foreach ($mode in @('reinstall', 'reprovision', 'add-config')) {
        Reset-Fixture
        $session = New-ConstructGitCredentialSession -NoPrompt -ExistingInstall -GitRunner $script:verifyRunner -ReadCredential $reader
        Resolve-ConstructGitUrls -Urls @('https://git.example/public.git') -Session $session
        Assert ($script:notes.Count -eq 0) "$mode public access has no warning"
        Resolve-ConstructGitUrls -Urls @('https://git.example/private.git') -Session $session
        Assert ($script:reads -eq 0 -and $script:screens -eq 0) "$mode does not prompt on refusal"
        Assert (($script:notes -join ' ') -like "*VM's stored git credentials*panel reports*") "$mode explains stored credentials and panel reporting"
    }

    Reset-Fixture
    $badB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('https://fixture-user:wrong-secret@git.example'))
    $session = New-ConstructGitCredentialSession -CredentialsB64 $badB64 -GitRunner $script:verifyRunner -ReadCredential $reader
    $errorText = ''
    try { $null = Resolve-GitCloneCredential -ProjectsDir $tmp -Names private -Session $session } catch { $errorText = $_.Exception.Message }
    Assert ($errorText -like '*Git credential rejected*HTTP Basic: Access denied*' -and $errorText -notlike '*wrong-secret*') 'unattended credential failure is clear and redacted'
    Assert ($script:reads -eq 0 -and $session.Verified.Count -eq 0) 'unattended failure never prompts or forwards credential'
    Reset-Fixture
    $session = New-ConstructGitCredentialSession -CredentialsB64 $badB64 -GitRunner $script:verifyRunner -ReadCredential $reader
    Resolve-ConstructGitUrls -Urls @('https://git.example/public.git') -Session $session
    Assert ($session.Verified.Count -eq 0 -and $script:calls.Count -eq 1) 'anonymous public access cannot validate or forward an unused supplied token'
    $malformed = ''
    try { $null = New-ConstructGitCredentialSession -CredentialsB64 'invalid' } catch { $malformed = $_.Exception.Message }
    Assert ($malformed -like 'Invalid -GitCloneCredentialsB64:*') 'invalid handoff rejected without echoing payload'

    Reset-Fixture
    $script:answers.Enqueue($good)
    $session = New-ConstructGitCredentialSession -GitRunner $script:verifyRunner -ReadCredential $reader
    Resolve-ConstructGitUrls -Urls @('https://git.example/config.git') -Session $session
    Resolve-ConstructGitUrls -Urls @('https://git.example/project.git') -Session $session
    Assert ($script:reads -eq 1 -and $session.Required['https://git.example'].Count -eq 2) 'config credential reused for imported project on same host'
    Assert ($script:calls.Count -eq 5) 'new project verification also rechecks earlier private repos'
    Resolve-ConstructGitUrls -Urls @('https://second.example/project.git') -Session $session
    Assert ($session.Verified.Count -eq 2 -and $script:screens -eq 1 -and $script:reads -eq 1) 'multiple hosts reuse credential in one session'

    Reset-Fixture
    $script:answers.Enqueue($good)
    $session = New-ConstructGitCredentialSession -GitRunner $script:verifyRunner -ReadCredential $reader
    Resolve-ConstructGitUrls -Urls @('https://old-user@git.example/config.git') -Session $session
    Assert ($script:calls[1].Arguments[-1] -eq 'https://git.example/config.git') 'embedded username cannot override entered credential'

    Reset-Fixture
    $script:answers.Enqueue($good)
    $allowed = Test-ConstructGitCredentialPromptAllowed -Action 'add-config' -ExistingInstall $false -InputRedirected $false
    $session = New-ConstructGitCredentialSession -NoPrompt:(-not $allowed) -GitRunner $script:verifyRunner -ReadCredential $reader
    Resolve-ConstructGitUrls -Urls @('https://git.example/config.git') -Session $session
    Assert ($allowed -and $script:reads -eq 1 -and $session.Verified.Count -eq 1) 'add-config without a VM is an initial install and prompts'
    foreach ($action in @('reprovision', 'reinstall', 'redownload')) {
        Assert (-not (Test-ConstructGitCredentialPromptAllowed -Action $action -InputRedirected $false)) "$action never permits credential prompt"
    }
    Assert (-not (Test-ConstructGitCredentialPromptAllowed -Action add-config -ExistingInstall $true)) 'add-config with an existing VM never prompts'
    Assert (-not (Test-ConstructGitCredentialPromptAllowed -BackupHasCredentials $true)) 'backup credentials suppress initial prompt'
    Assert (-not (Test-ConstructGitCredentialPromptAllowed -InputRedirected $true)) 'redirected stdin suppresses initial prompt'
    Reset-Fixture
    $session = New-ConstructGitCredentialSession -NoPrompt -GitRunner $script:verifyRunner -ReadCredential $reader
    Resolve-ConstructGitUrls -Urls @('https://git.example/private.git') -Session $session
    Assert (($script:notes -join ' ') -like '*No credential was supplied*unattended initial install*') 'unattended initial install does not claim VM credentials exist'

    Reset-Fixture
    $script:answers.Enqueue($good)
    $session = New-ConstructGitCredentialSession -GitRunner $script:verifyRunner -ReadCredential $reader
    Resolve-ConstructGitUrls -Urls @('https://git.example/config.git') -Session $session
    $session.GitRunner = { param($arguments, $credential, $timeout) @{ ExitCode = 128; Stdout = ''; Stderr = 'remote: access refused' } }
    $script:answers.Enqueue($null)
    $reuseOutput = Resolve-ConstructGitUrls -Urls @('https://another.example/project.git') -Session $session 6>&1
    Assert (($reuseOutput -join ' ') -like '*Previous credential not accepted for https://another.example*') 'reuse failure uses neutral wording'
    Assert (($script:notes -join ' ') -notlike '*Possibly a wrong password*') 'reuse failure does not blame a new password entry'
    Assert ($session.Verified.Contains('https://git.example')) 'skip of a newly discovered host preserves prior host success'

    # One entry is tried across all unresolved hosts; successes are final.
    foreach ($skipSecond in @($false, $true)) {
        Reset-Fixture
        $first = @{ User = 'first-user'; Token = 'first-fixture' }
        $second = @{ User = 'second-user'; Token = 'second-fixture' }
        $script:answers.Enqueue($first)
        if ($skipSecond) { $script:answers.Enqueue($null) } else { $script:answers.Enqueue($second) }
        $multiRunner = {
            param($arguments, $credential, $timeout)
            $script:calls.Add(@{ Arguments = $arguments; Credential = $credential })
            $valid = $credential -and (($arguments[-1] -like '*first.example*' -and $credential.Token -eq 'first-fixture') -or
                ($arguments[-1] -like '*second.example*' -and $credential.Token -eq 'second-fixture'))
            $code = 128; if ($valid) { $code = 0 }
            @{ ExitCode = $code; Stdout = ''; Stderr = 'remote: account refused' }
        }
        [IO.File]::WriteAllText((Join-Path $tmp 'multi.json'), '{"repos":[{"url":"https://first.example/r.git"},{"url":"https://second.example/r.git"}]}')
        $session = New-ConstructGitCredentialSession -GitRunner $multiRunner -ReadCredential $reader
        $result = Resolve-GitCloneCredential -ProjectsDir $tmp -Names multi -Session $session 6>&1
        $b64 = [string]$result[-1]
        $lines = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($b64)) -split "`n"
        Assert ($script:reads -eq 2 -and $script:screens -eq 1) "partial host success only prompts for remainder (skip=$skipSecond)"
        Assert (@($script:calls | Where-Object { $_.Credential -and $_.Arguments[-1] -like '*first.example*' }).Count -eq 1) "successful first host is never retried (skip=$skipSecond)"
        Assert ($lines[0] -eq 'https://first-user:first-fixture@first.example') "first host retains its own credential (skip=$skipSecond)"
        if ($skipSecond) {
            Assert ($lines.Count -eq 1 -and $session.Skipped.ContainsKey('https://second.example')) 'skipping second host preserves first verified handoff'
        } else {
            Assert ($lines.Count -eq 2 -and $lines[1] -eq 'https://second-user:second-fixture@second.example') 'second host receives different verified credential'
        }
        Assert (($script:notes -join ' ') -like '*Hosts still needing a credential: https://second.example*') 'remaining hosts shown before retry or skip'
    }

    # Clone runner sees only safe args and creates a minimal fixture checkout.
    Reset-Fixture
    $script:answers.Enqueue($good)
    $cloneRunner = {
        param($arguments, $credential, $timeout)
        if ($arguments -contains 'clone') {
            $script:calls.Add(@{ Arguments = $arguments; Credential = $credential; Timeout = $timeout })
            $null = New-Item -ItemType Directory -Path (Join-Path $arguments[-1] '.git') -Force
            return @{ ExitCode = 0; Stdout = ''; Stderr = '' }
        }
        & $script:verifyRunner $arguments $credential $timeout
    }
    $script:ConstructGitCredentialSession = New-ConstructGitCredentialSession -GitRunner $cloneRunner -ReadCredential $reader
    $oldLocal = $env:LOCALAPPDATA; $env:LOCALAPPDATA = $tmp
    $clone = Update-ConstructStagingClone -SourceRepo 'https://git.example/config.git'
    Assert (Test-Path -LiteralPath (Join-Path $clone '.git')) 'config clone completes after verification'
    $last = $script:calls[$script:calls.Count - 1]
    Assert ($last.Credential.Token -ceq $good.Token -and $last.Arguments -contains 'clone') 'PC clone uses same verified credential'
    Assert (($last.Arguments -join ' ') -notlike '*fixture-pat*') 'config clone argv contains no token'
    $script:ConstructGitCredentialSession.GitRunner = {
        param($arguments, $credential, $timeout)
        @{ ExitCode = 128; Stdout = ''; Stderr = "fatal: fixture fetch refused $($credential.Token)" }
    }
    $errorText = ''
    try { $null = Update-ConstructStagingClone -SourceRepo 'https://git.example/config.git' } catch { $errorText = $_.Exception.Message }
    Assert ($errorText -like '*fixture fetch refused*' -and $errorText -notlike '*fixture-pat*') 'config fetch stderr is thrown and redacted'
    $env:LOCALAPPDATA = $oldLocal

    $script:ConstructGitCredentialSession = $null
    $native = Invoke-ConstructCredentialGit -Arguments @('--version')
    Assert ($native.ExitCode -eq 0 -and $native.Stdout -like 'git version*') 'native runner works with isolated process environment'
    $native = Invoke-ConstructCredentialGit -Arguments @('ls-remote', '--', (Join-Path $tmp 'missing-repository'))
    Assert ($native.ExitCode -ne 0 -and $native.Stderr -like '*does not appear to be a git repository*') 'native stderr captured independently of stdout'

    # Execute the provisioner's explicit-credential block, without provisioning
    # a VM. This also covers Create-AgentVM's direct unattended handoff.
    Reset-Fixture
    $provisioner = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../Provision-AgentVM.ps1'))
    $blockStart = $provisioner.IndexOf('$cloneSkipHostsB64 =')
    $blockEnd = $provisioner.IndexOf('if (-not $cloneCredB64 -and $RestoreDir)', $blockStart)
    $block = [scriptblock]::Create($provisioner.Substring($blockStart, $blockEnd - $blockStart))
    $originalNewSession = ${function:New-ConstructGitCredentialSession}
    $originalProjectsDir = ${function:Get-ConstructConfigProjectsDir}
    try {
        function New-ConstructGitCredentialSession {
            param([switch]$NoPrompt, [string]$CredentialsB64)
            & $originalNewSession -NoPrompt:$NoPrompt -CredentialsB64 $CredentialsB64 -GitRunner $script:verifyRunner -ReadCredential $reader
        }
        function Get-ConstructConfigProjectsDir { param($ScriptsDir) $tmp }
        $GitCloneCredentialsB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('https://fixture-user:fixture-pat%3A%2F%40%20with%20space@git.example'))
        $Projects = 'private'
        $env:CONSTRUCT_GIT_SKIP_HOSTS_B64 = ''
        . $block
        Assert ($cloneCredB64 -eq $GitCloneCredentialsB64 -and $script:reads -eq 0) 'direct unattended provision verifies and forwards credential'
        $script:ConstructGitCredentialSession = & $originalNewSession -GitRunner $script:verifyRunner -ReadCredential $reader
        . $block
        Assert ($script:ConstructGitCredentialSession.NoPrompt -and $script:reads -eq 0) 'reused installer session cannot prompt for profiles discovered during sync'
        $script:ConstructGitCredentialSession = $null
        $GitCloneCredentialsB64 = $badB64
        $errorText = ''
        try { . $block } catch { $errorText = $_.Exception.Message }
        Assert ($errorText -like '*Git credential rejected*' -and $script:reads -eq 0) 'direct unattended provision fails without prompting'
    } finally {
        ${function:New-ConstructGitCredentialSession} = $originalNewSession
        ${function:Get-ConstructConfigProjectsDir} = $originalProjectsDir
    }

    # Check actual installer mode wiring (the installer itself needs Windows).
    $installer = [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../Auto-Install.ps1'))
    Assert ($installer -notmatch 'if \(\$GitCloneCredentialsB64\) \{ \$GitCloneCredentialsB64 \}') 'no unverified unattended bypass in installer'
    Assert ($installer -match 'Test-ConstructGitCredentialPromptAllowed -ExistingInstall:') 'installer uses the tested initial-install policy'
    Assert ($installer -match '(?s)Start-ConstructGitPreflight\s+\$chosenProjects = \$Projects\s+if .*Select-Projects') 'remote config import precedes project selection'
    Assert ($installer -match '(?s)# Project profiles to provision.\s+Start-ConstructGitPreflight\s+if .*?Select-Projects') 'local config import precedes project selection'
    Write-Host "$script:count passed"
} finally {
    $script:ConstructGitCredentialSession = $null
    $env:CONSTRUCT_GIT_SKIP_HOSTS_B64 = ''
    Remove-Item -LiteralPath $tmp -Recurse -Force
}
