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
function Get-ConstructStateInstanceName {
    <#
        WHICH INSTANCE's saved settings this rebuild replays -- the ONE answer, asked here
        and nowhere else.

        The settings belong to a VM, not to the checkout, and the ssh alias IS that VM's
        instance name (alias = name, the one derivation rule), LOWERCASED exactly the way
        Get-ConstructConfigBranchName normalises it. The default alias reads the legacy
        top-level keys of .construct-settings.json -- byte-identical to what this script
        always read -- and any other one reads
        %LOCALAPPDATA%\The-Construct\instances\<name>.json, so reprovisioning a second VM
        from the Desktop app can never replay the first VM's toggles.

        DELIBERATELY NOT a -InstanceName parameter of its own yet: this script forwards the
        identity it was given straight to Provision-AgentVM.ps1, and a name that selected
        the settings WITHOUT also retargeting the provisioner would read VM B's toggles and
        rebuild VM A. B11 (plan section 4.12, "Name-only targeting") adds -InstanceName to
        both scripts together; when it lands, this function is what it re-points.
    #>
    # $HostAlias, not $PSBoundParameters: inside a function that automatic variable holds
    # THIS function's parameters, not the script's. The parameter has no default, so an
    # empty value is exactly "not given".
    if ($HostAlias) { return "$HostAlias".Trim().ToLowerInvariant() }
    return 'agent-vm'
}
$instanceName = Get-ConstructStateInstanceName
$settings = @{}
$stateLib = Join-Path $PSScriptRoot 'lib\AgentVm.InstanceState.ps1'
if (Test-Path -LiteralPath $stateLib) {
    # Loaded in a CHILD SCOPE so the library can never leak state into this script's
    # scope; only the resolved settings object comes back.
    try {
        $loaded = & {
            param($libPath, $name, $dir)
            . $libPath
            Read-ConstructInstanceState -Name $name -Dir $dir
        } $stateLib $instanceName $PSScriptRoot
        if ($loaded) { $settings = $loaded }
    } catch { }
} else {
    # An older/partial checkout without the state library: today's single-file read.
    $settingsPath = Join-Path $PSScriptRoot '.construct-settings.json'
    if (Test-Path -LiteralPath $settingsPath) {
        try { $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } catch { }
    }
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
