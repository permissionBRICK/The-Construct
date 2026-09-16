#Requires -Version 5.1
# Run in a fresh process so Add-Type cannot reuse a previously compiled type.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http
$source = Join-Path (Split-Path $PSScriptRoot -Parent) 'lib/Construct.Download.cs'
if ($PSVersionTable.PSVersion.Major -le 5) {
    # Exercise the actual legacy compiler, including its preprocessor parser.
    Add-Type -Path $source -ReferencedAssemblies System.Net.Http
} else {
    Add-Type -Path $source -IgnoreWarnings -WarningAction SilentlyContinue
}
if (-not ('Construct.Download.Transfer' -as [type])) {
    throw 'ISO downloader type was not loaded'
}
Write-Host 'ISO downloader compiles successfully.'
