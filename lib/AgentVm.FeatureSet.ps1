#Requires -Version 5.1
# One table owns tier defaults, prompt order, installer parameters and panel keys.
function Get-ConstructFeatureTable {
    @(
        @{ Name='Companion'; Parameter='SkipCompanion'; Key='companion'; Minimal=$true; Full=$true; Prompt='Install the Construct Companion tray app?'; Invert=$true }
        @{ Name='Claude partial streaming'; Parameter='ClaudePartialStreaming'; Key='claudePartialStreaming'; Minimal=$true; Full=$true; Prompt='Enable Claude Code live streaming?' }
        @{ Name='Git credential store'; Parameter='GitCredentialStore'; Key='gitCredentialStore'; Minimal=$true; Full=$true; Prompt='Store git credentials on the VM in plaintext (~/.git-credentials)?' }
        @{ Name='VS Code serve-web'; Parameter='VsCodeServeWeb'; Key='vsCodeServeWeb'; Minimal=$false; Full=$true; Prompt='Enable the browser IDE on port 8000?' }
        @{ Name='VS Code tunnel'; Parameter='VsCodeTunnel'; Key='vsCodeTunnel'; Minimal=$false; Full=$false; Prompt='Enable the VS Code tunnel (vscode.dev)?' }
        @{ Name='SMB workspace share'; Parameter='SmbShare'; Key='smbShare'; Minimal=$false; Full=$true; Prompt='Enable the SMB workspace share?' }
        @{ Name='Map share to a drive letter'; Parameter='MountRepoShare'; Key='mountRepoShare'; Minimal=$false; Full=$false; Prompt='Map the workspace share to a drive letter (requires SMB)?' }
        @{ Name='Microphone passthrough'; Parameter='MicPassthrough'; Key='micPassthrough'; Minimal=$false; Full=$true; Prompt='Enable microphone passthrough?' }
        @{ Name='Automatic checkpoints'; Parameter='AutomaticCheckpoints'; Key='vmAutoCheckpoints'; Minimal=$false; Full=$false; Prompt='Enable automatic Hyper-V checkpoints?' }
        @{ Name='OpenCode background watcher'; Parameter='OpenCodeBackgroundWatcher'; Key='opencodeBackgroundWatcher'; Minimal=$false; Full=$true; Prompt='Enable the OpenCode background watcher patch?' }
        @{ Name='T3 Code'; Parameter='T3Code'; Key='t3code'; Minimal=$false; Full=$true; Prompt='Install the T3 Code web GUI (stable)?' }
        @{ Name='Patched T3 Code + Desktop'; Parameter='T3CodeLimitResume'; Key='t3codeLimitResume'; Minimal=$false; Full=$true; Prompt='Build patched T3 Code + Desktop?' }
        @{ Name='T3 Code HTTPS'; Parameter='T3CodeHttps'; Key='t3codeHttps'; Minimal=$true; Full=$true; Prompt='Enable T3 Code HTTPS (the VM default)?' }
    )
}

function ConvertTo-ConstructFeatureBoolean {
    param($Value)
    if ("$Value" -notin @('true', 'false')) { throw "Feature values must be true or false, got '$Value'." }
    return ("$Value" -eq 'true')
}

function Resolve-ConstructFeatureSet {
    [CmdletBinding()]
    param(
        [ValidateSet('minimal', 'full', 'custom')][string]$FeatureSet = 'minimal',
        [hashtable]$Bound = @{},
        [hashtable]$CustomAnswers = @{}
    )
    $values = [ordered]@{}
    $parameters = [ordered]@{}
    $settings = [ordered]@{}
    foreach ($row in Get-ConstructFeatureTable) {
        $value = $row.Minimal
        if ($FeatureSet -eq 'full') { $value = $row.Full }
        if ($FeatureSet -eq 'custom' -and $CustomAnswers.ContainsKey($row.Parameter)) {
            $value = ConvertTo-ConstructFeatureBoolean $CustomAnswers[$row.Parameter]
        }
        if ($Bound.ContainsKey($row.Parameter)) {
            # Empty is the existing installer's explicit 'keep the guest choice' value.
            if ($row.Parameter -ne 'SkipCompanion' -and "$($Bound[$row.Parameter])" -eq '') {
                $values[$row.Name] = ''
                $parameters[$row.Parameter] = ''
                continue
            }
            $value = ConvertTo-ConstructFeatureBoolean $Bound[$row.Parameter]
            if ($row.ContainsKey('Invert') -and $row.Invert) { $value = -not $value }
        }
        $values[$row.Name] = $value
        $settings[$row.Key] = $value
        if ($row.ContainsKey('Invert') -and $row.Invert) { $parameters[$row.Parameter] = [switch](-not $value) }
        else { $parameters[$row.Parameter] = "$value".ToLowerInvariant() }
    }
    $channel = 'stable'
    if ($Bound.ContainsKey('T3CodeChannel')) { $channel = ([string]$Bound.T3CodeChannel).ToLowerInvariant() }
    $parameters['T3CodeChannel'] = $channel
    if ($channel) { $settings['t3codeChannel'] = $channel }
    return [ordered]@{ Values=$values; Parameters=$parameters; Settings=$settings; ForwardParameters=@($parameters.Keys) }
}

# Only send values this run resolved, and only to parameters the installed script has.
function Add-ConstructFeatureArguments {
    param([hashtable]$Arguments, [System.Collections.IDictionary]$Values, $Command)
    if (-not $Command -or -not $Values) { return }
    foreach ($row in Get-ConstructFeatureTable) {
        $p = $row.Parameter
        if (($Values.Keys -contains $p) -and $Command.Parameters.ContainsKey($p)) { $Arguments[$p] = $Values[$p] }
    }
    if (($Values.Keys -contains 'T3CodeChannel') -and $Command.Parameters.ContainsKey('T3CodeChannel')) {
        $Arguments['T3CodeChannel'] = $Values['T3CodeChannel']
    }
}
