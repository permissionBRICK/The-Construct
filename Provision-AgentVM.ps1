#Requires -Version 5.1
<#
.SYNOPSIS
    Provision a construct sandbox VM from a Windows host and wire the host
    up for SSH + VS Code Remote-SSH access.

.DESCRIPTION
    Run this from a local checkout of the construct repo on your Windows
    machine. The script:

      1. Packs this repo folder (the folder the script lives in) into a tar.gz.
      2. Connects to the VM over SSH. When re-provisioning an existing VM, it
         first tries the root key saved from a previous run (~\.ssh\<LocalKeyName>):
         if that still authorizes us as root, the whole run uses it -- every
         command runs directly as root (no bootstrap key, no agent password, no
         sudo) and the VM's root key is left untouched (not regenerated). Only
         if that doesn't work does it fall back to the pre-seeded bootstrap key
         (baked into the autoinstall ISO for the agent user); if the bootstrap
         key isn't authorized either, it offers to install it via the agent
         password, and as a last resort prints a PuTTY command and stops.
      3. Uploads the archive, unpacks it to /opt/construct/repo, and runs the
         non-interactive provisioner (bin/provision.sh) -- as root directly on
         the fast path, otherwise via sudo.
      4. Obtains the root SSH private key: reuses the saved copy on the fast
         path, otherwise retrieves the one generated on the VM.
      5. Removes the bootstrap public key from the agent user's authorized_keys.
         The fast path never installs it, but still strips any leftover copy
         from a failed/manual prior run so it can't remain a standing credential.
      6. Configures the Windows host: ~\.ssh\ (private key + known_hosts +
         config Host entry) and VS Code's remote.SSH.remotePlatform.

    Requires the OpenSSH client (ssh, scp, ssh-keyscan, ssh-keygen) and tar.exe
    that ship with Windows 10/11. No Posh-SSH dependency.

.NOTES
    SECURITY: The bootstrap key in keys/ is intentionally committed so a fresh
    autoinstall VM can be provisioned unattended. It is removed from the VM's
    authorized_keys at the end of provisioning. The retrieved root private key
    grants full access to the VM; it is written to ~\.ssh\.
#>
[CmdletBinding()]
param(
    [string]$VmHost       = "agent-vm.mshome.net",
    [string]$HostAlias    = "agent-vm",
    [string]$SeedUser     = "agent",
    [string]$SeedPassword = "agent",
    # Optional NEW login password for the agent user, applied at the very end of
    # setup. Manual-fallback login only -- root access is via pubkey and is
    # unaffected. Empty or equal to $SeedPassword leaves the password unchanged.
    [string]$AgentPassword = "",
    # Optional git identity to apply as the VM's GLOBAL git config (user.name /
    # user.email). When not supplied, prompted with defaults from the saved
    # settings file and this host's own git identity. Empty leaves it unchanged.
    [string]$GitUserName,
    [string]$GitEmail,
    # Source repo/ref this install came from (threaded from install.ps1 via
    # Auto-Install / Create-AgentVM). Used to record the installed-commit update
    # marker for the control panel at the end of a successful provision. Default to
    # the canonical repo; a param-less run (e.g. a panel reprovision) keeps whatever
    # the settings file already records instead of resetting it.
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main",
    [string]$RemoteUser   = "root",
    [string]$AiTools      = "opencode,claude-code,codex",
    [string]$Projects     = "default",
    [string]$AgentName    = "",
    [string]$LocalKeyName = "agent_vm_ed25519",
    [int]$OpencodePort    = 4096,
    # Always install the VS Code CLI ("VS Code Server") on the VM so VS Code
    # Remote-SSH works out of the box. "true"/"false".
    [string]$VsCodeServer = "true",
    # Autostart `code serve-web` (browser VS Code over HTTP, gated by a connection
    # token, bound to 0.0.0.0). On by default. "true"/"false".
    [string]$VsCodeServeWeb = "true",
    # Opt in to also set up + register a `code tunnel` (reachable via vscode.dev
    # with no inbound port). When enabled, the script pauses after provisioning so
    # you can complete the one-time device sign-in, then continues to the reboot.
    # "true"/"false".
    [string]$VsCodeTunnel = "false",
    # Patch the Claude Code VS Code extension on the VM so it streams partial
    # assistant messages over Remote-SSH. The stock extension turns streaming OFF on
    # any remote, so the chat panel shows nothing until each turn is fully generated
    # (reads as "stuck before the thinking block"). This VM is reached over a local
    # link where the per-delta volume is a non-issue, so default it on. "true"/"false".
    [string]$ClaudePartialStreaming = "true",
    # Patch the Claude Code extension for microphone passthrough (recorder shim +
    # chat-mic speech gate) so the chat mic button survives a reprovision. Off by
    # default (opt-in); "false" reverts to stock. "true"/"false".
    [string]$MicPassthrough = "false",
    # Set up a Samba/SMB server on the VM that shares the workspace (the repos
    # folder) to this host. Credentials are generated once on the VM and persisted
    # in its config, so re-provisions keep the same login. "true"/"false".
    [string]$SmbShare = "true",
    # After provisioning, auto-mount the VM's workspace share on this host with
    # `net use` (/savecred /persistent:yes), so the repos appear as a drive.
    # "true"/"false". OFF by default: the SMB server still runs on the VM (see
    # -SmbShare) so the repos stay reachable at \\<host>\repo, but the host does
    # not map a drive letter unless you opt in with -MountRepoShare true.
    # Ignored when -SmbShare false. Only runs on -Action provision.
    [string]$MountRepoShare = "false",
    # Drive letter to map the workspace share to (no colon). Used as-is when it's
    # free or already mapped to this VM's share; if it's in use by something else,
    # you're prompted to pick another free letter (a non-interactive run falls back
    # to the next free letter automatically).
    [string]$SmbDriveLetter = "Z",
    [switch]$IncludeGit,
    # What this run does:
    #   provision (default) -- the normal full provision. With -RestoreDir it also
    #                          restores a saved config onto the VM after setup.
    #   export              -- connect to an already-installed VM and pull its
    #                          current agent config back to -BackupDir (no
    #                          provisioning, no reboot). With -ScanReposOnly it
    #                          only scans the project repos for unsaved work.
    [ValidateSet("provision", "export")]
    [string]$Action = "provision",
    # Where to write the exported backup (-Action export). A folder; receives
    # backup.tar.gz, an extracted/ copy, and repo-scan.json.
    [string]$BackupDir = "",
    # -Action export only: scan the project repos for uncommitted/unpushed work
    # and write repo-scan.json, without exporting the (much larger) config.
    [switch]$ScanReposOnly,
    # Restore a previously exported backup (a -BackupDir from a prior export run)
    # onto the VM at the end of provisioning. Used by the reinstall auto-restore.
    [string]$RestoreDir = "",
    # Optional git credentials for cloning private project repos during setup,
    # base64-encoded newline-separated `https://user:token@host` lines (see
    # Resolve-GitCloneCredential). Passed to provision.sh as GIT_CLONE_CREDENTIALS_B64.
    [string]$GitCloneCredentialsB64 = "",
    # Force the project repo checkout on/off ("true"/"false"). Empty = auto: on
    # when the selected projects declare any repos, off otherwise.
    [string]$CheckoutProjects = "",
    # Config-sync v2 (spec section 8): conflict resolution strategy for the
    # sync tick that runs before provisioning. 'ours' keeps the host side,
    # 'theirs' keeps the VM side. When omitted, a conflict stops provisioning
    # with instructions to resolve manually, commit, and re-run.
    [ValidateSet("ours", "theirs")]
    [string]$AutoResolve,
    # Set when this script is launched by an upper script (Auto-Install.ps1 /
    # Create-AgentVM.ps1), which owns the final "Press Enter" pause. When run on
    # its own this stays off and the script pauses at the end so a self-launched
    # window doesn't vanish before the output can be read.
    [switch]$Auto,
    # Suppress interactive config prompts (e.g. the alternate SMB drive-letter menu)
    # when launched from the control panel — the choices come from params instead.
    # (The end-of-run pause is controlled separately by -FromPanel.) Implied by -Auto
    # (an upper script pre-answers everything).
    [switch]$NonInteractive,
    # Launched from the control-panel extension: skip the end-of-run "Press Enter to
    # exit" pause only when provisioning is fully clean. Optional or critical errors
    # always force the pause so the result remains readable. A direct PowerShell run
    # leaves this off and pauses as usual.
    [switch]$FromPanel,
    # Path to a JSON result file written by the finally block when this script runs
    # as a de-elevated child (Invoke-DeElevatedProvision). The elevated parent polls
    # this file for the provision outcome. Not set on standalone or panel runs —
    # the result flows through globals and the console instead.
    [string]$ResultFile = "",
    # Path to a "ready" handshake file written atomically (temp+rename) immediately
    # after param binding succeeds. The elevated parent polls this to confirm the
    # child's script entry worked (vs. a bootstrap/decode/missing-script failure).
    # Contains the child PID so the parent can track liveness and stop on timeout.
    [string]$ReadyFile = ""
)

$ErrorActionPreference = "Stop"

# De-elevated child: signal the parent that Provision-AgentVM.ps1 has started
# (param binding succeeded, script was found). Written atomically so the parent
# never reads a partial PID.
if ($ReadyFile) {
    $readyTmp = "$ReadyFile.tmp.$PID"
    try {
        Set-Content -LiteralPath $readyTmp -Value "$PID" -Encoding ASCII -Force
        Move-Item -LiteralPath $readyTmp -Destination $ReadyFile -Force -ErrorAction Stop
    } catch {
        # Unable to signal readiness — exit before any provisioning work so the
        # parent's handshake timeout triggers a clean inline fallback.
        Remove-Item -LiteralPath $readyTmp -Force -ErrorAction SilentlyContinue
        Write-Warning "Could not publish ready handshake: $($_.Exception.Message)"
        exit 1
    }
}

# Decode native-command output (ssh/scp stdout) as UTF-8. Windows PowerShell 5.1
# otherwise decodes it with the OEM code page (CP437/850), which turns the remote's
# UTF-8 box-drawing/emoji bytes into mojibake like "Γûä" / "Γ£ö". Best-effort: some
# hosts (ISE) don't allow setting the console encoding.
try {
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [Console]::OutputEncoding = $utf8NoBom
    $OutputEncoding = $utf8NoBom
} catch { }

# Enable ANSI/VT processing so colour codes from the remote render instead of
# printing literally. Windows Terminal already has this on; legacy conhost may not.
try {
    if (-not ("Vt.Kernel32" -as [type])) {
        Add-Type -Namespace Vt -Name Kernel32 -MemberDefinition @"
[DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr GetStdHandle(int n);
[DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetConsoleMode(IntPtr h, out uint m);
[DllImport("kernel32.dll", SetLastError=true)] public static extern bool SetConsoleMode(IntPtr h, uint m);
"@
    }
    $stdout = [Vt.Kernel32]::GetStdHandle(-11)   # STD_OUTPUT_HANDLE
    $mode = 0
    if ([Vt.Kernel32]::GetConsoleMode($stdout, [ref]$mode)) {
        [Vt.Kernel32]::SetConsoleMode($stdout, $mode -bor 0x0004) | Out-Null  # ENABLE_VIRTUAL_TERMINAL_PROCESSING
    }
} catch { }

$RemoteKeyPath   = "/root/.ssh/codex_app_ed25519"        # produced by setup-root-ssh-key.sh
$RemoteArchive   = "/tmp/construct-repo.tar.gz"
$BootstrapKey    = Join-Path $PSScriptRoot "keys\bootstrap_ed25519"
$BootstrapPubKey = Join-Path $PSScriptRoot "keys\bootstrap_ed25519.pub"

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }

# Shared helpers: persisted settings, git-identity resolution, and the provision
# result parser. The parser is required for provisioning; export-only helpers keep
# their existing degraded fallbacks when the library is unavailable.
$commonLib = Join-Path $PSScriptRoot "lib\AgentVm.Common.ps1"
if (Test-Path -LiteralPath $commonLib) { . $commonLib }
if ($Action -eq 'provision' -and -not (Get-Command ConvertFrom-ConstructProvisionResult -ErrorAction SilentlyContinue)) {
    throw "Required host helper library is missing or invalid: $commonLib"
}

# --- Dependencies -----------------------------------------------------------

function Ensure-Tar {
    if (-not (Get-Command tar.exe -ErrorAction SilentlyContinue)) {
        throw "tar.exe not found. It ships with Windows 10 1803+ / Windows 11. Install it or upgrade Windows."
    }
}

function Remove-TreeRobust {
    # Delete a directory tree even when it holds paths longer than Windows'
    # 260-char MAX_PATH. PowerShell's `Remove-Item -Recurse` throws
    # "Could not find a part of the path '...'" on such paths -- and an extracted
    # config backup contains deep agent-session transcripts that blow past it. So
    # if the plain delete fails, empty the tree by mirroring an empty directory
    # onto it with robocopy (which handles long paths natively), then remove the
    # now-shallow directory.
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    try {
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction Stop
        return
    } catch {
        $origErr = $_.Exception.Message
        if (Get-Command robocopy.exe -ErrorAction SilentlyContinue) {
            $empty = Join-Path ([System.IO.Path]::GetTempPath()) ("construct-empty-" + [Guid]::NewGuid().ToString("N"))
            New-Item -ItemType Directory -Force -Path $empty | Out-Null
            try {
                # robocopy exit codes 0-7 are success; only treat the call as
                # advisory and check the result with Test-Path afterwards.
                & robocopy.exe $empty $Path /MIR /NJH /NJS /NFL /NDL /NP /R:0 /W:0 | Out-Null
                Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
            } finally {
                Remove-Item -LiteralPath $empty -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        if (Test-Path -LiteralPath $Path) {
            throw "Could not remove '$Path' (long-path delete failed): $origErr"
        }
    }
}

function Ensure-OpenSSH {
    if (Get-Command ssh.exe -ErrorAction SilentlyContinue) { return }
    Write-Step "OpenSSH client not found. Installing via winget..."
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        throw "ssh.exe not found and winget is not available to install it. Install OpenSSH manually: Settings > Apps > Optional Features > OpenSSH Client."
    }
    & winget.exe install --id Microsoft.OpenSSH.Beta --accept-source-agreements --accept-package-agreements
    # Refresh PATH so the current session can find ssh.exe
    $env:PATH = [System.Environment]::GetEnvironmentVariable("PATH", "Machine") + ";" + [System.Environment]::GetEnvironmentVariable("PATH", "User")
    if (-not (Get-Command ssh.exe -ErrorAction SilentlyContinue)) {
        throw "ssh.exe still not found after winget install. Restart PowerShell or install OpenSSH manually."
    }
    Write-Ok "OpenSSH client installed"
}

function Ensure-BootstrapKey {
    if (-not (Test-Path $BootstrapKey)) {
        throw "Bootstrap private key not found: $BootstrapKey`nGenerate it with: ssh-keygen -t ed25519 -N '' -C bootstrap@construct -f keys/bootstrap_ed25519"
    }
    if (-not (Test-Path $BootstrapPubKey)) {
        throw "Bootstrap public key not found: $BootstrapPubKey"
    }

    # The repo lives in a normal user folder, so the private key inherits broad
    # ACLs. Windows OpenSSH refuses to use a private key with permissions that
    # are "too open" (it silently ignores the key, so auth fails even when the
    # public key is authorized on the VM). Copy it to TEMP with owner-only ACLs
    # and use that copy.
    $secureKey = Join-Path $env:TEMP "ca_bootstrap_ed25519"
    # Remove any leftover (its locked-down ACL would block Copy-Item -Force on a
    # second run); deletion is allowed because we own the TEMP directory.
    Remove-Item -LiteralPath $secureKey -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $BootstrapKey -Destination $secureKey -Force
    & icacls $secureKey /inheritance:r | Out-Null
    & icacls $secureKey /grant:r "$($env:USERNAME):F" | Out-Null
    $script:SecureKeyPath = $secureKey
    $script:BootstrapKey = $secureKey
    $script:SshOpts = @(
        "-i", $secureKey,
        "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", "UserKnownHostsFile=$env:TEMP\construct-known_hosts",
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=15"
    )
}

function Ensure-VmReachable {
    while ($true) {
        Write-Step "Checking VM reachability ($script:VmHost, SSH port 22)"
        # Detect reachability with ssh itself -- the tool we use anyway -- rather
        # than a separate TCP probe, so there's no Test-NetConnection (and no
        # progress/warning banner) at all. We don't need to AUTHENTICATE here,
        # only to confirm sshd is answering: PreferredAuthentications=none makes
        # ssh offer no method, so the daemon replies with a permission-denied the
        # moment it's up. ssh's own stderr is captured, never printed. Only a
        # transport-level failure (no DNS, refused, timeout) counts as "not up";
        # an auth rejection means sshd answered, i.e. the VM is reachable.
        $probeOpts = @(
            "-o", "BatchMode=yes",
            "-o", "StrictHostKeyChecking=no",
            "-o", "UserKnownHostsFile=$env:TEMP\construct-known_hosts",
            "-o", "ConnectTimeout=5",
            "-o", "PreferredAuthentications=none"
        )
        $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        $probe = (& ssh.exe @probeOpts "$SeedUser@$($script:VmHost)" "true" 2>&1 | Out-String)
        $ErrorActionPreference = $prevEAP
        $reachable = ($probe -notmatch 'connect to host|Could not resolve hostname|Connection (refused|timed out|closed)|Network is unreachable|Operation timed out|No route to host')
        if ($reachable) {
            Write-Ok "VM is reachable at $($script:VmHost)"
            return
        }
        Write-Warning "Cannot reach $($script:VmHost) over SSH (port 22)."
        Write-Host "    Make sure the VM is running, then enter its Hyper-V hostname (without .mshome.net)." -ForegroundColor Yellow
        Write-Host "    Press Enter to retry with the current hostname." -ForegroundColor Yellow
        $current = $script:VmHost -replace '\.mshome\.net$', ''
        $name = Read-Host "VM hostname [$current]"
        if (-not [string]::IsNullOrWhiteSpace($name)) {
            $name = $name.Trim()
            $script:VmHost    = "$name.mshome.net"
            $script:HostAlias = $name
        }
    }
}

# --- SSH helpers (native ssh.exe / scp.exe) ---------------------------------

$script:SshOpts = @(
    "-i", $BootstrapKey,
    "-o", "StrictHostKeyChecking=accept-new",
    "-o", "UserKnownHostsFile=$env:TEMP\construct-known_hosts",
    "-o", "BatchMode=yes",
    "-o", "ConnectTimeout=15",
    # Detect a dead connection (e.g. the VM going down during the post-provision
    # reboot) within ~1 min instead of waiting out the multi-hour TCP timeout --
    # otherwise ssh.exe can hang indefinitely on a severed session.
    "-o", "ServerAliveInterval=15",
    "-o", "ServerAliveCountMax=4"
)

# Identity used for the provisioning SSH/SCP connection. Defaults to the seed
# (agent) user reached via the bootstrap key; the re-provision fast path
# (Enter-RootKeyFastPath) can switch this to root once it confirms the saved
# root key still works, after which every command runs directly as root (no
# sudo) and the VM's existing root key is left untouched.
$script:ConnectUser       = $SeedUser
$script:UseRootKey        = $false
$script:LocalRootKeyPath  = $null
$script:SecureRootKeyPath = $null

function Invoke-Ssh {
    param([Parameter(Mandatory)][string]$Command, [switch]$Sudo)
    if ($script:UseRootKey) {
        # Connected as root via the saved key: run the command directly (no sudo)
        # but through a login shell so PATH matches the sudo path -- e.g. so the
        # reboot/chpasswd in /usr/sbin resolve the same way they do under sudo.
        $escCmd = $Command.Replace("'", "'\''")
        $toRun  = "bash -lc '$escCmd'"
    } elseif ($Sudo) {
        $escPw  = $SeedPassword.Replace("'", "'\''")
        $escCmd = $Command.Replace("'", "'\''")
        $toRun  = "printf '%s\n' '$escPw' | sudo -S -p '' bash -lc '$escCmd'"
    } else {
        $toRun = $Command
    }
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    # -n: read stdin from /dev/null so ssh.exe never attaches to the host console.
    # Without it ssh can block (and fail to terminate) waiting on console stdin --
    # no remote command here reads local stdin (passwords are piped in remotely).
    $output = & ssh.exe -n @script:SshOpts "$($script:ConnectUser)@$VmHost" $toRun 2>$null
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    if ($exitCode -ne 0) {
        throw "Remote command failed (exit $exitCode): $Command"
    }
    return ($output | Out-String)
}

function Invoke-SshStream {
    # Like Invoke-Ssh, but streams the remote command's combined stdout/stderr to
    # the console line-by-line as it arrives instead of buffering and returning it
    # all at once. Use this for long-running steps (e.g. provision.sh) so the user
    # sees live progress. Throws on a non-zero remote exit code unless -NoThrow;
    # -PassThru returns the exit code plus every displayed line for protocol
    # parsers without changing the live console behaviour.
    param(
        [Parameter(Mandatory)][string]$Command,
        [switch]$Sudo,
        [switch]$PassThru,
        [switch]$NoThrow
    )
    # Keep colour but stay non-interactive: a colour-capable TERM plus FORCE_COLOR/
    # CLICOLOR_FORCE makes tools emit SGR colour even though stdout isn't a tty
    # (so they still skip animated progress bars). DEBIAN_FRONTEND keeps apt quiet.
    $envPrefix = "env TERM=xterm-256color FORCE_COLOR=1 CLICOLOR_FORCE=1 DEBIAN_FRONTEND=noninteractive"
    $escCmd = $Command.Replace("'", "'\''")
    # When connected as root via the saved key we're already root, so drop the
    # sudo wrapper and run the command straight through the login shell.
    if ($Sudo -and -not $script:UseRootKey) {
        $escPw = $SeedPassword.Replace("'", "'\''")
        $toRun = "printf '%s\n' '$escPw' | sudo -S -p '' $envPrefix bash -lc '$escCmd'"
    } else {
        $toRun = "$envPrefix bash -lc '$escCmd'"
    }
    # Deliberately NO pseudo-terminal: a PTY (-tt) makes remote programs draw
    # animated progress bars using carriage returns + ANSI cursor moves, which
    # turn into a wall of garbage when piped through Write-Host. Without a tty the
    # remote emits plain line output, and ssh still streams it as it arrives.
    # Strip stray CR and cursor/erase CSI sequences per line, but KEEP SGR colour
    # codes (the ones ending in 'm') so the console renders colour. The final-byte
    # class [@-ln-~] is @..~ with 'm' (0x6D) carved out.
    $esc    = [char]27
    $ansiRe = [regex]([regex]::Escape($esc) + '\[[0-9;?]*[ -/]*[@-ln-~]')
    $lines = New-Object System.Collections.Generic.List[string]
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & ssh.exe -n @script:SshOpts "$($script:ConnectUser)@$VmHost" $toRun 2>&1 | ForEach-Object {
        $displayLine = ((([string]$_) -replace "`r", "") -replace $ansiRe, "")
        $lines.Add($displayLine)
        Write-Host $displayLine
    }
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    if ($exitCode -ne 0 -and -not $NoThrow) {
        throw "Remote command failed (exit $exitCode): $Command"
    }
    if ($PassThru) {
        return [pscustomobject]@{ ExitCode = $exitCode; Lines = [string[]]$lines }
    }
}

function Invoke-Scp {
    param(
        [Parameter(Mandatory)][string]$LocalPath,
        [Parameter(Mandatory)][string]$RemotePath
    )
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & scp.exe @script:SshOpts $LocalPath "$($script:ConnectUser)@${VmHost}:${RemotePath}" 2>$null
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    if ($exitCode -ne 0) {
        throw "SCP upload failed (exit $exitCode): $LocalPath -> ${RemotePath}"
    }
}

function Invoke-ScpFrom {
    # Download a file FROM the VM to the host (the reverse of Invoke-Scp). Used by
    # -Action export to pull the config backup / repo scan back to the host.
    param(
        [Parameter(Mandatory)][string]$RemotePath,
        [Parameter(Mandatory)][string]$LocalPath
    )
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & scp.exe @script:SshOpts "$($script:ConnectUser)@${VmHost}:${RemotePath}" $LocalPath 2>$null
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    if ($exitCode -ne 0) {
        throw "SCP download failed (exit $exitCode): ${RemotePath} -> $LocalPath"
    }
}

# --- Bootstrap-key install via password (fallback for non-autoinstall VMs) --

function Test-KeyAuth {
    # True if the bootstrap key already lets us in (autoinstall VMs).
    # Lower ErrorActionPreference so ssh's benign stderr (e.g. "Permanently
    # added ... to known hosts") isn't promoted to a terminating error by the
    # script-wide 'Stop' setting.
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & ssh.exe -n @script:SshOpts "$SeedUser@$VmHost" "true" 2>&1 | Out-Null
    $ok = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEAP
    return $ok
}

function Test-Sudo {
    # True if $SeedPassword (or NOPASSWD) currently lets us run sudo as $SeedUser.
    # `-k` first clears any cached sudo timestamp so this reflects the password,
    # not a prior successful sudo; NOPASSWD VMs still pass regardless of password.
    param([Parameter(Mandatory)][string]$Password)
    $escPw = $Password.Replace("'", "'\''")
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & ssh.exe -n @script:SshOpts "$SeedUser@$VmHost" "printf '%s\n' '$escPw' | sudo -k -S -p '' true" 2>$null | Out-Null
    $ok = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEAP
    return $ok
}

function Ensure-Sudo {
    # Make sure sudo works before any privileged step runs. The seed password may
    # no longer be the agent user's password -- the optional custom password set
    # at the end of a previous provision changes it -- which makes every later
    # `sudo -S` fail (exit 1) on the very first command. If the seed password is
    # rejected, prompt for the current one and retry, updating $script:SeedPassword
    # so the rest of the run uses it. VMs provisioned with passwordless sudo never
    # reach the prompt. (Going forward this is rarely hit: provision.sh now grants
    # the seed user NOPASSWD sudo.)
    if (Test-Sudo $SeedPassword) { Write-Ok "sudo access confirmed"; return }

    Write-Warning "Could not use sudo as '$SeedUser' with the default password."
    Write-Host "    The agent login password may have been changed on a previous install." -ForegroundColor Yellow
    for ($i = 1; $i -le 3; $i++) {
        $sec = Read-Host "    Current login password for '$SeedUser' (attempt $i/3)" -AsSecureString
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($sec)
        try   { $pw = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
        if (Test-Sudo $pw) {
            $script:SeedPassword = $pw
            Write-Ok "sudo access confirmed"
            return
        }
        Write-Warning "That password was not accepted for sudo."
    }
    throw "Cannot obtain sudo on the VM as '$SeedUser'. Verify the agent login password, then re-run."
}

function Throw-BootstrapKeyHelp {
    # Last-resort fallback: the password attempt didn't authenticate either (e.g.
    # the VM disallows password login, or the password isn't the default). Tell
    # the user to authorize the key manually over PuTTY, then re-run.
    $pub = (Get-Content $BootstrapPubKey -Raw).Trim()
    $cmd = "mkdir -p ~/.ssh && chmod 700 ~/.ssh && echo '$pub' >> ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys"
    Write-Host ""
    Write-Warning "The VM did not accept the bootstrap key, so provisioning can't connect."
    Write-Host ""
    Write-Host "  1. Connect to the VM with PuTTY:" -ForegroundColor White
    Write-Host "       Host: $VmHost   User: $SeedUser   Password: $SeedPassword" -ForegroundColor Yellow
    Write-Host "  2. Paste and run this single command to authorize the key:" -ForegroundColor White
    Write-Host ""
    Write-Host "       $cmd" -ForegroundColor Cyan
    Write-Host ""
    Write-Host "  3. Re-run this script." -ForegroundColor White
    Write-Host ""
    throw "Bootstrap key not authorized on the VM. Add it via PuTTY (see above), then re-run."
}

function Install-BootstrapKeyViaPassword {
    # First-choice fallback for a VM that doesn't already accept the bootstrap
    # key (a hand-installed VM, or one whose ISO baked a different key): log in
    # with the seed password and append the bootstrap public key to
    # authorized_keys so the rest of provisioning can use key auth.
    #
    # Native ssh can't take a password as an argument, so we let ssh prompt on
    # the console (this path only runs when a human is present). If it can't
    # authenticate, we fall through to the PuTTY instructions.
    Write-Step "Bootstrap key not accepted; installing it via password"
    $pub    = (Get-Content $BootstrapPubKey -Raw).Trim()
    $escPub = $pub.Replace("'", "'\''")
    $remoteCmd = "umask 077; mkdir -p ~/.ssh; touch ~/.ssh/authorized_keys; grep -qxF '$escPub' ~/.ssh/authorized_keys || echo '$escPub' >> ~/.ssh/authorized_keys"
    $pwOpts = @(
        "-o", "PreferredAuthentications=password,keyboard-interactive",
        "-o", "PubkeyAuthentication=no",
        "-o", "NumberOfPasswordPrompts=3",
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", "UserKnownHostsFile=$env:TEMP\construct-known_hosts",
        "-o", "ConnectTimeout=15"
    )

    # Make sure ssh prompts on the console rather than using an askpass helper.
    # ($env:X = $null leaves an empty var that can still force askpass mode, so
    # remove them outright.)
    Remove-Item Env:SSH_ASKPASS         -ErrorAction SilentlyContinue
    Remove-Item Env:SSH_ASKPASS_REQUIRE -ErrorAction SilentlyContinue

    Write-Host "    Enter the VM password for '$SeedUser' when prompted (default: $SeedPassword)." -ForegroundColor Yellow

    # SilentlyContinue so ssh's stderr isn't promoted to a terminating error by
    # the script-wide 'Stop'. The password prompt is written to the console
    # directly, so it still appears.
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    try {
        for ($i = 1; $i -le 3; $i++) {
            & ssh.exe @pwOpts "$SeedUser@$VmHost" $remoteCmd
            if ($LASTEXITCODE -eq 0) { Write-Ok "Bootstrap key installed via password"; return $true }
            Write-Warning "Authentication failed (attempt $i of 3)."
        }
    } finally {
        $ErrorActionPreference = $prevEAP
    }
    return $false
}

# --- Re-provision fast path: connect straight in as root with the saved key --

function Enter-RootKeyFastPath {
    # On a re-provision of an existing VM the root private key from the previous
    # run is already saved on this host (~\.ssh\$LocalKeyName) and its public key
    # is still authorized for root on the VM. If that key lets us in as root, we
    # switch the whole run onto it: every later SSH/SCP command runs directly as
    # root over this key -- no bootstrap key, no agent password, no sudo -- and
    # provision.sh is told not to regenerate the VM's root key, so this saved key
    # stays valid. Returns $true once script state has been switched to root;
    # $false otherwise (no saved key, or it no longer authenticates), in which
    # case the caller falls back to the bootstrap-key / agent-password path.
    $localKey = Join-Path $HOME ".ssh\$LocalKeyName"
    if (-not (Test-Path -LiteralPath $localKey)) {
        Write-Ok "No saved root key at $localKey -- using the bootstrap-key path"
        return $false
    }

    # Windows OpenSSH silently ignores a private key whose ACL is "too open" (auth
    # then fails even though the public key is authorized). Copy it to TEMP with
    # owner-only ACLs and use that copy -- the same treatment the bootstrap key
    # gets in Ensure-BootstrapKey.
    $secureKey = Join-Path $env:TEMP "ca_root_$LocalKeyName"
    Remove-Item -LiteralPath $secureKey -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath $localKey -Destination $secureKey -Force
    & icacls $secureKey /inheritance:r | Out-Null
    & icacls $secureKey /grant:r "$($env:USERNAME):F" | Out-Null

    $opts = @(
        "-i", $secureKey,
        "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", "UserKnownHostsFile=$env:TEMP\construct-known_hosts",
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=15",
        "-o", "ServerAliveInterval=15",
        "-o", "ServerAliveCountMax=4"
    )

    # Probe as the remote (root) user. Lower ErrorActionPreference so ssh's benign
    # stderr isn't promoted to a terminating error by the script-wide 'Stop'.
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & ssh.exe -n @opts "$RemoteUser@$VmHost" "true" 2>&1 | Out-Null
    $ok = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEAP

    if (-not $ok) {
        Remove-Item -LiteralPath $secureKey -Force -ErrorAction SilentlyContinue
        Write-Ok "Saved root key did not authenticate -- falling back to the bootstrap-key path"
        return $false
    }

    $script:SshOpts           = $opts
    $script:ConnectUser       = $RemoteUser
    $script:UseRootKey        = $true
    $script:LocalRootKeyPath  = $localKey
    $script:SecureRootKeyPath = $secureKey
    return $true
}

# --- Step 1: pack this repo -------------------------------------------------

function New-RepoArchive {
    $repoDir = $PSScriptRoot
    $tarPath = Join-Path $env:TEMP "construct-repo.tar.gz"
    if (Test-Path $tarPath) { Remove-Item $tarPath -Force }

    # Exclude .git (unless -IncludeGit), ISOs, the host-only settings file, and the
    # saved config backup (.construct-backup holds plaintext secrets and must never
    # be uploaded into the VM's repo copy).
    $names = @(Get-ChildItem -Force -LiteralPath $repoDir |
               Where-Object { ($IncludeGit -or $_.Name -ne ".git") -and $_.Extension -ne ".iso" -and $_.Name -ne ".construct-settings.json" -and $_.Name -ne ".construct-backup" }).Name
    Write-Step "Packing repo ($repoDir) -> $tarPath"
    & tar.exe -czf $tarPath -C $repoDir @names
    if ($LASTEXITCODE -ne 0) { throw "tar failed packing the repo (exit $LASTEXITCODE)." }
    Write-Ok "Created $([math]::Round((Get-Item $tarPath).Length / 1KB)) KB archive"
    return $tarPath
}

# --- Host-side configuration ------------------------------------------------

function Protect-SshFile {
    param([Parameter(Mandatory)][string]$Path)
    # OpenSSH refuses a private key or ~/.ssh/config that other accounts can
    # read, or that a different account OWNS ("Bad owner or permissions") --
    # seen in the field when another admin account created ~/.ssh, whose
    # inherited ACEs then leaked onto our files. Owner-only: the current user
    # owns the file and holds the sole ACE (inheritance severed). WriteAllText
    # into a pre-existing file keeps the old owner, so /setowner is needed even
    # for files we just wrote.
    & icacls $Path /setowner "$($env:USERNAME)" /C /Q | Out-Null
    & icacls $Path /inheritance:r | Out-Null
    & icacls $Path /grant:r "$($env:USERNAME):F" | Out-Null
}

function Set-HostSshConfig {
    param([string]$PrivateKeyText)

    $sshDir = Join-Path $HOME ".ssh"
    if (-not (Test-Path $sshDir)) { New-Item -ItemType Directory -Path $sshDir | Out-Null }

    # Private key (LF line endings, owner-only ACL so OpenSSH accepts it).
    $keyPath = Join-Path $sshDir $LocalKeyName
    $normalized = ($PrivateKeyText -replace "`r`n", "`n").TrimEnd("`n") + "`n"
    [System.IO.File]::WriteAllText($keyPath, $normalized)
    Protect-SshFile $keyPath
    Write-Ok "Wrote private key: $keyPath"

    # known_hosts - remove ALL stale entries for this VM (full hostname AND the
    # short alias, including hashed ones) BEFORE accepting the current key, so a
    # re-provisioned VM with a new host key doesn't trip "REMOTE HOST
    # IDENTIFICATION HAS CHANGED". ssh-keygen -R rewrites ~/.ssh/known_hosts in
    # place and leaves entries for every other host untouched.
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & ssh-keygen -R $VmHost    2>$null
    & ssh-keygen -R $HostAlias 2>$null
    # -n + a connect timeout: this runs right after the reboot was kicked off, so
    # the VM may be on its way down -- don't attach to console stdin and don't sit
    # on a long TCP timeout if it's already gone (a stale/missing key just warns below).
    & ssh -n -o StrictHostKeyChecking=accept-new -o BatchMode=yes -o ConnectTimeout=10 -i $keyPath "$RemoteUser@$VmHost" "exit" 2>$null
    $ErrorActionPreference = $prevEAP
    $kh = Join-Path $sshDir "known_hosts"
    $have = (Test-Path $kh) -and (Select-String -Path $kh -Pattern ([regex]::Escape($VmHost)) -Quiet)
    if ($have) {
        Write-Ok "Host key accepted into known_hosts"
    } else {
        Write-Warning "Could not add host key for $VmHost to known_hosts. Run 'ssh $HostAlias' manually and accept the key."
    }

    # ~/.ssh/config Host entry (read by VS Code Remote-SSH). Replace any existing
    # entry for this alias in place and leave every other Host block untouched.
    $cfg = Join-Path $sshDir "config"
    $block = @"
Host $HostAlias
    HostName $VmHost
    User $RemoteUser
    IdentityFile $keyPath
    IdentitiesOnly yes
"@

    if (Test-Path $cfg) {
        # Walk the file block-by-block: a block starts at a "Host" line and runs
        # until the next "Host" line. Drop only the block whose Host line names
        # exactly our alias (the shape we write below); keep all others verbatim.
        $kept     = New-Object System.Collections.Generic.List[string]
        $skipping = $false
        $replaced = $false
        foreach ($line in (Get-Content -LiteralPath $cfg)) {
            if ($line -match '^\s*Host\s+(.+?)\s*$') {
                $patterns = $matches[1] -split '\s+'
                if ($patterns.Count -eq 1 -and $patterns[0] -eq $HostAlias) {
                    $skipping = $true    # start dropping this block (header + body)
                    $replaced = $true
                    continue
                }
                $skipping = $false       # a different Host block: resume keeping
            }
            if (-not $skipping) { $kept.Add($line) }
        }
        $text = ($kept -join "`r`n").TrimEnd("`r", "`n")
        if ($text) { $text += "`r`n`r`n" }
        $text += $block
        [System.IO.File]::WriteAllText($cfg, $text + "`r`n")
        if ($replaced) { Write-Ok "Replaced existing Host '$HostAlias' in $cfg" }
        else           { Write-Ok "Added Host '$HostAlias' to $cfg" }
    } else {
        [System.IO.File]::WriteAllText($cfg, $block + "`r`n")
        Write-Ok "Added Host '$HostAlias' to $cfg"
    }
    Protect-SshFile $cfg
    return $keyPath
}

function Set-VsCodeRemotePlatform {
    $userDir = Join-Path $env:APPDATA "Code\User"
    if (-not (Test-Path $userDir)) {
        Write-Warning "VS Code user dir not found ($userDir); skipping remote.SSH.remotePlatform. Set it manually if needed."
        return
    }
    $path = Join-Path $userDir "settings.json"
    $key = "remote.SSH.remotePlatform"

    $settings = [pscustomobject]@{}
    if (Test-Path $path) {
        Copy-Item $path "$path.bak" -Force
        try {
            $raw = Get-Content $path -Raw
            if ($raw.Trim()) { $settings = $raw | ConvertFrom-Json -ErrorAction Stop }
        } catch {
            Write-Warning "Could not parse $path (comments/JSONC?). Not modifying it. Add this manually:"
            Write-Host "  `"$key`": { `"$HostAlias`": `"linux`" }"
            return
        }
    }

    if (-not ($settings.PSObject.Properties.Name -contains $key)) {
        $settings | Add-Member -NotePropertyName $key -NotePropertyValue ([pscustomobject]@{})
    }
    $platforms = $settings.$key
    if ($platforms.PSObject.Properties.Name -contains $HostAlias) {
        $platforms.$HostAlias = "linux"
    } else {
        $platforms | Add-Member -NotePropertyName $HostAlias -NotePropertyValue "linux"
    }

    ($settings | ConvertTo-Json -Depth 30) | Set-Content -Path $path -Encoding UTF8
    Write-Ok "Set $key -> { $HostAlias = linux } in VS Code settings"
}

function Set-OpenCodeRemote {
    # Register this VM's OpenCode remote-server URL in the OpenCode GUI desktop
    # app so the user doesn't have to add the server by hand. The app keeps its
    # state in %APPDATA%\ai.opencode.desktop\opencode.global.dat: a JSON object
    # whose "server" value is itself a JSON-ENCODED STRING holding a "list" of
    # saved servers, each shaped like { type, displayName, http: { url } }. We
    # add an entry for $Url only if no server with that exact url exists yet
    # (idempotent across re-provisions) and leave every other key -- and any
    # servers the user added themselves -- untouched.
    param(
        [Parameter(Mandatory)][string]$Url,
        [Parameter(Mandatory)][string]$DisplayName
    )

    $cfgPath = Join-Path $env:APPDATA "ai.opencode.desktop\opencode.global.dat"
    if (-not (Test-Path -LiteralPath $cfgPath)) {
        Write-Warning "OpenCode GUI config not found ($cfgPath); skipping. Add the server manually: $Url"
        return
    }

    # Parse the outer object. Bail out WITHOUT touching the file if it isn't the
    # JSON we expect, so a format change in the app can never corrupt its state.
    try {
        $raw = Get-Content -LiteralPath $cfgPath -Raw
        $top = $raw | ConvertFrom-Json -ErrorAction Stop
    } catch {
        Write-Warning "Could not parse OpenCode config ($cfgPath); not modifying it. Add the server manually: $Url"
        return
    }

    # Parse the inner "server" object out of its JSON string; synthesize a fresh
    # one if the key is missing/empty/garbled.
    $server = $null
    if (($top.PSObject.Properties.Name -contains "server") -and $top.server) {
        try { $server = $top.server | ConvertFrom-Json -ErrorAction Stop } catch { $server = $null }
    }
    if (-not $server) { $server = [pscustomobject]@{ list = @() } }
    if (-not ($server.PSObject.Properties.Name -contains "list") -or $null -eq $server.list) {
        $server | Add-Member -NotePropertyName list -NotePropertyValue @() -Force
    }

    # Already present? Match on the exact url so this is a no-op on re-provision.
    $existing = @($server.list) | Where-Object { $_.http -and $_.http.url -eq $Url }
    if ($existing.Count -gt 0) {
        Write-Ok "OpenCode server already configured ($Url)"
        return
    }

    # Append and write back: the inner object as a COMPACT JSON string (the shape
    # the app stores), then the whole file with NO BOM -- Electron's JSON.parse
    # throws on a leading byte-order mark.
    $entry = [pscustomobject]@{
        type        = "http"
        displayName = $DisplayName
        http        = [pscustomobject]@{ url = $Url }
    }
    $server.list = @($server.list) + $entry

    Copy-Item -LiteralPath $cfgPath "$cfgPath.bak" -Force
    $serverJson = $server | ConvertTo-Json -Depth 30 -Compress
    if ($top.PSObject.Properties.Name -contains "server") {
        $top.server = $serverJson
    } else {
        $top | Add-Member -NotePropertyName server -NotePropertyValue $serverJson
    }
    $json = $top | ConvertTo-Json -Depth 30
    [System.IO.File]::WriteAllText($cfgPath, $json, (New-Object System.Text.UTF8Encoding $false))
    Write-Ok "Added OpenCode server '$DisplayName' ($Url)"
    Write-Host "    Fully quit and reopen the OpenCode GUI app to pick it up (if it's open now, it may overwrite this on exit)." -ForegroundColor DarkGray
}

function Invoke-Net {
    # Run net.exe tolerating non-zero exits and stderr WITHOUT them becoming
    # terminating errors. This script sets $ErrorActionPreference = 'Stop', and on
    # PowerShell 7.4+ ($PSNativeCommandUseErrorActionPreference defaults to $true)
    # a native command that exits non-zero then THROWS under 'Stop' -- which is how
    # an expected failure like `net use Z: /delete` on an unmapped drive ("The
    # network connection could not be found") could abort the whole provision.
    # Returns @{ Code = <int>; Output = <string> }.
    param([Parameter(ValueFromRemainingArguments)][string[]]$NetArgs)

    # These LOCAL overrides shadow the script's prefs for this function's scope
    # only (auto-reverting on return), so an expected non-zero exit -- e.g. `net
    # use Z: /delete` on an unmapped drive -- can't become a terminating error.
    # PS 7.4+ would otherwise throw under the script's $ErrorActionPreference =
    # 'Stop'; on Windows PowerShell 5.1 the PSNative* variable doesn't exist and
    # assigning a harmless local is a no-op.
    $ErrorActionPreference = 'Continue'
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        $raw  = & net.exe @NetArgs 2>&1
        $code = $LASTEXITCODE
    } catch {
        $raw  = $_.Exception.Message
        $code = 1
    }
    return @{ Code = $code; Output = (@($raw | ForEach-Object { "$_" }) -join "`n") }
}

function Get-DriveMaps {
    # One snapshot of drive-letter usage, so we don't shell out per letter.
    # Returns @{ Net = @{ <L> = <remote UNC> }; Local = @{ <L> = $true } }, where
    # Net covers network mappings (from `net use`, incl. disconnected ones) and
    # Local covers every filesystem drive PowerShell sees (local disks + mappings).
    $net = @{}
    foreach ($line in ((Invoke-Net use).Output -split "`n")) {
        # e.g. "OK           Z:        \\host\share   Microsoft Windows Network"
        if ($line -match '\b([A-Za-z]):\s+(\\\\\S+)') { $net[$matches[1].ToUpper()] = $matches[2] }
    }
    $local = @{}
    foreach ($d in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        if ($d.Name.Length -eq 1) { $local[$d.Name.ToUpper()] = $true }
    }
    return @{ Net = $net; Local = $local }
}

function Get-DriveState {
    # Classify a single drive letter against a snapshot from Get-DriveMaps and our
    # target UNC: 'ours' (a mapping to THIS share), 'other' (a different share or a
    # local disk), or 'free'.
    param([string]$Letter, [hashtable]$Maps, [string]$OurUnc)
    $L = ($Letter.TrimEnd(':')).ToUpper()
    if ($Maps.Net.ContainsKey($L)) {
        if ($Maps.Net[$L].TrimEnd('\') -ieq $OurUnc.TrimEnd('\')) { return 'ours' }
        return 'other'
    }
    if ($Maps.Local.ContainsKey($L)) { return 'other' }
    return 'free'
}

function Select-AlternateDriveLetter {
    # The preferred letter is taken by something that isn't our share. Ask which
    # free letter to use instead (arrow-key menu, like the rest of the installer),
    # offering a "skip" choice. On a non-interactive host, don't block -- take the
    # first free letter (or skip if none). Returns a bare letter, or $null to skip.
    param([string]$Preferred, [string]$OccupiedBy, [hashtable]$Maps, [string]$OurUnc)

    $free = @()
    foreach ($code in (90..68)) {           # 'Z'..'D'
        $l = [string][char]$code
        if ((Get-DriveState -Letter $l -Maps $Maps -OurUnc $OurUnc) -eq 'free') { $free += $l }
    }
    if ($free.Count -eq 0) {
        Write-Warning "Drive ${Preferred}: is in use ($OccupiedBy) and no other drive letter is free; skipping the share mount."
        return $null
    }

    # Auto-pick (don't prompt) when there's no interactive console, no menu helper, or
    # the caller asked for non-interactive/auto (control-panel or upper-script launch).
    $haveMenu = [bool](Get-Command Show-Menu -ErrorAction SilentlyContinue)
    if ([Console]::IsInputRedirected -or -not $haveMenu -or $Auto -or $NonInteractive) {
        Write-Warning "Drive ${Preferred}: is in use ($OccupiedBy); using $($free[0]): for the workspace share."
        return $free[0]
    }

    Write-Host ""
    Write-Host "Drive ${Preferred}: is already in use by $OccupiedBy (not the agent VM share)." -ForegroundColor Yellow
    $opts = @($free | ForEach-Object { "Map the workspace share to ${_}:" })
    $opts += "Skip - don't map the share now"
    $idx = Show-Menu -Title "Which drive letter should the workspace share use?" -Options $opts -Default 0
    if ($idx -ge $free.Count) { return $null }   # chose "Skip"
    return $free[$idx]
}

function Mount-RepoShare {
    # Map the VM's workspace SMB share to a drive letter on this host, saving the
    # credentials so it reconnects automatically (across logon/reboot). Mirrors the
    # other host-config helpers: best-effort, never throws -- on failure it prints
    # the manual `net use` command and moves on. Returns the mapped "Z:" string on
    # success, otherwise $null.
    param(
        [Parameter(Mandatory)][string]$UncPath,   # \\host\share
        [Parameter(Mandatory)][string]$SmbUser,
        [Parameter(Mandatory)][string]$SmbPassword,
        [string]$Preferred = "Z"
    )

    # Decide which drive letter to use:
    #   - If some drive ALREADY maps to this share (e.g. a prior run that picked a
    #     non-default letter), reuse it and just refresh the credentials.
    #   - Else if the preferred letter is free, use it (no prompt).
    #   - Else the preferred letter is taken by something that isn't our share, so
    #     ask the user which free letter to use instead (or skip).
    $maps = Get-DriveMaps
    $pref = ($Preferred.TrimEnd(':')).ToUpper()

    $existing = ""
    foreach ($code in (90..68)) {           # 'Z'..'D'
        $l = [string][char]$code
        if ((Get-DriveState -Letter $l -Maps $maps -OurUnc $UncPath) -eq 'ours') { $existing = $l; break }
    }

    if ($existing) {
        $device = "${existing}:"
    } else {
        $prefState = Get-DriveState -Letter $pref -Maps $maps -OurUnc $UncPath
        if ($prefState -eq 'free') {
            $device = "${pref}:"
        } else {
            $occupiedBy = if ($maps.Net.ContainsKey($pref)) { $maps.Net[$pref] } else { "a local drive" }
            $alt = Select-AlternateDriveLetter -Preferred $pref -OccupiedBy $occupiedBy -Maps $maps -OurUnc $UncPath
            if (-not $alt) {
                Write-Warning "Skipping the workspace-share mount. Map it later from this host with:"
                Write-Host "      net use <drive>: $UncPath /user:$SmbUser <password> /savecred /persistent:yes" -ForegroundColor Cyan
                return $null
            }
            $device = "${alt}:"
        }
    }

    # Drop any existing mapping on the chosen letter first, so we can recreate it
    # with fresh credentials (our own stale mapping, or a leftover from before).
    # Expected to "fail" when nothing is mapped -- Invoke-Net swallows that.
    Invoke-Net use $device /delete /y | Out-Null

    # Pre-seed the credential in Windows Credential Manager keyed to the server, so
    # the saved login survives even if `net use` itself is finicky about combining
    # an inline password with /savecred on some Windows builds. Best-effort.
    $server = $UncPath.TrimStart('\').Split('\')[0]
    if ($server) {
        $prevEAP = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try { & cmdkey.exe /add:$server /user:$SmbUser /pass:$SmbPassword 2>&1 | Out-Null } catch { }
        $ErrorActionPreference = $prevEAP
    }

    # net use <device> <unc> <password> /user:<user> /savecred /persistent:yes
    # Generated SMB passwords are alphanumeric, so no embedded quotes/spaces to
    # escape. /savecred stores the credential in Windows Credential Manager;
    # /persistent:yes restores the mapping at logon and reconnects when the VM
    # (briefly down during the post-provision reboot) comes back. Retry a few times
    # in case the share isn't answering the instant we ask (service still starting).
    $last = ""
    for ($attempt = 1; $attempt -le 4; $attempt++) {
        # Form A -- the requested form: inline password + /savecred.
        $r = Invoke-Net use $device $UncPath $SmbPassword "/user:$SmbUser" /savecred /persistent:yes
        if ($r.Code -eq 0) {
            Write-Ok "Mounted $UncPath -> $device (credentials saved, persistent)"
            return $device
        }
        $last = $r.Output
        Invoke-Net use $device /delete /y | Out-Null
        # Form B -- some Windows builds reject an inline password together with
        # /savecred; fall back to mapping with the cmdkey-stored credential.
        $r = Invoke-Net use $device $UncPath /persistent:yes
        if ($r.Code -eq 0) {
            Write-Ok "Mounted $UncPath -> $device (credentials saved, persistent)"
            return $device
        }
        $last = $r.Output
        # Clean up a half-made mapping before retrying so the letter is free.
        Invoke-Net use $device /delete /y | Out-Null
        if ($attempt -lt 4) {
            Write-Host "    Share not ready yet (attempt $attempt/4); retrying..." -ForegroundColor DarkGray
            Start-Sleep -Seconds 3
        }
    }

    Write-Warning "Could not auto-mount $UncPath ($($last -replace '\s+', ' '))."
    Write-Host "    The share is set up on the VM; map it from this host once it's reachable:" -ForegroundColor DarkGray
    Write-Host "      net use ${device} $UncPath /user:$SmbUser <password> /savecred /persistent:yes" -ForegroundColor Cyan
    return $null
}

function Select-Projects {
    # Prompt the user to pick which project profiles from projects/ to load.
    # Returns a comma-separated PROJECTS value (or "default" if none chosen). The
    # real UI is the checkbox-style Select-ProjectProfiles in the shared lib; the
    # comma prompt below is only a fallback for when that lib isn't alongside us.
    # Config-sync v2: prefer the shared config projects dir; fall back to the
    # shipped projects/ in the repo checkout (pre-migration / degraded mode).
    $projDir = if (Get-Command Get-ConstructConfigProjectsDir -ErrorAction SilentlyContinue) {
        Get-ConstructConfigProjectsDir -ScriptsDir $PSScriptRoot
    } else { Join-Path $PSScriptRoot "projects" }
    if (Get-Command Select-ProjectProfiles -ErrorAction SilentlyContinue) {
        return (Select-ProjectProfiles -ProjectsDir $projDir)
    }

    # --- Fallback (shared lib unavailable): plain comma-list prompt -----------
    if (-not (Test-Path $projDir)) { return "default" }
    $skip = @("default", "project.schema")
    $available = @(Get-ChildItem -LiteralPath $projDir -Filter *.json -File |
                   Where-Object { $skip -notcontains $_.BaseName } |
                   Sort-Object Name)
    if ($available.Count -eq 0) { return "default" }

    Write-Step "Select project configs to load"
    Write-Host "    Each profile installs its runtimes (node/python/.NET) and declares its repos." -ForegroundColor White
    for ($i = 0; $i -lt $available.Count; $i++) {
        Write-Host ("      {0}. {1}" -f ($i + 1), $available[$i].BaseName) -ForegroundColor Yellow
    }
    Write-Host "    Enter numbers or names (comma-separated), 'all', or press Enter for none." -ForegroundColor White
    $sel = Read-Host "    Projects"
    if ([string]::IsNullOrWhiteSpace($sel)) { return "default" }
    if ($sel.Trim().ToLower() -eq "all") {
        return (($available | ForEach-Object { $_.BaseName }) -join ",")
    }

    $chosen = New-Object System.Collections.Generic.List[string]
    foreach ($tok in ($sel -split ",")) {
        $t = $tok.Trim()
        if ($t -eq "") { continue }
        if ($t -match '^[0-9]+$') {
            $idx = [int]$t - 1
            if ($idx -ge 0 -and $idx -lt $available.Count) { $chosen.Add($available[$idx].BaseName) }
            else { Write-Warning "No project numbered '$t' -- ignoring." }
        } else {
            $match = $available | Where-Object { $_.BaseName -eq $t } | Select-Object -First 1
            if ($match) { $chosen.Add($match.BaseName) } else { Write-Warning "Unknown project '$t' -- ignoring." }
        }
    }
    $uniq = @($chosen | Select-Object -Unique)
    if ($uniq.Count -eq 0) { return "default" }
    return ($uniq -join ",")
}

# ============================================================================
# Main
# ============================================================================

$script:ProvisionResult = $null
$script:ProvisionFailureMessage = ""
$script:ProvisionRawLines = @()
if ($Action -eq 'provision') {
    $global:ConstructProvisionHadErrors = $false
    $global:ConstructProvisionErrors = @()
    $global:ConstructProvisionFailureMessage = ""
}

function Show-ProvisionResultScreen {
    param(
        [AllowNull()]$Result,
        [string]$FailureMessage = ""
    )

    Write-Host ""
    Write-Host "============================================================" -ForegroundColor DarkGray
    if ($FailureMessage) {
        Write-Host "RESULT: PROVISIONING FAILED" -ForegroundColor Red
    } elseif ($Result -and $Result.ErrorCount -gt 0) {
        Write-Host ("RESULT: PROVISIONING COMPLETED WITH {0} ERROR(S)" -f $Result.ErrorCount) -ForegroundColor Red
    } else {
        Write-Host "RESULT: VM PROVISIONED CLEANLY" -ForegroundColor Green
    }

    if ($Result) {
        foreach ($item in @($Result.Errors)) {
            Write-Host ("  - {0} (exit {1})" -f $item.Title, $item.ExitCode) -ForegroundColor Red
        }
    }
    if ($FailureMessage) {
        Write-Host "  - $FailureMessage" -ForegroundColor Red
    }
    Write-Host "============================================================" -ForegroundColor DarkGray

    # Copyable AI-agent fix prompt: built only for VM-side step failures (items
    # with a Title), not host-side messages. The user pastes this into their AI
    # coding agent on the VM (Claude Code over VS Code Remote-SSH) to diagnose.
    if ($Result) {
        $stepLines = @()
        foreach ($item in @($Result.Errors)) {
            if (-not $item.Title) { continue }
            $line = "- Step '$($item.Title)' failed (exit $($item.ExitCode))"
            if ($item.LogPath) {
                $line += "; log: $($item.LogPath)"
            }
            $stepLines += $line
        }
        if ($stepLines.Count -gt 0) {
            Write-Host ""
            Write-Host "  Paste this into your AI coding agent on the VM to diagnose:" -ForegroundColor Yellow
            Write-Host "  ............................................................" -ForegroundColor DarkGray
            Write-Host ""
            $logPaths = @($Result.Errors | Where-Object { $_.LogPath } | ForEach-Object { $_.LogPath })
            $logRef = if ($logPaths.Count -eq 1) {
                "Read the provisioning log at $($logPaths[0])"
            } elseif ($logPaths.Count -gt 1) {
                "Read the provisioning logs at: $($logPaths -join ', ')"
            } else {
                "Check the provisioning output above"
            }
            Write-Host "  On the last Construct provisioning run, the following step(s) failed:" -ForegroundColor White
            foreach ($sl in $stepLines) {
                Write-Host "  $sl" -ForegroundColor White
            }
            Write-Host "  $logRef and diagnose and fix the underlying problem." -ForegroundColor White
            Write-Host ""
            Write-Host "  ............................................................" -ForegroundColor DarkGray
        }
    }
}

# Run the whole flow inside try/finally so that, when launched on its own (not
# -Auto), the window pauses at the end -- on success OR error -- instead of
# closing before the output can be read. In -Auto mode the calling script owns
# the pause, so we stay silent here.
try {

Write-Host ""
Write-Host "The Construct VM provisioner" -ForegroundColor White
Write-Host "Target: $VmHost  |  seed user: $SeedUser  |  final user: $RemoteUser" -ForegroundColor DarkGray
Write-Host ""

# Project selection + git identity are only meaningful for a real provision;
# -Action export just pulls the current config back and must not prompt for them.
$gitIdentity = @{ Name = ""; Email = "" }
if ($Action -eq 'provision') {
    # Let the user pick project profiles unless -Projects was passed explicitly.
    if (-not $PSBoundParameters.ContainsKey('Projects')) {
        $Projects = Select-Projects
    }
    Write-Ok "Projects: $Projects"

    # Resolve the git identity to apply on the VM. Defaults come from the saved
    # settings file then this host's own git identity; the choice is saved so future
    # reprovisions don't need to re-specify it. When an upper script already supplied
    # both values they're used as-is (no prompt). Falls back to the raw params if the
    # shared lib wasn't found.
    if (Get-Command Resolve-GitIdentity -ErrorAction SilentlyContinue) {
        $giParams = @{ Dir = $PSScriptRoot }
        if ($PSBoundParameters.ContainsKey('GitUserName')) { $giParams['Name']  = $GitUserName }
        if ($PSBoundParameters.ContainsKey('GitEmail'))    { $giParams['Email'] = $GitEmail }
        if ($giParams.ContainsKey('Name') -and $giParams.ContainsKey('Email')) { $giParams['NoPrompt'] = $true }
        $gitIdentity = Resolve-GitIdentity @giParams
    } else {
        $gitIdentity = @{ Name = $GitUserName; Email = $GitEmail }
    }
    if ($gitIdentity.Name -or $gitIdentity.Email) {
        Write-Ok ("Git identity: {0} <{1}>" -f $gitIdentity.Name, $gitIdentity.Email)
    }
}

Ensure-Tar
Ensure-OpenSSH

# A ~/.ssh/config (or saved key) with a foreign owner or extra-user ACEs makes
# OpenSSH abort EVERY invocation ("Bad owner or permissions") -- including this
# script's own ssh/scp calls below -- so repair the ACLs before the first
# connection attempt, not just when the files are (re)written at the end.
foreach ($f in @((Join-Path $HOME ".ssh\config"), (Join-Path $HOME ".ssh\$LocalKeyName"))) {
    if (Test-Path -LiteralPath $f) { Protect-SshFile $f }
}

$archivePath = New-RepoArchive
Ensure-VmReachable

# Accept the VM's host key before any SSH operations (overwrite to clear stale keys from previous VMs).
Write-Step "Accepting VM host key"
$prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
& ssh-keyscan -T 5 $VmHost 2>$null | Out-File -Encoding ascii "$env:TEMP\construct-known_hosts"
$ErrorActionPreference = $prevEAP
Write-Ok "Host key stored"

# Re-provision fast path: if the root key saved from a previous run still lets us
# in as root, use it for the whole run -- no bootstrap key, no agent password, no
# sudo -- and leave the VM's root key untouched. Only if that doesn't work do we
# fall back to the bootstrap-key path below.
Write-Step "Checking for a saved root key (re-provision fast path)"
if (Enter-RootKeyFastPath) {
    Write-Ok "Connected as root with the saved key ($script:LocalRootKeyPath)"
} else {
    # Fall back to the bootstrap key. Autoinstall VMs already have it authorized;
    # a hand-installed (or freshly recreated) VM does not — try installing it via
    # the seed password (you'll be prompted; default is 'agent'), and if that
    # can't authenticate, fall back to the manual PuTTY instructions.
    Ensure-BootstrapKey

    Write-Step "Checking bootstrap key authentication"
    if (Test-KeyAuth) {
        Write-Ok "Bootstrap key accepted"
    } else {
        Install-BootstrapKeyViaPassword | Out-Null
        if (-not (Test-KeyAuth)) { Throw-BootstrapKeyHelp }
        Write-Ok "Key authentication working"
    }

    # Confirm we can sudo before any privileged step. On a re-provision the agent
    # login password may differ from the seed default (a custom password set on a
    # previous run), which would otherwise make the first sudo command below fail.
    Write-Step "Checking sudo access"
    Ensure-Sudo
}

# Upload the archive via SCP (remove any stale copy owned by root from a previous run).
Write-Step "Uploading repo archive to $RemoteArchive"
Invoke-Ssh -Sudo -Command "rm -f $RemoteArchive"
Invoke-Scp -LocalPath $archivePath -RemotePath "/tmp/construct-repo.tar.gz"
Write-Ok "Uploaded"

# Unpack into /opt/construct/repo.
Write-Step "Unpacking repo on the VM"
Invoke-Ssh -Sudo -Command "mkdir -p /opt/construct && rm -rf /opt/construct/repo && mkdir -p /opt/construct/repo && tar -xzf $RemoteArchive -C /opt/construct/repo && chown -R ${SeedUser}:${SeedUser} /opt/construct"
Write-Ok "Repo in place at /opt/construct/repo"

# ── -Action export: pull the current config back to the host, then stop ──────
# The repo (with the current export/scan scripts) is now on the VM. We connected
# above exactly like a provision would; from here we only read, never change the
# VM, and we never reboot.
if ($Action -eq 'export') {
    if (-not $BackupDir) { throw "-Action export requires -BackupDir." }
    New-Item -ItemType Directory -Force -Path $BackupDir | Out-Null

    if ($ScanReposOnly) {
        Write-Step "Scanning project repos for unsaved work"
        # Redirect to a file on the VM so any login-shell banner can't pollute the
        # JSON; `&&` so a failed scan propagates (it doesn't get masked by chmod)
        # and chmod runs only on success (so the seed user can pull it on the
        # bootstrap path). The finally always removes the VM-side file.
        try {
            Invoke-Ssh -Sudo -Command "bash /opt/construct/repo/bin/scan-repos.sh > /tmp/construct-repo-scan.json 2>/dev/null && chmod 644 /tmp/construct-repo-scan.json"
            Invoke-ScpFrom -RemotePath "/tmp/construct-repo-scan.json" -LocalPath (Join-Path $BackupDir "repo-scan.json")
        } finally {
            try { Invoke-Ssh -Sudo -Command "rm -f /tmp/construct-repo-scan.json" } catch { }
        }
        Write-Ok "Repo scan saved to $(Join-Path $BackupDir 'repo-scan.json')"
    } else {
        Write-Step "Exporting agent config from the VM"
        # The tarball holds plaintext secrets, so the finally ALWAYS removes the
        # VM-side copy -- even if the export, download, or extract throws. The
        # `&& chmod` keeps a failed export from being reported as success.
        $tgz = Join-Path $BackupDir "backup.tar.gz"
        try {
            Write-Host "  --- live export output ---" -ForegroundColor DarkGray
            Invoke-SshStream -Sudo -Command "EXPORT_HOME=/root INCLUDE_AUTH=true INCLUDE_HISTORY=true OUT=/tmp/construct-config-backup.tar.gz CONFIG_FILE=/etc/construct/config.env REPO_DIR=/opt/construct/repo PROJECTS_STORE=/opt/construct/projects bash /opt/construct/repo/bin/export-config.sh && chmod 644 /tmp/construct-config-backup.tar.gz"
            Write-Host "  --- end export output ---" -ForegroundColor DarkGray
            Invoke-ScpFrom -RemotePath "/tmp/construct-config-backup.tar.gz" -LocalPath $tgz
        } finally {
            try { Invoke-Ssh -Sudo -Command "rm -f /tmp/construct-config-backup.tar.gz" } catch { }
        }
        Write-Ok "Backup saved to $tgz"

        # Extract on the host for the project-profile merge below + inspection.
        $extract = Join-Path $BackupDir "extracted"
        # A prior extract holds deep agent-session transcripts whose paths exceed
        # Windows MAX_PATH; a plain Remove-Item -Recurse chokes on them with
        # "Could not find a part of the path '...jsonl'". Remove-TreeRobust falls
        # back to a robocopy mirror-empty that handles long paths.
        Remove-TreeRobust -Path $extract
        New-Item -ItemType Directory -Force -Path $extract | Out-Null
        & tar.exe -xzf $tgz -C $extract
        if ($LASTEXITCODE -ne 0) { throw "Failed to extract the backup ($tgz)." }

        # Merge generated project profiles into the config projects dir (config-sync
        # v2: shared %LOCALAPPDATA%\The-Construct\config\projects), never overwriting
        # an existing profile of the same name. Falls back to the shipped repo checkout's
        # projects/ when the config dir hasn't been initialized yet (degraded mode).
        $genDir  = Join-Path $extract "projects"
        $projDir = if (Get-Command Get-ConstructConfigProjectsDir -ErrorAction SilentlyContinue) {
            Get-ConstructConfigProjectsDir -ScriptsDir $PSScriptRoot
        } else { Join-Path $PSScriptRoot "projects" }
        $added = @()
        if (Test-Path -LiteralPath $genDir) {
            if (-not (Test-Path -LiteralPath $projDir)) { New-Item -ItemType Directory -Force -Path $projDir | Out-Null }
            foreach ($pf in @(Get-ChildItem -LiteralPath $genDir -Filter *.json -File -ErrorAction SilentlyContinue)) {
                $dest = Join-Path $projDir $pf.Name
                if (Test-Path -LiteralPath $dest) {
                    Write-Host "    project profile already exists, keeping: $($pf.BaseName)" -ForegroundColor DarkGray
                    continue
                }
                Copy-Item -LiteralPath $pf.FullName -Destination $dest
                $added += $pf.BaseName
            }
        }
        if ($added.Count -gt 0) {
            Write-Ok ("Added project profile(s): {0}" -f ($added -join ", "))
        } else {
            Write-Ok "No new project profiles to add"
        }
    }

    # Local cleanup (mirrors the end-of-provision cleanup) and stop here -- export
    # mode never provisions or reboots. The finally block owns the optional pause.
    Remove-Item "$env:TEMP\construct-known_hosts" -Force -ErrorAction SilentlyContinue
    if ($script:SecureKeyPath)     { Remove-Item -LiteralPath $script:SecureKeyPath     -Force -ErrorAction SilentlyContinue }
    if ($script:SecureRootKeyPath) { Remove-Item -LiteralPath $script:SecureRootKeyPath -Force -ErrorAction SilentlyContinue }
    Write-Host ""
    Write-Host "Done (config export)." -ForegroundColor Green
    return
}

# ── Config-sync v2 (spec section 8 / D13): sync tick before provisioning ────
# After SSH is up and the repo archive is uploaded, but BEFORE provision.sh runs:
# run the config sync so the VM store gets seeded/updated from the host config
# repo. The tick's write-back seeds/updates the VM store so generate-runtime-config.sh
# resolves from it. On Conflict/Blocked, STOP provisioning with the resolve-commit-
# rerun message unless -AutoResolve handled it. Degraded (no git): the lib call
# does the additive seed -- still call it.
if (Get-Command Initialize-ConstructConfigStore -ErrorAction SilentlyContinue) {
    $syncConfigDir = Initialize-ConstructConfigStore -ScriptsDir $PSScriptRoot
    if ((Get-Command Test-ConstructGitAvailable -ErrorAction SilentlyContinue) -and
        (Test-ConstructGitAvailable) -and
        (Get-Command Initialize-ConstructConfigRepo -ErrorAction SilentlyContinue)) {
        Initialize-ConstructConfigRepo -ConfigDir $syncConfigDir | Out-Null
    }

    # Backstop (spec section 9): fold profiles captured in the reinstall backup
    # back into the host config repo BEFORE the sync tick. Normally the pre-wipe
    # tick already carried the VM's profiles home; but when it could not (VM
    # unreachable, git hiccup, sync never having worked on this host), the
    # backup's extracted store copy is the ONLY surviving source -- without this
    # merge a reinstall provisions a blank store and every profile silently
    # vanishes (observed in the field). Additive only: an existing host profile
    # of the same name always wins, reserved names and invalid files are skipped.
    if ($RestoreDir) {
        $bkProjDir  = Join-Path $RestoreDir "extracted\projects"
        $cfgProjDir = Join-Path $syncConfigDir "projects"
        if (Test-Path -LiteralPath $bkProjDir) {
            $restoredProfiles = @()
            foreach ($f in @(Get-ChildItem -LiteralPath $bkProjDir -Filter *.json -File -ErrorAction SilentlyContinue)) {
                $bkName = $f.BaseName
                if ($script:RESERVED_PROFILE_NAMES -contains $bkName.ToLowerInvariant()) { continue }
                if (Test-Path -LiteralPath (Join-Path $cfgProjDir "$bkName.json")) { continue }
                try { $bkObj = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json } catch {
                    Write-Warning "Backup profile '$bkName' is not valid JSON; not restored"
                    continue
                }
                if (Get-Command Test-ConstructProfile -ErrorAction SilentlyContinue) {
                    $bkValid = Test-ConstructProfile -Name $bkName -Object $bkObj
                    if (-not $bkValid.Ok) {
                        Write-Warning "Backup profile '$bkName' failed validation ($($bkValid.Errors -join '; ')); not restored"
                        continue
                    }
                }
                Copy-Item -LiteralPath $f.FullName -Destination (Join-Path $cfgProjDir "$bkName.json") -Force
                $restoredProfiles += $bkName
            }
            if ($restoredProfiles.Count -gt 0) {
                Write-Ok "Restored $($restoredProfiles.Count) project profile(s) from the backup into the host config: $($restoredProfiles -join ', ')"
            }
        }
    }
    if (Get-Command Invoke-ConstructConfigSync -ErrorAction SilentlyContinue) {
        # Snapshot host profiles so anything the tick brings UP from the VM (an
        # agent-created profile) can join this run's selection below.
        $syncProjDir = Join-Path $syncConfigDir "projects"
        $profilesBeforeSync = @(Get-ChildItem -LiteralPath $syncProjDir -Filter *.json -File -ErrorAction SilentlyContinue |
            ForEach-Object { $_.BaseName })
        # The config-sync engine's default SSH invoker (Invoke-ConstructVmSsh)
        # needs ~/.ssh/agent_vm_ed25519 or a Host alias -- neither exists yet
        # on a fresh install.  Route through this script's already-authenticated
        # SSH session ($script:SshOpts / $script:ConnectUser) instead.
        #
        # The payload is piped through stdin (not embedded in the command line)
        # because Write-ConstructVmStore's batch script for a fresh-VM seed
        # with many profiles can exceed Windows' ~32K native argument limit
        # once base64-encoded.  The remote side reads stdin into a temp file,
        # decodes it, and executes it -- the same base64-transport idea as
        # Invoke-ConstructVmSsh, just via stdin instead of argument.  The
        # Invoke-Ssh-style sudo wrapper handles the bootstrap-key case where
        # $script:ConnectUser is the seed user rather than root.
        $provisionSshInvoker = {
            param([string]$BashCommand)
            $scriptLf = ($BashCommand -replace "`r`n", "`n")
            $b64 = [Convert]::ToBase64String(
                [System.Text.Encoding]::UTF8.GetBytes($scriptLf))
            # The remote command is short and fixed-size; the large base64
            # payload travels through stdin, not the command-line argument.
            # 'cat' reads stdin into a temp file, base64 decodes it, then
            # bash runs the decoded script.
            # base64 -di, not -d: Windows PowerShell 5.1 terminates the piped
            # stdin string with CRLF, and strict GNU base64 rejects the stray
            # \r ("invalid input", exit 1) -- the read then looked like an
            # unreachable VM and fresh installs seeded zero profiles (the
            # original work-PC zero-repos bug). -i ignores non-alphabet bytes,
            # which also keeps clean pwsh-7/LF payloads working unchanged.
            $stdinDecode = "f=`$(mktemp) && cat > `"`$f.b64`" && base64 -di < `"`$f.b64`" > `"`$f`" && rm -f `"`$f.b64`""
            if ($script:UseRootKey) {
                # Connected as root via saved key -- no sudo needed.
                # Wrap in bash -lc for login-shell PATH (same as Invoke-Ssh).
                # The stdin decode has no single quotes, so this is clean.
                $toRun = "bash -lc '$stdinDecode && bash `"`$f`"; rc=`$?; rm -f `"`$f`"; exit `$rc'"
            } else {
                # Bootstrap key (agent user): decode stdin first (as agent),
                # then pipe the password to sudo for the decoded script.
                # No outer bash -lc wrapper -- the remote sshd login shell
                # handles $f expansion, and sudo elevates the inner bash.
                $escPw = $SeedPassword.Replace("'", "'\''")
                $toRun = "$stdinDecode && printf '%s\n' '$escPw' | sudo -S -p '' bash `"`$f`"; rc=`$?; rm -f `"`$f`"; exit `$rc"
            }
            # EAP 'Continue', not 'SilentlyContinue': 5.1 discards native-stderr
            # ErrorRecords under SilentlyContinue even when 2>-redirected, which
            # left the field warning as a bare "(exit 1)" with the remote
            # base64 error erased. Continue lets the records reach the file;
            # the 2> redirect keeps them off the console.
            $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
            $errFile = [System.IO.Path]::GetTempFileName()
            try {
                # Pipe the base64 through stdin (no -n flag); ssh reads from
                # the pipe and gets EOF when it closes -- no console-blocking.
                # stderr goes to a temp file (not $null): when the remote command
                # fails, ssh's own message (auth, host key, connection) is the
                # diagnosis, and a discarded stderr is how this seed once failed
                # with a clean-looking console and a repo-less VM.
                $output = $b64 | & ssh.exe @script:SshOpts "$($script:ConnectUser)@$VmHost" $toRun 2>$errFile
                $code = $LASTEXITCODE
                if ($null -eq $code) { $code = -1 }
                if ($code -ne 0) {
                    $errTail = ""
                    try { $errTail = ((Get-Content -LiteralPath $errFile -ErrorAction SilentlyContinue | Select-Object -Last 3) -join " | ") } catch { }
                    Write-Warning "Config-sync SSH to the VM failed (exit ${code})$(if ($errTail) { ": $errTail" })"
                }
                $outStr = if ($null -ne $output) { ($output -join "`n") } else { "" }
                return [pscustomobject]@{ Code = $code; Output = $outStr }
            } catch {
                return [pscustomobject]@{ Code = -1; Output = "" }
            } finally {
                Remove-Item -LiteralPath $errFile -ErrorAction SilentlyContinue
                $ErrorActionPreference = $prev
            }
        }
        $syncArgs = @{
            ConfigDir       = $syncConfigDir
            VmHost          = $VmHost
            SshReadInvoker  = $provisionSshInvoker
            SshWriteInvoker = $provisionSshInvoker
        }
        if ($PSBoundParameters.ContainsKey('AutoResolve')) { $syncArgs['AutoResolve'] = $AutoResolve }
        $syncResult = Invoke-ConstructConfigSync @syncArgs
        if ($syncResult.Conflict -or $syncResult.Blocked) {
            $syncReason = if ($syncResult.Reason) { $syncResult.Reason } else { "Merge conflict detected." }
            throw "Config sync conflict: $syncReason -- resolve the conflict in the config repo ($syncConfigDir), commit, and re-run."
        }
        # Surface the tick's warnings: they carry the only trace of a skipped VM
        # side ("VM unreachable"), a degraded run, or invalid profiles. Swallowing
        # them once let a fresh install print "Config sync completed" while the
        # store seed had silently never happened (observed in the field).
        foreach ($w in @($syncResult.Warnings)) {
            if ($w) { Write-Warning "Config sync: $w" }
        }
        if ($syncResult.Ran) { Write-Ok "Config sync completed" }
        $hostHasProfiles = @($profilesBeforeSync | Where-Object {
            $script:RESERVED_PROFILE_NAMES -notcontains $_.ToLowerInvariant()
        }).Count -gt 0
        # HARD STOPS when the host has profiles the VM must receive. Provisioning
        # continues into generate-runtime-config.sh + checkout right after this,
        # so any silently-skipped seed here becomes "zero repos cloned" with a
        # clean-looking log -- fail loudly instead:
        #  - VmReadOk=$false: the engine could not read the VM store over SSH.
        #  - Ran=$false: the tick bailed before the VM side (repo init failed --
        #    e.g. git "dubious ownership" when an elevated/different-admin console
        #    doesn't own the config repo -- or the sync lock was stuck).
        #  - Seeded with a $null WriteBack: the seed write itself failed. (Seeded
        #    with zero host profiles is a valid no-op and hostHasProfiles=false.)
        if ($hostHasProfiles -and $syncResult.VmReadOk -eq $false) {
            throw "Config sync could not READ the VM store over SSH, so the project profiles were not seeded -- a fresh install would provision with zero repos.  Check the SSH warnings above, verify connectivity to $VmHost, and re-run."
        }
        if ($hostHasProfiles -and -not $syncResult.Ran) {
            $ranReason = if ($syncResult.Reason) { $syncResult.Reason } else { "unknown" }
            throw "Config sync did not run (reason: $ranReason), so the project profiles were not seeded to the VM.  Fix the cause above (config repo ownership/lock) and re-run.  Config dir: $syncConfigDir"
        }
        if ($syncResult.Seeded -and $null -eq $syncResult.WriteBack -and $hostHasProfiles) {
            throw "Config sync attempted to seed project profiles to the VM but the write-back failed -- the VM store is empty.  Verify SSH connectivity to $VmHost and re-run."
        }
        # Selected projects that have NO profile in this console's host store can
        # never seed or clone. The classic cause: this console is elevated under a
        # DIFFERENT account (UAC admin credentials), whose %LOCALAPPDATA% store is
        # empty -- the selection (passed via -Projects) still lists the profiles
        # the real user sees. Warn with the resolved store path so that mismatch
        # is visible instead of surfacing as "zero repos" minutes later.
        $missingSel = @(("$Projects").Split(',') | ForEach-Object { $_.Trim() } | Where-Object {
            $_ -and ($script:RESERVED_PROFILE_NAMES -notcontains $_.ToLowerInvariant()) -and
            -not (Test-Path -LiteralPath (Join-Path $syncProjDir "$_.json"))
        })
        if ($missingSel.Count -gt 0) {
            Write-Warning ("Selected project(s) have no profile in this console's host config store and cannot seed/clone: " +
                "$($missingSel -join ', ').  Store: $syncProjDir (user: $env:USERNAME).  " +
                "If the panel shows these profiles, this console is likely elevated under a different account.")
        }

        # Auto-enable profiles that newly arrived from the VM: add them to THIS
        # run's -Projects (so their repos/sdks/mcp provision right now, and the
        # checkout auto-decide sees their repos) and persist them into the saved
        # selection so future reprovisions keep them without a manual re-tick.
        $profilesAfterSync = @(Get-ChildItem -LiteralPath $syncProjDir -Filter *.json -File -ErrorAction SilentlyContinue |
            ForEach-Object { $_.BaseName })
        $newProfiles = @($profilesAfterSync | Where-Object {
            ($profilesBeforeSync -notcontains $_) -and
            ($script:RESERVED_PROFILE_NAMES -notcontains $_.ToLowerInvariant())
        })
        if ($newProfiles.Count -gt 0) {
            $selection = @(("$Projects").Split(',') | ForEach-Object { $_.Trim() } | Where-Object { $_ })
            foreach ($np in $newProfiles) { if ($selection -notcontains $np) { $selection += $np } }
            $Projects = $selection -join ','
            Write-Ok "Auto-enabled new project profile(s) from the VM: $($newProfiles -join ', ') (projects now: $Projects)"
            if (Get-Command Save-ConstructSettings -ErrorAction SilentlyContinue) {
                Save-ConstructSettings -Dir $PSScriptRoot -Values @{ projects = $selection }
            }
        }
    }
}

# Run the non-interactive provisioner.
Write-Step "Provisioning the VM (this can take several minutes)"
$agentNameArg = if ($AgentName) { $AgentName } else { "$HostAlias-agent" }
# Git identity is base64-encoded so values with spaces/apostrophes survive the
# env -> SSH -> bash layers untouched (empty -> left unchanged on the VM).
$gitNameB64  = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$gitIdentity.Name))
$gitEmailB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes([string]$gitIdentity.Email))
$gitCredStore = if ($gitIdentity.CredentialStore) { "true" } else { "false" }
# When we connected over the saved root key, that key must keep working after the
# run, so tell provision.sh NOT to regenerate the VM's root key. (setup-root-ssh-key.sh
# already preserves an existing key, but skipping it entirely also avoids re-emitting
# the key to the provisioning log.)
$setupRootKeyArg = if ($script:UseRootKey) { "false" } else { "true" }
# Feature 2: clone the selected projects' repos during provisioning. Credentials
# for private repos come from -GitCloneCredentialsB64 (the up-front prompt), or on
# a restore from the saved backup's git-credentials so the checkout can authenticate.
$cloneCredB64 = $GitCloneCredentialsB64
if (-not $cloneCredB64 -and $RestoreDir) {
    $restoredCreds = Join-Path $RestoreDir "extracted\home\.git-credentials"
    if (Test-Path -LiteralPath $restoredCreds) {
        $credLines = @(Get-Content -LiteralPath $restoredCreds | Where-Object { $_.Trim() })
        if ($credLines.Count -gt 0) {
            $cloneCredB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($credLines -join "`n")))
        }
    }
}
# Preserve the VS Code serve-web connection token across the reinstall so the
# browser ?tkn= URL stays the same. It rides in the backup outside home (at
# etc/construct/vscode-serve-web.token) and must be in place BEFORE serve-web
# starts -- so pass it (base64) into provision.sh -> install-vscode.sh here,
# rather than via restore-config.sh, which runs after serve-web is already up.
$serveWebTokenB64 = ""
if ($RestoreDir) {
    $savedSwTok = Join-Path $RestoreDir "extracted\etc\construct\vscode-serve-web.token"
    if (Test-Path -LiteralPath $savedSwTok) {
        $swTokText = (Get-Content -LiteralPath $savedSwTok -Raw).Trim()
        if ($swTokText) {
            $serveWebTokenB64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($swTokText))
        }
    }
}
# Detect the desktop VS Code client's commit so the VM can pre-seed the exact
# matching Remote-SSH server (~/.vscode-server) during provisioning -- that makes
# the FIRST connect after a reinstall as fast as the second (no server download).
# Blank when `code` isn't on PATH or the output is unexpected; the VM then seeds
# latest stable instead, and a mismatch just falls back to Remote-SSH's normal
# on-demand download.
$vsCodeCommit = ""
try {
    $codeCmd = Get-Command code -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($codeCmd) {
        # Desktop `code --version` prints three lines (version / commit sha /
        # arch); the standalone CLI prints one line with "(commit <sha>)".
        $verText = ((& $codeCmd.Source --version 2>$null) -join "`n")
        if ($verText -match '(?m)^([0-9a-f]{40})$') {
            $vsCodeCommit = $Matches[1]
        } elseif ($verText -match 'commit\s+([0-9a-f]{40})') {
            $vsCodeCommit = $Matches[1]
        }
    }
} catch { }
if ($vsCodeCommit) {
    Write-Host "  Desktop VS Code commit: $vsCodeCommit (pre-seeding the matching Remote-SSH server)" -ForegroundColor DarkGray
}
# Auto-decide the checkout when not forced: on iff the selected projects declare repos.
# Always print the decision -- a silent "false" here surfaces on the VM as cloning
# being skipped with no explanation, which has already cost a debugging session.
$checkoutArg = $CheckoutProjects
if (-not $checkoutArg) {
    $repoUrls = @()
    $checkoutProjDir = Join-Path $PSScriptRoot 'projects'
    if (Get-Command Get-ProjectRepoUrls -ErrorAction SilentlyContinue) {
        if (Get-Command Get-ConstructConfigProjectsDir -ErrorAction SilentlyContinue) {
            $checkoutProjDir = Get-ConstructConfigProjectsDir -ScriptsDir $PSScriptRoot
        }
        $repoUrls = @(Get-ProjectRepoUrls -ProjectsDir $checkoutProjDir -Names $Projects)
    }
    $checkoutArg = if ($repoUrls.Count -gt 0) { "true" } else { "false" }
    if ($repoUrls.Count -gt 0) {
        Write-Ok "Project checkout: ON ($($repoUrls.Count) repo URL(s) declared by: $Projects)"
    } else {
        Write-Warning "Project checkout: OFF -- no repo URLs found for '$Projects' in $checkoutProjDir. Repos will NOT be cloned; add repos[] to the profile (or check the selection) if that's unexpected."
    }
} else {
    Write-Ok "Project checkout: forced '$checkoutArg' via -CheckoutProjects"
}
$envPrefix = "env AI_TOOLS='$AiTools' PROJECTS='$Projects' SSH_USER='$SeedUser' AGENT_NAME='$agentNameArg' CLAUDE_USER='$RemoteUser' GIT_USER_NAME_B64='$gitNameB64' GIT_USER_EMAIL_B64='$gitEmailB64' GIT_CREDENTIAL_STORE='$gitCredStore' GIT_CLONE_CREDENTIALS_B64='$cloneCredB64' CHECKOUT_PROJECTS='$checkoutArg' SETUP_ROOT_SSH_KEY='$setupRootKeyArg' VSCODE_SERVER='$VsCodeServer' VSCODE_SERVE_WEB='$VsCodeServeWeb' VSCODE_TUNNEL='$VsCodeTunnel' VSCODE_SERVE_WEB_TOKEN_B64='$serveWebTokenB64' VSCODE_CLIENT_COMMIT='$vsCodeCommit' SMB_SHARE='$SmbShare' CLAUDE_PARTIAL_STREAMING='$ClaudePartialStreaming' MIC_PASSTHROUGH='$MicPassthrough'"
Write-Host "  --- live provisioning output ---" -ForegroundColor DarkGray
$provisionStream = Invoke-SshStream -Sudo -PassThru -NoThrow -Command "$envPrefix bash /opt/construct/repo/bin/provision.sh"
Write-Host "  --- end provisioning output ---" -ForegroundColor DarkGray
# Store raw output for the result file (de-elevated child → parent fidelity).
$script:ProvisionRawLines = @($provisionStream.Lines)
$script:ProvisionResult = ConvertFrom-ConstructProvisionResult -Lines $provisionStream.Lines
$global:ConstructProvisionErrors = @($script:ProvisionResult.Errors)
if (-not $script:ProvisionResult.IsValid) {
    throw "VM provisioner did not emit a valid result sentinel (remote exit $($provisionStream.ExitCode))."
}
if ($provisionStream.ExitCode -eq 3) {
    if ($script:ProvisionResult.ErrorCount -eq 0) {
        throw "VM provisioner exited 3 but reported no optional failures."
    }
    $global:ConstructProvisionHadErrors = $true
    Write-Host ("    Provisioning reached the end with {0} optional error(s); host setup will continue." -f $script:ProvisionResult.ErrorCount) -ForegroundColor Yellow
} elseif ($provisionStream.ExitCode -ne 0) {
    $global:ConstructProvisionHadErrors = $true
    throw "VM provisioning failed in a critical step (remote exit $($provisionStream.ExitCode))."
} elseif ($script:ProvisionResult.ErrorCount -ne 0) {
    throw "VM provisioner exited 0 but reported $($script:ProvisionResult.ErrorCount) failure(s)."
} else {
    Write-Ok "Provisioning finished cleanly"
}

# Restore a saved config onto the freshly provisioned VM (the reinstall
# auto-restore). Done AFTER provision.sh so the user's saved instruction/config
# files overwrite the freshly generated ones and auth/memory/skills come back;
# the project checkout inside provision.sh already used the restored git
# credentials (passed via the env above), so private repos cloned.
if ($RestoreDir) {
    $restoreTgz = Join-Path $RestoreDir "backup.tar.gz"
    if (Test-Path -LiteralPath $restoreTgz) {
        Write-Step "Restoring saved agent config onto the VM"
        Invoke-Ssh -Sudo -Command "rm -f /tmp/construct-config-restore.tar.gz"
        # The upload is INSIDE the try so the finally still removes the (plaintext
        # secret) tarball even if scp fails mid-transfer. restore-config.sh is the
        # only command in the stream, so its non-zero exit propagates (a failed
        # restore throws and "Saved config restored" is NOT printed).
        try {
            Invoke-Scp -LocalPath $restoreTgz -RemotePath "/tmp/construct-config-restore.tar.gz"
            Invoke-SshStream -Sudo -Command "EXPORT_HOME=/root BACKUP_TGZ=/tmp/construct-config-restore.tar.gz bash /opt/construct/repo/bin/restore-config.sh"
        } finally {
            try { Invoke-Ssh -Sudo -Command "rm -f /tmp/construct-config-restore.tar.gz" } catch { }
        }
        Write-Ok "Saved config restored"
    } else {
        Write-Host "    -RestoreDir set but no backup.tar.gz in $RestoreDir -- skipping restore." -ForegroundColor DarkGray
    }
}

# Get the root private key for the host-side config below. On the fast path we
# already hold it locally (and told the VM not to regenerate it), so reuse the
# saved copy; otherwise retrieve the freshly generated one from the VM.
if ($script:UseRootKey) {
    Write-Step "Reusing the saved root SSH private key (VM key left unchanged)"
    $privateKeyText = [System.IO.File]::ReadAllText($script:LocalRootKeyPath)
    Write-Ok "Using existing key $script:LocalRootKeyPath"
} else {
    Write-Step "Retrieving root SSH private key from the VM"
    $keyText = Invoke-Ssh -Sudo -Command "cat $RemoteKeyPath"
    $m = [regex]::Match($keyText, "(?s)-----BEGIN[^-]*PRIVATE KEY-----.*?-----END[^-]*PRIVATE KEY-----")
    if (-not $m.Success) { throw "Could not find a private key in the output of: cat $RemoteKeyPath" }
    $privateKeyText = $m.Value
    Write-Ok "Retrieved private key"
}

# Read the VS Code tunnel status back from the VM NOW -- while SSH is still up and
# before the reboot below -- so we can (a) pause for the one-time device sign-in
# if a registration is pending, and (b) report tunnel state in the final summary.
$tunnelStatus = @{}
if ($VsCodeServer -eq "true") {
    try {
        $raw = Invoke-Ssh -Sudo -Command "cat /etc/construct/vscode-status 2>/dev/null || true"
        foreach ($line in ($raw -split "`n")) {
            if ($line -match '^\s*([A-Z_]+)=(.*)$') { $tunnelStatus[$matches[1]] = $matches[2].Trim() }
        }
    } catch { }
}

# Read the workspace SMB-share details back from the VM (set up by
# setup-smb-share.sh) and, when enabled, auto-mount the share on THIS host. Done
# NOW -- while the VM is still up and before the reboot below -- because `net use`
# needs the share reachable at map time; once mapped with /persistent:yes it
# survives the reboot and reconnects on its own. $smbMountedDrive feeds the
# summary at the end.
$smbStatus = @{}
$smbMountedDrive = $null
try {
    $raw = Invoke-Ssh -Sudo -Command "cat /etc/construct/smb-status 2>/dev/null || true"
    foreach ($line in ($raw -split "`n")) {
        if ($line -match '^\s*([A-Z_]+)=(.*)$') { $smbStatus[$matches[1]] = $matches[2].Trim() }
    }
} catch { }

if ($Action -eq "provision" -and $smbStatus['SMB_ENABLED'] -eq "yes" -and $MountRepoShare -eq "true") {
    $smbShareName = if ($smbStatus['SMB_SHARE_NAME']) { $smbStatus['SMB_SHARE_NAME'] } else { "repo" }
    $smbUser      = if ($smbStatus['SMB_USER'])       { $smbStatus['SMB_USER'] }       else { "dev" }
    $smbPass      = $smbStatus['SMB_PASSWORD']
    # Prefer the stable DNS name (survives the VM's DHCP address changing, which
    # matters for a persistent mapping); fall back to the reported LAN IP.
    $smbHost = $VmHost
    if (-not $smbHost -and $smbStatus['SMB_IP']) { $smbHost = $smbStatus['SMB_IP'] }
    $smbUnc = "\\$smbHost\$smbShareName"
    if ($smbPass) {
        Write-Step "Mounting the VM workspace share on this host"
        # Belt-and-suspenders: the mount is a convenience and must NEVER abort the
        # provision (the repos are already set up on the VM). Mount-RepoShare is
        # already non-throwing, but guard the call too so nothing here is fatal.
        try {
            $smbMountedDrive = Mount-RepoShare -UncPath $smbUnc -SmbUser $smbUser -SmbPassword $smbPass -Preferred $SmbDriveLetter
        } catch {
            Write-Warning "Auto-mount of $smbUnc failed ($($_.Exception.Message)); the share is still available on the VM."
        }
    } else {
        Write-Warning "VM reported the SMB share enabled but no password; skipping host mount."
    }
}

function Get-TunnelLoginLine {
    # Pull the current device-login instruction line from the VM's journal. Used
    # as a fallback if the status file didn't capture one yet (the service may
    # need another moment to emit it). The VM is still up here (pre-reboot).
    for ($i = 0; $i -lt 15; $i++) {
        try {
            $out = Invoke-Ssh -Sudo -Command "journalctl -u code-tunnel -o cat --no-pager -n 200 2>/dev/null | grep -Ei 'github.com/login/device|microsoft.com/devicelogin|use code|grant access' | tail -n1 || true"
            $line = ($out | Out-String).Trim()
            if ($line) { return $line }
        } catch { }
        Start-Sleep -Seconds 2
    }
    return ""
}

# If the tunnel needs its one-time device sign-in, do it NOW -- before we finish
# setup. The VM is still up and the code-tunnel service is polling for the device
# code, so pausing here lets the user complete the GitHub/Microsoft sign-in against
# a code that is still valid. (On a full install we reboot at the end, which would
# rotate the code, so it must be done here.)
if ($tunnelStatus['VSCODE_TUNNEL_NEEDS_SIGNIN'] -eq "yes") {
    $loginLine = ""
    if ($tunnelStatus['VSCODE_TUNNEL_LOGIN_B64']) {
        try { $loginLine = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($tunnelStatus['VSCODE_TUNNEL_LOGIN_B64'])) } catch { }
    }
    if (-not $loginLine) { $loginLine = Get-TunnelLoginLine }

    Write-Host ""
    Write-Step "VS Code tunnel: one-time device sign-in required"
    Write-Host "    Register the tunnel now, before finishing setup:" -ForegroundColor Yellow
    if ($loginLine) {
        Write-Host "      $loginLine" -ForegroundColor Cyan
    } else {
        $journalCmd = if ($script:UseRootKey) { "journalctl -u code-tunnel -n 50" } else { "sudo journalctl -u code-tunnel -n 50" }
        Write-Host "      Could not read the device code automatically. In another window run:" -ForegroundColor Yellow
        Write-Host "        ssh $($script:ConnectUser)@$VmHost `"$journalCmd`"" -ForegroundColor Cyan
        Write-Host "      and use the github.com/login/device link shown there." -ForegroundColor Cyan
    }
    Write-Host "    Open the link, enter the code, and authorize access." -ForegroundColor White
    Read-Host "    Press Enter once sign-in is complete to finish setup"
}

# Decide whether to reboot the VM at the end of provisioning. We only reboot when
# it actually earns its downtime:
#
#   * Full install / reinstall — the bootstrap or seed-password path (NOT
#     $script:UseRootKey). A bootstrap key was authorized on the VM THIS run and
#     must be stripped; the strip and reboot MUST share the one authenticated agent
#     session (once the key is gone we can't reconnect as agent to reboot
#     separately), and a freshly built OS gets its clean first restart.
#   * A reprovision where the VM reports a genuinely pending reboot — e.g. a project
#     provisioning command or an ALLOW_HOST_PACKAGES host package pulled a new
#     kernel. Ubuntu drops /var/run/reboot-required for exactly that; we probe it
#     over the still-open session and honour it.
#
# A plain reprovision of an already-provisioned, still-running VM (the root-key fast
# path, nothing pending) is left UP. An audit of the whole provisioning chain
# confirmed every step applies its effect live: systemd units are daemon-reloaded
# and (re)started during the run (provision.sh restarts construct so the compose
# stack re-reads its regenerated config), config files are re-read at the next tool
# launch, the docker group is picked up by the fresh post-run SSH login, and there
# is no apt upgrade so the running kernel is never replaced — so a reboot here would
# only add downtime.
$doReboot = -not $script:UseRootKey
if (-not $doReboot) {
    # Fast path (reprovision of a running VM): reboot only if the VM itself flags
    # one as required. `|| echo no` keeps the probe from throwing on a clean VM.
    try {
        $rebootRequired = (Invoke-Ssh -Sudo -Command "test -e /var/run/reboot-required && echo yes || echo no").Trim()
        if ($rebootRequired -eq "yes") {
            $doReboot = $true
            Write-Host "    VM reports a pending reboot (/var/run/reboot-required) -- rebooting to apply it." -ForegroundColor DarkGray
        }
    } catch { }
}

# Remove the bootstrap public key from the agent user's authorized_keys. When a
# reboot follows (the bootstrap path) this MUST share the one authenticated
# session: once the key is gone we can't reconnect, so removing it in a separate
# call would silently fail to authenticate; the session stays valid after removal
# (auth already happened). On the fast path we connected as root and never
# authorized the bootstrap key this run, but a leftover copy from a failed or manual
# prior run would remain a standing credential (and provision.sh grants that agent
# user passwordless sudo), so we strip it opportunistically there too whenever the
# committed public key is available — root can do this without the bootstrap key,
# and the sed applies live (no reboot needed).
$rmBootstrapCmd = ""
if (Test-Path -LiteralPath $BootstrapPubKey) {
    $pubKeyContent  = (Get-Content $BootstrapPubKey -Raw).Trim()
    $escPubKey      = $pubKeyContent.Replace("/", "\/")
    # `|| true` so a missing/empty authorized_keys (possible on the fast path)
    # doesn't abort the chain.
    $rmBootstrapCmd = "sed -i '/$escPubKey/d' /home/${SeedUser}/.ssh/authorized_keys 2>/dev/null || true; "
}

# Optionally set the agent user's login password. It's only a manual-fallback
# credential (root logs in by pubkey), and we change it inside the same already-
# authenticated session, so it can never lock provisioning out mid-run. The new
# password is base64-encoded so any characters survive the SSH/shell layers
# untouched; chpasswd (run as root) sets it — live for subsequent logins.
$pwChangeCmd = ""
if ($AgentPassword -and ($AgentPassword -ne $SeedPassword)) {
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${SeedUser}:${AgentPassword}"))
    $pwChangeCmd = "echo '$b64' | base64 -d | chpasswd; "
    Write-Ok "Setting a custom login password for '$SeedUser'"
}

# Tell the host-side caller (Auto-Install's open-VS-Code wait) whether this run
# reboots the VM, and capture the CURRENT boot id over the still-authenticated
# session first. The reboot below is backgrounded on the VM (sleep 3; reboot),
# so the OLD boot's sshd keeps answering for several seconds after we return --
# a port/ssh probe alone can pass on the boot that is about to vanish.
# Wait-VmSshReady compares against this baseline to require a genuinely NEW
# boot. Best-effort: an empty baseline makes the waiter fall back to its
# fresh-boot uptime heuristic.
$global:ConstructVmRebootIssued    = $doReboot
$global:ConstructVmPreRebootBootId = ""
if ($doReboot) {
    try {
        $global:ConstructVmPreRebootBootId = "$(Invoke-Ssh -Command 'cat /proc/sys/kernel/random/boot_id')".Trim()
    } catch { }
}

if ($doReboot) {
    if ($rmBootstrapCmd) {
        Write-Step "Removing bootstrap key and rebooting the VM"
    } else {
        Write-Step "Rebooting the VM"
    }
    # Redirect the backgrounded reboot's stdin too (</dev/null), not just
    # stdout/stderr: otherwise it inherits and holds the SSH channel's stdin open,
    # so ssh.exe never sees the session close and hangs (the VM then reboots out
    # from under it).
    Invoke-Ssh -Sudo -Command "${pwChangeCmd}${rmBootstrapCmd}nohup sh -c 'sleep 3; reboot' </dev/null >/dev/null 2>&1 &"
    if ($rmBootstrapCmd) {
        Write-Ok "Bootstrap key removed; VM will reboot in a few seconds"
    } else {
        Write-Ok "VM will reboot in a few seconds"
    }
} else {
    # Reprovision, no reboot: still apply the credential-hygiene commands live over
    # the open session, then leave the VM running (its services are already up and
    # the host's persistent SMB mapping stays connected). Skip the SSH call when
    # there's nothing to run; the trailing `true` keeps the command valid + exit 0.
    Write-Step "Finishing up (no reboot -- reprovision leaves the VM running)"
    if ($pwChangeCmd -or $rmBootstrapCmd) {
        Invoke-Ssh -Sudo -Command "${pwChangeCmd}${rmBootstrapCmd}true"
    }
    if ($rmBootstrapCmd) {
        Write-Ok "Any stray bootstrap key removed; VM left running"
    } else {
        Write-Ok "VM left running -- reprovision applied live, no reboot needed"
    }
}

# Configure the Windows host (local — no VM connection needed).
Write-Step "Configuring the Windows host (~\.ssh and VS Code)"
$keyPath = Set-HostSshConfig -PrivateKeyText $privateKeyText
Set-VsCodeRemotePlatform
if (",$AiTools," -like "*,opencode,*") {
    Set-OpenCodeRemote -Url "http://${VmHost}:${OpencodePort}" -DisplayName $HostAlias
}

# Clean up temporary known_hosts.
Remove-Item "$env:TEMP\construct-known_hosts" -Force -ErrorAction SilentlyContinue

# Remove the temporary owner-only copies of the private keys (last SSH op is done).
if ($script:SecureKeyPath) {
    Remove-Item -LiteralPath $script:SecureKeyPath -Force -ErrorAction SilentlyContinue
}
if ($script:SecureRootKeyPath) {
    Remove-Item -LiteralPath $script:SecureRootKeyPath -Force -ErrorAction SilentlyContinue
}

Write-Host ""
# Record the PROVISIONED-commit marker now that the provision has SUCCEEDED. This is a
# SEPARATE key from installedCommit: installedCommit tracks the installed Construct
# (extension + scripts -- recorded by install / Update-Construct), while provisionedCommit
# records the version the VM was actually provisioned with (mirrors the current
# installedCommit -- the scripts doing this provision). The panel flags the Provision
# button when they differ (the installed Construct is newer than what the VM ran with).
# We do NOT touch installedCommit here, so a reprovision can't wrongly clear the
# "update available" banner. Best-effort: guarded + never fatal.
if ($Action -eq "provision" -and (Get-Command Set-ConstructProvisionedMarker -ErrorAction SilentlyContinue)) {
    try {
        $pvSha = Set-ConstructProvisionedMarker -Dir $PSScriptRoot
        Write-Host ""
        Write-Host "Recorded provisioned commit for the control panel: $pvSha" -ForegroundColor DarkGray
    } catch {
        Write-Warning "Could not record the provisioned marker: $($_.Exception.Message)"
    }
}

# Install ffmpeg on the HOST for microphone passthrough (the panel spawns it locally to
# capture the mic -- a VS Code webview can't). Done HERE, at the very end, rather than
# up-front in Auto-Install's pre-step: winget can be slow and shouldn't block the user's
# install prompts. Runs in Provision's context -- elevated for install/reinstall, the user
# for a panel reprovision; either way winget --scope user targets the current user, and it's
# idempotent (a no-op when ffmpeg is already present). Best-effort: never fails the provision.
if ($Action -eq "provision" -and (Get-Command Ensure-Ffmpeg -ErrorAction SilentlyContinue)) {
    try { Ensure-Ffmpeg | Out-Null }
    catch { Write-Warning "Could not ensure ffmpeg (mic passthrough) -- continuing: $($_.Exception.Message)" }
}

Write-Host "Done." -ForegroundColor Green
Write-Host "Connect from a terminal:" -ForegroundColor White
Write-Host "    ssh $VmHost" -ForegroundColor Yellow
Write-Host "Or in VS Code: Remote Explorer -> SSH -> $HostAlias (platform preset to linux)." -ForegroundColor White

Write-Host ""
Write-Host "To set up Claude Code in VS Code:" -ForegroundColor White
Write-Host "    1. Connect to the VM via Remote-SSH in VS Code" -ForegroundColor DarkGray
Write-Host "    2. Open the terminal and run 'claude' to complete first-time setup" -ForegroundColor DarkGray
Write-Host "    3. Use the Claude Code icon in the top-right of the editor to start" -ForegroundColor DarkGray
if (",$AiTools," -like "*,opencode,*") {
    Write-Host ""
    Write-Host "OpenCode remote URL:" -ForegroundColor White
    Write-Host "    http://${VmHost}:${OpencodePort}" -ForegroundColor Yellow
    Write-Host "This was added to the OpenCode GUI app automatically (if its config was present)." -ForegroundColor DarkGray
    Write-Host "    If not, add it by hand -> Manage Servers -> Add Server. Paste the hostname, leave user and pw unchanged." -ForegroundColor DarkGray
}
if (",$AiTools," -like "*,codex,*") {
    Write-Host ""
    Write-Host "To set up Codex for remote:" -ForegroundColor White
    Write-Host "    1. Open Codex -> Settings -> Connections" -ForegroundColor DarkGray
    Write-Host "    2. Add SSH connection, then pick the VM ($HostAlias) from the list" -ForegroundColor DarkGray
    Write-Host "    3. Log into your Codex account again on the remote" -ForegroundColor DarkGray
}
if ($VsCodeServer -eq "true") {
    Write-Host ""
    Write-Host "VS Code Remote-SSH:" -ForegroundColor White
    Write-Host "    Server installed -- connect via Remote Explorer -> SSH -> $HostAlias." -ForegroundColor DarkGray
}
if ($tunnelStatus['VSCODE_SERVE_WEB_ENABLED'] -eq "yes") {
    $swUrl = $tunnelStatus['VSCODE_SERVE_WEB_URL']
    $swTok = $tunnelStatus['VSCODE_SERVE_WEB_TOKEN']
    Write-Host ""
    Write-Host "VS Code Server (serve-web, browser):" -ForegroundColor White
    if ($swUrl -and $swTok) {
        Write-Host "    $swUrl/?tkn=$swTok" -ForegroundColor Yellow
    } elseif ($swUrl) {
        Write-Host "    $swUrl  (token: ssh $HostAlias `"sudo cat /etc/construct/vscode-serve-web.token`")" -ForegroundColor Yellow
    }
    Write-Host "    Browser VS Code over HTTP, gated by the connection token above." -ForegroundColor DarkGray
}
if ($tunnelStatus['VSCODE_TUNNEL_DEPLOYED'] -eq "yes") {
    $tunnelName = ($HostAlias.ToLower() -replace '[^a-z0-9-]', '-') -replace '-+', '-'
    $tunnelName = $tunnelName.Trim('-')
    if ($tunnelStatus['VSCODE_TUNNEL_NAME']) { $tunnelName = $tunnelStatus['VSCODE_TUNNEL_NAME'] }
    $tunnelUrl = if ($tunnelStatus['VSCODE_TUNNEL_URL']) { $tunnelStatus['VSCODE_TUNNEL_URL'] } else { "https://vscode.dev/tunnel/$tunnelName" }

    Write-Host ""
    Write-Host "VS Code Remote Tunnel:" -ForegroundColor White
    Write-Host "    Open: $tunnelUrl" -ForegroundColor Yellow
    if ($tunnelStatus['VSCODE_TUNNEL_NEEDS_SIGNIN'] -eq "yes") {
        # The user completed (or was prompted for) the device sign-in in the pause
        # above; once that's done the tunnel comes up automatically -- after the
        # reboot on a full install, or immediately on a no-reboot reprovision (the
        # code-tunnel service is already running).
        if ($doReboot) {
            Write-Host "    You completed the one-time sign-in above -- the tunnel comes up after the reboot." -ForegroundColor Green
        } else {
            Write-Host "    You completed the one-time sign-in above -- the tunnel is coming up now." -ForegroundColor Green
        }
    } elseif ($tunnelStatus['VSCODE_TUNNEL_AUTHED'] -eq "yes") {
        Write-Host "    Status: registered and live -- open the URL in a browser or VS Code Remote Explorer." -ForegroundColor Green
    } else {
        Write-Host "    Service is deployed. If the tunnel isn't registered yet, re-run with -VsCodeTunnel true to sign in." -ForegroundColor DarkGray
    }
}
if ($smbStatus['SMB_ENABLED'] -eq "yes") {
    $smbShareName = if ($smbStatus['SMB_SHARE_NAME']) { $smbStatus['SMB_SHARE_NAME'] } else { "repo" }
    $smbUser      = if ($smbStatus['SMB_USER'])       { $smbStatus['SMB_USER'] }       else { "dev" }
    $smbUnc       = "\\$VmHost\$smbShareName"
    Write-Host ""
    Write-Host "Workspace file share (SMB):" -ForegroundColor White
    if ($smbMountedDrive) {
        Write-Host "    Mounted at $smbMountedDrive  ($smbUnc)" -ForegroundColor Yellow
        Write-Host "    Credentials saved + persistent -- it reconnects at logon and after any VM reboot." -ForegroundColor DarkGray
        Write-Host "    The repos folder opens as root, the same identity the agents use." -ForegroundColor DarkGray
    } else {
        Write-Host "    Share: $smbUnc  (user $smbUser, files accessed as root)" -ForegroundColor Yellow
        Write-Host "    Mount it from this host:" -ForegroundColor DarkGray
        Write-Host "      net use Z: $smbUnc /user:$smbUser <password> /savecred /persistent:yes" -ForegroundColor Cyan
        Write-Host "      password: ssh $HostAlias `"sudo sed -n 's/^SMB_PASSWORD=//p' /etc/construct/config.env`"" -ForegroundColor DarkGray
    }
}
Write-Host ""

}
catch {
    $script:ProvisionFailureMessage = $_.Exception.Message
    if ($Action -eq 'provision') {
        $global:ConstructProvisionHadErrors = $true
        $global:ConstructProvisionFailureMessage = $script:ProvisionFailureMessage
    }
    # Standalone: show the failure above our own pause (readable even on a
    # double-click run). Chained (-Auto): rethrow so the upper script owns the
    # single error display + pause. De-elevated child (-ResultFile): don't
    # rethrow — the result file written in the finally block IS the signal, and
    # the result screen shows the error without a raw stack trace.
    if ($Auto -and -not $ResultFile) { throw }
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if ($Action -eq 'provision') {
        Show-ProvisionResultScreen -Result $script:ProvisionResult -FailureMessage $script:ProvisionFailureMessage
    }
    # De-elevated child (-ResultFile): write the result file so the elevated parent
    # can read the outcome. Atomic: write to a temp file, then rename, so the
    # parent never observes a partial (torn) write. No pause — the parent's
    # Wait-Exit owns the single authoritative final screen and error prompt;
    # pausing here would create competing pauses and risk orphaned consoles.
    if ($ResultFile) {
        $provExitCode = if ($script:ProvisionFailureMessage) { 1 }
                        elseif ($global:ConstructProvisionHadErrors) { 3 }
                        else { 0 }
        $resultData = @{
            ExitCode       = $provExitCode
            HadErrors      = [bool]$global:ConstructProvisionHadErrors
            FailureMessage = [string]$script:ProvisionFailureMessage
            RawSentinel    = [string[]]$script:ProvisionRawLines
        }
        # Atomic publication: write to temp then rename. Never write the watched
        # final name directly — a partial file would cause a torn read in the
        # polling parent. If atomic publication fails, exit nonzero so the parent
        # reports publication failure instead of observing garbage.
        $tmpResult = "$ResultFile.tmp.$PID"
        try {
            $resultData | ConvertTo-Json -Depth 4 |
                Set-Content -LiteralPath $tmpResult -Encoding UTF8 -Force
            Move-Item -LiteralPath $tmpResult -Destination $ResultFile -Force -ErrorAction Stop
        } catch {
            Write-Warning "Could not publish provision result: $($_.Exception.Message)"
            Remove-Item -LiteralPath $tmpResult -Force -ErrorAction SilentlyContinue
            exit 1
        }
        exit $provExitCode
    }
    # An upper script (-Auto) owns the single final pause. Direct runs pause as
    # before; panel runs skip it only when fully clean so errors cannot disappear
    # with the console window.
    if (-not $Auto -and ((-not $FromPanel) -or $global:ConstructProvisionHadErrors)) {
        Write-Host ""
        Read-Host "Press Enter to exit"
    }
}
