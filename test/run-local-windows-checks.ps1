#Requires -Version 5.1
# Run with Windows PowerShell 5.1 on a disposable Windows test machine.
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    node test/t3-reprovision-host.test.mjs
    if ($LASTEXITCODE) { throw 'T3 reprovision host tests failed' }
    # Separate processes preserve each test script's exit-code contract.
    foreach ($test in @(
        'test/t3-desktop-handoff.test.ps1',
        'test/native-iso-host.test.ps1',
        'service/tests/host-installer.test.ps1',
        'service/tests/Constructd.Tests/Network/network-script.test.ps1',
        'test/remote-driver.test.ps1',
        'test/remote-install.test.ps1'
    )) {
        powershell.exe -NoProfile -ExecutionPolicy Bypass -File $test
        if ($LASTEXITCODE) { throw "$test failed" }
    }
    . ./lib/Construct.Iso.ps1
    $exe = Resolve-ConstructIsoBuilder -ScriptsDir $PWD.Path -SourceDir (Join-Path $env:TEMP 'absent-iso-source')
    $previousDotnetRoot = $env:DOTNET_ROOT
    try {
        $env:DOTNET_ROOT = 'C:\nonexistent-dotnet'
        & $exe --help
        if ($LASTEXITCODE) { throw 'Pinned ISO builder smoke check failed' }
    } finally { $env:DOTNET_ROOT = $previousDotnetRoot }
} finally { Pop-Location }
