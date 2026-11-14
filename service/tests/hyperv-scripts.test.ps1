#Requires -Version 5.1
<#
    Tests for the Hyper-V driver's own PowerShell, the files the service ships in its
    package (service/src/Constructd.Windows/HyperV/Scripts, copied to hyperv\ next to
    the executable). Run:

        pwsh -NoProfile -File service/tests/hyperv-scripts.test.ps1

      (a) every file parses, and none uses syntax Windows PowerShell 5.1 lacks;
      (b) every function the service calls is defined by the files it dot-sources;
      (c) Get-ConstructVmFacts against stubbed cmdlets.

    The files are the driver's only copy: they were moved from the old client's
    drivers\hyperv-local and lib\AgentVm.Common.ps1, which are gone.

    The .NET suite runs this file too (HostInstallerTests).
#>
$ErrorActionPreference = 'Stop'

$here     = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path -Parent (Split-Path -Parent $here)
$scripts  = Join-Path $repoRoot 'service/src/Constructd.Windows/HyperV/Scripts'
$csharp   = Join-Path $repoRoot 'service/src/Constructd.Windows'

$script:pass = 0; $script:fail = 0
function ok($name, $cond) {
    if ($cond) { $script:pass++; Write-Host "  PASS  $name" }
    else       { $script:fail++; Write-Host "  FAIL  $name" -ForegroundColor Red }
}

function Get-Functions([string]$Path) {
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($Path, [ref]$null, [ref]$null)
    $map = @{}
    foreach ($f in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) {
        $map[$f.Name] = $f.Extent.Text.Replace("`r`n", "`n")
    }
    return $map
}

$files = @('HyperV.Driver.ps1', 'HyperV.ChildVm.ps1', 'HyperV.WindowsLicense.ps1')

# ── (a) Parsing and 5.1 syntax ───────────────────────────────────────────────
Write-Host ""
Write-Host "=== Parser checks ===" -ForegroundColor Cyan
foreach ($name in $files) {
    $path = Join-Path $scripts $name
    $errors = $null
    $tokens = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    ok "parse: $name has zero errors" ($errors.Count -eq 0)
    $code = ($tokens | Where-Object { $_.Kind -ne 'Comment' } | ForEach-Object { $_.Text }) -join ' '
    ok "$name has no null-coalescing or null-conditional operator" (-not ($code -match '\?\?|\?\.'))
    # powershell.exe reads a file without a BOM in the ANSI code page: non-ASCII text
    # outside comments would be read wrongly there.
    $bytes = [IO.File]::ReadAllBytes($path)
    $bom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    ok "$name is ASCII outside comments, or carries a BOM" ($bom -or -not ($code -match '[^\x00-\x7F]'))
}

# ── (b) What the service calls is defined ────────────────────────────────────
Write-Host ""
Write-Host "=== Called functions are defined ===" -ForegroundColor Cyan
$defined = @{}
foreach ($name in $files) { foreach ($k in (Get-Functions (Join-Path $scripts $name)).Keys) { $defined[$k] = $name } }
$sources = @(Get-ChildItem -LiteralPath (Join-Path $csharp 'HyperV') -Filter '*.cs') + @(Get-Item (Join-Path $csharp 'Network/HyperVGuestAddressProvider.cs'))
$called = @{}
foreach ($file in $sources) {
    $text = Get-Content -Raw -LiteralPath $file.FullName
    # Every contract-style name in the C# source, comments included: a name a comment cites has to exist too.
    foreach ($m in [regex]::Matches($text, '(?<![\w-])((?:Get|Set|New|Remove|Start|Stop|Save|Detach|Wait|Clear|Restore|Test|Dismount)-Construct\w+)')) { $called[$m.Groups[1].Value] = $file.Name }
}
ok "the C# driver calls at least the 20 functions it is known to call" ($called.Count -ge 20)
foreach ($fn in ($called.Keys | Sort-Object)) {
    ok "$fn (called from $($called[$fn])) is defined in the package" ($defined.ContainsKey($fn))
}
# The removal routes to the lib's functions, which came along.
foreach ($fn in @('Remove-AgentVm', 'Get-VhdChain', 'Remove-FileWithRetry')) {
    ok "$fn is defined in HyperV.Driver.ps1" ($defined[$fn] -eq 'HyperV.Driver.ps1')
}
$driverText = Get-Content -Raw -LiteralPath (Join-Path $scripts 'HyperV.Driver.ps1')
ok "the driver defines its progress fallbacks" ($driverText -match 'function Write-Step' -and $driverText -match 'function Write-Ok' -and $driverText -match 'function Write-Note')

# ── (c) Get-ConstructVmFacts ─────────────────────────────────────────────────
Write-Host ""
Write-Host "=== Get-ConstructVmFacts ===" -ForegroundColor Cyan
. (Join-Path $scripts 'HyperV.Driver.ps1')
$script:vmId = [guid]'6f2a1c4e-9a43-4f63-8d1e-0c4f1d1b2a33'
$script:missing = $false
$script:cimFilter = ''
function Get-VM {
    param($Name, $ErrorAction)
    if ($script:missing) {
        $record = New-Object System.Management.Automation.ErrorRecord((New-Object Exception 'no vm'), 'InvalidParameter,Microsoft.HyperV.PowerShell.Commands.GetVM', 'InvalidArgument', $Name)
        throw $record
    }
    [pscustomobject]@{ Name = $Name; Id = $script:vmId; State = 'Running'; ProcessorCount = 6; MemoryStartup = 16GB }
}
function Get-VMHardDiskDrive { param($VM, $ErrorAction) @([pscustomobject]@{ Path = 'C:\vm\a.vhdx' }, [pscustomobject]@{ Path = 'C:\vm\b.vhdx' }) }
function Get-VHD { param($Path, $ErrorAction) if ($Path -eq 'C:\vm\a.vhdx') { @{ Size = 200GB } } else { @{ Size = 56GB } } }
function Get-CimInstance { param($Namespace, $ClassName, $Filter, $ErrorAction) $script:cimFilter = $Filter; @([pscustomobject]@{ BIOSGUID = '{0B9A2D11-1C2B-4F7E-9E5A-2F63A0C1D2E3}' }) }

$facts = Get-ConstructVmFacts -Name 'agent-vm'
ok "facts carry the VM id" ($facts.Id -eq '6f2a1c4e-9a43-4f63-8d1e-0c4f1d1b2a33')
ok "facts carry the state as Hyper-V reports it" ($facts.State -eq 'Running')
ok "facts carry vCPUs and startup RAM" ($facts.Cpu -eq 6 -and $facts.MemoryBytes -eq 16GB)
ok "facts sum the virtual size of every attached disk" ($facts.DiskBytes -eq 256GB)
ok "facts carry the firmware id of the realized settings" ($facts.BiosGuid -eq '{0B9A2D11-1C2B-4F7E-9E5A-2F63A0C1D2E3}' -and $script:cimFilter -match "VirtualSystemIdentifier='6f2a1c4e-9a43-4f63-8d1e-0c4f1d1b2a33'" -and $script:cimFilter -match 'Realized')
$json = @{ ok = $true; value = $facts } | ConvertTo-Json -Compress -Depth 6
ok "facts serialize to the envelope the service parses" ($json -match '"DiskBytes":274877906944' -and $json -match '"Cpu":6')
$script:missing = $true
ok "a VM Hyper-V does not know is null, not an error" ($null -eq (Get-ConstructVmFacts -Name 'agent-vm'))

Write-Host ""
Write-Host "hyperv-scripts: $($script:pass) passed, $($script:fail) failed"
if ($script:fail -gt 0) { exit 1 }
