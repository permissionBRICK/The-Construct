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
    # Set when this script is launched by an upper script (Auto-Install.ps1 /
    # Create-AgentVM.ps1), which owns the final "Press Enter" pause. When run on
    # its own this stays off and the script pauses at the end so a self-launched
    # window doesn't vanish before the output can be read.
    [switch]$Auto
)

$ErrorActionPreference = "Stop"

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

# Shared helpers: persisted settings + git-identity resolution. Optional -- if the
# lib isn't alongside this script we fall back to the passed-in values below.
$commonLib = Join-Path $PSScriptRoot "lib\AgentVm.Common.ps1"
if (Test-Path -LiteralPath $commonLib) { . $commonLib }

# --- Dependencies -----------------------------------------------------------

function Ensure-Tar {
    if (-not (Get-Command tar.exe -ErrorAction SilentlyContinue)) {
        throw "tar.exe not found. It ships with Windows 10 1803+ / Windows 11. Install it or upgrade Windows."
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
    "-o", "ConnectTimeout=15"
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
    $output = & ssh.exe @script:SshOpts "$($script:ConnectUser)@$VmHost" $toRun 2>$null
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
    # sees live progress. Throws on a non-zero remote exit code.
    param([Parameter(Mandatory)][string]$Command, [switch]$Sudo)
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
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & ssh.exe @script:SshOpts "$($script:ConnectUser)@$VmHost" $toRun 2>&1 | ForEach-Object {
        Write-Host ((([string]$_) -replace "`r", "") -replace $ansiRe, "")
    }
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    if ($exitCode -ne 0) {
        throw "Remote command failed (exit $exitCode): $Command"
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
    & ssh.exe @script:SshOpts "$SeedUser@$VmHost" "true" 2>&1 | Out-Null
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
    & ssh.exe @script:SshOpts "$SeedUser@$VmHost" "printf '%s\n' '$escPw' | sudo -k -S -p '' true" 2>$null | Out-Null
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
        "-o", "ConnectTimeout=15"
    )

    # Probe as the remote (root) user. Lower ErrorActionPreference so ssh's benign
    # stderr isn't promoted to a terminating error by the script-wide 'Stop'.
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & ssh.exe @opts "$RemoteUser@$VmHost" "true" 2>&1 | Out-Null
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

function Set-HostSshConfig {
    param([string]$PrivateKeyText)

    $sshDir = Join-Path $HOME ".ssh"
    if (-not (Test-Path $sshDir)) { New-Item -ItemType Directory -Path $sshDir | Out-Null }

    # Private key (LF line endings, owner-only ACL so OpenSSH accepts it).
    $keyPath = Join-Path $sshDir $LocalKeyName
    $normalized = ($PrivateKeyText -replace "`r`n", "`n").TrimEnd("`n") + "`n"
    [System.IO.File]::WriteAllText($keyPath, $normalized)
    & icacls $keyPath /inheritance:r | Out-Null
    & icacls $keyPath /grant:r "$($env:USERNAME):F" | Out-Null
    Write-Ok "Wrote private key: $keyPath"

    # known_hosts - remove ALL stale entries for this VM (full hostname AND the
    # short alias, including hashed ones) BEFORE accepting the current key, so a
    # re-provisioned VM with a new host key doesn't trip "REMOTE HOST
    # IDENTIFICATION HAS CHANGED". ssh-keygen -R rewrites ~/.ssh/known_hosts in
    # place and leaves entries for every other host untouched.
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & ssh-keygen -R $VmHost    2>$null
    & ssh-keygen -R $HostAlias 2>$null
    & ssh -o StrictHostKeyChecking=accept-new -o BatchMode=yes -i $keyPath "$RemoteUser@$VmHost" "exit" 2>$null
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

function Select-Projects {
    # Prompt the user to pick which project profiles from projects/ to load.
    # Returns a comma-separated PROJECTS value (or "default" if none chosen). The
    # real UI is the checkbox-style Select-ProjectProfiles in the shared lib; the
    # comma prompt below is only a fallback for when that lib isn't alongside us.
    $projDir = Join-Path $PSScriptRoot "projects"
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
        if (Test-Path -LiteralPath $extract) { Remove-Item -LiteralPath $extract -Recurse -Force }
        New-Item -ItemType Directory -Force -Path $extract | Out-Null
        & tar.exe -xzf $tgz -C $extract
        if ($LASTEXITCODE -ne 0) { throw "Failed to extract the backup ($tgz)." }

        # Merge generated project profiles into projects/, never overwriting an
        # existing profile of the same name.
        $genDir  = Join-Path $extract "projects"
        $projDir = Join-Path $PSScriptRoot "projects"
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
$checkoutArg = $CheckoutProjects
if (-not $checkoutArg) {
    $repoUrls = @()
    if (Get-Command Get-ProjectRepoUrls -ErrorAction SilentlyContinue) {
        $repoUrls = @(Get-ProjectRepoUrls -ProjectsDir (Join-Path $PSScriptRoot 'projects') -Names $Projects)
    }
    $checkoutArg = if ($repoUrls.Count -gt 0) { "true" } else { "false" }
}
$envPrefix = "env AI_TOOLS='$AiTools' PROJECTS='$Projects' SSH_USER='$SeedUser' AGENT_NAME='$agentNameArg' CLAUDE_USER='$RemoteUser' GIT_USER_NAME_B64='$gitNameB64' GIT_USER_EMAIL_B64='$gitEmailB64' GIT_CREDENTIAL_STORE='$gitCredStore' GIT_CLONE_CREDENTIALS_B64='$cloneCredB64' CHECKOUT_PROJECTS='$checkoutArg' SETUP_ROOT_SSH_KEY='$setupRootKeyArg' VSCODE_SERVER='$VsCodeServer' VSCODE_SERVE_WEB='$VsCodeServeWeb' VSCODE_TUNNEL='$VsCodeTunnel' VSCODE_SERVE_WEB_TOKEN_B64='$serveWebTokenB64' VSCODE_CLIENT_COMMIT='$vsCodeCommit'"
Write-Host "  --- live provisioning output ---" -ForegroundColor DarkGray
Invoke-SshStream -Sudo -Command "$envPrefix bash /opt/construct/repo/bin/provision.sh"
Write-Host "  --- end provisioning output ---" -ForegroundColor DarkGray
Write-Ok "Provisioning finished"

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

# If the tunnel needs its one-time device sign-in, do it NOW -- before the reboot.
# The VM is still up and the code-tunnel service is polling for the device code,
# so pausing here lets the user complete the GitHub/Microsoft sign-in against a
# code that is still valid. (Rebooting first would rotate the code.)
if ($tunnelStatus['VSCODE_TUNNEL_NEEDS_SIGNIN'] -eq "yes") {
    $loginLine = ""
    if ($tunnelStatus['VSCODE_TUNNEL_LOGIN_B64']) {
        try { $loginLine = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($tunnelStatus['VSCODE_TUNNEL_LOGIN_B64'])) } catch { }
    }
    if (-not $loginLine) { $loginLine = Get-TunnelLoginLine }

    Write-Host ""
    Write-Step "VS Code tunnel: one-time device sign-in required"
    Write-Host "    Register the tunnel now, BEFORE the reboot:" -ForegroundColor Yellow
    if ($loginLine) {
        Write-Host "      $loginLine" -ForegroundColor Cyan
    } else {
        $journalCmd = if ($script:UseRootKey) { "journalctl -u code-tunnel -n 50" } else { "sudo journalctl -u code-tunnel -n 50" }
        Write-Host "      Could not read the device code automatically. In another window run:" -ForegroundColor Yellow
        Write-Host "        ssh $($script:ConnectUser)@$VmHost `"$journalCmd`"" -ForegroundColor Cyan
        Write-Host "      and use the github.com/login/device link shown there." -ForegroundColor Cyan
    }
    Write-Host "    Open the link, enter the code, and authorize access." -ForegroundColor White
    Read-Host "    Press Enter once sign-in is complete to finish setup and reboot the VM"
}

# Remove the bootstrap public key from the agent user's authorized_keys, then
# reboot — in one authenticated session. On the bootstrap path the removal and
# reboot MUST share one session: once the key is gone we can't reconnect, so
# removing it in a separate call would silently fail to authenticate. The
# session stays valid after the key is removed (auth already happened); the
# reboot is backgrounded with a short delay so the SSH command returns cleanly
# before the VM goes down. On the fast path we connected as root and never
# authorized the bootstrap key this run, but a leftover copy from a failed or
# manual prior run would remain a standing credential (and provision.sh grants
# that agent user passwordless sudo), so we strip it opportunistically there too
# whenever the committed public key is available — root can do this without the
# bootstrap key.
$rmBootstrapCmd = ""
if (Test-Path -LiteralPath $BootstrapPubKey) {
    $pubKeyContent  = (Get-Content $BootstrapPubKey -Raw).Trim()
    $escPubKey      = $pubKeyContent.Replace("/", "\/")
    # `|| true` so a missing/empty authorized_keys (possible on the fast path)
    # doesn't abort the chain before the reboot.
    $rmBootstrapCmd = "sed -i '/$escPubKey/d' /home/${SeedUser}/.ssh/authorized_keys 2>/dev/null || true; "
}

if ($rmBootstrapCmd) {
    Write-Step "Removing bootstrap key and rebooting the VM"
} else {
    Write-Step "Rebooting the VM"
}

# Optionally set the agent user's login password as the LAST thing before the
# reboot. It's only a manual-fallback credential (root logs in by pubkey), and
# we change it inside the same already-authenticated session, so it can never
# lock provisioning out mid-run. The new password is base64-encoded so any
# characters survive the SSH/shell layers untouched; chpasswd (run as root) sets it.
$pwChangeCmd = ""
if ($AgentPassword -and ($AgentPassword -ne $SeedPassword)) {
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes("${SeedUser}:${AgentPassword}"))
    $pwChangeCmd = "echo '$b64' | base64 -d | chpasswd; "
    Write-Ok "Setting a custom login password for '$SeedUser'"
}

Invoke-Ssh -Sudo -Command "${pwChangeCmd}${rmBootstrapCmd}nohup sh -c 'sleep 3; reboot' >/dev/null 2>&1 &"
if ($rmBootstrapCmd) {
    Write-Ok "Bootstrap key removed; VM will reboot in a few seconds"
} else {
    Write-Ok "VM will reboot in a few seconds"
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
        # above; once that's done the tunnel comes up automatically after reboot.
        Write-Host "    You completed the one-time sign-in above -- the tunnel comes up after the reboot." -ForegroundColor Green
    } elseif ($tunnelStatus['VSCODE_TUNNEL_AUTHED'] -eq "yes") {
        Write-Host "    Status: registered and live -- open the URL in a browser or VS Code Remote Explorer." -ForegroundColor Green
    } else {
        Write-Host "    Service is deployed. If the tunnel isn't registered yet, re-run with -VsCodeTunnel true to sign in." -ForegroundColor DarkGray
    }
}
Write-Host ""

}
catch {
    # Standalone: show the failure above our own pause (readable even on a
    # double-click run). Chained (-Auto): rethrow so the upper script owns the
    # single error display + pause.
    if ($Auto) { throw }
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    # Only pause when run standalone; an upper script (-Auto) does its own pause.
    if (-not $Auto) {
        Write-Host ""
        Read-Host "Press Enter to exit"
    }
}
