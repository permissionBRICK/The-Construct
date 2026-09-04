#Requires -Version 5.1
<#
.SYNOPSIS
    Mint a one-time T3 Code pairing link for one Construct VM and print it as JSON.

.DESCRIPTION
    Non-interactive by design: it never prompts, never pauses and opens no window of
    its own. The Construct-built T3 Code Desktop app runs it hidden to link a VM's T3
    server as a remote environment on its own (auto-link, plan section 4.12 "T3
    Desktop topology"); it can equally be run from a console.

    It connects to the VM over SSH as root -- with the key provisioning wrote to
    ~\.ssh\<LocalKeyName>, falling back to the ~\.ssh\config Host alias -- and asks the
    VM's own t3 CLI for a pairing link bound to the origin the VM advertises
    (T3CODE_PUBLIC_BASE_URL when Construct's TLS proxy is up, else the client-reachable
    host and the plain port). The link carries the ADMINISTRATIVE scope set when the
    VM's T3 build understands `--scopes` (Construct's patched build); a stock T3 issues
    its standard client scopes.

    Output is ONE JSON line on stdout:
        {"ok":true,"instance":"<name>","pairUrl":"https://...","scopes":"administrative"|"standard"}
        {"ok":false,"instance":"<name>","error":"<why>"}
    Exit code 0 when a link was minted, 1 otherwise. Nothing else goes to stdout.

.PARAMETER InstanceName
    Name-only targeting (plan section 4.12): the Construct instance to link, resolved
    through the client-side registry. Empty (and the default instance) = the default
    VM's literals.

.PARAMETER VmHost / HostAlias / SshPort / LocalKeyName
    Explicit identity overrides; they win over the registry entry and must agree with it.

.PARAMETER Ttl
    How long the one-time link stays valid (t3's duration syntax). Default 10m.

.PARAMETER Scopes
    "administrative" (default; falls back to standard on a T3 without the flag) or
    "standard".
#>
[CmdletBinding()]
param(
    [string]$InstanceName = "",
    [string]$VmHost      = "agent-vm.mshome.net",
    [string]$HostAlias   = "agent-vm",
    [string]$RemoteUser  = "root",
    [string]$LocalKeyName = "agent_vm_ed25519",
    [ValidateRange(1, 65535)]
    [int]$SshPort        = 22,
    [string]$Ttl         = "10m",
    [ValidateSet("administrative", "standard")]
    [string]$Scopes      = "administrative"
)

$ErrorActionPreference = "Stop"

# Everything this script says goes to stdout as ONE JSON line; a caller that spawned it
# hidden reads exactly that. Diagnostics go to stderr.
function Write-Result([hashtable]$obj) {
    $ordered = [ordered]@{}
    foreach ($k in @('ok', 'instance', 'pairUrl', 'scopes', 'error')) {
        if ($obj.ContainsKey($k)) { $ordered[$k] = $obj[$k] }
    }
    [Console]::Out.WriteLine(([pscustomobject]$ordered | ConvertTo-Json -Compress -Depth 3))
}
function Fail([string]$why) {
    Write-Result @{ ok = $false; instance = $script:ResolvedName; error = $why }
    exit 1
}

$script:ResolvedName = if ($InstanceName) { "$InstanceName".Trim().ToLowerInvariant() } else { 'agent-vm' }

# Resolve -InstanceName before anything reads the identity below (the same block
# Get-AgentUsage.ps1 / Update-T3Code.ps1 use): explicit values still win and must agree
# with the entry; an unknown name is an error, never a fallback to the default VM.
$script:IsDefault = $true
if ($InstanceName) {
    $instanceTargetLib = Join-Path $PSScriptRoot "lib\AgentVm.InstanceTarget.ps1"
    if (-not (Test-Path -LiteralPath $instanceTargetLib)) {
        Fail "-InstanceName needs lib/AgentVm.InstanceTarget.ps1, which is missing from this install. Update The Construct."
    }
    . $instanceTargetLib
    $explicitTarget = @{}
    foreach ($tp in @('VmHost', 'HostAlias', 'SshPort', 'LocalKeyName')) {
        if ($PSBoundParameters.ContainsKey($tp)) { $explicitTarget[$tp] = $PSBoundParameters[$tp] }
    }
    try {
        $instanceTarget = Resolve-ConstructVmTarget -Name $InstanceName -Explicit $explicitTarget
    } catch {
        Fail ("$($_.Exception.Message)".Trim())
    }
    if (-not $PSBoundParameters.ContainsKey('VmHost'))       { $VmHost       = [string]$instanceTarget.VmHost }
    if (-not $PSBoundParameters.ContainsKey('HostAlias'))    { $HostAlias    = [string]$instanceTarget.HostAlias }
    if (-not $PSBoundParameters.ContainsKey('SshPort'))      { $SshPort      = [int]$instanceTarget.SshPort }
    if (-not $PSBoundParameters.ContainsKey('LocalKeyName')) { $LocalKeyName = [string]$instanceTarget.KeyName }
    $script:IsDefault = [bool]$instanceTarget.IsDefault
}

try {
    if (-not (Get-Command ssh.exe -ErrorAction SilentlyContinue)) {
        Fail "ssh.exe not found. It ships with Windows 10/11 (OpenSSH Client)."
    }

    # Decode ssh's stdout as UTF-8 (the VM prints JSON).
    try {
        $utf8NoBom = New-Object System.Text.UTF8Encoding $false
        [Console]::OutputEncoding = $utf8NoBom
        $OutputEncoding = $utf8NoBom
    } catch { }

    $keyPath = Join-Path $HOME ".ssh\$LocalKeyName"
    $sshPortArgs = if ($SshPort -ne 22) { @("-p", "$SshPort") } else { @() }
    if (Test-Path -LiteralPath $keyPath) {
        $sshTarget = "$RemoteUser@$VmHost"
        $sshOpts = @(
            "-i", $keyPath,
            "-o", "IdentitiesOnly=yes",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "UserKnownHostsFile=$HOME\.ssh\known_hosts",
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=15"
        )
    } else {
        $sshTarget = $HostAlias
        $sshOpts = @(
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=15"
        )
    }

    # The VM-side script: the control panel's pairing script (extension/src/t3code.js
    # buildPairingScript), with the scope flag on top. The origin is decided ON THE VM
    # from what it advertises; the label names the instance so the server's connection
    # list says which machine a session belongs to.
    $label = if ($script:IsDefault) { "construct-t3-desktop" } else { "construct-t3-desktop-$($script:ResolvedName)" }
    $hostExpr = if ($script:IsDefault) {
        # The default VM is reached at its own mshome name; CONSTRUCT_EXTERNAL_HOST is
        # deliberately NOT read for it (config.env is user-editable).
        '$(hostname).mshome.net'
    } else {
        # A remote/forwarded VM is not reachable at its mshome name: prefer the
        # client-reachable host the provisioner recorded, fall back to the mshome name.
        '${ext:-$(hostname).mshome.net}'
    }
    $wantScopes = $Scopes
    # Both land inside single quotes on the VM's command line.
    if ($Ttl -notmatch '^[0-9A-Za-z ]{1,32}$') { Fail "-Ttl must be a t3 duration such as 10m or 1h" }
    $bash = @"
set -uo pipefail
CONFIG_FILE=/etc/construct/config.env
cfgget() {
  _v="`$(sed -n "s/^`$1=//p" "`$CONFIG_FILE" 2>/dev/null | head -1)"
  case "`$_v" in
    "'"*"'") _v="`${_v#\'}"; _v="`${_v%\'}"; _v="`${_v//\'\\\'\'/\'}" ;;
  esac
  printf '%s' "`$_v"
}
command -v t3 >/dev/null 2>&1 || { echo "t3 is not installed on the VM" >&2; exit 2; }
T3CODE_PORT="`$(cfgget T3CODE_PORT)"; T3CODE_PORT="`${T3CODE_PORT:-5177}"
T3CODE_PUBLIC_BASE_URL="`$(cfgget T3CODE_PUBLIC_BASE_URL)"
ext="`$(cfgget CONSTRUCT_EXTERNAL_HOST)"
if [ -n "`$T3CODE_PUBLIC_BASE_URL" ]; then base="`$T3CODE_PUBLIC_BASE_URL"; else base="http://${hostExpr}:`$T3CODE_PORT"; fi
scopes=standard
extra=""
if [ "$wantScopes" = "administrative" ] && t3 auth pairing create --help 2>&1 | grep -q -- '--scopes'; then
  scopes=administrative; extra="--scopes administrative"
fi
out="`$(t3 auth pairing create --json --ttl '$Ttl' --label '$label' --base-url "`$base" `$extra --log-level none 2>&1)" || { printf '%s\n' "`$out" >&2; exit 3; }
printf 'CONSTRUCT_PAIRING_SCOPES=%s\n' "`$scopes"
printf '%s\n' "`$out"
"@
    # Base64 over the wire: PowerShell would otherwise pipe the script with CRLF line
    # endings and OEM encoding, and a remote command line must not carry quotes.
    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($bash))
    $remoteCmd = "printf %s '$b64' | base64 -d | bash"

    $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "Continue"
    $stderrFile = [System.IO.Path]::GetTempFileName()
    try {
        $raw = & ssh.exe @sshPortArgs @sshOpts $sshTarget $remoteCmd 2>$stderrFile | Out-String
        $rc = $LASTEXITCODE
        $err = ""
        try { $err = (Get-Content -LiteralPath $stderrFile -Raw -ErrorAction SilentlyContinue) } catch { }
    } finally {
        $ErrorActionPreference = $prevEAP
        Remove-Item -LiteralPath $stderrFile -Force -ErrorAction SilentlyContinue
    }
    if ($rc -ne 0) {
        $why = switch ($rc) {
            2 { "t3 is not installed on the VM" }
            3 { "the VM's t3 could not mint a pairing link" }
            255 { "could not reach $sshTarget over SSH (is the VM running and provisioned?)" }
            default { "pairing link script exited $rc" }
        }
        $detail = "$err".Trim()
        if ($detail) { $why = "$why -- $($detail -split "`n" | Select-Object -Last 1)" }
        Fail $why
    }

    $scopesUsed = "standard"
    if ("$raw" -match '(?m)^CONSTRUCT_PAIRING_SCOPES=(\w+)\s*$') { $scopesUsed = $matches[1] }
    $pairUrl = ""
    if ("$raw" -match '"pairUrl"\s*:\s*"([^"]+)"') { $pairUrl = $matches[1] }
    if (-not $pairUrl) { Fail "the VM's t3 printed no pairing link" }
    Write-Result @{ ok = $true; instance = $script:ResolvedName; pairUrl = $pairUrl; scopes = $scopesUsed }
    exit 0
} catch {
    Fail ("$($_.Exception.Message)".Trim())
}
