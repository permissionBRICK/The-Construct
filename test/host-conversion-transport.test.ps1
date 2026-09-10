#Requires -Version 5.1
# Local transport checks; no Hyper-V, relay, service or ISO needed.
$ErrorActionPreference = 'Stop'
$tokens = $null; $errors = $null
$file = Join-Path (Split-Path -Parent $PSScriptRoot) 'service/host/ConvertTo-ConstructHost.ps1'
$ast = [Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
foreach ($name in @('Write-GuestInput', 'Format-GuestFailure', 'Quote-Argument')) {
    $node = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
    Invoke-Expression ($node.Extent.Text -replace '^function ([^ (]+)', 'function global:$1')
}
# StreamWriter reproduces the .NET Framework console-code-page behavior, even
# when running these tests on Linux pwsh. The production helper must bypass it.
$payload = @{owner='DOMAIN\Jürgen';files=@{helper='§ ─ … —'};vmToken='sensitive-token-fixture'} | ConvertTo-Json -Depth 4 -Compress
$expected = [Text.Encoding]::UTF8.GetBytes($payload)
foreach ($encoding in @([Text.Encoding]::GetEncoding(850), [Text.Encoding]::Unicode, (New-Object Text.UTF8Encoding($true)))) {
    $stream = New-Object IO.MemoryStream
    $writer = New-Object IO.StreamWriter($stream, $encoding)
    $process = [pscustomobject]@{StandardInput=$writer}
    Write-GuestInput $process $payload
    $actual = $stream.ToArray() # MemoryStream allows this after Close.
    if ([Convert]::ToBase64String($actual) -cne [Convert]::ToBase64String($expected)) { throw "Stdin used console encoding or added a BOM: $($encoding.WebName)" }
}
$oldStream=New-Object IO.MemoryStream
$oldWriter=New-Object IO.StreamWriter($oldStream, ([Text.Encoding]::GetEncoding(850)))
$oldWriter.Write($payload); $oldWriter.Flush()
if ([Convert]::ToBase64String($oldStream.ToArray()) -ceq [Convert]::ToBase64String($expected)) { throw 'The regression fixture did not reproduce the old encoding defect.' }
$oldWriter.Dispose()
$raw = "Guest enrollment failed: UnicodeDecodeError`nAuthorization: Bearer sensitive-token-fixture`nJSON token: sensitive-token-fixture"
$detail = Format-GuestFailure $raw 1 $payload
if ($detail -notmatch 'UnicodeDecodeError' -or $detail -notmatch 'exit 1' -or $detail.Contains('sensitive-token-fixture')) { throw 'Enrollment error was hidden or credential leaked.' }
if ((Format-GuestFailure 'Permission denied (publickey).' 255) -notmatch 'Permission denied') { throw 'SSH authentication diagnostic lost.' }
if ((Format-GuestFailure ('a' * 10000) 1).Length -gt 4200) { throw 'Failure output is unbounded.' }
if ((Format-GuestFailure '' 1) -notmatch 'no diagnostic') { throw 'Empty error has no explanation.' }
Write-Host 'Guest SSH transport checks passed (UTF-8 across OEM/Unicode/BOM defaults, old defect reproduced, diagnostics and redaction).'
