#Requires -Version 5.1
[CmdletBinding()]
param(
    # Optional identity overrides forwarded to Provision-AgentVM.ps1 (with param
    # probing so an older provisioner is never handed unknown args).
    [string]$VmHost,
    [string]$HostAlias,
    [ValidateRange(0, 65535)]
    [int]$SshPort = 0,
    [string]$LocalKeyName
)

$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $PSScriptRoot '.construct-settings.json'
$settings = @{}
if (Test-Path -LiteralPath $settingsPath) {
    try { $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } catch { }
}

function As-BoolString($value, [bool]$fallback) {
    if ($null -eq $value) { return $fallback.ToString().ToLowerInvariant() }
    return ([bool]$value).ToString().ToLowerInvariant()
}

$provision = Join-Path $PSScriptRoot 'Provision-AgentVM.ps1'
if (-not (Test-Path -LiteralPath $provision)) { throw "Provision-AgentVM.ps1 not found beside $PSCommandPath" }

$params = @{
    Action                    = 'provision'
    FromPanel                 = $true
    NonInteractive            = $true
    VsCodeServeWeb            = As-BoolString $settings.vsCodeServeWeb $true
    VsCodeTunnel              = As-BoolString $settings.vsCodeTunnel $false
    SmbShare                  = As-BoolString $settings.smbShare $true
    ClaudePartialStreaming    = As-BoolString $settings.claudePartialStreaming $true
    MicPassthrough            = As-BoolString $settings.micPassthrough $false
    OpenCodeBackgroundWatcher = As-BoolString $settings.opencodeBackgroundWatcher $false
    T3Code                    = As-BoolString $settings.t3code $true
    T3CodeChannel             = if ($settings.t3codeChannel -eq 'nightly') { 'nightly' } else { 'stable' }
    T3CodeLimitResume         = As-BoolString $settings.t3codeLimitResume $true
}
if ($settings.projects -is [System.Array] -and $settings.projects.Count -gt 0) {
    $params.Projects = ($settings.projects -join ',')
}

# Forward caller-supplied identity overrides. Each one is a hard requirement when
# given: a provisioner that lacks the parameter (or a failed metadata probe) must
# FAIL here rather than silently reprovision the default VM / port 22. Only
# parameters the caller did not supply are omitted silently.
$provCmd = Get-Command -Name $provision -CommandType ExternalScript -ErrorAction Stop
# HTTPS for the T3 web GUI (config.env T3CODE_HTTPS). An ABSENT setting stays
# unset, which is how the provisioner is told to keep whatever the VM saved
# (default true) -- so a rebuild launched from the Desktop app never flips the
# VM's HTTPS state by accident. Unlike the identity overrides below, an older
# provisioner without the parameter is NOT an error here: it simply predates
# HTTPS support, so drop the value instead of failing the rebuild.
if ($null -ne $settings.t3codeHttps -and $provCmd.Parameters.ContainsKey('T3CodeHttps')) {
    $params.T3CodeHttps = As-BoolString $settings.t3codeHttps $true
}
$identity = @{}
if ($PSBoundParameters.ContainsKey('VmHost')       -and $VmHost)        { $identity['VmHost']       = $VmHost }
if ($PSBoundParameters.ContainsKey('HostAlias')    -and $HostAlias)     { $identity['HostAlias']    = $HostAlias }
if ($PSBoundParameters.ContainsKey('SshPort')      -and $SshPort -ne 0) { $identity['SshPort']      = $SshPort }
if ($PSBoundParameters.ContainsKey('LocalKeyName') -and $LocalKeyName)  { $identity['LocalKeyName'] = $LocalKeyName }
foreach ($k in @($identity.Keys)) {
    if (-not $provCmd.Parameters.ContainsKey($k)) {
        throw "Provision-AgentVM.ps1 in $PSScriptRoot does not support -$k; update The Construct or omit -$k."
    }
    $params[$k] = $identity[$k]
}

Write-Host 'Starting Construct reprovision to rebuild/update patched T3 Code...' -ForegroundColor Cyan
& $provision @params
