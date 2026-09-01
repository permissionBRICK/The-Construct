#Requires -Version 5.1
<#
.SYNOPSIS
    Loads the Construct hypervisor driver for a backend into the CALLER's scope.

.DESCRIPTION
    DOT-SOURCE this script -- it is deliberately a script and not a function,
    because a function cannot dot-source into its caller's scope. Dot-sourcing
    runs these statements in the caller's scope, so the driver's own dot-source
    below lands there too and the contract functions are usable directly:

        $driverLoader = Join-Path $PSScriptRoot "drivers\Load-ConstructDriver.ps1"
        . $driverLoader -Backend "hyperv-local"
        Get-ConstructVmState -Name "Agent-VM"

    Import-ConstructDriver (defined here, and available to the caller afterwards)
    resolves a backend NAME to its driver file, validating it: an unknown backend
    throws a clear error naming the ones that exist. Only "hyperv-local" exists
    today; "hyperv-remote" and a Proxmox driver are the planned additions
    (docs/drivers.md, docs/plans/modular-remote-architecture.md §4.2).

.PARAMETER Backend
    Backend id. Empty or omitted = "hyperv-local", which keeps every existing
    call site on today's local Hyper-V path.
#>
[CmdletBinding()]
param([string]$Backend = "hyperv-local")

function Import-ConstructDriver {
    <#
        Resolve a backend id to its driver file path. Throws for an unknown
        backend, or when the driver file is missing from the install. Returns the
        path so the caller can dot-source it:

            . (Import-ConstructDriver -Backend "hyperv-local")
    #>
    [CmdletBinding()]
    param(
        [string]$Backend = "hyperv-local",
        [string]$DriverRoot
    )

    if (-not $DriverRoot) { $DriverRoot = $PSScriptRoot }
    $id = ([string]$Backend).Trim().ToLowerInvariant()
    if (-not $id) { $id = "hyperv-local" }

    # Backend id -> path segments under drivers\. Segment-wise Join-Path so the
    # path is built with the platform's separator (the contract test runs on Linux).
    $known = [ordered]@{
        'hyperv-local' = @('hyperv-local', 'HyperVLocal.Driver.ps1')
    }
    if (-not $known.Contains($id)) {
        throw "Unknown Construct backend '$Backend'. Known backends: $(($known.Keys) -join ', ')."
    }

    $path = $DriverRoot
    foreach ($seg in $known[$id]) { $path = Join-Path $path $seg }
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Construct driver for backend '$id' not found at $path. Update The Construct."
    }
    return $path
}

. (Import-ConstructDriver -Backend $Backend -DriverRoot $PSScriptRoot)
