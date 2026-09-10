# Exercise the host installer's saved-setting handling without installing a service.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    "$root/service/host/Install-ConstructHost.ps1", [ref]$null, [ref]$errors)
if ($errors) { throw ($errors | Out-String) }
$function = $ast.Find({ param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -eq 'Get-ConstructBrowserConsoleEnabled'
}, $true)
Invoke-Expression $function.Extent.Text
$dir = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid())
New-Item -ItemType Directory $dir | Out-Null
$path = Join-Path $dir 'appsettings.Production.json'
try {
    if (-not (Get-ConstructBrowserConsoleEnabled $path)) { throw 'Fresh installation did not enable console' }
    Set-Content $path '{"Constructd":{"Persistence":"Sqlite"}}'
    if (-not (Get-ConstructBrowserConsoleEnabled $path)) { throw 'Legacy installation did not gain default' }
    Set-Content $path '{"Constructd":{"BrowserConsoleEnabled":false}}'
    if (Get-ConstructBrowserConsoleEnabled $path) { throw 'Explicit administrator opt-out was lost' }
    Set-Content $path '{"Constructd":{"BrowserConsoleEnabled":true}}'
    if (-not (Get-ConstructBrowserConsoleEnabled $path)) { throw 'Existing enabled setting was lost' }
    Set-Content $path '{"Constructd":{"BrowserConsoleEnabled":"false"}}'
    $rejected = $false
    try { Get-ConstructBrowserConsoleEnabled $path | Out-Null } catch { $rejected = $true }
    if (-not $rejected) { throw 'Invalid boolean was silently accepted' }
    'PASS: fresh/default, legacy upgrade, explicit opt-out, enabled, malformed setting'
} finally { Remove-Item $dir -Recurse }
