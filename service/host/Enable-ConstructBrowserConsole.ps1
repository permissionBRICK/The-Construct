#Requires -Version 5.1
# Run on the Construct service host. Takes effect on the next service update/restart.
# By default, use the settings beside the registered constructd executable.
# Pass -SettingsPath to configure a different installation explicitly.
[CmdletBinding()]
param(
    [string]$SettingsPath,
    [switch]$Disable
)
$ErrorActionPreference = 'Stop'
if ([string]::IsNullOrWhiteSpace($SettingsPath)) {
    # Read the service registration rather than assuming a checkout location.
    # This covers the UI installer, older installs and custom publish directories,
    # and also works when the service is stopped.
    try {
        $service = Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\constructd' -Name ImagePath -ErrorAction Stop
    } catch {
        throw 'Could not read the constructd service installation. Run on the Windows host or pass -SettingsPath explicitly.'
    }
    $imagePath = [Environment]::ExpandEnvironmentVariables([string]$service.ImagePath)
    if ($imagePath -notmatch '^\s*(?:"([^\"]+\.exe)"|(.+?\.exe)(?=\s|$))') {
        throw 'Could not locate the constructd executable in its service registration. Pass -SettingsPath explicitly.'
    }
    $exe = $Matches[1]
    if (-not $exe) { $exe = $Matches[2] }
    $SettingsPath = Join-Path (Split-Path -Parent $exe) 'appsettings.Production.json'
}
$SettingsPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($SettingsPath)
$config = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
if (-not $config.Constructd) { throw 'Constructd configuration section is missing.' }
$config.Constructd | Add-Member -NotePropertyName BrowserConsoleEnabled -NotePropertyValue (-not $Disable.IsPresent) -Force
$temp = $SettingsPath + '.console-' + [Guid]::NewGuid().ToString('N') + '.tmp'
try {
    [IO.File]::WriteAllText($temp, ($config | ConvertTo-Json -Depth 50), (New-Object Text.UTF8Encoding($false)))
    [IO.File]::Replace($temp, $SettingsPath, [System.Management.Automation.Language.NullString]::Value)
} finally {
    if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp }
}
Write-Output "Browser console setting saved in $SettingsPath. Activate it with the next host update or service restart."
