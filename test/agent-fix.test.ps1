#Requires -Version 5.1
<#
    Host side of the "Autofix it" choice after a provisioning run with failed VM
    steps (lib/AgentVm.Common.ps1): the fix prompt, the menus, the remote command,
    and where Auto-Install.ps1 / Provision-AgentVM.ps1 offer it. Show-Menu, the
    clipboard, ssh.exe and the console-attached run are stubbed; nothing connects
    anywhere. Run:

        pwsh -NoProfile -File test/agent-fix.test.ps1
#>
$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here "..\lib\AgentVm.Common.ps1")

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}
function FromB64([string]$s) { [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($s)) }

# ── the prompt ───────────────────────────────────────────────────────────────
$errors = @(
    [pscustomobject]@{ Title = "Installing .NET SDK"; ExitCode = 7; LogPath = "/var/log/construct/provision/step-3.log" },
    [pscustomobject]@{ Title = ""; ExitCode = 1; LogPath = "" }
)
$lines = @(Get-ConstructAgentFixPrompt -Errors $errors)
ok "prompt: header, one line per titled step, then the log instruction" ($lines.Count -eq 3)
ok "prompt: names the step, exit code and log" ($lines[1] -eq "- Step 'Installing .NET SDK' failed (exit 7); log: /var/log/construct/provision/step-3.log")
ok "prompt: one log is referenced directly" ($lines[2] -eq "Read the provisioning log at /var/log/construct/provision/step-3.log and diagnose and fix the underlying problem.")
$two = @(Get-ConstructAgentFixPrompt -Errors @(
    [pscustomobject]@{ Title = "A"; ExitCode = 1; LogPath = "/l/a.log" },
    [pscustomobject]@{ Title = "B"; ExitCode = 2; LogPath = "/l/b.log" }))
ok "prompt: several logs are listed" ($two[-1] -eq "Read the provisioning logs at: /l/a.log, /l/b.log and diagnose and fix the underlying problem.")
$none = @(Get-ConstructAgentFixPrompt -Errors @([pscustomobject]@{ Title = "C"; ExitCode = 3; LogPath = "" }))
ok "prompt: no log falls back to the output above" ($none[-1] -eq "Check the provisioning output above and diagnose and fix the underlying problem.")
ok "prompt: host-side messages alone build no prompt" (@(Get-ConstructAgentFixPrompt -Errors @([pscustomobject]@{ Title = ""; ExitCode = 1; LogPath = "" })).Count -eq 0)
ok "prompt: no errors build no prompt" (@(Get-ConstructAgentFixPrompt -Errors @()).Count -eq 0 -and @(Get-ConstructAgentFixPrompt -Errors $null).Count -eq 0)
$printed = (Write-ConstructAgentFixPrompt -Lines $lines 6>&1 | ForEach-Object { "$_" }) -join "`n"
ok "prompt block: keeps its copyable layout" (
    $printed -match 'Paste this into your AI coding agent on the VM to diagnose:' -and
    $printed -match "(?m)^  - Step 'Installing \.NET SDK' failed" -and
    ([regex]::Matches($printed, '\.{60}')).Count -eq 2)
ok "prompt block: prints nothing without a prompt" (@(Write-ConstructAgentFixPrompt -Lines @() 6>&1).Count -eq 0)

# ── model choices ────────────────────────────────────────────────────────────
$claude = @(Get-ConstructAgentFixModels -Agent claude)
$codex = @(Get-ConstructAgentFixModels -Agent codex)
$opencode = @(Get-ConstructAgentFixModels -Agent opencode)
ok "models: Claude = default, Opus, Fable, custom" ((($claude | ForEach-Object { $_.Value }) -join ',') -eq ',opus,fable,' -and $claude[3].Custom)
ok "models: Codex = default, latest Astra, latest Sol, custom" ((($codex | ForEach-Object { $_.Value }) -join ',') -eq ',latest:astra,latest:sol,' -and $codex[3].Custom)
ok "models: OpenCode = default, custom" ($opencode.Count -eq 2 -and $opencode[0].Value -eq '' -and $opencode[1].Custom)
ok "models: the default comes first" ($claude[0].Label -like 'Default*' -and $codex[0].Label -like 'Default*' -and $opencode[0].Label -like 'Default*')

# ── the remote command ───────────────────────────────────────────────────────
$prompt = $lines -join "`n"
$odd = "x'; rm -rf / `"`$(id)`""
$cmd = Get-ConstructAgentFixCommand -Agent opencode -Prompt $prompt -Model $odd
ok "command: one bash -lc around the guest script" ($cmd -match "^bash -lc 'bash /opt/construct/repo/bin/construct-agent-fix\.sh --agent opencode --prompt-b64 [A-Za-z0-9+/=]+ --model-b64 [A-Za-z0-9+/=]+'$")
ok "command: nothing a shell interprets besides the outer quotes" ($cmd.Substring(10, $cmd.Length - 11) -notmatch "['`"`$;&|<>\\]")
ok "command: the prompt round-trips" ((FromB64 ([regex]::Match($cmd, '--prompt-b64 (\S+)').Groups[1].Value)) -eq $prompt)
ok "command: a hostile model name round-trips" ((FromB64 ([regex]::Match($cmd, "--model-b64 ([^ ']+)").Groups[1].Value)) -eq $odd)
ok "command: no model flag for the default model" ((Get-ConstructAgentFixCommand -Agent claude -Prompt "p") -notmatch 'model')

# ── the console-attached run (real processes, Linux/macOS only) ──────────────
# The agent session must own the console: its output must neither vanish into a
# caller's `$null = ...` nor reach the console through a pipe.
if ($IsLinux -or $IsMacOS) {
    $argvDir = Join-Path ([IO.Path]::GetTempPath()) ("agent-fix-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $argvDir | Out-Null
    try {
        $argvOut = Join-Path $argvDir 'argv'
        $argvScript = Join-Path $argvDir 'argv.sh'
        Set-Content -Path $argvScript -Value "printf '%s\n' `"`$@`" > '$argvOut'; exit 7"
        function Invoke-Captured { Invoke-ConstructConsoleProcess -FilePath 'bash' -ArgumentList @($argvScript, '-t', 'agent-vm', "bash -lc 'a b'", 'two  spaces', 'trail\ x\', '') }
        $captured = @(Invoke-Captured)
        $argv = @(Get-Content -Path $argvOut)
        ok "console run: returns only the exit code" ($captured.Count -eq 1 -and $captured[0] -eq 7)
        ok "console run: every argument arrives as one, unchanged" (
            ($argv[0..5] -join '|') -eq "-t|agent-vm|bash -lc 'a b'|two  spaces|trail\ x\|")
        $null = Invoke-ConstructConsoleProcess -FilePath 'bash' -ArgumentList @('-c', 'exit 0')
        $threw = $false
        try { $null = Invoke-ConstructConsoleProcess -FilePath 'bash' -ArgumentList @('-c', 'echo "x"') } catch { $threw = $true }
        ok "console run: refuses an argument with a double quote" $threw
    } finally {
        Remove-Item -Recurse -Force -Path $argvDir -ErrorAction SilentlyContinue
    }
}

# ── the menu, with stubs ─────────────────────────────────────────────────────
function Test-ConstructConsoleInput { $script:interactive }
function Show-Menu {
    param($Title, $Options, $Default)
    $script:menus.Add([pscustomobject]@{ Title = $Title; Options = @($Options); Default = $Default })
    if ($script:answers.Count -eq 0) { throw "unexpected menu: $Title" }
    $a = $script:answers[0]; $script:answers.RemoveAt(0); return $a
}
function Invoke-TuiInput { param($ScreenTitle, $Body, $Prompt, $Default, [switch]$NoScreen) $script:typed }
function Copy-ConstructTextToClipboard { param($Text) $script:clipboard = $Text; return $true }
function Start-Sleep { param($Seconds) }
function ssh.exe {
    $script:sshCalls.Add(@($args))
    $global:LASTEXITCODE = $script:probeExit
    if ($script:probeExit -ne 0) { "ssh: Could not resolve hostname agent-vm" }
}
function Invoke-ConstructConsoleProcess {
    param($FilePath, [string[]]$ArgumentList)
    if ($FilePath -ne 'ssh.exe') { throw "unexpected console program: $FilePath" }
    $script:sshCalls.Add(@($ArgumentList))
    return $script:agentExit
}
function Reset-Stubs([int[]]$Answers) {
    $script:menus = New-Object System.Collections.Generic.List[object]
    $script:answers = New-Object System.Collections.Generic.List[int]
    foreach ($a in $Answers) { $script:answers.Add($a) }
    $script:sshCalls = New-Object System.Collections.Generic.List[object]
    $script:clipboard = $null
    $script:typed = ""
    $script:interactive = $true
    $script:probeExit = 0
    $script:agentExit = 0
}
function Agent-Run { @($script:sshCalls | Where-Object { $_ -contains '-t' }) }

# Enter, Enter, Down+Enter: autofix -> Claude Code -> Opus.
Reset-Stubs @(0, 0, 1)
Invoke-ConstructAgentFixMenu -PromptLines $lines -SshTarget 'agent-vm' 6>$null
ok "menu: autofix comes first and is the default" ($script:menus[0].Options[0] -like 'Autofix it*' -and $script:menus[0].Default -eq 0)
ok "menu: then copy, then close" ($script:menus[0].Options[1] -like 'Copy the prompt*' -and $script:menus[0].Options[2] -eq 'Close')
ok "menu: agents are Claude Code, Codex, OpenCode (+ Back)" (
    $script:menus[1].Options.Count -eq 4 -and $script:menus[1].Options[0] -like 'Claude Code*' -and
    $script:menus[1].Options[1] -like 'Codex*' -and $script:menus[1].Options[2] -like 'OpenCode*' -and $script:menus[1].Options[3] -eq 'Back')
ok "menu: the model menu belongs to the picked agent" ($script:menus[2].Title -like '*Claude Code*' -and $script:menus[2].Options[1] -like 'Opus*')
$run = @(Agent-Run)
ok "autofix: waits for SSH, then one interactive run" ($script:sshCalls.Count -eq 2 -and $run.Count -eq 1)
ok "autofix: the probe never prompts (BatchMode, no stdin)" ($script:sshCalls[0] -contains 'BatchMode=yes' -and $script:sshCalls[0] -contains '-n')
ok "autofix: the run goes to the provisioned ssh alias" ($run[0] -contains 'agent-vm')
$remote = [string]$run[0][-1]
ok "autofix: runs the guest script for Claude" ($remote -like "bash -lc 'bash /opt/construct/repo/bin/construct-agent-fix.sh --agent claude *")
ok "autofix: with the chosen model" ((FromB64 ([regex]::Match($remote, "--model-b64 ([^ ']+)").Groups[1].Value)) -eq 'opus')
ok "autofix: with the prompt shown above" ((FromB64 ([regex]::Match($remote, '--prompt-b64 (\S+)').Groups[1].Value)) -eq $prompt)

# Codex -> latest Sol.
Reset-Stubs @(0, 1, 2)
Invoke-ConstructAgentFixMenu -PromptLines $lines -SshTarget 'agent-vm' 6>$null
$remote = [string](@(Agent-Run)[0][-1])
ok "autofix: Codex latest Sol is resolved on the VM" ($remote -match '--agent codex ' -and (FromB64 ([regex]::Match($remote, "--model-b64 ([^ ']+)").Groups[1].Value)) -eq 'latest:sol')

# OpenCode -> custom model typed in.
Reset-Stubs @(0, 2, 1)
$script:typed = 'openrouter/z-ai/glm-5'
Invoke-ConstructAgentFixMenu -PromptLines $lines -SshTarget 'agent-vm' 6>$null
$remote = [string](@(Agent-Run)[0][-1])
ok "autofix: a custom model is asked for and used" ($remote -match '--agent opencode ' -and (FromB64 ([regex]::Match($remote, "--model-b64 ([^ ']+)").Groups[1].Value)) -eq 'openrouter/z-ai/glm-5')

# Custom left empty = the agent's default model.
Reset-Stubs @(0, 0, 3)
Invoke-ConstructAgentFixMenu -PromptLines $lines -SshTarget 'agent-vm' 6>$null
ok "autofix: an empty custom model means the default" (([string](@(Agent-Run)[0][-1])) -notmatch 'model-b64')

# Back from the agent menu returns to the first menu; Back from a model menu to the agent menu.
Reset-Stubs @(0, 3, 0, 1, 4, 0, 0)
Invoke-ConstructAgentFixMenu -PromptLines $lines -SshTarget 'agent-vm' 6>$null
ok "menu: Back returns one level up" (
    $script:menus.Count -eq 7 -and $script:menus[2].Title -like 'How do you want*' -and
    $script:menus[5].Title -like 'Which agent*' -and @(Agent-Run).Count -eq 1)

# Copy.
Reset-Stubs @(1)
$out = (Invoke-ConstructAgentFixMenu -PromptLines $lines -SshTarget 'agent-vm' 6>&1 | ForEach-Object { "$_" }) -join "`n"
ok "copy: puts exactly the prompt on the clipboard" ($script:clipboard -eq $prompt)
ok "copy: says so, starts nothing" ($out -match 'on the clipboard' -and $script:sshCalls.Count -eq 0)

# Close.
Reset-Stubs @(2)
Invoke-ConstructAgentFixMenu -PromptLines $lines -SshTarget 'agent-vm' 6>$null
ok "close: neither copies nor connects" ($null -eq $script:clipboard -and $script:sshCalls.Count -eq 0 -and $script:menus.Count -eq 1)

# No ssh target: no autofix row.
Reset-Stubs @(0)
Invoke-ConstructAgentFixMenu -PromptLines $lines -SshTarget '' 6>$null
ok "no ssh target: copy and close only, copy is the default" (
    $script:menus[0].Options.Count -eq 2 -and $script:menus[0].Options[0] -like 'Copy*' -and $script:clipboard -eq $prompt)

# A console nobody can answer: nothing at all (Show-Menu would return the default, autofix).
Reset-Stubs @()
$script:interactive = $false
Invoke-ConstructAgentFixMenu -PromptLines $lines -SshTarget 'agent-vm' 6>$null
ok "redirected input: no menu, no agent" ($script:menus.Count -eq 0 -and $script:sshCalls.Count -eq 0)

# No prompt (no VM step failed): no menu.
Reset-Stubs @()
Invoke-ConstructAgentFixMenu -PromptLines @() -SshTarget 'agent-vm' 6>$null
ok "no prompt: no menu" ($script:menus.Count -eq 0)

# Unreachable VM: gives up after the wait and points back at the prompt.
Reset-Stubs @()
$script:probeExit = 255
$out = (Invoke-ConstructAgentFix -SshTarget 'agent-vm' -Agent claude -Prompt 'p' -WaitSeconds 0 6>&1 | ForEach-Object { "$_" }) -join "`n"
ok "unreachable: no agent run" (@(Agent-Run).Count -eq 0)
ok "unreachable: says why and what to do instead" ($out -match 'Could not reach the VM over SSH' -and $out -match 'Could not resolve hostname' -and $out -match 'Paste the prompt')

# The agent's exit code is reported.
Reset-Stubs @()
$script:agentExit = 3
$out = (Invoke-ConstructAgentFix -SshTarget 'agent-vm' -Agent codex -Prompt 'p' -Label 'Codex' 6>&1 | ForEach-Object { "$_" }) -join "`n"
ok "agent exit: a failed session is reported with its code" ($out -match 'Codex session ended with exit code 3')
Reset-Stubs @()
$out = (Invoke-ConstructAgentFix -SshTarget 'agent-vm' -Agent codex -Prompt 'p' -Model 'latest:sol' -Label 'Codex' 6>&1 | ForEach-Object { "$_" }) -join "`n"
ok "agent start: names the model family in words" ($out -match 'Opening Codex on the VM \(the latest sol model\)' -and $out -match 'Codex session closed\.')
ok "agent start: says how to leave Codex" ($out -match 'type /quit to come back')
Reset-Stubs @()
$out = (Invoke-ConstructAgentFix -SshTarget 'agent-vm' -Agent claude -Prompt 'p' -Label 'Claude Code' 6>&1 | ForEach-Object { "$_" }) -join "`n"
ok "agent start: says how to leave Claude Code" ($out -match 'type /exit to come back')
ok "agent run: the function returns only the exit code" (@(Invoke-ConstructAgentFix -SshTarget 'agent-vm' -Agent claude -Prompt 'p' 6>$null).Count -eq 1)

# ── where the scripts offer it ───────────────────────────────────────────────
$aiScript = Get-Content -Raw (Join-Path $here "..\Auto-Install.ps1")
$waitExit = [regex]::Match($aiScript, '(?s)function Wait-Exit \{.*?\n\}\r?\n').Value
ok "auto-install: Wait-Exit prints the shared prompt" ($waitExit -match 'Write-ConstructAgentFixPrompt -Lines \$fixPrompt')
ok "auto-install: the menu comes right before the final pause, only after failed steps" (
    $waitExit -match '\$global:ConstructProvisionHadErrors -and \$fixPrompt\.Count -gt 0' -and
    $waitExit.IndexOf('Invoke-ConstructAgentFixMenu') -gt 0 -and
    $waitExit.IndexOf('Invoke-ConstructAgentFixMenu') -lt $waitExit.IndexOf('Read-Host "Press Enter to exit"'))
ok "auto-install: connects through the provisioner's alias, else the VM's" (
    $waitExit -match '\$global:ConstructProvisionSshTarget' -and $waitExit -match 'VmIdentity\.HostAlias')

$prov = Get-Content -Raw (Join-Path $here "..\Provision-AgentVM.ps1")
ok "provisioner: records the alias right after writing the ssh config" (
    $prov -match "Set-HostSshConfig -PrivateKeyText \`$privateKeyText\r?\nif \(\`$Action -eq 'provision'\) \{ \`$global:ConstructProvisionSshTarget = \`$HostAlias \}")
ok "provisioner: a new run starts without an alias" ($prov -match '\$global:ConstructProvisionSshTarget = ""')
$finally = $prov.Substring($prov.LastIndexOf("`nfinally {"))
ok "provisioner: the menu only in its own pause (never under -Auto, where the caller owns it)" (
    $finally -match "if \(-not \`$Auto -and \(\(-not \`$FromPanel\) -or \`$global:ConstructProvisionHadErrors -or \`$script:BackupActionFailed\)\) \{" -and
    $finally.IndexOf('Invoke-ConstructAgentFixMenu') -gt $finally.IndexOf('if (-not $Auto -and') -and
    $finally.IndexOf('Invoke-ConstructAgentFixMenu') -lt $finally.IndexOf('Read-Host "Press Enter to exit"'))
ok "provisioner: the result screen uses the shared prompt" ($prov -match 'Write-ConstructAgentFixPrompt -Lines @\(Get-ConstructAgentFixPrompt -Errors @\(\$Result\.Errors\)\)')

Write-Host ""
Write-Host "  agent-fix host tests - $($script:pass)/$($script:pass + $script:fail) passed"
if ($script:fail -gt 0) { exit 1 }
