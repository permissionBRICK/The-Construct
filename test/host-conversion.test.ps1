#Requires -Version 5.1
# Run manually on Windows PowerShell 5.1 or pwsh. No host installation or ISO download.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
function Import-Function($File, $Name) {
    $t=$null; $e=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repo $File),[ref]$t,[ref]$e)
    if ($e.Count) { throw ($e | Out-String) }
    $function=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $Name},$true)
    if (-not $function) { throw "Missing function $Name" }
    Invoke-Expression ($function.Extent.Text -replace '^function ([^ (]+)', 'function global:$1')
}
Import-Function 'service/host/ConvertTo-ConstructHost.ps1' 'Quote-Argument'
Import-Function 'service/host/ConvertTo-ConstructHost.ps1' 'Assert-NoLinks'
Import-Function 'service/host/ConvertTo-ConstructHost.ps1' 'Expand-VerifiedPackage'
Import-Function 'service/host/ConvertTo-ConstructHost.ps1' 'Write-JsonFile'
$utf8 = New-Object Text.UTF8Encoding($false)
if ((Quote-Argument 'C:\a b\') -cne '"C:\a b\\"') { throw 'Trailing slash quoting failed.' }
if ((Quote-Argument 'a"b') -cne '"a\"b"') { throw 'Embedded quote escaping failed.' }
$work = Join-Path ([IO.Path]::GetTempPath()) ('construct-conversion-test-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
try {
    Write-JsonFile (Join-Path $work 'result.json') @{ok=$false}
    Write-JsonFile (Join-Path $work 'result.json') @{ok=$true}
    if (-not (Get-Content -Raw (Join-Path $work 'result.json') | ConvertFrom-Json).ok) { throw 'Atomic handoff replacement failed.' }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($relative in @('scripts/bin/helper.sh','scripts/../../escape','scripts/a','scripts/a')) {
        # Separate archives cover valid extraction and path traversal rejection.
        $zip = Join-Path $work ([guid]::NewGuid().ToString('N') + '.zip')
        $archive=[IO.Compression.ZipFile]::Open($zip,[IO.Compression.ZipArchiveMode]::Create)
        $entry=$archive.CreateEntry($relative); $out=New-Object IO.StreamWriter($entry.Open()); $out.Write('fixture'); $out.Dispose(); $archive.Dispose()
        $refused=$false
        try { Expand-VerifiedPackage $zip (Join-Path $work 'unpacked') } catch { $refused=$true }
        if ($relative.Contains('..') -ne $refused) { throw "Wrong extraction decision: $relative" }
    }
    $source = Get-Content -Raw (Join-Path $repo 'lib/AgentVm.Common.ps1')
    if ($source -match 'Name\s*=\s*"(VirtualMachinePlatform|HypervisorPlatform|Microsoft-Windows-Subsystem-Linux)"') { throw 'Obsolete WSL feature prerequisite remains.' }
    $rsa=New-Object Security.Cryptography.RSACryptoServiceProvider(2048)
    $public=New-Object Security.Cryptography.RSACryptoServiceProvider
    try {
        $public.FromXmlString($rsa.ToXmlString($false))
        $encrypted=$public.Encrypt([Text.Encoding]::UTF8.GetBytes('credential fixture'),$true)
        if ([Text.Encoding]::UTF8.GetString($rsa.Decrypt($encrypted,$true)) -ne 'credential fixture') { throw 'Credential handoff failed.' }
    } finally { $public.Dispose(); $rsa.Dispose() }
    Write-Host 'Host conversion PowerShell checks passed (quoting, atomic handoff, extraction, no WSL prerequisite, RSA).'
} finally { Remove-Item -LiteralPath $work -Recurse }
