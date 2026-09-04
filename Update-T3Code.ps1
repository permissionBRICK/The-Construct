#Requires -Version 5.1
[CmdletBinding()]
param(
    # Optional identity overrides forwarded to Provision-AgentVM.ps1 (with param
    # probing so an older provisioner is never handed unknown args).
    [string]$VmHost,
    [string]$HostAlias,
    [ValidateRange(0, 65535)]
    [int]$SshPort = 0,
    [string]$LocalKeyName,
    # NAME-ONLY TARGETING (B11, plan section 4.12): the instance to rebuild, resolved
    # through the client-side registry (lib\AgentVm.InstanceTarget.ps1). It replaces the
    # four overrides above -- the T3 Desktop updater passes one name instead of matching
    # a remote's base URL to four derived values. Empty (and the default instance) means
    # exactly what it always did: no identity is forwarded and the provisioner uses its
    # own defaults. An identity override that DISAGREES with the named entry is an error.
    [string]$InstanceName = ""
)

$ErrorActionPreference = 'Stop'

# Resolve -InstanceName BEFORE anything else: it decides which VM every argument below
# is about. Explicit overrides still win (and must agree with the entry); an unknown
# name stops the run naming the instances this PC does know.
if ($InstanceName) {
    $instanceTargetLib = Join-Path $PSScriptRoot 'lib\AgentVm.InstanceTarget.ps1'
    if (-not (Test-Path -LiteralPath $instanceTargetLib)) {
        throw "-InstanceName needs lib/AgentVm.InstanceTarget.ps1, which is missing from this install. Update The Construct, or pass -VmHost/-HostAlias/-SshPort/-LocalKeyName instead."
    }
    . $instanceTargetLib
    $explicitTarget = @{}
    foreach ($tp in @('VmHost', 'HostAlias', 'SshPort', 'LocalKeyName')) {
        if ($PSBoundParameters.ContainsKey($tp)) { $explicitTarget[$tp] = $PSBoundParameters[$tp] }
    }
    $instanceTarget = Resolve-ConstructVmTarget -Name $InstanceName -Explicit $explicitTarget
    # The DEFAULT instance forwards NOTHING, exactly as a param-less run always did: an
    # older provisioner keeps working, and the identity block below stays empty.
    if (-not $instanceTarget.IsDefault) {
        if (-not $PSBoundParameters.ContainsKey('VmHost'))       { $VmHost       = [string]$instanceTarget.VmHost }
        if (-not $PSBoundParameters.ContainsKey('HostAlias'))    { $HostAlias    = [string]$instanceTarget.HostAlias }
        if (-not $PSBoundParameters.ContainsKey('SshPort'))      { $SshPort      = [int]$instanceTarget.SshPort }
        if (-not $PSBoundParameters.ContainsKey('LocalKeyName')) { $LocalKeyName = [string]$instanceTarget.KeyName }
    }
}

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
# A value counts as "supplied" when the caller bound it OR when -InstanceName resolved
# it above -- a name-targeted rebuild has to reach the same VM a four-argument one does.
$identity = @{}
if ($VmHost)        { $identity['VmHost']       = $VmHost }
if ($HostAlias)     { $identity['HostAlias']    = $HostAlias }
if ($SshPort -ne 0) { $identity['SshPort']      = $SshPort }
if ($LocalKeyName)  { $identity['LocalKeyName'] = $LocalKeyName }
foreach ($k in @($identity.Keys)) {
    if (-not $provCmd.Parameters.ContainsKey($k)) {
        throw "Provision-AgentVM.ps1 in $PSScriptRoot does not support -$k; update The Construct or omit -$k."
    }
    $params[$k] = $identity[$k]
}

Write-Host 'Starting Construct reprovision to rebuild/update patched T3 Code...' -ForegroundColor Cyan
# Provision-AgentVM.ps1 owns the error display: without -Auto it catches its own failure,
# shows the result screen and RETURNS normally, recording the outcome in these globals.
# The launcher (the Construct-built T3 Desktop app's update control waits on this
# process) can only see an exit code, so translate: 1 = provisioning failed,
# 3 = reached the end with optional errors (the provisioner's own result-file codes).
$global:ConstructProvisionHadErrors = $false
$global:ConstructProvisionFailureMessage = $null
& $provision @params
if ($global:ConstructProvisionFailureMessage) { exit 1 }
if ($global:ConstructProvisionHadErrors) { exit 3 }
exit 0
