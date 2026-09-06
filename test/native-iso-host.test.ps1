# Resolver/invocation regression tests; no WSL, network, real SDK or Hyper-V needed.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../lib/Construct.Iso.ps1')
$work = Join-Path ([IO.Path]::GetTempPath()) ('construct-iso-host-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$passed = 0
function Assert($Condition, $Message) {
    if (-not $Condition) { throw "FAIL: $Message" }
    $script:passed++
    Write-Host "PASS: $Message"
}
function Assert-Throws([scriptblock]$Action, [string]$Pattern) {
    $caught = $false
    try { & $Action | Out-Null } catch {
        if ($_.Exception.Message -notmatch $Pattern) { throw }
        $caught = $true
    }
    Assert $caught "throws $Pattern"
}
try {
    $checkout = Join-Path $work 'checkout with spaces'
    New-Item -ItemType Directory -Path (Join-Path $checkout 'config') -Force | Out-Null
    $artifact = Join-Path $work 'artifact'
    New-Item -ItemType Directory -Path $artifact | Out-Null
    $artifactExe = Join-Path $artifact 'Construct.Iso.exe'
    [IO.File]::WriteAllText($artifactExe, 'verified executable')
    $script:archive = Join-Path $work 'release.zip'
    Compress-Archive -Path $artifactExe -DestinationPath $script:archive
    $release = @{
        repository = 'permissionBRICK/construct-iso'; tag = ('build-' + ('a' * 40))
        zipSha256 = (Get-FileHash $script:archive).Hash.ToLowerInvariant()
        exeSha256 = (Get-FileHash $artifactExe).Hash.ToLowerInvariant()
    }
    $release | ConvertTo-Json | Set-Content (Join-Path $checkout 'config/iso-builder.json')
    $script:downloads = 0
    function Invoke-WebRequest {
        param([switch]$UseBasicParsing, [string]$Uri, [string]$OutFile)
        $script:downloads++
        Assert ($Uri -eq "https://github.com/permissionBRICK/construct-iso/releases/download/$($release.tag)/Construct.Iso-win-x64.zip") 'download uses pinned release'
        [IO.File]::Copy($script:archive, $OutFile)
    }
    $noSource = Join-Path $work 'no source'
    $exe = Resolve-ConstructIsoBuilder -ScriptsDir $checkout -SourceDir $noSource
    Assert ((Get-Content -Raw $exe) -eq 'verified executable') 'verified download is published'
    Assert ($script:downloads -eq 1) 'one download'
    $again = Resolve-ConstructIsoBuilder -ScriptsDir $checkout -SourceDir $noSource
    Assert ($again -eq $exe -and $script:downloads -eq 1) 'verified cache is reused offline'
    [IO.File]::WriteAllText($exe, 'damaged cache')
    Resolve-ConstructIsoBuilder -ScriptsDir $checkout -SourceDir $noSource | Out-Null
    Assert ((Get-Content -Raw $exe) -eq 'verified executable') 'damaged cached executable is repaired'
    [IO.File]::WriteAllText($exe, 'retain existing executable')
    [IO.File]::WriteAllText($script:archive, 'corrupt zip')
    Assert-Throws { Resolve-ConstructIsoBuilder -ScriptsDir $checkout -SourceDir $noSource } 'archive checksum mismatch'
    Assert ((Get-Content -Raw $exe) -eq 'retain existing executable') 'failed download preserves old executable'
    Assert (@(Get-ChildItem (Split-Path $exe) -Directory).Count -eq 0) 'failed download scratch is removed'

    $source = Join-Path $work 'source with spaces'
    $project = Join-Path $source 'src/Construct.Iso/Construct.Iso.csproj'
    New-Item -ItemType Directory -Path (Split-Path $project) -Force | Out-Null
    [IO.File]::WriteAllText($project, '<Project />')
    function dotnet {
        if ($args[0] -eq '--list-sdks') { $global:LASTEXITCODE = 0; '10.0.301 [/sdk]'; return }
        Assert ($args[0] -eq 'publish' -and $args[1] -eq $project) 'source path with spaces stays one argument'
        Assert ($args -contains '--self-contained' -and $args -contains 'win-x64') 'local build is self-contained Windows x64'
        $out = $args[[array]::IndexOf($args, '-o') + 1]
        New-Item -ItemType Directory -Path $out -Force | Out-Null
        [IO.File]::WriteAllText((Join-Path $out 'Construct.Iso.exe'), 'local build')
        $global:LASTEXITCODE = 0
    }
    Resolve-ConstructIsoBuilder -ScriptsDir $checkout -SourceDir $source | Out-Null
    Assert ((Get-Content -Raw $exe) -eq 'local build') 'available local source and SDK win over download'
    Remove-Item Function:dotnet

    function Invoke-TestIsoTool {
        param([string]$Mode, [Parameter(ValueFromPipeline = $true)][string]$Text)
        process {
            $script:request = $Text | ConvertFrom-Json
            $script:mode = $Mode
            [IO.File]::WriteAllText($script:request.OutputIso, 'ISO')
            $global:LASTEXITCODE = 0
        }
    }
    $out = Join-Path $work 'output media.iso'
    $secret = 'quotes''" $ ä'
    $oldEncoding = $OutputEncoding
    Invoke-ConstructNativeIsoBuild -ToolPath Invoke-TestIsoTool -SourceIso (Join-Path $work 'source.iso') `
        -OutputIso $out -BootstrapPublicKeyPath (Join-Path $work 'public key.pub') -Password $secret `
        -Hostname work-vm -Overwrite
    Assert ($script:mode -eq '--request-stdin') 'arguments carry only the stdin protocol flag'
    Assert ($script:request.Password -ceq $secret) 'credentials survive JSON without shell interpolation'
    Assert ($script:request.Overwrite -eq $true) 'local force rebuild permits atomic replacement'
    Assert ($OutputEncoding -eq $oldEncoding) 'native stdin encoding is restored'

    foreach ($file in @('Auto-Install.ps1', 'service/host/Install-ConstructHost.ps1', 'lib/Construct.Iso.ps1')) {
        $path = Join-Path $PSScriptRoot "../$file"
        $tokens = $null; $errors = $null
        $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
        Assert ($errors.Count -eq 0) "$file parses"
        $wsl = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'wsl.exe' }, $true))
        Assert ($wsl.Count -eq 0) "$file never invokes WSL"
    }
    Write-Host "$passed native ISO host checks passed."
} finally {
    Remove-Item -LiteralPath $work -Recurse -Force
}
