# Run on the Construct service host. Takes effect on the next service update/restart.
[CmdletBinding()]
param(
    [string]$SettingsPath = 'C:\Construct\service\publish\appsettings.Production.json',
    [switch]$Disable
)
$ErrorActionPreference = 'Stop'
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
Write-Output 'Browser console setting saved. Activate it with the next host update or service restart.'
