#Requires -Version 5.1
# Run with Windows PowerShell 5.1 on a disposable Windows test machine.
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # Separate processes preserve each test script's exit-code contract.
    foreach ($test in @(
        'test/native-iso-host.test.ps1',
        'test/hyperv-prereqs.test.ps1',
        'test/windows-license-firmware.test.ps1',
        'service/tests/host-installer.test.ps1',
        'service/tests/hyperv-scripts.test.ps1',
        'service/tests/Constructd.Tests/Network/network-script.test.ps1',
        'service/tests/Constructd.Tests/Console/console-script.test.ps1',
        'service/tests/Constructd.Tests/Windows/windows-guest-report.test.ps1'
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
