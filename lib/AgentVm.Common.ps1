#Requires -Version 5.1
<#
.SYNOPSIS
    What is left of the old client's shared helpers: the functions that other code still
    dot-sources this file for.

.NOTES
    * The old Update-Construct.ps1 (updater classes E and F, docs/plans/construct-platform-design.md
      13.2) downloads a release, dot-sources lib\AgentVm.Common.ps1 from the download and calls
      Remove-ConstructStaleSourceFiles, Write-ConstructSourceManifest, Install-ControlPanelExtension
      and Set-ConstructInstalledMarker. Set-ConstructInstalledMarker is the upgrade hook: it runs
      install.ps1 of the download, which installs the device agent. test/agent-bootstrap.test.ps1
      runs those updater bodies against this tree.
    * service\host\Install-ConstructHost.ps1 dot-sources it for Ensure-HyperV.

    The functions are unchanged from the old client. They reuse the caller's Write-Ok /
    Write-Note helpers, resolved at call time.
#>

$script:ConstructTuiActive = $false

function Test-ConstructTui {
    # TUI screens need a real interactive console to wipe and redraw.
    return ($script:ConstructTuiActive -and -not [Console]::IsInputRedirected)
}

function Show-ConstructHeader {
    <#
        Matrix-style header: green "digital rain" (random 0/1 -- ASCII only, so
        it aligns and renders on any console code page, no katakana to mangle)
        framing the project title. Drawn at the top of every TUI screen and once
        at launch.
    #>
    $w    = 56
    $rain = { param([int]$n) -join (1..$n | ForEach-Object { @('0', '1')[(Get-Random -Maximum 2)] }) }
    $cent = {
        param([string]$s)
        $p = [int](($w - $s.Length) / 2)
        (" " * $p) + $s + (" " * ($w - $p - $s.Length))
    }
    $body = @(
        (& $cent ""),
        (& $cent "T H E   C O N S T R U C T"),
        (& $cent "agent sandbox loader"),
        (& $cent "")
    )
    Write-Host ""
    Write-Host ("   " + (& $rain ($w + 4))) -ForegroundColor DarkGreen
    Write-Host ("   " + (& $rain ($w + 4))) -ForegroundColor Green
    foreach ($l in $body) {
        Write-Host "   "          -NoNewline
        Write-Host (& $rain 1)    -ForegroundColor DarkGreen -NoNewline
        Write-Host " "            -NoNewline
        Write-Host $l             -ForegroundColor Green     -NoNewline
        Write-Host " "            -NoNewline
        Write-Host (& $rain 1)    -ForegroundColor DarkGreen
    }
    Write-Host ("   " + (& $rain ($w + 4))) -ForegroundColor Green
    Write-Host ("   " + (& $rain ($w + 4))) -ForegroundColor DarkGreen
    Write-Host ""
}

$script:ConstructTuiNotices = New-Object System.Collections.Generic.List[string]

function Add-ConstructTuiNotice {
    <# Queue a message for the top of the next TUI screen (see Show-TuiScreen). #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Message)
    if ($null -eq $script:ConstructTuiNotices) { $script:ConstructTuiNotices = New-Object System.Collections.Generic.List[string] }
    $script:ConstructTuiNotices.Add($Message)
}

function Show-TuiScreen {
    <#
        Start a new TUI screen: wipe the console and redraw the Construct header,
        then the warnings queued since the previous screen (Add-ConstructTuiNotice),
        then the given step title and optional body lines. Subsequent output
        (menus, prompts, progress) belongs to this screen until the next call.
        With TUI off it degrades to the normal scrolling Write-Step output, so
        call sites don't need to branch.
    #>
    [CmdletBinding()]
    param(
        [string]   $Title,
        [string[]] $Body
    )
    if (-not (Test-ConstructTui)) {
        if ($null -ne $script:ConstructTuiNotices) { $script:ConstructTuiNotices.Clear() }
        if ($Title) { Write-Host "`n==> $Title" -ForegroundColor Cyan }
        foreach ($b in @($Body)) { if ($null -ne $b) { Write-Host "    $b" -ForegroundColor White } }
        return
    }
    Clear-Host
    Show-ConstructHeader
    if ($null -ne $script:ConstructTuiNotices -and $script:ConstructTuiNotices.Count -gt 0) {
        foreach ($n in $script:ConstructTuiNotices) { Write-Host "  WARNING: $n" -ForegroundColor Yellow }
        Write-Host ""
        $script:ConstructTuiNotices.Clear()
    }
    if ($Title) {
        Write-Host "  $Title" -ForegroundColor Cyan
        Write-Host ""
    }
    foreach ($b in @($Body)) { if ($null -ne $b) { Write-Host "  $b" -ForegroundColor White } }
    if (@($Body).Count -gt 0) { Write-Host "" }
}

function Ensure-HyperV {
    <#
        Validate (and where possible enable) the host's virtualization stack so
        Hyper-V can run VMs. Assumes the caller is already elevated.
          1. Confirm hardware virtualization (Intel VT-x / AMD-V) is enabled in
             firmware. The script can't turn that on -- only the user can, in
             BIOS/UEFI -- so abort with guidance if it's positively off.
          2. Enable any of the required Windows features that aren't already on:
             Hyper-V and its PowerShell tools. Native ISO construction does not
             require WSL components.
          3. If a feature can't be enabled, point Windows 10/11 Home users at a
             community workaround (Hyper-V isn't officially supported there);
             other editions get generic guidance. Either way it throws.
          4. If enabling a feature needs a reboot, prompt and reboot.
    #>
    [CmdletBinding()]
    param()

    Show-TuiScreen -Title "Checking Hyper-V installation" -Body @(
        "Validating hardware virtualization and the required Windows features..."
    )

    # 1. Hardware virtualization. If a hypervisor is already running it's
    #    obviously enabled; otherwise read the CPU firmware flag and block ONLY
    #    when we positively know it's off (a CPU reports False and none report
    #    True). A null/unknown reading is left for Hyper-V to surface later
    #    rather than risk a false alarm.
    $cs = Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue
    if ($cs.HypervisorPresent) {
        Write-Ok "Hardware virtualization is enabled (hypervisor present)"
    } else {
        $fwFlags = @(Get-CimInstance Win32_Processor -ErrorAction SilentlyContinue |
                     Select-Object -ExpandProperty VirtualizationFirmwareEnabled)
        if ($fwFlags -contains $true) {
            Write-Ok "Hardware virtualization is enabled"
        } elseif ($fwFlags -contains $false) {
            Write-Host ""
            Write-Warning "Hardware virtualization is disabled in your system firmware (BIOS/UEFI)."
            Write-Host "    Hyper-V cannot run virtual machines until it is turned on." -ForegroundColor Yellow
            Write-Host "    Reboot into your BIOS/UEFI setup and enable the virtualization option:" -ForegroundColor Yellow
            Write-Host "      - Intel: 'Intel VT-x' / 'Virtualization Technology'" -ForegroundColor Yellow
            Write-Host "      - AMD:   'AMD-V' / 'SVM Mode'" -ForegroundColor Yellow
            Write-Host "    Save the change, boot back into Windows, then re-run this script." -ForegroundColor Yellow
            Write-Host ""
            throw "Hardware virtualization is disabled in firmware. Enable it in BIOS/UEFI and re-run."
        } else {
            Write-Note "Could not determine the firmware virtualization state; continuing."
        }
    }

    # 2. Required Windows features. -All also pulls in each feature's parents.
    $requiredFeatures = @(
        @{ Name = "Microsoft-Hyper-V"; Label = "Hyper-V" }
        @{ Name = "Microsoft-Hyper-V-Management-PowerShell"; Label = "Hyper-V PowerShell tools" }
    )
    $rebootNeeded  = $false
    $installFailed = $false
    foreach ($feat in $requiredFeatures) {
        $state = Get-WindowsOptionalFeature -Online -FeatureName $feat.Name -ErrorAction SilentlyContinue
        if ($state -and $state.State -eq "EnablePending") {
            Write-Note "$($feat.Label) is waiting for a Windows restart"
            $rebootNeeded = $true
            continue
        }
        if ($state -and $state.State -eq "Enabled") {
            Write-Ok "$($feat.Label) already enabled"
            continue
        }
        Write-Host "    $($feat.Label) not enabled. Installing now..." -ForegroundColor Yellow
        try {
            $result = Enable-WindowsOptionalFeature -Online -FeatureName $feat.Name -All -NoRestart -ErrorAction Stop
            if ($result.RestartNeeded) { $rebootNeeded = $true }
            Write-Ok "$($feat.Label) installed"
        } catch {
            Write-Warning "Failed to enable $($feat.Label): $($_.Exception.Message)"
            $installFailed = $true
        }
    }

    # 3. Only if something failed do we look at the Windows edition: all Home
    #    variants report an EditionID starting with "Core" (Core, CoreN,
    #    CoreSingleLanguage, CoreCountrySpecific); Pro/Enterprise/Education don't.
    if ($installFailed) {
        Write-Host ""
        Write-Warning "One or more virtualization features could not be enabled."
        $editionId = $null
        try {
            $editionId = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name EditionID -ErrorAction Stop).EditionID
        } catch { }
        $caption = (Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue).Caption

        if ($editionId -like 'Core*') {
            Write-Host ""
            Write-Host "    You appear to be running $caption." -ForegroundColor Yellow
            Write-Host "    Hyper-V is not officially supported on Windows Home editions, but you" -ForegroundColor Yellow
            Write-Host "    can enable it with this community guide:" -ForegroundColor Yellow
            Write-Host "        https://gist.github.com/HimDek/6edde284203a620745fad3f762be603b" -ForegroundColor Cyan
            Write-Host "    Follow it, reboot, then re-run this script." -ForegroundColor Yellow
            Write-Host ""
            throw "Hyper-V could not be enabled on this Windows Home edition. See the guide above."
        }

        Write-Host "    Edition: $caption" -ForegroundColor Yellow
        Write-Host "    Enable the features manually (Windows Features / DISM) and make sure" -ForegroundColor Yellow
        Write-Host "    hardware virtualization is turned on in your BIOS/UEFI, then re-run." -ForegroundColor Yellow
        Write-Host ""
        throw "Required virtualization features could not be enabled. See the guidance above."
    }

    # 4. Reboot if enabling a feature asked for one.
    if ($rebootNeeded) {
        Write-Host ""
        Write-Warning "A reboot is required to finish enabling the virtualization features."
        Write-Host "    After the reboot, re-run this script to continue." -ForegroundColor Yellow
        if (Test-ConstructTui) {
            $r = Show-Menu -Title "Reboot now?" -Options @(
                "Reboot now      restart Windows immediately, then re-run this script",
                "Exit            reboot later yourself, then re-run this script"
            ) -Default 0
            if ($r -eq 0) { Restart-Computer -Force }
        } else {
            Read-Host "Press Enter to reboot now (or Ctrl+C to cancel)"
            Restart-Computer -Force
        }
        exit
    }

    # Feature state alone does not prove this PowerShell process can create VMs.
    try {
        Import-Module Hyper-V -Global -ErrorAction Stop
        Get-Command New-VM -Module Hyper-V -ErrorAction Stop | Out-Null
    } catch {
        throw "Hyper-V PowerShell tools are not available: $($_.Exception.Message). Restart Windows if installation requested it, then re-run from 64-bit PowerShell."
    }
}

function Show-Menu {
    <#
        Interactive up/down arrow-key selector, in the style of the Ubuntu
        installer menus. Redraws the option list in place as the highlight
        moves. Returns the zero-based index of the chosen option.

        On a non-interactive host (input redirected, no console keys available)
        it returns -Default instead of blocking on ReadKey.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]   $Title,
        [Parameter(Mandatory)][string[]] $Options,
        [int] $Default = 0
    )

    if ([Console]::IsInputRedirected) { return $Default }

    $selected = [Math]::Max(0, [Math]::Min($Default, $Options.Count - 1))

    # Width of the highlight bar = longest option + prefix + a little padding,
    # so every row clears the previous highlight cleanly when redrawn.
    $longest = 0
    foreach ($o in $Options) { if ($o.Length -gt $longest) { $longest = $o.Length } }
    $barWidth = $longest + 6

    Write-Host ""
    Write-Host "  $Title" -ForegroundColor Cyan
    Write-Host "  (Use the Up/Down arrow keys, then press Enter)" -ForegroundColor DarkGray
    Write-Host ""

    $listTop = [Console]::CursorTop
    try { [Console]::CursorVisible = $false } catch { }
    try {
        while ($true) {
            try { [Console]::SetCursorPosition(0, $listTop) } catch { }
            for ($i = 0; $i -lt $Options.Count; $i++) {
                $prefix = if ($i -eq $selected) { "  > " } else { "    " }
                $line   = ($prefix + $Options[$i]).PadRight($barWidth)
                if ($i -eq $selected) {
                    Write-Host $line -ForegroundColor Black -BackgroundColor White
                } else {
                    Write-Host $line
                }
            }
            $key = [Console]::ReadKey($true)
            switch ($key.Key) {
                'UpArrow'   { $selected = ($selected - 1 + $Options.Count) % $Options.Count }
                'DownArrow' { $selected = ($selected + 1) % $Options.Count }
                'Enter'     { return $selected }
            }
        }
    } finally {
        try { [Console]::CursorVisible = $true } catch { }
        Write-Host ""
    }
}

function Get-ConstructSettingsPath {
    param([Parameter(Mandatory)][string]$Dir)
    return (Join-Path $Dir ".construct-settings.json")
}

. (Join-Path $PSScriptRoot 'AgentVm.SettingsMigration.ps1')

function Read-ConstructSettings {
    # Saved settings as a PSCustomObject, or $null if none/unreadable.
    param([Parameter(Mandatory)][string]$Dir)
    $path = Get-ConstructSettingsPath -Dir $Dir
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (ConvertTo-ConstructCurrentSettings (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json)) } catch { return $null }
}

function Save-ConstructSettings {
    # Merge the given keys into the saved settings file, preserving other keys.
    # Best-effort: a write failure only warns.
    param(
        [Parameter(Mandatory)][string]$Dir,
        [Parameter(Mandatory)][hashtable]$Values
    )
    $path = Get-ConstructSettingsPath -Dir $Dir
    $merged = [ordered]@{}
    $existing = Read-ConstructSettings -Dir $Dir
    if ($existing) { foreach ($p in $existing.PSObject.Properties) { $merged[$p.Name] = $p.Value } }
    foreach ($k in $Values.Keys) { $merged[$k] = $Values[$k] }
    try {
        ([pscustomobject]$merged | ConvertTo-Json -Depth 10) | Set-Content -LiteralPath $path -Encoding UTF8
    } catch {
        Write-Warning "Could not save settings to $path : $($_.Exception.Message)"
    }
}

function Find-VSCodeCli {
    # Locate the `code` CLI: PATH first, then the standard user/system install
    # locations (a fresh winget install isn't on the current session's PATH yet).
    # Each (base, subpath) pair guards its base for $null FIRST -- Join-Path with a
    # null -Path is a terminating error under EAP=Stop (e.g. ${env:ProgramFiles(x86)}
    # is undefined on 32-bit Windows), and this must never throw.
    $cmd = Get-Command code -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $cands = @(
        @($env:LOCALAPPDATA,        'Programs\Microsoft VS Code\bin\code.cmd'),
        @($env:ProgramFiles,        'Microsoft VS Code\bin\code.cmd'),
        @(${env:ProgramFiles(x86)}, 'Microsoft VS Code\bin\code.cmd')
    )
    foreach ($pair in $cands) {
        $base = $pair[0]
        if (-not $base) { continue }
        $c = Join-Path $base $pair[1]
        if (Test-Path -LiteralPath $c) { return $c }
    }
    return $null
}

function Invoke-VSCodeCli {
    <#
        Run the `code` CLI and return ONLY its exit code (0 = success), leaving the
        caller to decide success/failure from that. WHY this exists: `code` is a Node
        program that writes to stderr even when it succeeds -- e.g. the DEP0169
        `url.parse()` DeprecationWarning. In Windows PowerShell 5.1 a native command's
        stderr captured with `2>&1` becomes ErrorRecord objects in the pipeline, and
        under $ErrorActionPreference='Stop' (which install.ps1/Auto-Install.ps1 set)
        the FIRST such record is promoted to a TERMINATING error whose .Exception
        .Message is the stderr line. That made a successful `--install-extension`
        (exit 0, warning on stderr) throw and be reported as "Could not install ...:
        <the deprecation warning>" -- a false negative that may leave the panel
        installed yet the user told it failed.

        The fix, robust across PowerShell editions:
          * pin $ErrorActionPreference='Continue' for the call so a stderr write can
            NEVER be promoted to a terminating error (this is the actual trigger --
            the redirect operator alone isn't enough on 5.1);
          * send stderr to $null so the Node warning noise doesn't clutter the console
            (a REAL failure is still surfaced by the caller via the non-zero exit code);
          * set NODE_OPTIONS=--no-deprecation for the child so `code`'s own Node process
            suppresses deprecation warnings at the source (belt-and-suspenders).
        Success/failure is decided ONLY by the returned $LASTEXITCODE, never by whether
        anything was written to stderr.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Code,
        [Parameter(Mandatory)][string[]]$CodeArgs
    )
    $prevEap = $ErrorActionPreference
    $prevNodeOpts = $env:NODE_OPTIONS
    # Prepend --no-deprecation without clobbering any pre-existing NODE_OPTIONS.
    $env:NODE_OPTIONS = ("--no-deprecation " + ($(if ($prevNodeOpts) { $prevNodeOpts } else { "" }))).Trim()
    $ErrorActionPreference = 'Continue'
    # Sentinel so a failure-to-LAUNCH is reported as failure, not success. A native
    # command that actually runs overwrites $LASTEXITCODE with its real exit code; but
    # if `code` can't be invoked at all (missing/deleted/unrunnable path), no process
    # runs, so $LASTEXITCODE would keep a STALE value -- possibly 0 (false success),
    # which is exactly what EAP=Continue would otherwise let slip through. Pre-seed a
    # non-zero sentinel. Use $global: so the engine's post-run update (it targets the
    # global $LASTEXITCODE) is what we read back, not a shadowed local copy.
    $global:LASTEXITCODE = 127
    try {
        & $Code @CodeArgs 2>$null | Out-Null
        return $global:LASTEXITCODE
    } catch {
        # A terminating failure to invoke the CLI (e.g. command-not-found) -- NOT a
        # native stderr write, which EAP=Continue keeps non-terminating. Report failure.
        return 127
    } finally {
        $ErrorActionPreference = $prevEap
        if ($null -eq $prevNodeOpts) { Remove-Item Env:\NODE_OPTIONS -ErrorAction SilentlyContinue }
        else { $env:NODE_OPTIONS = $prevNodeOpts }
    }
}

function Get-VSCodeExtensionDir {
    # Where the control-panel extension is installed on the host: a fixed folder
    # under the user's VS Code extensions dir. VS Code scans every subfolder of
    # ~/.vscode/extensions and loads any with a valid package.json, so a stable
    # (un-versioned) name lets updates overwrite in place. Pure given $env:USERPROFILE.
    [CmdletBinding()]
    param([string]$Name = "construct-control-panel")
    $base = if ($env:USERPROFILE) { $env:USERPROFILE } else { $HOME }
    return (Join-Path (Join-Path (Join-Path $base ".vscode") "extensions") $Name)
}

function Build-ControlPanelVsix {
    <#
        Package the control-panel extension at <SourceRoot>\extension into a .vsix at
        <OutFile>, WITHOUT vsce / Node: we generate the OPC package by hand -- the
        extension/ payload plus `extension.vsixmanifest` and `[Content_Types].xml` at
        the root -- and zip it with .NET. This exists because modern VS Code only loads
        extensions installed through `code --install-extension`; a bare folder copied
        into ~/.vscode/extensions is ignored (never registered in extensions.json).
        Dev-only files (test/, node_modules, ARCHITECTURE.md, .vscode, .gitignore) are
        excluded -- the same set .vscodeignore lists. Best-effort: returns the OutFile
        path on success, or $null (never throws). Pure w.r.t. VS Code (no `code` needed).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceRoot,
        [Parameter(Mandatory)][string]$OutFile
    )
    $staging = $null
    try {
        $src = Join-Path $SourceRoot "extension"
        $pkgPath = Join-Path $src "package.json"
        if (-not (Test-Path -LiteralPath $pkgPath)) {
            Write-Warning "Control-panel extension not found at $src; can't package it."
            return $null
        }
        $pkg = Get-Content -LiteralPath $pkgPath -Raw | ConvertFrom-Json

        # Stage: extension/<payload> + the two OPC files at the package root.
        $staging = Join-Path ([System.IO.Path]::GetTempPath()) (".construct-vsix-" + [guid]::NewGuid().ToString("N"))
        $payload = Join-Path $staging "extension"
        New-Item -ItemType Directory -Path $payload -Force | Out-Null
        Get-ChildItem -LiteralPath $src -Force |
            Where-Object { $_.Name -notin @('test', 'node_modules', '.vscode', '.gitignore', 'ARCHITECTURE.md') } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $payload -Recurse -Force }

        # [Content_Types].xml: one <Default> per distinct file extension actually in the
        # payload (a missing type makes some OPC readers reject the package), plus the
        # manifest's own .vsixmanifest.
        $ctMap = @{
            '.js' = 'application/javascript'; '.json' = 'application/json'; '.css' = 'text/css';
            '.html' = 'text/html'; '.svg' = 'image/svg+xml'; '.png' = 'image/png';
            '.sh' = 'application/x-sh'; '.md' = 'text/markdown'; '.vsixmanifest' = 'text/xml'
        }
        $exts = @(Get-ChildItem -LiteralPath $payload -Recurse -File |
                  ForEach-Object { $_.Extension.ToLowerInvariant() } |
                  Where-Object { $_ } | Sort-Object -Unique)
        if ($exts -notcontains '.vsixmanifest') { $exts += '.vsixmanifest' }
        $defaults = ($exts | ForEach-Object {
            $ct = if ($ctMap.ContainsKey($_)) { $ctMap[$_] } else { 'application/octet-stream' }
            '<Default Extension="{0}" ContentType="{1}"/>' -f $_, $ct
        }) -join ''
        $contentTypes = '<?xml version="1.0" encoding="utf-8"?>' +
            '<Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">' + $defaults + '</Types>'
        Set-Content -LiteralPath (Join-Path $staging '[Content_Types].xml') -Value $contentTypes -Encoding UTF8 -NoNewline

        # extension.vsixmanifest, templated from package.json. Compute the raw values
        # first (an `if` is a statement, so it can't sit in an argument position), then
        # XML-escape each so a stray &, <, >, or quote in package.json can't break the XML.
        $rawId        = if ($pkg.name) { $pkg.name } else { 'construct-control-panel' }
        $rawVer       = if ($pkg.version) { $pkg.version } else { '0.0.0' }
        $rawPublisher = if ($pkg.publisher) { $pkg.publisher } else { 'unknown' }
        $rawDisplay   = if ($pkg.displayName) { $pkg.displayName } else { $rawId }
        $rawDesc      = if ($pkg.description) { $pkg.description } else { '' }
        $rawEngine    = if ($pkg.engines -and $pkg.engines.vscode) { $pkg.engines.vscode } else { '*' }
        $rawKind      = if ($pkg.extensionKind) { @($pkg.extensionKind)[0] } else { 'ui' }
        $id        = [System.Security.SecurityElement]::Escape([string]$rawId)
        $ver       = [System.Security.SecurityElement]::Escape([string]$rawVer)
        $publisher = [System.Security.SecurityElement]::Escape([string]$rawPublisher)
        $display   = [System.Security.SecurityElement]::Escape([string]$rawDisplay)
        $desc      = [System.Security.SecurityElement]::Escape([string]$rawDesc)
        $engine    = [System.Security.SecurityElement]::Escape([string]$rawEngine)
        $kind      = [System.Security.SecurityElement]::Escape([string]$rawKind)
        $manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<PackageManifest Version="2.0.0" xmlns="http://schemas.microsoft.com/developer/vsx-schema/2011" xmlns:d="http://schemas.microsoft.com/developer/vsx-schema-design/2011">
  <Metadata>
    <Identity Language="en-US" Id="$id" Version="$ver" Publisher="$publisher" />
    <DisplayName>$display</DisplayName>
    <Description xml:space="preserve">$desc</Description>
    <Tags></Tags>
    <Categories>Other</Categories>
    <GalleryFlags>Public</GalleryFlags>
    <Properties>
      <Property Id="Microsoft.VisualStudio.Code.Engine" Value="$engine" />
      <Property Id="Microsoft.VisualStudio.Code.ExtensionKind" Value="$kind" />
      <Property Id="Microsoft.VisualStudio.Code.ExecutesCode" Value="true" />
    </Properties>
  </Metadata>
  <Installation>
    <InstallationTarget Id="Microsoft.VisualStudio.Code"/>
  </Installation>
  <Dependencies/>
  <Assets>
    <Asset Type="Microsoft.VisualStudio.Code.Manifest" Path="extension/package.json" Addressable="true" />
  </Assets>
</PackageManifest>
"@
        Set-Content -LiteralPath (Join-Path $staging 'extension.vsixmanifest') -Value $manifest -Encoding UTF8

        # Zip with EXPLICIT forward-slash entry names -- .NET Framework's
        # ZipFile.CreateFromDirectory has historically used backslashes on Windows,
        # which breaks OPC/VSIX readers; building entries by hand avoids that.
        if (Test-Path -LiteralPath $OutFile) { Remove-Item -LiteralPath $OutFile -Force }
        Add-Type -AssemblyName System.IO.Compression | Out-Null
        Add-Type -AssemblyName System.IO.Compression.FileSystem | Out-Null
        $fs = [System.IO.File]::Open($OutFile, [System.IO.FileMode]::Create)
        try {
            $zip = New-Object System.IO.Compression.ZipArchive($fs, [System.IO.Compression.ZipArchiveMode]::Create)
            try {
                $baseLen = ((Resolve-Path -LiteralPath $staging).Path.TrimEnd('\', '/')).Length
                foreach ($f in Get-ChildItem -LiteralPath $staging -Recurse -File) {
                    $rel = $f.FullName.Substring($baseLen).TrimStart('\', '/').Replace('\', '/')
                    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $f.FullName, $rel) | Out-Null
                }
            } finally { $zip.Dispose() }
        } finally { $fs.Dispose() }
        return $OutFile
    } catch {
        Write-Warning "Could not package the control-panel extension: $($_.Exception.Message)"
        return $null
    } finally {
        if ($staging -and (Test-Path -LiteralPath $staging)) {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

function Install-ControlPanelExtension {
    <#
        Install the control-panel extension into VS Code the SUPPORTED way: package it
        to a .vsix (Build-ControlPanelVsix -- no vsce/Node) and run
        `code --install-extension --force`. This replaced the old folder-copy, which
        modern VS Code ignores (a bare folder in ~/.vscode/extensions is never
        registered in extensions.json, so it silently doesn't load). Idempotent:
        --force reinstalls the same version, so "Update Construct" refreshes. Also
        removes any stale folder-copy left by the old approach. Best-effort: never
        throws; returns $true only on a confirmed install.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SourceRoot)

    $code = Find-VSCodeCli
    if (-not $code) {
        Write-Warning "VS Code CLI not found, so the control panel can't be installed. Install VS Code and re-run (or run: code --install-extension <the .vsix>)."
        return $false
    }
    $vsix = Join-Path ([System.IO.Path]::GetTempPath()) ("construct-control-panel-" + [guid]::NewGuid().ToString("N") + ".vsix")
    if (-not (Build-ControlPanelVsix -SourceRoot $SourceRoot -OutFile $vsix)) { return $false }
    try {
        # Drop the stale unregistered folder-copy (old install approach) so it can't sit
        # dead alongside the properly-installed extension.
        $stale = Get-VSCodeExtensionDir
        if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Recurse -Force -ErrorAction SilentlyContinue }

        # `code` is a native command: decide success ONLY by its exit code. A non-zero
        # exit does NOT throw, and (via Invoke-VSCodeCli) `code`'s DEP0169 deprecation
        # warning on stderr is NOT mistaken for a failure -- previously, under
        # $ErrorActionPreference='Stop', that stderr write was promoted to a terminating
        # error and reported as "Could not install ...: <the warning>" even though the
        # panel installed (exit 0).
        $exit = Invoke-VSCodeCli -Code $code -CodeArgs @('--install-extension', $vsix, '--force')
        if ($exit -ne 0) {
            Write-Warning "VS Code rejected the control-panel install (code --install-extension exit $exit)."
            return $false
        }
        Write-Host "==> Installed the Construct control panel into VS Code (reload/restart VS Code to see it)." -ForegroundColor Cyan
        return $true
    } catch {
        Write-Warning "Could not install the control-panel extension: $($_.Exception.Message)"
        return $false
    } finally {
        Remove-Item -LiteralPath $vsix -Force -ErrorAction SilentlyContinue
    }
}

function Set-ConstructInstalledMarker {
    <#
        The upgrade hook (docs/plans/construct-platform-design.md 13.2). The old
        Update-Construct.ps1 calls this from the downloaded release after the extension
        install; in this release it moves the install to the Construct device agent:
        install.ps1 from -Root asks a running Companion to quit (its own /v1/quit),
        installs the agent package for this user without elevation and starts it, and
        starts the Companion again when anything fails. Only then is the marker advanced.
        A failure throws, so the old updater reports "fail" and its button can retry.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Ref,
        [scriptblock]$CommitFetcher,
        [string]$Commit = ""
    )
    $installer = Join-Path $Root 'install.ps1'
    if (-not (Test-Path -LiteralPath $installer)) { throw "install.ps1 is missing from $Root; the Construct agent cannot be installed." }
    & $installer -Repo $Repo -Ref $Ref
    return (Save-ConstructInstalledMarker @PSBoundParameters)
}

function Save-ConstructInstalledMarker {
    <# Record the actual installed source, never the current remote HEAD. Preserve
       the complete prior tuple if no source revision can be established. Archives
       expand .construct-revision via git export-subst; checkouts use git HEAD.
       CommitFetcher remains an injection seam for offline tests. #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Ref,
        [scriptblock]$CommitFetcher,
        [string]$Commit = ""
    )
    $sha = ""
    try {
        if ($Commit) {
            $sha = $Commit
        } elseif ($CommitFetcher) {
            $sha = [string](& $CommitFetcher $Repo $Ref)
        } elseif (Test-Path -LiteralPath (Join-Path $Root '.git')) {
            $sha = [string](& git -C $Root rev-parse HEAD 2>$null)
            if ($LASTEXITCODE -ne 0) { $sha = "" }
        } elseif (Test-Path -LiteralPath (Join-Path $Root '.construct-revision')) {
            $sha = (Get-Content -LiteralPath (Join-Path $Root '.construct-revision') -Raw).Trim()
        }
        if ($sha -notmatch '^[0-9a-f]{7,40}$') { $sha = "" }
    } catch {
        Write-Host "    (couldn't read the source commit for the update marker: $($_.Exception.Message))" -ForegroundColor DarkGray
    }
    try {
        if ($sha) {
            # Fresh, self-consistent tuple.
            Save-ConstructSettings -Dir $Root -Values @{ constructRepo = $Repo; constructRef = $Ref; installedCommit = $sha }
        } else {
            $priorCommit = ""
            try { $ex = Read-ConstructSettings -Dir $Root; if ($ex -and $ex.installedCommit) { $priorCommit = [string]$ex.installedCommit } } catch { }
            if ($priorCommit) {
                # Preserve the WHOLE prior tuple (repo+ref+commit) -- never pair the old
                # commit with a newly-switched repo/ref. Leave the settings untouched.
            } else {
                # First install / no commit recorded yet: record repo/ref only.
                Save-ConstructSettings -Dir $Root -Values @{ constructRepo = $Repo; constructRef = $Ref }
            }
        }
    } catch {
        Write-Warning "Could not record the update marker: $($_.Exception.Message)"
    }
    return $sha
}

function Get-ConstructConfigDir {
    <#
        The dedicated host config directory: a single machine-wide location
        OUTSIDE any per-slug zip checkout. %LOCALAPPDATA%\The-Construct\config
        (or %TEMP%\The-Construct\config when LOCALAPPDATA is absent). Pure path
        math, no side effects. Mirrors host.js configDir().
    #>
    $base = $env:LOCALAPPDATA
    if (-not $base) { $base = $env:TEMP }
    if (-not $base) { $base = [System.IO.Path]::GetTempPath() }
    return (Join-Path (Join-Path $base "The-Construct") "config")
}

function Test-ConstructSourceRelativePath {
    param([string]$Path)
    if (-not $Path -or $Path.Length -gt 240 -or $Path -match '[\\\x00-\x1f:*?"<>|]' -or $Path.StartsWith('/')) { return $false }
    foreach ($part in ($Path -split '/')) {
        if (-not $part -or $part -eq '.' -or $part -eq '..' -or $part.EndsWith('.') -or $part.EndsWith(' ')) { return $false }
    }
    return $true
}

function Write-ConstructSourceManifest {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Zip, [Parameter(Mandatory)][string]$Commit, [string]$ManifestDir)
    $archive = $null; $temporary = $null
    try {
        if ($Commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Invalid source commit.' }
        if (-not $ManifestDir) { $ManifestDir = Join-Path (Split-Path -Parent (Get-ConstructConfigDir)) 'source-manifests' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
        $files = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
        $prefix = ''
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName; $top = ($name -split '/')[0]
            if (-not $prefix) { $prefix = $top }
            if ($top -cne $prefix -or $top -notmatch '^[A-Za-z0-9_.-]+-main$') { throw 'Invalid archive prefix.' }
            if ($name.EndsWith('/')) { continue }
            if ($name.Length -le ($prefix.Length + 1)) { throw 'Invalid archive file.' }
            $relative = $name.Substring($prefix.Length + 1)
            if (-not (Test-ConstructSourceRelativePath $relative) -or $files.ContainsKey($relative)) { throw 'Invalid archive file.' }
            $inputStream = $entry.Open(); $sha = [Security.Cryptography.SHA256]::Create()
            try { $digest = [BitConverter]::ToString($sha.ComputeHash($inputStream)).Replace('-', '').ToLowerInvariant() }
            finally { $sha.Dispose(); $inputStream.Dispose() }
            $files.Add($relative, $digest)
        }
        if ($files.Count -eq 0) { throw 'Empty source archive.' }
        [IO.Directory]::CreateDirectory($ManifestDir) | Out-Null
        $temporary = Join-Path $ManifestDir ('.source-' + [guid]::NewGuid().ToString('N'))
        $lines = @($files.Keys | ForEach-Object { $files[$_] + '  ' + $_ })
        [IO.File]::WriteAllLines($temporary, [string[]]$lines, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination (Join-Path $ManifestDir ($Commit + '.sha256')) -Force
        return $true
    } catch {
        Write-Warning "Could not record the source manifest ($($_.Exception.GetType().Name)); the host cache is unavailable until the next update."
        return $false
    } finally {
        if ($archive) { $archive.Dispose() }
        if ($temporary -and (Test-Path -LiteralPath $temporary)) { Remove-Item -LiteralPath $temporary -Force }
    }
}

$script:ConstructSourceLocalArtifactPattern = '(^|/)(\.construct-settings\.json|[^/]*\.iso|\.env|[^/]*\.local|[^/]*\.log)$|^projects/[^/]+\.json$|(^|/)(\.construct-backup|\.construct-tools|runtime|__pycache__|bin|obj|node_modules|TestResults|\.vs)/|(^|/)\.claude/worktrees/|^(test|extension/test|service/(src|tests)|companion/(src|tests)|docs/plans|\.github|scripts|assets)/|^(service/Constructd\.sln|companion/Construct\.Companion\.sln|console-viewer/test_server\.py)$'

function Test-ConstructSourceLocalArtifact { param([Parameter(Mandatory)][string]$Relative) return [bool]($Relative -match $script:ConstructSourceLocalArtifactPattern) }

function Remove-ConstructStaleSourceFiles {
    <#
        After a release archive was expanded over an existing checkout, delete the files
        the archive no longer carries (an older archive shipped tests and .NET sources),
        so the checkout equals the release again. Local artifacts survive. Returns the
        number of files removed. Never throws: a stale file is reported by the identity
        check later, not a failed update.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Zip)
    $removed = 0
    try {
        Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
        $keep = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
        try {
            foreach ($entry in $archive.Entries) {
                if (-not $entry.Name) { continue }
                $slash = $entry.FullName.IndexOf('/')
                if ($slash -lt 0) { continue }
                [void]$keep.Add($entry.FullName.Substring($slash + 1))
            }
        } finally { $archive.Dispose() }
        if ($keep.Count -eq 0) { return 0 }
        $prefix = [IO.Path]::GetFullPath($Root).TrimEnd([char[]]@('/', '\')) + [IO.Path]::DirectorySeparatorChar
        foreach ($file in Get-ChildItem -LiteralPath $Root -File -Force -Recurse) {
            $relative = $file.FullName.Substring($prefix.Length).Replace('\', '/')
            if ($keep.Contains($relative) -or (Test-ConstructSourceLocalArtifact -Relative $relative)) { continue }
            Remove-Item -LiteralPath $file.FullName -Force -ErrorAction SilentlyContinue
            if (-not (Test-Path -LiteralPath $file.FullName)) { $removed++ }
        }
        foreach ($dir in Get-ChildItem -LiteralPath $Root -Directory -Force -Recurse | Sort-Object { $_.FullName.Length } -Descending) {
            if (-not (Get-ChildItem -LiteralPath $dir.FullName -Force | Select-Object -First 1)) { Remove-Item -LiteralPath $dir.FullName -Force -ErrorAction SilentlyContinue }
        }
    } catch { }
    return $removed
}
