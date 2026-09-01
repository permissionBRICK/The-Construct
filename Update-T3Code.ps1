#Requires -Version 5.1
[CmdletBinding()]
param(
    # Optional identity overrides forwarded to Provision-AgentVM.ps1 (with param
    # probing so an older provisioner is never handed unknown args).
    [string]$VmHost,
    [string]$HostAlias,
    [int]$SshPort = 0
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

# Forward optional identity overrides only when the caller supplied them AND the
# target Provision-AgentVM.ps1 declares the matching parameter (skew guard: an
# older provisioner must never receive an unknown argument).
try {
    $provCmd = Get-Command -Name $provision -CommandType ExternalScript -ErrorAction Stop
    if ($PSBoundParameters.ContainsKey('VmHost') -and $VmHost -and $provCmd.Parameters.ContainsKey('VmHost')) {
        $params['VmHost'] = $VmHost
    }
    if ($PSBoundParameters.ContainsKey('HostAlias') -and $HostAlias -and $provCmd.Parameters.ContainsKey('HostAlias')) {
        $params['HostAlias'] = $HostAlias
    }
    if ($PSBoundParameters.ContainsKey('SshPort') -and $SshPort -ne 0 -and $provCmd.Parameters.ContainsKey('SshPort')) {
        $params['SshPort'] = $SshPort
    }
} catch { }

Write-Host 'Starting Construct reprovision to rebuild/update patched T3 Code...' -ForegroundColor Cyan
& $provision @params
