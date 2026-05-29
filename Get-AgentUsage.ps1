#Requires -Version 5.1
<#
.SYNOPSIS
    Collect token-usage statistics from a provisioned construct agent VM and save
    a combined JSON report via a file-picker dialog.

.DESCRIPTION
    Run this from your Windows host AFTER a VM has been provisioned with
    Provision-AgentVM.ps1. It:

      1. Connects to the VM over SSH as root, using the key that provisioning
         wrote to ~\.ssh\<LocalKeyName> (falling back to the ~\.ssh\config Host
         alias the provisioner set up).
      2. Ensures ccusage is available on the VM (uses an existing ccusage / bunx /
         npx, otherwise installs it via npm or a self-contained bun runtime).
      3. Runs ccusage for all three coding agents -- Claude Code, Codex and
         OpenCode -- with --json, reading each tool's local usage files under
         /root (where the provisioned agents run).
      4. Combines the three results into one JSON document and lets you save it
         through a standard Save-As dialog.

    ccusage reports raw token counts (input / output / cache-create / cache-read)
    and the model per session, with no baked-in dollar cost -- so cost can be
    computed downstream under any pricing scheme.

.PARAMETER VmHost
    VM hostname (default agent-vm.mshome.net), matching Provision-AgentVM.ps1.

.PARAMETER HostAlias
    The ~\.ssh\config Host alias provisioning created; used to connect when the
    explicit key file is not found.

.PARAMETER Report
    ccusage report granularity: session (default), daily, weekly or monthly.

.PARAMETER OutFile
    Write the combined JSON straight to this path and skip the file picker.

.NOTES
    Read-only: it never modifies agent data, only installs ccusage if missing.
#>
[CmdletBinding()]
param(
    [string]$VmHost      = "agent-vm.mshome.net",
    [string]$HostAlias   = "agent-vm",
    [string]$RemoteUser  = "root",
    [string]$LocalKeyName = "agent_vm_ed25519",
    [ValidateSet("session", "daily", "weekly", "monthly")]
    [string]$Report      = "session",
    [string]$OutFile     = "",
    # Set when launched by an upper script that owns the final pause.
    [switch]$Auto
)

$ErrorActionPreference = "Stop"

# Decode native-command output (ssh stdout) as UTF-8 so the remote's JSON and any
# box-drawing/emoji bytes survive Windows PowerShell 5.1's default OEM decoding.
try {
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [Console]::OutputEncoding = $utf8NoBom
    $OutputEncoding = $utf8NoBom
} catch { }

function Write-Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "    $msg" -ForegroundColor Green }

# --- Connection -------------------------------------------------------------

# Prefer the explicit key provisioning wrote (root login by pubkey). If it isn't
# there, fall back to the Host alias in ~\.ssh\config, which the provisioner also
# configured to use that same key.
$keyPath = Join-Path $HOME ".ssh\$LocalKeyName"
if (Test-Path -LiteralPath $keyPath) {
    $script:SshTarget = "$RemoteUser@$VmHost"
    $script:SshOpts = @(
        "-i", $keyPath,
        "-o", "IdentitiesOnly=yes",
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", "UserKnownHostsFile=$HOME\.ssh\known_hosts",
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=15"
    )
} else {
    # No saved key: lean on the ~\.ssh\config Host entry (IdentityFile + User set
    # there by the provisioner).
    $script:SshTarget = $HostAlias
    $script:SshOpts = @(
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=15"
    )
}

function Test-Connection {
    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    & ssh.exe @script:SshOpts $script:SshTarget "true" 2>$null | Out-Null
    $ok = ($LASTEXITCODE -eq 0)
    $ErrorActionPreference = $prevEAP
    return $ok
}

# --- Remote collector script ------------------------------------------------

# This bash script runs on the VM. It ensures ccusage is available, runs it for
# each of the three agents, and assembles ONE combined JSON object on stdout.
# Everything diagnostic (installer noise, errors) goes to stderr so stdout stays
# pure JSON -- the only thing this PowerShell side captures. jq (installed by the
# VM's bootstrap) builds the combined object and per-tool error fallbacks safely.
$remoteScript = @'
set -u
REPORT="__REPORT__"

CC=()
ensure_ccusage() {
  if command -v ccusage >/dev/null 2>&1; then CC=(ccusage); return; fi
  if command -v bunx    >/dev/null 2>&1; then CC=(bunx ccusage@latest); return; fi
  if command -v npx     >/dev/null 2>&1; then CC=(npx -y ccusage@latest); return; fi

  echo "ccusage not found on the VM; attempting to install it..." >&2

  # Preferred: a global npm install when Node is present.
  if command -v npm >/dev/null 2>&1; then
    npm i -g ccusage >&2 2>&1 || true
    if command -v ccusage >/dev/null 2>&1; then CC=(ccusage); return; fi
  fi

  # Otherwise install the self-contained bun runtime and run ccusage via bunx.
  if ! command -v bun >/dev/null 2>&1; then
    command -v unzip >/dev/null 2>&1 || { (apt-get update && apt-get install -y unzip) >&2 2>&1 || true; }
    curl -fsSL https://bun.sh/install | bash >&2 2>&1 || true
  fi
  export BUN_INSTALL="${BUN_INSTALL:-$HOME/.bun}"
  export PATH="$BUN_INSTALL/bin:$PATH"
  if command -v bunx >/dev/null 2>&1; then CC=(bunx ccusage@latest); return; fi

  CC=()
}

# Run ccusage for one tool, returning valid JSON either way: the real report on
# success, or a small {error,...} object describing what went wrong.
capture() {
  local tool="$1" out rc errfile
  if [ "${#CC[@]}" -eq 0 ]; then
    jq -n --arg t "$tool" '{error:"no JavaScript runtime available to run ccusage", tool:$t}'
    return
  fi
  errfile="$(mktemp)"
  out="$("${CC[@]}" "$tool" "$REPORT" --json 2>"$errfile")"; rc=$?
  if [ "$rc" -ne 0 ] || ! printf '%s' "$out" | jq -e . >/dev/null 2>&1; then
    local detail; detail="$(tr '\n' ' ' <"$errfile" | head -c 500)"
    jq -n --arg t "$tool" --arg d "$detail" \
      '{error:("ccusage failed for "+$t), detail:$d}'
  else
    printf '%s' "$out"
  fi
  rm -f "$errfile"
}

ensure_ccusage

claude_json="$(capture claude)"
codex_json="$(capture codex)"
opencode_json="$(capture opencode)"

jq -n \
  --arg host "$(hostname)" \
  --arg report "$REPORT" \
  --argjson claude "$claude_json" \
  --argjson codex "$codex_json" \
  --argjson opencode "$opencode_json" \
  '{
     generatedAt: (now | todate),
     vmHost: $host,
     report: $report,
     tools: { claude: $claude, codex: $codex, opencode: $opencode }
   }'
'@

$remoteScript = $remoteScript.Replace("__REPORT__", $Report)

function Invoke-RemoteCollector {
    # Send the collector script base64-encoded so no quoting/encoding survives
    # intact regardless of its contents, decode it to a temp file on the VM, run
    # it, and return its stdout (the combined JSON). ssh stderr is discarded so
    # only the JSON comes back.
    $scriptLf = ($remoteScript -replace "`r`n", "`n")
    $b64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($scriptLf))
    $remoteCmd = "f=`$(mktemp) && printf %s '$b64' | base64 -d > `"`$f`" && bash `"`$f`"; rc=`$?; rm -f `"`$f`"; exit `$rc"

    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    $output = & ssh.exe @script:SshOpts $script:SshTarget $remoteCmd 2>$null
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = $prevEAP
    if ($exitCode -ne 0) {
        throw "Remote usage collection failed (exit $exitCode). Re-run with -Verbose, or check ccusage on the VM."
    }
    return ($output | Out-String)
}

# --- Save (file picker) -----------------------------------------------------

function Save-Report {
    param([Parameter(Mandatory)][string]$Json)

    $stamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $defaultName = "construct-usage-$HostAlias-$Report-$stamp.json"

    # Explicit -OutFile bypasses the dialog (automation / -Auto).
    if ($OutFile) {
        [System.IO.File]::WriteAllText($OutFile, $Json)
        Write-Ok "Saved usage report to $OutFile"
        return
    }

    $saved = $null
    try {
        Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
        $dlg = New-Object System.Windows.Forms.SaveFileDialog
        $dlg.Title = "Save combined agent usage report"
        $dlg.Filter = "JSON files (*.json)|*.json|All files (*.*)|*.*"
        $dlg.FileName = $defaultName
        try { $dlg.InitialDirectory = [Environment]::GetFolderPath('Desktop') } catch { }
        if ($dlg.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
            $saved = $dlg.FileName
        }
    } catch {
        Write-Warning "Could not open a Save dialog ($($_.Exception.Message))."
    }

    if (-not $saved) {
        # No dialog (or cancelled): fall back to the Desktop so work isn't lost.
        $saved = Join-Path ([Environment]::GetFolderPath('Desktop')) $defaultName
        Write-Warning "Falling back to: $saved"
    }

    [System.IO.File]::WriteAllText($saved, $Json)
    Write-Ok "Saved usage report to $saved"
}

# ============================================================================
# Main
# ============================================================================
try {

Write-Host ""
Write-Host "Construct agent VM usage collector" -ForegroundColor White
Write-Host "Target: $script:SshTarget  |  report: $Report" -ForegroundColor DarkGray
Write-Host ""

if (-not (Get-Command ssh.exe -ErrorAction SilentlyContinue)) {
    throw "ssh.exe not found. It ships with Windows 10/11 (OpenSSH Client). Install it and re-run."
}

Write-Step "Connecting to the VM"
if (-not (Test-Connection)) {
    throw "Cannot reach $script:SshTarget over SSH as root. Make sure the VM is running and was provisioned (Provision-AgentVM.ps1 writes the root key to ~\.ssh\$LocalKeyName), or pass -VmHost / -LocalKeyName / -HostAlias."
}
Write-Ok "Connected"

Write-Step "Collecting usage with ccusage (installing it on the VM if needed; this can take a minute)"
$json = (Invoke-RemoteCollector).Trim()

# Validate the JSON before saving so we never write garbage from a broken run.
try {
    $null = $json | ConvertFrom-Json -ErrorAction Stop
} catch {
    Write-Host ""
    Write-Host "Remote output was not valid JSON:" -ForegroundColor Red
    Write-Host $json
    throw "Did not receive valid JSON from the VM."
}
Write-Ok "Received combined usage JSON"

Save-Report -Json $json

Write-Host ""
Write-Host "Done." -ForegroundColor Green

}
catch {
    if ($Auto) { throw }
    Write-Host ""
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if (-not $Auto) {
        Write-Host ""
        Read-Host "Press Enter to exit"
    }
}
