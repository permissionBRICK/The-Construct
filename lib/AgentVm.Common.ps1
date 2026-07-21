#Requires -Version 5.1
<#
.SYNOPSIS
    Shared helpers for the Windows agent-VM scripts (Auto-Install.ps1 and
    Create-AgentVM.ps1): an interactive arrow-key menu, a checkbox-style project
    selector, a destructive-action confirmation, Hyper-V / virtualization
    validation, and Hyper-V VM teardown.

.NOTES
    Dot-source this file from the calling script:

        . (Join-Path $PSScriptRoot 'lib\AgentVm.Common.ps1')

    The functions reuse the caller's Write-Step / Write-Ok / Write-Note helpers
    (both callers define them with the same signatures), resolved at call time.
#>

# ── Full-screen TUI mode ──────────────────────────────────────────────────────
# When enabled (Auto-Install.ps1 turns it on for its interactive phase), every
# prompt below runs as a full-window "screen": the console is wiped and redrawn
# with the Construct header plus only the current step, so the window never
# shows more than one menu at a time. The caller disables it at the "all set"
# banner, after which output scrolls as a normal log. Because the lib is
# dot-sourced, the flag lives in each calling script's own scope -- enabling it
# in Auto-Install.ps1 doesn't change Provision-AgentVM.ps1's behaviour.

$script:ConstructTuiActive = $false

function Enable-ConstructTui  { $script:ConstructTuiActive = $true }
function Disable-ConstructTui { $script:ConstructTuiActive = $false }

function Test-ConstructTui {
    # TUI screens need a real interactive console to wipe and redraw.
    return ($script:ConstructTuiActive -and -not [Console]::IsInputRedirected)
}

function ConvertFrom-ConstructProvisionResult {
    <#
        Parse provision.sh's uncoloured sentinel block without consulting any
        process/global state. ANSI stripping is defensive: some SSH/console
        layers can leave SGR sequences around otherwise plain sentinel lines.
    #>
    [CmdletBinding()]
    param([AllowNull()][AllowEmptyCollection()][string[]]$Lines)

    $clean = New-Object System.Collections.Generic.List[string]
    $ansi = [regex]'\x1B\[[0-?]*[ -/]*[@-~]'
    foreach ($line in @($Lines)) {
        $clean.Add(($ansi.Replace(([string]$line), '') -replace "`r", ''))
    }

    $start = -1
    $end = -1
    for ($i = 0; $i -lt $clean.Count; $i++) {
        if ($clean[$i] -eq '===CONSTRUCT-PROVISION-RESULT===') {
            $start = $i
            $end = -1
        } elseif ($start -ge 0 -and $clean[$i] -eq '===END-CONSTRUCT-PROVISION-RESULT===') {
            $end = $i
        }
    }

    $errors = New-Object System.Collections.Generic.List[object]
    $declared = -1
    if ($start -ge 0 -and $end -gt $start) {
        for ($i = $start + 1; $i -lt $end; $i++) {
            if ($clean[$i] -match '^errors=([0-9]+)$') {
                $declared = [int]$Matches[1]
            } elseif ($clean[$i] -match '^error=([^|]*)\|(-?[0-9]+)(?:\|(.*))?$') {
                $errors.Add([pscustomobject]@{
                    Title    = $Matches[1]
                    ExitCode = [int]$Matches[2]
                    LogPath  = if ($Matches[3]) { $Matches[3] } else { "" }
                })
            }
        }
    }

    $found = ($start -ge 0 -and $end -gt $start)
    $errorCount = 0
    if ($declared -ge 0) { $errorCount = $declared }
    $errorItems = [object[]]$errors
    [pscustomobject]@{
        Found = $found
        IsValid = ($found -and $declared -ge 0 -and $declared -eq $errors.Count)
        ErrorCount = $errorCount
        Errors = $errorItems
    }
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

function Show-TuiScreen {
    <#
        Start a new TUI screen: wipe the console and redraw the Construct header,
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
        if ($Title) { Write-Host "`n==> $Title" -ForegroundColor Cyan }
        foreach ($b in @($Body)) { if ($null -ne $b) { Write-Host "    $b" -ForegroundColor White } }
        return
    }
    Clear-Host
    Show-ConstructHeader
    if ($Title) {
        Write-Host "  $Title" -ForegroundColor Cyan
        Write-Host ""
    }
    foreach ($b in @($Body)) { if ($null -ne $b) { Write-Host "  $b" -ForegroundColor White } }
    if (@($Body).Count -gt 0) { Write-Host "" }
}

function Invoke-TuiConfirm {
    <#
        Yes/no decision as an arrow-key menu (replacing [Y/n] Read-Host prompts).
        Starts its own TUI screen unless -NoScreen (use that when the question
        belongs to a screen that's already showing context the user still needs).
        On a non-interactive host it returns the default without blocking.
        Returns $true for yes.
    #>
    [CmdletBinding()]
    param(
        [string]   $ScreenTitle,
        [string[]] $Body,
        [Parameter(Mandatory)][string] $Question,
        [string]   $YesLabel = "Yes",
        [string]   $NoLabel  = "No",
        [switch]   $DefaultNo,
        [switch]   $NoScreen
    )
    if ([Console]::IsInputRedirected) { return (-not $DefaultNo) }
    if (-not $NoScreen) { Show-TuiScreen -Title $ScreenTitle -Body $Body }
    $def = if ($DefaultNo) { 1 } else { 0 }
    return ((Show-Menu -Title $Question -Options @($YesLabel, $NoLabel) -Default $def) -eq 0)
}

function Invoke-TuiInput {
    <#
        Free-text prompt on its own TUI screen (unless -NoScreen). An empty
        answer returns -Default. Returns the trimmed string.
    #>
    [CmdletBinding()]
    param(
        [string]   $ScreenTitle,
        [string[]] $Body,
        [Parameter(Mandatory)][string] $Prompt,
        [string]   $Default = "",
        [switch]   $NoScreen
    )
    if (-not $NoScreen) { Show-TuiScreen -Title $ScreenTitle -Body $Body }
    $ans = Read-Host "  $Prompt"
    if ([string]::IsNullOrWhiteSpace($ans)) { return $Default }
    return $ans.Trim()
}

function Ensure-HyperV {
    <#
        Validate (and where possible enable) the host's virtualization stack so
        Hyper-V can run VMs. Assumes the caller is already elevated.
          1. Confirm hardware virtualization (Intel VT-x / AMD-V) is enabled in
             firmware. The script can't turn that on -- only the user can, in
             BIOS/UEFI -- so abort with guidance if it's positively off.
          2. Enable any of the required Windows features that aren't already on:
             Hyper-V, Virtual Machine Platform, Windows Hypervisor Platform.
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
        @{ Name = "Microsoft-Hyper-V";      Label = "Hyper-V" },
        @{ Name = "VirtualMachinePlatform"; Label = "Virtual Machine Platform" },
        @{ Name = "HypervisorPlatform";     Label = "Windows Hypervisor Platform" }
    )
    $rebootNeeded  = $false
    $installFailed = $false
    foreach ($feat in $requiredFeatures) {
        $state = Get-WindowsOptionalFeature -Online -FeatureName $feat.Name -ErrorAction SilentlyContinue
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

function Select-ProjectProfiles {
    <#
        Interactive checkbox-style selector for the project profiles in
        projects/ (every *.json except the blank "default" and "project.schema").
        Built on the same in-place terminal redraw as Show-Menu: Up/Down moves
        the highlight, Space toggles the highlighted profile on/off, and the list
        ends with two action rows -- "Open projects config folder" (launches
        Explorer, leaving the menu open) and "Continue". Every loaded profile
        starts selected and the cursor starts on "Continue", so a bare Enter
        accepts the all-selected default.

        The folder is watched while the menu is open: dropping a new *.json in
        (or removing one) refreshes the list in place, with any newly added
        profile defaulting to selected. When no profiles are present the menu
        still shows -- with a note that the VM will be built from the default
        blank config -- offering only the two actions.

        Returns a comma-separated list of the chosen profile base names, or
        "default" when none are selected. On a non-interactive host (input
        redirected) it can't drive the menu, so it returns every available
        profile (or "default" if there are none) -- matching the all-selected
        default state.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string] $ProjectsDir)

    $skip = @("default", "project.schema")

    # Snapshot the selectable profile base names: every *.json in the folder,
    # sorted, minus the blank default + schema. .sample files don't match *.json.
    $scan = {
        if (-not (Test-Path -LiteralPath $ProjectsDir)) { return @() }
        @(Get-ChildItem -LiteralPath $ProjectsDir -Filter *.json -File -ErrorAction SilentlyContinue |
          Where-Object { $skip -notcontains $_.BaseName } |
          Sort-Object Name | ForEach-Object { $_.BaseName })
    }

    $names = @(& $scan)

    # Non-interactive host: can't read keys, so fall back to the all-selected
    # default (install everything, or "default" when nothing is configured).
    if ([Console]::IsInputRedirected) {
        if ($names.Count -eq 0) { return "default" }
        return ($names -join ",")
    }

    # Selection state keyed by profile name; every loaded profile starts on.
    $selected = @{}
    foreach ($n in $names) { $selected[$n] = $true }

    # In TUI mode this selector is a screen of its own.
    if (Test-ConstructTui) { Clear-Host; Show-ConstructHeader }

    Write-Host ""
    Write-Host "  Select project configs to install" -ForegroundColor Cyan
    Write-Host "  (Up/Down to move, Space to toggle, Enter to activate a row)" -ForegroundColor DarkGray
    Write-Host ""

    # Start on "Continue": every profile defaults to selected, so a plain Enter
    # accepts the all-selected state without any cursor travel.
    $cursor      = $names.Count + 2
    $listTop     = [Console]::CursorTop
    $prevLines   = 0    # rows drawn last pass, so we can clear leftovers on shrink
    $maxBarWidth = 0    # only grows, so a narrowing list still clears cleanly
    try { [Console]::CursorVisible = $false } catch { }

    try {
        while ($true) {
            # Re-scan; if the folder changed, rebuild selection state preserving
            # existing toggles and defaulting any newly added profile to selected.
            $current = @(& $scan)
            if (($current -join "`n") -ne ($names -join "`n")) {
                $newSel = @{}
                foreach ($n in $current) {
                    $newSel[$n] = if ($selected.ContainsKey($n)) { $selected[$n] } else { $true }
                }
                $selected = $newSel
                $names    = $current
            }

            # Rows = one per profile, then the three action buttons.
            $rowCount = $names.Count + 3
            $idxOpen  = $names.Count        # "Open projects config folder"
            $idxLink  = $names.Count + 1    # "Link a remote config repo..."
            $idxCont  = $names.Count + 2    # "Continue"
            if ($cursor -ge $rowCount) { $cursor = $rowCount - 1 }
            if ($cursor -lt 0)         { $cursor = 0 }

            # Width of the highlight bar = longest row content + prefix + padding.
            # The empty-folder note lines are far longer than any row, so fold
            # them into the width too -- otherwise, after the menu has been empty,
            # the narrower profile/action rows wouldn't fully overwrite the note
            # text on the auto-refresh redraw, leaving stale characters behind.
            $emptyNote = "    (No project configs found -- the VM will be built from the default blank config.)"
            $emptyHint = "    Drop *.json profiles into the folder below and they'll appear here."
            $contents = New-Object System.Collections.Generic.List[string]
            foreach ($n in $names) { $contents.Add("[x] $n") }
            $contents.Add("Open projects config folder")
            $contents.Add("Link a remote config repo...")
            $contents.Add("Continue")
            $longest = 0
            foreach ($c in $contents) { if ($c.Length -gt $longest) { $longest = $c.Length } }
            $barWidth = $longest + 6
            if ($names.Count -eq 0) {
                foreach ($noteLine in @($emptyNote, $emptyHint)) {
                    if ($noteLine.Length -gt $barWidth) { $barWidth = $noteLine.Length }
                }
            }
            if ($barWidth -gt $maxBarWidth) { $maxBarWidth = $barWidth }

            try { [Console]::SetCursorPosition(0, $listTop) } catch { }
            $linesPrinted = 0

            # Empty-folder note (drawn in place of the profile rows).
            if ($names.Count -eq 0) {
                Write-Host $emptyNote.PadRight($maxBarWidth) -ForegroundColor DarkYellow
                Write-Host $emptyHint.PadRight($maxBarWidth) -ForegroundColor DarkGray
                Write-Host ("").PadRight($maxBarWidth)
                $linesPrinted += 3
            }

            # Profile rows: [x]/[ ] checkbox, highlighted row inverted.
            for ($i = 0; $i -lt $names.Count; $i++) {
                $n      = $names[$i]
                $mark   = if ($selected[$n]) { "[x]" } else { "[ ]" }
                $prefix = if ($i -eq $cursor) { "  > " } else { "    " }
                $line   = ($prefix + "$mark $n").PadRight($maxBarWidth)
                if ($i -eq $cursor) {
                    Write-Host $line -ForegroundColor Black -BackgroundColor White
                } elseif ($selected[$n]) {
                    Write-Host $line -ForegroundColor White
                } else {
                    Write-Host $line -ForegroundColor DarkGray
                }
                $linesPrinted++
            }

            # Blank separator, then the two action rows.
            Write-Host ("").PadRight($maxBarWidth)
            $linesPrinted++
            foreach ($act in @(@{ Idx = $idxOpen; Text = "Open projects config folder" },
                               @{ Idx = $idxLink; Text = "Link a remote config repo..." },
                               @{ Idx = $idxCont; Text = "Continue" })) {
                $prefix = if ($act.Idx -eq $cursor) { "  > " } else { "    " }
                $line   = ($prefix + $act.Text).PadRight($maxBarWidth)
                if ($act.Idx -eq $cursor) {
                    Write-Host $line -ForegroundColor Black -BackgroundColor White
                } else {
                    Write-Host $line -ForegroundColor Cyan
                }
                $linesPrinted++
            }

            # Clear any rows left over from a previous, taller render (file removed).
            if ($prevLines -gt $linesPrinted) {
                $blank = " " * $maxBarWidth
                for ($k = 0; $k -lt ($prevLines - $linesPrinted); $k++) { Write-Host $blank }
            }
            $prevLines = $linesPrinted

            # Wait for a key, but wake periodically so a folder change redraws the
            # list even while no key is pressed.
            $sig = ($names -join "`n")
            $key = $null
            while ($true) {
                if ([Console]::KeyAvailable) { $key = [Console]::ReadKey($true); break }
                Start-Sleep -Milliseconds 200
                if ((@(& $scan) -join "`n") -ne $sig) { break }   # folder changed -> redraw
            }
            if ($null -eq $key) { continue }   # woke for a refresh, not a keypress

            switch ($key.Key) {
                'UpArrow'   { $cursor = ($cursor - 1 + $rowCount) % $rowCount }
                'DownArrow' { $cursor = ($cursor + 1) % $rowCount }
                'Spacebar'  {
                    if ($cursor -lt $names.Count) {
                        $n = $names[$cursor]; $selected[$n] = -not $selected[$n]
                    }
                }
                'Enter' {
                    if ($cursor -lt $names.Count) {
                        # Enter on a profile toggles it too (handy alongside Space).
                        $n = $names[$cursor]; $selected[$n] = -not $selected[$n]
                    } elseif ($cursor -eq $idxOpen) {
                        # Open Explorer to the folder, leaving the menu running so
                        # the user can add profiles and watch them appear.
                        if (-not (Test-Path -LiteralPath $ProjectsDir)) {
                            New-Item -ItemType Directory -Path $ProjectsDir -Force | Out-Null
                        }
                        $full = (Resolve-Path -LiteralPath $ProjectsDir).Path
                        try { Start-Process -FilePath explorer.exe -ArgumentList $full } catch { }
                    } elseif ($cursor -eq $idxLink) {
                        # Link a remote config repo: prompt for URL, ensure git,
                        # clone to staging ONCE, then import per candidate so a
                        # name collision on one candidate never aborts the rest
                        # (interactive contract: prompt for a rename instead of
                        # throwing; the CLI keeps the hard-error contract).
                        Write-Host ""
                        $repoUrl = Read-Host "  Git repo URL"
                        if (-not [string]::IsNullOrWhiteSpace($repoUrl)) {
                            $repoUrl = $repoUrl.Trim()
                            if (-not (Ensure-ConstructGit)) {
                                Write-Host "    git is required for remote config repos." -ForegroundColor Yellow
                                Start-Sleep -Seconds 2
                            } else {
                                $configDir = Get-ConstructConfigDir
                                $null = Initialize-ConstructConfigStore -ScriptsDir (Split-Path -Parent $ProjectsDir)
                                try {
                                    $cloneDir = Update-ConstructStagingClone -SourceRepo $repoUrl
                                    $importCands = @(Get-ConstructImportCandidates -SourceDir $cloneDir)
                                    if ($importCands.Count -eq 0) {
                                        Write-Host "    No importable profiles found in the repo." -ForegroundColor Yellow
                                    }
                                    $srcHasProjSubdir = Test-Path -LiteralPath (Join-Path $cloneDir "projects")

                                    foreach ($cand in $importCands) {
                                        $candName = $cand.BaseName
                                        try {
                                            # -NoFetch: the staging clone was refreshed above.
                                            $importResult = Import-ConstructConfigs -ConfigDir $configDir -SourceRepo $repoUrl `
                                                -Names @($candName) -NoFetch
                                            foreach ($imp in @($importResult.Imported)) {
                                                Write-Host "    Imported: $imp" -ForegroundColor Green
                                                # Default newly imported profiles to selected.
                                                $selected[$imp] = $true
                                            }
                                            foreach ($e in @($importResult.Errors)) {
                                                Write-Host "    $e" -ForegroundColor Yellow
                                            }
                                        } catch {
                                            $errMsg = $_.Exception.Message
                                            if ($errMsg -notmatch "Name collision") {
                                                Write-Host "    ${candName}: $errMsg" -ForegroundColor Red
                                                continue
                                            }
                                            # Collision (D17 interactive): prompt for a rename,
                                            # validating the target -- reserved names and
                                            # already-existing profiles are refused (never a
                                            # silent overwrite), with a re-prompt on refusal.
                                            $relPath = if ($srcHasProjSubdir) { "projects/$($cand.Name)" } else { $cand.Name }
                                            $suggestName = "$candName-2"
                                            $suffix = 2
                                            while (-not (Test-ConstructRenameTarget -ConfigDir $configDir -NewName $suggestName `
                                                         -RemoteUrl $repoUrl -PathInRemote $relPath).Ok) {
                                                $suffix++
                                                $suggestName = "$candName-$suffix"
                                            }
                                            Write-Host "    A profile named '$candName' already exists." -ForegroundColor Yellow
                                            while ($true) {
                                                $newName = Read-Host "  Rename to (Enter for '$suggestName', '-' to skip)"
                                                if ([string]::IsNullOrWhiteSpace($newName)) { $newName = $suggestName }
                                                $newName = $newName.Trim()
                                                if ($newName -eq '-') {
                                                    Write-Host "    Skipped '$candName'." -ForegroundColor DarkGray
                                                    break
                                                }
                                                $target = Test-ConstructRenameTarget -ConfigDir $configDir -NewName $newName `
                                                    -RemoteUrl $repoUrl -PathInRemote $relPath
                                                if (-not $target.Ok) {
                                                    Write-Host "    $($target.Reason)" -ForegroundColor Yellow
                                                    continue
                                                }
                                                $renamed = Import-ConstructConfigAs -ConfigDir $configDir -SourceFile $cand.FullName `
                                                    -NewName $newName -RemoteUrl $repoUrl -PathInRemote $relPath -CloneDir $cloneDir
                                                if ($renamed.Ok) {
                                                    Write-Host "    Imported as '$newName'." -ForegroundColor Green
                                                    $selected[$newName] = $true
                                                } else {
                                                    Write-Host "    $($renamed.Error)" -ForegroundColor Red
                                                }
                                                break
                                            }
                                        }
                                    }
                                    # Record the linked remote even when every candidate
                                    # collided or was skipped -- the link itself succeeded.
                                    Register-ConstructConfigRemote -ConfigDir $configDir -RemoteUrl $repoUrl
                                } catch {
                                    Write-Host "    $($_.Exception.Message)" -ForegroundColor Red
                                }
                                Start-Sleep -Seconds 2
                            }
                        }
                        # Force a rescan on the next loop iteration.
                        $names = @()
                    } else {
                        # Continue: return the chosen profiles.
                        $chosen = @($names | Where-Object { $selected[$_] })
                        if ($chosen.Count -eq 0) { return "default" }
                        return ($chosen -join ",")
                    }
                }
            }
        }
    } finally {
        try { [Console]::CursorVisible = $true } catch { }
        Write-Host ""
    }
}

function Confirm-Reinstall {
    <#
        Loud, last-chance confirmation for the irreversible VM delete. Defaults
        to NO: the user must type the literal word "yes" (anything else, including
        a bare Enter, cancels). Returns $true only when deletion is confirmed.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$VmName)

    # The last-chance warning gets a screen of its own in TUI mode.
    if (Test-ConstructTui) { Clear-Host; Show-ConstructHeader }

    Write-Host ""
    Write-Host "  ******************************  WARNING  ******************************" -ForegroundColor Red
    Write-Host ""
    Write-Host "   A COMPLETE REINSTALL will PERMANENTLY DELETE the virtual machine"        -ForegroundColor Red
    Write-Host "   '$VmName' and its virtual hard disk, including ALL DATA on it."           -ForegroundColor Red
    Write-Host ""
    Write-Host "   This action is IRREVERSIBLE: there is no checkpoint to roll back to"      -ForegroundColor Red
    Write-Host "   and no way to recover the disk once it has been deleted."                 -ForegroundColor Red
    Write-Host ""
    Write-Host "  ***********************************************************************"   -ForegroundColor Red
    Write-Host ""
    Write-Host "  To proceed you must type " -NoNewline -ForegroundColor Yellow
    Write-Host "yes"                          -NoNewline -ForegroundColor White
    Write-Host " exactly. Anything else cancels."        -ForegroundColor Yellow
    $answer = Read-Host "  Delete '$VmName' and ALL its data? [no]"
    return ($answer.Trim().ToLowerInvariant() -eq 'yes')
}

function Remove-FileWithRetry {
    # Delete a file, retrying for a while: Hyper-V's VMMS keeps a brief lock on
    # the .vhdx/.avhdx for several seconds after Remove-VM (or a just-finished
    # merge), so a single Remove-Item usually fails with "file in use". No-op if
    # the file is already gone.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Path,
        [int]$TimeoutSeconds = 60
    )
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ($true) {
        try {
            Remove-Item -LiteralPath $Path -Force -ErrorAction Stop
            Write-Ok "Deleted virtual disk: $Path"
            return
        } catch {
            if ((Get-Date) -ge $deadline) {
                Write-Warning "Could not delete '$Path' after ${TimeoutSeconds}s ($($_.Exception.Message)). Delete it manually."
                return
            }
            Start-Sleep -Seconds 3
        }
    }
}

function Get-VhdChain {
    # Return every backing file for a virtual disk: the given file plus its full
    # parent chain (a checkpoint .avhdx references the base .vhdx via ParentPath).
    # This lets us delete the real base disk even when a merge hasn't yet
    # collapsed the chain back to a single .vhdx.
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)
    $chain = New-Object System.Collections.Generic.List[string]
    $cur = $Path
    while ($cur -and (Test-Path -LiteralPath $cur) -and ($chain -notcontains $cur)) {
        $chain.Add($cur)
        $vhd = Get-VHD -Path $cur -ErrorAction SilentlyContinue
        if (-not $vhd -or [string]::IsNullOrEmpty($vhd.ParentPath)) { break }
        $cur = $vhd.ParentPath
    }
    return $chain
}

function Remove-AgentVm {
    <#
        Tear down an existing Hyper-V VM and delete its virtual hard disk:
          1. Hard power-off if it is running.
          2. Resolve each attached disk's full backing-file chain (the base
             .vhdx plus any checkpoint .avhdx differencing files) -- captured
             before the VM is removed, since we can't query it afterwards.
          3. Remove the VM definition. This makes Hyper-V merge the checkpoint
             .avhdx files into the base and auto-delete them -- asynchronously.
          4. Wait for checkpointing to finish: the last .avhdx lingers for a few
             seconds after the VM is gone, so wait until every .avhdx we saw has
             disappeared from disk before touching the base .vhdx (deleting it
             mid-merge hits a locked file).
          5. Delete any orphaned .avhdx, then the base .vhdx, retrying past
             VMMS's brief lock.
        No-op if the VM does not exist.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$VmName)

    $vm = Get-VM -Name $VmName -ErrorAction SilentlyContinue
    if (-not $vm) { return }

    Write-Step "Deleting existing VM '$VmName'"

    # 1. Hard power-off if running (we're destroying it -- no clean save).
    if ($vm.State -ne 'Off') {
        Write-Note "Powering off the VM..."
        Stop-VM -Name $VmName -TurnOff -Force -ErrorAction SilentlyContinue
        $deadline = (Get-Date).AddMinutes(3)
        while ((Get-VM -Name $VmName -ErrorAction SilentlyContinue).State -ne 'Off' -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
        }
    }

    # 2. Resolve every backing file BEFORE removing the VM -- once it's gone we
    #    can't query them. Split into the base .vhdx (the chain root we want to
    #    delete) and any checkpoint .avhdx differencing files. Removing the VM
    #    makes Hyper-V merge the .avhdx into the base and auto-delete them, which
    #    is asynchronous -- so we wait for that below before deleting the base.
    $baseDisks = New-Object System.Collections.Generic.List[string]
    $diffDisks = New-Object System.Collections.Generic.List[string]
    foreach ($p in @(Get-VMHardDiskDrive -VMName $VmName -ErrorAction SilentlyContinue |
                     Select-Object -ExpandProperty Path)) {
        foreach ($f in (Get-VhdChain -Path $p)) {
            if ($f -match '\.avhdx$') {
                if ($diffDisks -notcontains $f) { $diffDisks.Add($f) }
            } elseif ($baseDisks -notcontains $f) {
                $baseDisks.Add($f)
            }
        }
    }

    # 3. Remove the VM definition. This triggers the checkpoint merge + auto-
    #    removal of the .avhdx files; the base .vhdx stays on disk.
    Remove-VM -Name $VmName -Force -ErrorAction Stop
    Write-Ok "VM removed from Hyper-V"

    # 4. Wait for checkpointing to finish. Even after the VM is gone the last
    #    checkpoint .avhdx lingers for a few seconds while Hyper-V merges it into
    #    the base and then deletes it; deleting the .vhdx before that finishes
    #    hits a locked file. Give it a moment, then wait until every .avhdx we
    #    saw has actually disappeared from disk (bounded), then settle briefly.
    Start-Sleep -Seconds 3
    if ($diffDisks.Count -gt 0) {
        Write-Note "Waiting for the checkpoint merge to finish (Hyper-V auto-removes the .avhdx)..."
        $deadline = (Get-Date).AddMinutes(10)
        while (($diffDisks | Where-Object { Test-Path -LiteralPath $_ }) -and (Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 2
        }
        Start-Sleep -Seconds 2
    }

    # 5. Delete the disk file(s): any .avhdx Hyper-V did NOT auto-remove (orphan),
    #    then the base .vhdx -- retrying past any residual VMMS lock.
    if ($baseDisks.Count -eq 0 -and $diffDisks.Count -eq 0) {
        Write-Note "No virtual disk files were attached to delete."
    }
    foreach ($f in $diffDisks) { Remove-FileWithRetry -Path $f }
    foreach ($f in $baseDisks) { Remove-FileWithRetry -Path $f }
}

# ── Persisted setup settings + git identity ──────────────────────────────────
# A small JSON file kept NEXT TO the scripts ($PSScriptRoot of the caller) that
# remembers host-side choices across runs -- notably the git identity to apply
# to the VM -- so a reprovision doesn't have to re-specify them. For the web
# (iex) installer this folder is the extracted repo under %LOCALAPPDATA%, which
# Expand-Archive -Force refreshes WITHOUT deleting files that aren't part of the
# archive, so this file survives re-runs.

function Get-ConstructSettingsPath {
    param([Parameter(Mandatory)][string]$Dir)
    return (Join-Path $Dir ".construct-settings.json")
}

function Read-ConstructSettings {
    # Saved settings as a PSCustomObject, or $null if none/unreadable.
    param([Parameter(Mandatory)][string]$Dir)
    $path = Get-ConstructSettingsPath -Dir $Dir
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    try { return (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) } catch { return $null }
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

function Get-HostGitIdentity {
    # This host's own global git identity, or empty strings if git isn't
    # installed or the values aren't set.
    $name = ""; $email = ""
    if (Get-Command git -ErrorAction SilentlyContinue) {
        $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        try {
            $name  = (& git config --global user.name)  2>$null
            $email = (& git config --global user.email) 2>$null
        } catch { }
        $ErrorActionPreference = $prev
    }
    return @{ Name = ("$name").Trim(); Email = ("$email").Trim() }
}

function Resolve-GitIdentity {
    <#
        Decide the git user.name / user.email to apply to the VM as its global
        git config, and persist the choice next to the scripts.

        Defaults, highest priority first:
          1. A value passed in -Name / -Email (e.g. forwarded from an upper script).
          2. The saved settings file in -Dir (a previous run's choice).
          3. This host's own global git identity.

        Also decides whether to enable git's plaintext credential store on the VM
        (credential.helper store) so pushes/pulls don't re-prompt -- default yes,
        with a security warning. Its default comes from -CredentialStore ("yes" /
        "no"), then the saved setting, then yes.

        Unless -NoPrompt, the user is prompted for each value with the resolved
        default offered on Enter. The final values are saved back to -Dir.
        Returns @{ Name = <string>; Email = <string>; CredentialStore = <bool> }
        (Name / Email may be "").
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Dir,
        [string]$Name,
        [string]$Email,
        [string]$CredentialStore,
        [switch]$NoPrompt
    )

    $saved   = Read-ConstructSettings -Dir $Dir
    $hostGit = Get-HostGitIdentity
    $savedHasCred = $saved -and ($saved.PSObject.Properties.Name -contains 'gitCredentialStore')

    $defName  = if     ($Name)                              { $Name }
                elseif ($saved -and $saved.gitUserName)     { [string]$saved.gitUserName }
                elseif ($hostGit.Name)                      { $hostGit.Name }
                else                                        { "" }
    $defEmail = if     ($Email)                             { $Email }
                elseif ($saved -and $saved.gitEmail)        { [string]$saved.gitEmail }
                elseif ($hostGit.Email)                     { $hostGit.Email }
                else                                        { "" }
    $defCred  = if     ($CredentialStore)                   { $CredentialStore -eq 'yes' }
                elseif ($savedHasCred)                      { [bool]$saved.gitCredentialStore }
                else                                        { $true }

    $resName  = $defName
    $resEmail = $defEmail
    $resCred  = $defCred

    if (-not $NoPrompt) {
        Show-TuiScreen -Title "Git identity (applied as the VM's global git config)" -Body @(
            "Used for commits made inside the VM. Press Enter to accept each default."
        )
        $hint = if ($defName)  { " (press Enter for '$defName')" }  else { " (leave blank to skip)" }
        $ans  = Read-Host "    Git user name$hint"
        if (-not [string]::IsNullOrWhiteSpace($ans)) { $resName = $ans.Trim() }
        $hint = if ($defEmail) { " (press Enter for '$defEmail')" } else { " (leave blank to skip)" }
        $ans  = Read-Host "    Git email$hint"
        if (-not [string]::IsNullOrWhiteSpace($ans)) { $resEmail = $ans.Trim() }

        Write-Host ""
        Write-Host "    Store git credentials on the VM so pushes/pulls don't re-prompt?" -ForegroundColor White
        Write-Host "      WARNING: credentials are saved in PLAINTEXT (~/.git-credentials) and are" -ForegroundColor Yellow
        Write-Host "      readable by anything on the VM -- including the AI agents, so a prompt-" -ForegroundColor Yellow
        Write-Host "      injection attack could exfiltrate them." -ForegroundColor Yellow
        if ([Console]::IsInputRedirected) {
            # Can't drive a menu: keep the resolved default.
        } else {
            # Same screen -- the warning above is context the user still needs.
            $resCred = Invoke-TuiConfirm -NoScreen -DefaultNo:(-not $defCred) `
                -Question "Store git credentials on the VM?" `
                -YesLabel "Yes  store them (convenient, plaintext)" `
                -NoLabel  "No   re-authenticate per push/pull"
        }
    }

    Save-ConstructSettings -Dir $Dir -Values @{ gitUserName = $resName; gitEmail = $resEmail; gitCredentialStore = $resCred }
    return @{ Name = $resName; Email = $resEmail; CredentialStore = $resCred }
}

# ── Saved agent-config backup (export/restore across reinstall) ──────────────
# Helpers for the "save current config and restore it after a reinstall" feature
# and the Feature-2 clone-credential prompt. The export/restore SSH work itself
# lives in Provision-AgentVM.ps1; these are the host-side bits shared with
# Auto-Install.ps1.

function Get-ConstructBackupDir {
    # Directory NEXT TO the scripts where an exported VM config backup is saved
    # (and read back from on restore). Gitignored -- it holds plaintext secrets
    # (subscription auth tokens, git credentials).
    param([Parameter(Mandatory)][string]$Dir)
    return (Join-Path $Dir ".construct-backup")
}

function Test-BackupHasGitCredentials {
    <#
        $true when a saved config backup holds a non-empty git-credentials file
        (extracted\home\.git-credentials). This is exactly the credential
        Provision-AgentVM.ps1 falls back to for cloning private project repos on a
        restore, so when it's present the up-front clone-credential prompt is
        redundant and can be skipped -- the checkout still authenticates from the
        restored credentials. (Path mirrors the restore fallback in
        Provision-AgentVM.ps1.)
    #>
    [CmdletBinding()]
    param([AllowEmptyString()][AllowNull()][string]$BackupDir)
    if ([string]::IsNullOrWhiteSpace($BackupDir)) { return $false }
    $credFile = Join-Path $BackupDir "extracted\home\.git-credentials"
    if (-not (Test-Path -LiteralPath $credFile)) { return $false }
    try {
        $lines = @(Get-Content -LiteralPath $credFile -ErrorAction Stop | Where-Object { $_.Trim() })
        return ($lines.Count -gt 0)
    } catch { return $false }
}

function Get-ProjectRepoUrls {
    # Every repo URL declared by the named project profiles (a comma-separated
    # string or an array of names), read from <ProjectsDir>\<name>.json. Missing
    # files / profiles without repos are skipped. Returns a (possibly empty)
    # array of unique URL strings.
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectsDir,
        [Parameter(Mandatory)][AllowNull()]$Names
    )
    $list = New-Object System.Collections.Generic.List[string]
    $nameArr = if ($Names -is [string]) { $Names -split ',' } else { @($Names) }
    foreach ($n in $nameArr) {
        $name = ("$n").Trim()
        if (-not $name) { continue }
        $file = Join-Path $ProjectsDir "$name.json"
        if (-not (Test-Path -LiteralPath $file)) { continue }
        try { $json = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } catch { continue }
        if ($json.repos) {
            foreach ($r in $json.repos) { if ($r.url) { $list.Add([string]$r.url) } }
        }
    }
    return @($list | Select-Object -Unique)
}

function Resolve-GitCloneCredential {
    <#
        If any of the selected project profiles declare http(s) repo URLs, prompt
        ONCE for a git username + token to use for cloning them during
        provisioning (Enter on the username skips entirely). Builds one
        `<proto>://<user>:<token>@<host>` credential line per distinct http(s)
        host found, URL-encoding the user/token, and returns them base64-encoded
        (newline-joined) for handoff to the VM (env GIT_CLONE_CREDENTIALS_B64).

        Returns "" when skipped, when there are no repos, or when the only repo
        URLs are ssh:// / git@ forms (a username/token can't authenticate those,
        so they don't trigger the prompt).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ProjectsDir,
        [Parameter(Mandatory)][AllowNull()]$Names,
        [switch]$NoPrompt
    )
    if ($NoPrompt) { return "" }
    $urls = Get-ProjectRepoUrls -ProjectsDir $ProjectsDir -Names $Names
    if (-not $urls -or @($urls).Count -eq 0) { return "" }

    # Distinct http(s) hosts (with port, minus any embedded userinfo) and the
    # scheme each was first seen with.
    $hostProto = [ordered]@{}
    foreach ($u in $urls) {
        if ($u -match '^(https?)://(?:[^/@]+@)?([^/]+)') {
            $h = $matches[2]
            if (-not $hostProto.Contains($h)) { $hostProto[$h] = $matches[1] }
        }
    }
    if ($hostProto.Count -eq 0) { return "" }   # only ssh/git@ URLs -- nothing to prompt for
    $hostList = @($hostProto.Keys)

    Show-TuiScreen -Title "Git credentials for cloning project repos" -Body @(
        "The selected projects clone repos from: $($hostList -join ', ')",
        "Enter credentials to use for the clone, or press Enter to skip",
        "(skip if the repos are public or you'll authenticate another way)."
    )
    $user = Read-Host "    Git username (press Enter to skip)"
    if ([string]::IsNullOrWhiteSpace($user)) { Write-Note "No clone credentials entered -- skipping."; return "" }
    $secure = Read-Host "    Git token / password" -AsSecureString
    $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try   { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }
    if ([string]::IsNullOrWhiteSpace($token)) { Write-Note "No token entered -- skipping."; return "" }

    $encUser = [uri]::EscapeDataString($user.Trim())
    $encTok  = [uri]::EscapeDataString($token)
    $lines = foreach ($h in $hostList) { "{0}://{1}:{2}@{3}" -f $hostProto[$h], $encUser, $encTok, $h }
    Write-Ok ("Clone credentials set for: {0}" -f ($hostList -join ', '))
    return [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))
}

function Confirm-RepoScan {
    <#
        Given the project-repo scan (parsed from scan-repos.sh JSON), warn about
        any repo with uncommitted or unpushed work that a reinstall would destroy,
        and ask whether to continue. Returns $true to proceed (no risky repos, or
        the user confirmed) and $false to abort. Defaults to NO when there is risk.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][AllowNull()]$Repos)

    $risky = @()
    if ($Repos) { $risky = @($Repos | Where-Object { [int]$_.dirty -gt 0 -or [int]$_.unpushed -gt 0 }) }
    if ($risky.Count -eq 0) { return $true }

    # The data-loss warning gets a screen of its own in TUI mode.
    if (Test-ConstructTui) { Clear-Host; Show-ConstructHeader }

    Write-Host ""
    Write-Host "  *********************  UNCOMMITTED / UNPUSHED WORK  *********************" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "   A reinstall DELETES the VM disk. These repos in the workspace have work"  -ForegroundColor Yellow
    Write-Host "   that is not safely on a remote and would be LOST:"                        -ForegroundColor Yellow
    Write-Host ""
    foreach ($r in $risky) {
        $bits = @()
        if ([int]$r.dirty    -gt 0) { $bits += "$($r.dirty) uncommitted" }
        if ([int]$r.unpushed -gt 0) { $bits += "$($r.unpushed) unpushed" }
        $remote = if ($r.url) { $r.url } else { "(no remote!)" }
        Write-Host ("     - {0}: {1}   {2}" -f $r.name, ($bits -join ", "), $remote) -ForegroundColor White
    }
    Write-Host ""
    Write-Host "   Consider committing/pushing inside the VM first." -ForegroundColor Yellow
    if ([Console]::IsInputRedirected) { return $false }
    $c = Show-Menu -Title "Continue with the reinstall and lose this work?" -Options @(
        "Abort     keep my work; cancel the reinstall (recommended)",
        "Continue  reinstall anyway and LOSE the work listed above"
    ) -Default 0
    return ($c -eq 1)
}

# ── Remote-SSH / control-panel host support ──────────────────────────────────
# Helpers that make the host ready for the control-panel extension's Remote-SSH
# features (the "Open on VM" / project-open buttons, the VM-power probe, and the
# end-of-install deep link). All best-effort: a failure warns but never aborts the
# install. Ensure-VSCodeRemoteSsh is meant to run NON-elevated (winget user scope);
# Add-HyperVAdminMembership needs admin (the installer is already elevated).

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

function Ensure-VSCodeRemoteSsh {
    <#
        Make sure VS Code and the Remote-SSH extension are installed on the host so
        the control panel's "Open on VM" / project-open buttons and the end-of-install
        deep link work. Idempotent: detects an existing install, else tries winget
        (user scope; no elevation -- run this BEFORE Auto-Install self-elevates), then
        installs the ms-vscode-remote.remote-ssh extension. If winget is unavailable it
        prints a manual hint and moves on. Never throws. Returns $true if VS Code is
        present afterwards, else $false.
    #>
    [CmdletBinding()]
    param()

    $code = Find-VSCodeCli
    if (-not $code) {
        if (Get-Command winget -ErrorAction SilentlyContinue) {
            Write-Host "==> Installing VS Code (winget)..." -ForegroundColor Cyan
            try {
                & winget install --id Microsoft.VisualStudioCode -e --silent `
                    --accept-package-agreements --accept-source-agreements --scope user 2>&1 | Out-Null
            } catch {
                Write-Warning "winget could not install VS Code: $($_.Exception.Message)"
            }
            $code = Find-VSCodeCli
        }
    }
    if (-not $code) {
        Write-Warning "VS Code isn't installed and couldn't be installed automatically."
        Write-Host "    Install it from https://code.visualstudio.com/ , then run:" -ForegroundColor DarkGray
        Write-Host "        code --install-extension ms-vscode-remote.remote-ssh" -ForegroundColor DarkGray
        return $false
    }
    Write-Host "==> Ensuring the VS Code Remote-SSH extension..." -ForegroundColor Cyan
    try {
        # Idempotent: a no-op (exit 0) when the extension is already installed. Decide
        # success ONLY by the exit code -- a native command's non-zero exit does NOT
        # throw in Windows PowerShell 5.1, and (via Invoke-VSCodeCli) a stderr write
        # such as `code`'s DEP0169 deprecation warning is NOT treated as failure.
        $exit = Invoke-VSCodeCli -Code $code -CodeArgs @('--install-extension', 'ms-vscode-remote.remote-ssh')
        if ($exit -eq 0) {
            Write-Host "    Remote-SSH extension present." -ForegroundColor Green
        } else {
            Write-Warning "code --install-extension exited $exit; the Remote-SSH extension may not be installed."
            Write-Host "    Install it manually:  code --install-extension ms-vscode-remote.remote-ssh" -ForegroundColor DarkGray
        }
    } catch {
        Write-Warning "Could not install the Remote-SSH extension: $($_.Exception.Message)"
    }
    return $true
}

function Ensure-Ffmpeg {
    <#
        Make sure ffmpeg is on the host so the control panel's microphone passthrough
        can capture the mic. The panel spawns ffmpeg LOCALLY (a UI extension runs on the
        host) to read the default DirectShow capture device -- a VS Code webview can't
        reach the mic (its iframe Permissions-Policy omits `microphone`), so a native
        host recorder is the only capture path that works. Idempotent: a no-op if ffmpeg
        is already on PATH; else tries winget (user scope, no elevation -- run BEFORE
        Auto-Install self-elevates). If winget is unavailable it prints a manual hint and
        moves on. Best-effort: never throws, never blocks the install. Returns $true if
        ffmpeg is present afterwards, else $false.
    #>
    [CmdletBinding()]
    param()

    if (Get-Command ffmpeg -ErrorAction SilentlyContinue) {
        Write-Host "==> ffmpeg present (microphone passthrough ready)." -ForegroundColor Green
        return $true
    }
    if (Get-Command winget -ErrorAction SilentlyContinue) {
        Write-Host "==> Installing ffmpeg for microphone passthrough (winget)..." -ForegroundColor Cyan
        try {
            # Gyan.FFmpeg is the standard Windows ffmpeg package. User scope needs no
            # elevation. A stderr write here doesn't fail the run (own try/catch; the
            # PATH re-check below is the real verdict). winget updates PATH for NEW
            # sessions, so ffmpeg may only resolve after VS Code is (re)started.
            & winget install --id Gyan.FFmpeg -e --silent `
                --accept-package-agreements --accept-source-agreements --scope user 2>&1 | Out-Null
        } catch {
            Write-Warning "winget could not install ffmpeg: $($_.Exception.Message)"
        }
    }
    if (Get-Command ffmpeg -ErrorAction SilentlyContinue) {
        Write-Host "    ffmpeg installed (restart VS Code so it's on PATH for the panel)." -ForegroundColor Green
        return $true
    }
    Write-Warning "ffmpeg isn't installed, so microphone passthrough can't capture the mic yet."
    Write-Host "    Install it, then restart VS Code:  winget install Gyan.FFmpeg" -ForegroundColor DarkGray
    return $false
}

function Add-HyperVAdminMembership {
    <#
        Add the current user to the local "Hyper-V Administrators" group so the
        (non-elevated) control-panel extension can read VM power state via Get-VM --
        and thus offer "Start & connect" for a stopped VM -- without a UAC prompt on
        every status refresh. Needs admin (the installer is already elevated). The
        group is resolved by its well-known SID (S-1-5-32-578) so it works on
        non-English Windows. Idempotent and best-effort; never throws. Membership
        takes effect at the user's next sign-in.
    #>
    [CmdletBinding()]
    param()

    try {
        $sid = [System.Security.Principal.SecurityIdentifier]'S-1-5-32-578'
        $grp = Get-LocalGroup -SID $sid -ErrorAction Stop
        # Use the desktop user (explorer.exe owner) rather than the elevated
        # identity so over-the-shoulder UAC / ABR adds the RIGHT account.
        # When no desktop shell is found, skip rather than fall back to the
        # elevated SID (which is precisely the wrong account).
        $desktopUser = Get-DesktopUser
        if (-not $desktopUser) {
            Write-Note "Desktop user could not be determined -- skipping Hyper-V Administrators membership."
            return
        }
        $desktopSid = (New-Object System.Security.Principal.NTAccount($desktopUser)).Translate(
            [System.Security.Principal.SecurityIdentifier]).Value
        $already = @(Get-LocalGroupMember -Group $grp -ErrorAction Stop |
                     Where-Object { $_.SID.Value -eq $desktopSid })
        if ($already.Count -gt 0) { Write-Note "Already in '$($grp.Name)'."; return }
        # -Member accepts a SID string; -SID on this cmdlet would identify the GROUP.
        Add-LocalGroupMember -Group $grp -Member $desktopSid -ErrorAction Stop
        Write-Ok "Added you to '$($grp.Name)' (effective at next sign-in)."
    } catch {
        Write-Note "Could not update Hyper-V Administrators membership: $($_.Exception.Message)"
    }
}

function Get-RemoteOpenLink {
    # Build the `vscode://vscode-remote/ssh-remote+<alias><path>` deep link that opens
    # the VM's workspace in VS Code over Remote-SSH. The alias is the VM host's first
    # DNS label (agent-vm.mshome.net -> agent-vm), matching the SSH Host alias the
    # provisioner writes and the extension's own URIs. Pure.
    [CmdletBinding()]
    param([string]$VmHost = "agent-vm", [string]$WorkspaceRoot = "/root/repos")
    $alias = ($VmHost -split '\.')[0]
    $path  = if ($WorkspaceRoot.StartsWith("/")) { $WorkspaceRoot } else { "/$WorkspaceRoot" }
    return "vscode://vscode-remote/ssh-remote+$alias$path"
}

function Select-VmCodeWindow {
    <#
        Pure filter: from top-level window records (Title, ProcessName), pick the
        VS Code windows attached to the VM over Remote-SSH. Remote-SSH stamps
        "[SSH: <authority>]" into the window title, where the authority is the SSH
        Host alias (the VM host's first DNS label -- what Get-RemoteOpenLink and the
        extension's deep links use) or the full host name (a manual connect). Both
        must match; "agent-vm2", the same alias under a FOREIGN domain
        ("agent-vm.example.net"), or an ssh session in a terminal window must not
        -- so the authority must be EXACTLY the alias or the full host (plus an
        optional :port), and the process exactly VS Code ('Code'/'Code - Insiders').
    #>
    [CmdletBinding()]
    param(
        [object[]]$Windows = @(),
        [string]$VmHost = "agent-vm"
    )
    $alias = ($VmHost -split '\.')[0]
    $names = @([regex]::Escape($alias), [regex]::Escape($VmHost)) | Select-Object -Unique
    $pattern = "\[SSH:\s*($($names -join '|'))(:\d+)?\]"
    return @($Windows | Where-Object {
        $_ -and @('Code', 'Code - Insiders') -contains $_.ProcessName -and $_.Title -match $pattern
    })
}

function Close-VmVsCodeWindow {
    <#
        Ask every VS Code window attached to the VM over Remote-SSH to close, and
        return how many were asked. Used by the reinstall/redownload flow right
        before the VM is deleted: an attached window would only degrade into
        reconnect-error popups while its backend is destroyed and rebuilt.

        WHY Win32 instead of Get-Process/CloseMainWindow: every VS Code window
        belongs to ONE Electron main process, so .MainWindowTitle exposes a single
        title no matter how many windows are open -- per-window targeting needs
        EnumWindows. WM_CLOSE is the graceful ask (hot exit backs up dirty
        editors); nothing is ever force-killed, so a window showing a modal dialog
        may legitimately stay open. UIPI permits the elevated installer to post to
        the user's medium-integrity windows (only lower->higher is blocked).
        Best-effort and Windows-only: returns 0 (never throws) on any failure.
    #>
    [CmdletBinding()]
    param([string]$VmHost = "agent-vm")
    try {
        if ($env:OS -ne 'Windows_NT') { return 0 }
        if (-not ('ConstructNative.TopWindows' -as [type])) {
            Add-Type -TypeDefinition @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace ConstructNative {
    public static class TopWindows {
        private delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int max);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
        private const uint WM_CLOSE = 0x0010;
        public class Info { public long Handle; public string Title; public uint Pid; }
        public static List<Info> List() {
            var found = new List<Info>();
            EnumWindows(delegate(IntPtr h, IntPtr l) {
                if (!IsWindowVisible(h)) return true;
                var sb = new StringBuilder(1024);
                if (GetWindowText(h, sb, sb.Capacity) <= 0) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                found.Add(new Info { Handle = h.ToInt64(), Title = sb.ToString(), Pid = pid });
                return true;
            }, IntPtr.Zero);
            return found;
        }
        public static bool Close(long handle) { return PostMessage(new IntPtr(handle), WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
    }
}
"@
        }
        $records = foreach ($w in [ConstructNative.TopWindows]::List()) {
            $pname = ""
            try { $pname = (Get-Process -Id $w.Pid -ErrorAction Stop).ProcessName } catch { $pname = "" }
            [pscustomobject]@{ Title = $w.Title; ProcessName = $pname; Handle = $w.Handle }
        }
        $closed = 0
        foreach ($t in @(Select-VmCodeWindow -Windows $records -VmHost $VmHost)) {
            if ([ConstructNative.TopWindows]::Close($t.Handle)) { $closed++ }
        }
        return $closed
    } catch {
        return 0
    }
}

function Open-RemoteWorkspace {
    <#
        Launch a vscode:// deep link (from Get-RemoteOpenLink) so VS Code opens the
        VM over Remote-SSH. Routed through explorer.exe rather than ShellExecute on
        the URI: the install chain runs elevated, and a direct open from an elevated
        process would start VS Code as Administrator. Explorer forwards the open to
        the existing desktop shell, so VS Code starts as the real (non-admin) user.
        Best-effort: returns $true once the open was handed to the shell, $false on
        failure (e.g. no explorer.exe on this host); never throws.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Link)
    try {
        Start-Process -FilePath explorer.exe -ArgumentList $Link -ErrorAction Stop
        return $true
    } catch {
        return $false
    }
}

# ── De-elevated provisioning ─────────────────────────────────────────────────
# The install chain runs elevated (Hyper-V needs admin), but provisioning should
# run as the real desktop user so config-sync resolves the right profile, SMB
# mappings land in the visible session, and git doesn't flag "dubious ownership".
# These helpers launch Provision-AgentVM.ps1 in a non-elevated console via a
# one-shot scheduled task with InteractiveToken + LeastPrivilege.

function Get-DesktopUser {
    <#
        Resolve the real desktop user's DOMAIN\User identity. In same-user UAC
        elevation WindowsIdentity::GetCurrent() IS the desktop user, but with
        over-the-shoulder UAC (or Admin By Request) it returns the admin account
        that granted elevation. Explorer.exe in the SAME interactive session
        always runs as the real desktop user, so its process owner is the
        authoritative identity. Returns $null when no unambiguous desktop shell
        is available (Server Core, headless, multi-user RDP with no console
        explorer) — callers must handle the null and take a loud fallback.
    #>
    [CmdletBinding()]
    param()
    try {
        # Filter explorer.exe to the installer's interactive session so
        # multi-user / RDP hosts don't pick another logged-on user's shell.
        $sessionId = [System.Diagnostics.Process]::GetCurrentProcess().SessionId
        $explorerPids = @(Get-Process -Name 'explorer' -ErrorAction SilentlyContinue |
                          Where-Object { $_.SessionId -eq $sessionId } |
                          Select-Object -ExpandProperty Id)
        if ($explorerPids.Count -eq 0) { return $null }
        $explorer = Get-CimInstance Win32_Process `
                        -Filter "ProcessId=$($explorerPids[0])" -ErrorAction Stop
        if ($explorer) {
            $owner = Invoke-CimMethod -InputObject $explorer -MethodName GetOwner -ErrorAction Stop
            if ($owner.ReturnValue -eq 0 -and $owner.User) {
                $domain = if ($owner.Domain) { $owner.Domain } else { $env:USERDOMAIN }
                return "$domain\$($owner.User)"
            }
        }
    } catch { }
    return $null
}

function Build-ProvisionEncodedCommand {
    <#
        Serialize a provision parameter set into a single -EncodedCommand blob that
        powershell.exe can accept. The params ride as JSON->base64 inside the script
        text, so values with spaces, quotes, or special characters survive intact.
        Returns the base64-encoded UTF-16LE script string.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][hashtable]$Params,
        [Parameter(Mandatory)][string]$ResultFile,
        [Parameter(Mandatory)][string]$ReadyFile
    )
    # Inner base64: the param hashtable as JSON, then base64, so it embeds safely
    # inside the script string without quoting issues.
    $json = ConvertTo-Json -InputObject $Params -Depth 4 -Compress
    $b64  = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json))
    # The script decodes the JSON, converts the PSCustomObject to a hashtable
    # (PS 5.1's ConvertFrom-Json returns PSCustomObject, which can't be splatted),
    # adds -ResultFile and -ReadyFile, and calls the provisioner. The ready-file
    # handshake is written by Provision-AgentVM.ps1 itself (after param binding),
    # not by this bootstrap — that way a missing/broken script or binding failure
    # leaves no handshake and the parent falls back to inline elevated.
    $escapedScript = $ScriptPath -replace "'", "''"
    $escapedResult = $ResultFile -replace "'", "''"
    $escapedReady  = $ReadyFile  -replace "'", "''"
    $script = @"
`$ErrorActionPreference = 'Stop'
try {
    `$Host.UI.RawUI.WindowTitle = 'Construct - Provisioning the VM'
    `$Host.UI.RawUI.BackgroundColor = [ConsoleColor]::Black
    if (`$Host.UI.RawUI.ForegroundColor -eq [ConsoleColor]::Black) {
        `$Host.UI.RawUI.ForegroundColor = [ConsoleColor]::Gray
    }
    Clear-Host
} catch { }
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
`$json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('$b64'))
`$obj  = ConvertFrom-Json `$json
`$ht   = @{}
foreach (`$p in `$obj.PSObject.Properties) {
    if (`$p.Value -is [bool]) { `$ht[`$p.Name] = [switch]`$p.Value }
    else                      { `$ht[`$p.Name] = `$p.Value }
}
`$ht['ResultFile'] = '$escapedResult'
`$ht['ReadyFile']  = '$escapedReady'
& '$escapedScript' @`$ht
"@
    # Outer encoding: -EncodedCommand expects UTF-16LE base64.
    return [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($script))
}

# De-elevation kill switch. $false = Invoke-DeElevatedProvision always runs
# Provision-AgentVM.ps1 inline in the calling (elevated) console — the
# scheduled-task machinery below is skipped entirely but kept for re-enabling.
# Disabled because the de-elevated child window prompts the user for choices
# the parent already answered via parameters, and if the user doesn't respond
# the parent's 30-second ready-handshake timeout kills the child and falls
# back inline anyway — a strictly worse version of just running inline. Flip
# to $true only after the child runs prompt-free under the passed parameters.
#
# ACCEPTED tradeoff while disabled: inline elevated provisioning writes the
# host-side profile bits (Set-HostSshConfig -> $HOME\.ssh, VS Code Remote-SSH
# settings -> %APPDATA%) under the ELEVATED token's profile. For the normal
# self-elevated UAC case that is the same user profile, so nothing changes;
# only under over-the-shoulder UAC / Admin By Request do they land in the
# approving admin's profile instead of the desktop user's. That is exactly the
# long-standing pre-de-elevation behaviour, and the problem that motivated
# de-elevation turned out not to be permissions-related.
$script:ConstructDeElevationEnabled = $false

function Invoke-DeElevatedProvision {
    <#
        Launch Provision-AgentVM.ps1 in a non-elevated console as the desktop user.
        When already non-elevated (panel reprovision), runs Provision inline — no
        task, no result file, identical to the pre-change behaviour.

        TEMPORARILY DISABLED via $script:ConstructDeElevationEnabled above: always
        runs inline until the child's spurious interactive prompts are fixed.

        On return, $global:ConstructProvisionHadErrors / ConstructProvisionErrors /
        ConstructProvisionFailureMessage are set so the caller's Wait-Exit works
        unchanged. Throws on critical failure (matching the inline behaviour).

        Falls back to an inline elevated call (with a warning) if the scheduled task
        can't be created — never leaves the VM created-but-unprovisioned silently.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][hashtable]$ProvisionParams,
        [int]$TimeoutSeconds = 7200
    )

    # ── De-elevation disabled: run inline in this console. ───────────────────────
    if (-not $script:ConstructDeElevationEnabled) {
        & $ScriptPath @ProvisionParams
        return
    }

    # ── Not elevated? Run inline (panel reprovision — already non-elevated). ─────
    $isElevated = ([Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
                   ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isElevated) {
        & $ScriptPath @ProvisionParams
        return
    }

    Write-Step "De-elevating: provisioning will run as the desktop user"

    # Resolve the desktop user BEFORE creating the task — the admin identity from
    # an over-the-shoulder UAC / ABR session is NOT the desktop user. If no
    # unambiguous desktop shell is found (Server Core, headless, multi-user RDP),
    # fall back to inline elevated with a loud warning.
    $desktopUser = Get-DesktopUser
    if (-not $desktopUser) {
        Write-Warning "Could not determine the desktop user (no explorer.exe in this session)."
        Write-Warning "Falling back to inline elevated provisioning (config-sync may use the elevated profile)."
        & $ScriptPath @ProvisionParams
        return
    }

    $taskName = "ConstructProvision-$([guid]::NewGuid().ToString('N').Substring(0, 8))"

    # Clean up orphaned tasks from prior runs (crashed/aborted installs).
    try {
        Get-ScheduledTask -TaskPath '\' -ErrorAction SilentlyContinue |
            Where-Object { $_.TaskName -like 'ConstructProvision-*' -and $_.State -ne 'Running' } |
            ForEach-Object {
                Unregister-ScheduledTask -TaskName $_.TaskName -Confirm:$false -ErrorAction SilentlyContinue
            }
    } catch { }

    # ── Per-run IPC directory ────────────────────────────────────────────────────
    # The elevated parent's $env:TEMP is inaccessible to a different desktop user
    # (over-the-shoulder UAC). Use a GUID-named subdirectory under ProgramData
    # with an explicit ACL granting only SYSTEM, Administrators, and the resolved
    # desktop user — never a predictable or world-writable path.
    $ipcBase = if ($env:ProgramData) { $env:ProgramData } else { Join-Path $env:SystemDrive 'ProgramData' }
    $ipcDir  = Join-Path $ipcBase "construct-provision-$([guid]::NewGuid().ToString('N'))"
    try {
        New-Item -ItemType Directory -Path $ipcDir -Force -ErrorAction Stop | Out-Null
        $acl     = New-Object System.Security.AccessControl.DirectorySecurity
        $acl.SetAccessRuleProtection($true, $false)
        $inherit = [System.Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
        $none    = [System.Security.AccessControl.PropagationFlags]::None
        $allow   = [System.Security.AccessControl.AccessControlType]::Allow
        $full    = [System.Security.AccessControl.FileSystemRights]::FullControl
        $modify  = [System.Security.AccessControl.FileSystemRights]::Modify
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            ([System.Security.Principal.SecurityIdentifier]'S-1-5-32-544'), $full, $inherit, $none, $allow)))
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            ([System.Security.Principal.SecurityIdentifier]'S-1-5-18'), $full, $inherit, $none, $allow)))
        $desktopNT = New-Object System.Security.Principal.NTAccount($desktopUser)
        $acl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
            $desktopNT, $modify, $inherit, $none, $allow)))
        Set-Acl -Path $ipcDir -AclObject $acl
    } catch {
        Write-Warning "Could not create IPC directory: $($_.Exception.Message)"
        Write-Warning "Falling back to inline elevated provisioning (config-sync may use the elevated profile)."
        Remove-Item -LiteralPath $ipcDir -Recurse -Force -ErrorAction SilentlyContinue
        & $ScriptPath @ProvisionParams
        return
    }

    $resultFile = Join-Path $ipcDir "result.json"
    $readyFile  = Join-Path $ipcDir "ready"

    # ── Register + start the one-shot scheduled task ─────────────────────────────
    # 'Interactive' (the ScheduledTasks-cmdlet enum name for the COM API's
    # InteractiveToken) runs under the desktop session's standard token;
    # RunLevel Limited ensures no elevation. The task opens a visible powershell
    # console in the user's desktop session.
    try {
        $encodedCmd = Build-ProvisionEncodedCommand `
            -ScriptPath $ScriptPath -Params $ProvisionParams `
            -ResultFile $resultFile -ReadyFile $readyFile

        $action    = New-ScheduledTaskAction -Execute 'powershell.exe' `
                        -Argument "-NoProfile -ExecutionPolicy Bypass -EncodedCommand $encodedCmd"
        $principal = New-ScheduledTaskPrincipal `
                        -UserId $desktopUser `
                        -LogonType Interactive `
                        -RunLevel Limited
        $settings  = New-ScheduledTaskSettingsSet `
                        -AllowStartIfOnBatteries `
                        -DontStopIfGoingOnBatteries `
                        -ExecutionTimeLimit (New-TimeSpan -Seconds $TimeoutSeconds)

        Register-ScheduledTask -TaskName $taskName `
            -Action $action -Principal $principal -Settings $settings `
            -ErrorAction Stop | Out-Null
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
        Write-Ok "Provisioning started in a de-elevated console"
    } catch {
        # Fallback: run provision inline elevated, with a loud warning. This keeps
        # the VM from sitting created-but-unprovisioned when the task mechanism fails
        # (e.g. explorer unavailable, ABR session quirk, Task Scheduler disabled).
        # Start-ScheduledTask can have launched/queued an instance even if the command
        # reports an error — stop it and confirm it's dead before fallback.
        Write-Warning "Could not start de-elevated provisioning: $($_.Exception.Message)"
        try { Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue } catch { }
        $taskConfirmedStopped = $false
        $startStopDeadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $startStopDeadline) {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            if (-not $task -or ($task.State -ne 'Running' -and $task.State -ne 'Queued')) {
                $taskConfirmedStopped = $true; break
            }
            Start-Sleep -Seconds 1
        }
        if (-not $taskConfirmedStopped) {
            # Preserve the task and IPC directory for diagnosis. Set globals so
            # Wait-Exit renders the persistent final screen before throwing.
            $global:ConstructProvisionHadErrors = $true
            $global:ConstructProvisionErrors = @()
            $global:ConstructProvisionFailureMessage = "De-elevated task '$taskName' may still be running after a failed start -- cannot safely fall back. Check Task Scheduler."
            throw $global:ConstructProvisionFailureMessage
        }
        try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue } catch { }
        Remove-Item -LiteralPath $ipcDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Warning "Falling back to inline elevated provisioning (config-sync may use the elevated profile)."
        & $ScriptPath @ProvisionParams
        return
    }

    # ── Start handshake: wait for Provision-AgentVM.ps1 to confirm it started ────
    # The ready file is written atomically (temp+rename) by Provision-AgentVM.ps1
    # itself AFTER its param block binds successfully. If this file never appears,
    # the bootstrap or script entry failed — stop the task, verify it's no longer
    # running, then fall back to inline elevated. Once the ready file arrives,
    # provisioning is about to start and a fallback would risk double-provisioning.
    $childPid       = $null
    $childStartTime = $null
    $handshakeOk    = $false
    $handshakeLimit = 30   # seconds
    $handshakeStart = Get-Date

    while (((Get-Date) - $handshakeStart).TotalSeconds -lt $handshakeLimit) {
        if (Test-Path -LiteralPath $readyFile) {
            try {
                $raw = (Get-Content -LiteralPath $readyFile -Raw -ErrorAction Stop).Trim()
                if ($raw -match '^\d+$') {
                    $childPid = [int]$raw
                    # Capture the child's start time for identity verification
                    # before any later Stop-Process (guards against PID recycling).
                    $childProc = Get-Process -Id $childPid -ErrorAction SilentlyContinue
                    if ($childProc) { $childStartTime = $childProc.StartTime }
                    $handshakeOk = $true
                    break
                }
            } catch { }
        }
        Start-Sleep -Seconds 1
    }

    if (-not $handshakeOk) {
        # Bootstrap or entry failure: Provision-AgentVM.ps1 never started.
        # Stop the task and CONFIRM it's no longer running before fallback —
        # if it can't be confirmed stopped, throw instead of risking two provisioners.
        Write-Warning "De-elevated child did not start within ${handshakeLimit}s -- bootstrap may have failed."
        try { Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue } catch { }
        $taskConfirmedStopped = $false
        $stopDeadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $stopDeadline) {
            $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
            if (-not $task -or ($task.State -ne 'Running' -and $task.State -ne 'Queued')) {
                $taskConfirmedStopped = $true; break
            }
            Start-Sleep -Seconds 1
        }
        if (-not $taskConfirmedStopped) {
            # Preserve the task and IPC directory for diagnosis. Set globals so
            # Wait-Exit renders the persistent final screen before throwing.
            $global:ConstructProvisionHadErrors = $true
            $global:ConstructProvisionErrors = @()
            $global:ConstructProvisionFailureMessage = "De-elevated task '$taskName' could not be confirmed stopped -- cannot safely fall back. Check Task Scheduler."
            throw $global:ConstructProvisionFailureMessage
        }
        try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue } catch { }
        Remove-Item -LiteralPath $ipcDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Warning "Falling back to inline elevated provisioning (config-sync may use the elevated profile)."
        & $ScriptPath @ProvisionParams
        return
    }

    # ── Poll for completion ──────────────────────────────────────────────────────
    # Primary signal: the result file appears (the child's finally block writes it
    # atomically via temp + rename). Secondary: the child PID is gone (crash).
    Write-Host "    Waiting for de-elevated provisioning to finish (this can take several minutes)..." -ForegroundColor DarkGray
    $deadline  = (Get-Date).AddSeconds($TimeoutSeconds)
    $pollSec   = 5
    $completed = $false

    while ((Get-Date) -lt $deadline) {
        # Check the result file first (the primary signal).
        if (Test-Path -LiteralPath $resultFile) {
            try {
                $sz = (Get-Item -LiteralPath $resultFile -ErrorAction Stop).Length
                if ($sz -gt 0) { $completed = $true; break }
            } catch { }
        }

        # Liveness: is the child process still alive?
        $childAlive = $false
        if ($childPid) {
            $proc = Get-Process -Id $childPid -ErrorAction SilentlyContinue
            $childAlive = ($null -ne $proc)
        }
        if (-not $childAlive) {
            # Child process is gone — grace period for the result file to flush.
            Start-Sleep -Seconds 3
            if (Test-Path -LiteralPath $resultFile) {
                try {
                    $sz = (Get-Item -LiteralPath $resultFile -ErrorAction Stop).Length
                    if ($sz -gt 0) { $completed = $true }
                } catch { }
            }
            break
        }

        Start-Sleep -Seconds $pollSec
    }

    # On timeout: stop the child so it doesn't continue provisioning after the
    # parent has given up. Verify process identity (PID + start time) to guard
    # against PID recycling before killing.
    if (-not $completed -and $childPid) {
        $proc = Get-Process -Id $childPid -ErrorAction SilentlyContinue
        if ($proc -and $proc.ProcessName -match '^powershell' -and
            $childStartTime -and $proc.StartTime -eq $childStartTime) {
            Write-Warning "Stopping timed-out de-elevated child (PID $childPid)."
            Stop-Process -Id $childPid -Force -ErrorAction SilentlyContinue
        }
    }

    # ── Read and integrate the result ────────────────────────────────────────────
    $result = $null
    if ($completed) {
        try {
            $resultJson = Get-Content -LiteralPath $resultFile -Raw -ErrorAction Stop
            $result     = ConvertFrom-Json $resultJson
        } catch {
            $result = $null
        }
    }

    # Wait for the child process to actually exit before cleanup — the child
    # publishes the result then exits without pausing (no Read-Host), so this
    # is a short bounded wait, not a user-interactive one.
    $childExited = $true
    if ($childPid) {
        $childExited = $false
        $exitDeadline = (Get-Date).AddSeconds(10)
        while ((Get-Date) -lt $exitDeadline) {
            $proc = Get-Process -Id $childPid -ErrorAction SilentlyContinue
            if (-not $proc) { $childExited = $true; break }
            Start-Sleep -Milliseconds 500
        }
        if (-not $childExited) {
            # Child is still alive after result publication — stop it (identity-
            # verified) and confirm termination before cleanup.
            $proc = Get-Process -Id $childPid -ErrorAction SilentlyContinue
            if ($proc -and $proc.ProcessName -match '^powershell' -and
                $childStartTime -and $proc.StartTime -eq $childStartTime) {
                Write-Warning "De-elevated child (PID $childPid) did not exit after result publication -- stopping it."
                Stop-Process -Id $childPid -Force -ErrorAction SilentlyContinue
                Start-Sleep -Seconds 2
                $proc = Get-Process -Id $childPid -ErrorAction SilentlyContinue
                $childExited = (-not $proc)
            }
        }
        if (-not $childExited) {
            # Cannot confirm termination — preserve task/IPC for diagnosis.
            # Set globals so Wait-Exit renders the persistent final screen.
            $global:ConstructProvisionHadErrors = $true
            $global:ConstructProvisionErrors = @()
            $global:ConstructProvisionFailureMessage = "De-elevated child (PID $childPid) could not be confirmed terminated. Check Task Scheduler for '$taskName'."
            throw $global:ConstructProvisionFailureMessage
        }
    }

    # ── Clean up the scheduled task + IPC directory ──────────────────────────────
    try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue } catch { }
    Remove-Item -LiteralPath $ipcDir -Recurse -Force -ErrorAction SilentlyContinue

    if (-not $completed -or -not $result) {
        $global:ConstructProvisionHadErrors = $true
        $msg = if ($completed) {
            "De-elevated provisioning finished but its result file could not be read."
        } else {
            "De-elevated provisioning did not complete (the child process may have crashed or timed out)."
        }
        $global:ConstructProvisionFailureMessage = $msg
        throw $msg
    }

    # Integrate: set the same globals an inline call would, so Wait-Exit works.
    # Derive HadErrors from ExitCode — never trust the child's HadErrors field
    # since an inconsistent value can suppress Wait-Exit or render a false failure.
    $global:ConstructProvisionFailureMessage = [string]$result.FailureMessage
    $global:ConstructProvisionErrors         = @()

    if ($result.ExitCode -eq 0) {
        $global:ConstructProvisionHadErrors = $false
    } else {
        $global:ConstructProvisionHadErrors = $true
    }

    # Parse the raw sentinel block for end-to-end fidelity — the same lines
    # provision.sh emitted, processed by ConvertFrom-ConstructProvisionResult,
    # preserving marker validity and the 3713991 prompt data.
    # Validate: exit 0/3 requires a valid sentinel block whose error count
    # matches declared. A missing/malformed/count-mismatched sentinel is a
    # critical failure — the prompt data cannot be trusted.
    if ($result.ExitCode -eq 0 -or $result.ExitCode -eq 3) {
        $parsed = $null
        if ($result.RawSentinel) {
            $parsed = ConvertFrom-ConstructProvisionResult -Lines @($result.RawSentinel)
        }
        if (-not $parsed -or -not $parsed.IsValid) {
            $global:ConstructProvisionHadErrors = $true
            $global:ConstructProvisionFailureMessage = "De-elevated provisioning exited $($result.ExitCode) but the result sentinel is missing or malformed."
            throw $global:ConstructProvisionFailureMessage
        }
        if ($result.ExitCode -eq 0 -and $parsed.ErrorCount -ne 0) {
            $global:ConstructProvisionHadErrors = $true
            $global:ConstructProvisionFailureMessage = "De-elevated provisioning exited 0 but reported $($parsed.ErrorCount) failure(s)."
            throw $global:ConstructProvisionFailureMessage
        }
        if ($result.ExitCode -eq 3 -and $parsed.ErrorCount -eq 0) {
            $global:ConstructProvisionHadErrors = $true
            $global:ConstructProvisionFailureMessage = "De-elevated provisioning exited 3 but reported no optional failures."
            throw $global:ConstructProvisionFailureMessage
        }
        $global:ConstructProvisionErrors = @($parsed.Errors)
    }

    if ($result.ExitCode -ne 0 -and $result.ExitCode -ne 3) {
        $global:ConstructProvisionFailureMessage = [string]$result.FailureMessage
        throw "De-elevated provisioning failed: $($result.FailureMessage)"
    }
    if ($result.ExitCode -eq 3) {
        Write-Host ("    De-elevated provisioning completed with {0} optional error(s)." -f @($global:ConstructProvisionErrors).Count) -ForegroundColor Yellow
    } else {
        Write-Ok "De-elevated provisioning completed cleanly"
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

function Resolve-MarkerSource {
    <#
        Decide which repo/ref to record in the installed-commit marker, treating them
        as a SOURCE PAIR. Rules:
          - If EITHER was supplied explicitly (an install/create path), record the full
            EFFECTIVE pair as given -- the default filled the other side, and that pair
            is what was actually downloaded/provisioned, so we never mix an explicit
            repo with a stale ref (or vice-versa).
          - Only when NEITHER was supplied (a truly param-less reprovision) do we
            preserve whatever the settings file already records, so a reprovision
            refreshes installedCommit without resetting the source. Missing existing
            values fall back to the passed defaults.
        Pure. Returns @{ Repo = <string>; Ref = <string> }.
    #>
    [CmdletBinding()]
    param(
        [string]$Repo, [string]$Ref,
        [bool]$RepoSupplied, [bool]$RefSupplied,
        [string]$ExistingRepo = "", [string]$ExistingRef = ""
    )
    if ($RepoSupplied -or $RefSupplied) {
        return @{ Repo = $Repo; Ref = $Ref }
    }
    return @{
        Repo = $(if ($ExistingRepo) { $ExistingRepo } else { $Repo })
        Ref  = $(if ($ExistingRef)  { $ExistingRef }  else { $Ref })
    }
}
function Set-ConstructInstalledMarker {
    <#
        Record which Construct repo/ref/commit is installed on this host, so the
        control panel's update check has a base to diff against. The SHA fetch is
        best-effort, and the (repo, ref, installedCommit) TUPLE is written ATOMICALLY:
          * SHA fetched             -> write all three (a fresh, self-consistent marker).
          * fetch failed, prior
            commit exists           -> write NOTHING; preserve the whole prior tuple.
          * fetch failed, no prior
            commit                  -> record repo/ref only (no commit yet).
        Why atomic: the panel diffs `compare(installedCommit...ref)` on `repo`, so a
        preserved commit must never be paired with a newly-switched repo/ref (that
        yields a permanent 404/null check). And a failed fetch must never blank a good
        installedCommit -- writing "" was a real bug: one transient GitHub blip during
        any (re)install/reprovision permanently hid the update banner (checkConstruct
        treats "" as "no marker"), and only the -- now hidden -- Update button
        re-records it. Returns the SHA ("" on failure). Never throws.
        `-CommitFetcher` injects the SHA lookup for tests (default: GitHub API).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$Repo,
        [Parameter(Mandatory)][string]$Ref,
        [scriptblock]$CommitFetcher
    )
    $sha = ""
    try {
        if ($CommitFetcher) {
            $sha = [string](& $CommitFetcher $Repo $Ref)
        } else {
            # -TimeoutSec so a slow/unreachable GitHub can't stall the install (this now
            # runs on the fresh-install path too, before Auto-Install launches).
            $sha = (Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/commits/$Ref" `
                      -Headers @{ "User-Agent" = "construct-control-panel" } -UseBasicParsing -TimeoutSec 20).sha
        }
    } catch {
        Write-Host "    (couldn't fetch the commit id for the update marker: $($_.Exception.Message))" -ForegroundColor DarkGray
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

function Set-ConstructProvisionedMarker {
    <#
        Record which commit the VM was LAST PROVISIONED with, in a SEPARATE key
        (provisionedCommit) from installedCommit. installedCommit tracks the installed
        Construct (extension + scripts, set by install/update); provisionedCommit tracks
        what the VM actually ran with. The panel flags the Provision button when they
        differ (VM behind the installed Construct). We mirror the CURRENT installedCommit
        -- i.e. the version of the scripts doing this provision -- rather than a fresh
        fetch, so it can't claim a newer commit than what's actually installed. Best-effort;
        never throws. Returns the recorded sha ("" if no installedCommit is known yet).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Dir)
    $sha = ""
    try {
        $existing = Read-ConstructSettings -Dir $Dir
        if ($existing -and $existing.installedCommit) { $sha = [string]$existing.installedCommit }
    } catch { }
    try {
        Save-ConstructSettings -Dir $Dir -Values @{ provisionedCommit = $sha }
    } catch {
        Write-Warning "Could not record the provisioned marker: $($_.Exception.Message)"
    }
    return $sha
}

# ── Config-sync v2 engine (docs/config-sync.md) ─────────────────────────────
# A host-side sync engine that keeps project profiles in a local git repo
# (%LOCALAPPDATA%\The-Construct\config) and synchronises them with the VM's
# /opt/construct/projects store over SSH. Mirrors the JS engine in
# extension/src/configsync.js -- same layout, same branch scheme, same merge
# semantics -- so both surfaces produce identical repos. All functions are
# Windows PowerShell 5.1-compatible (#Requires -Version 5.1 discipline: no
# ternary, no ??, no Test-Json, no -AsHashtable, no PS6+ syntax).

# ── Reserved names ───────────────────────────────────────────────────────────
# 'default' is the shipped read-only seed, 'project.schema' is the schema file.
# Case-insensitive, trimmed. Matches JS isReservedProfileName exactly.
$script:RESERVED_PROFILE_NAMES = @("default", "project.schema")

# ── Config dir ───────────────────────────────────────────────────────────────

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

function Initialize-ConstructConfigStore {
    <#
        Ensure the config tree exists (projects/, manifest/, bases/). Returns the
        config dir path. ($ScriptsDir is kept for call-site compatibility; the
        legacy pre-v2 migration that used it was removed -- it re-copied stale
        profiles from the shipped projects/ folder whenever a file vanished from
        the config dir, resurrecting deleted/outdated profiles forever.)
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptsDir)

    $configDir = Get-ConstructConfigDir
    $projDir   = Join-Path $configDir "projects"
    $manifDir  = Join-Path $configDir "manifest"
    $basesDir  = Join-Path $configDir "bases"
    foreach ($d in @($projDir, $manifDir, $basesDir)) {
        if (-not (Test-Path -LiteralPath $d)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
        }
    }
    Repair-ConstructConfigOwnership -Path $configDir

    return $configDir
}

function Repair-ConstructConfigOwnership {
    <#
        git refuses to use a repo whose top-level folder is owned by another
        account ("fatal: detected dubious ownership"), which silently breaks
        every sync tick -- this engine's AND the VS Code extension's, which runs
        non-elevated. That state arises when the config dir was created by an
        elevated installer (owner becomes BUILTIN\Administrators) or by a
        different admin account. Repair: make the invoking user the owner
        (recursively, so a pre-existing .git is covered too) and grant them
        FullControl. Best-effort -- on failure warn with the manual command and
        let provisioning continue; the sync engines report the precise git
        error if it still bites.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path)

    if ($env:OS -ne 'Windows_NT') { return }
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $owner = $null
    try {
        $me = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
        $owner = (Get-Acl -LiteralPath $Path).Owner
        if ($owner -and ($owner -ieq $me)) { return }
        & icacls $Path /setowner $me /T /C /Q 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls /setowner exited $LASTEXITCODE" }
        & icacls $Path /grant "${me}:(OI)(CI)F" /Q 2>&1 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "icacls /grant exited $LASTEXITCODE" }
        Write-Verbose "Repaired config dir ownership: $Path (was: $owner, now: $me)"
    } catch {
        Write-Warning ("Config dir '$Path' is owned by '$owner', not '$env:USERNAME', and could not be repaired ($($_.Exception.Message)). " +
            "git will refuse to sync it (""dubious ownership""). Fix manually from an elevated prompt: icacls ""$Path"" /setowner ""$env:USERNAME"" /T")
    }
}

function Wait-VmSshReady {
    <#
        Poll the VM until SSH is genuinely back after the end-of-provisioning
        reboot. Used before opening VS Code Remote-SSH at the end of an
        install/reinstall.

        The reboot is backgrounded on the VM (sleep 3; reboot), so the OLD
        boot's sshd keeps answering for several seconds after provisioning
        returns -- any probe that only asks "is the port up?" can pass on the
        boot that is about to vanish, and VS Code then opens into a connection
        error. So, when -SshTarget is given, a probe only counts as ready with
        PROOF the VM restarted: an SSH exec reads the VM's current boot id and
        uptime, and readiness requires the boot id to differ from
        -BaselineBootId (captured pre-reboot), or -- when no baseline could be
        captured -- an uptime under $FreshBootMaxUptimeSec (an install keeps
        the VM up far longer than that before the final reboot).

        The SSH probe is NOT gated behind a TCP pre-check: the TCP probe dials
        -VmHost directly while ssh resolves the alias's HostName from
        ~/.ssh/config (the FQDN the rest of the tooling uses), and a hostname
        that only resolves on one of those paths would deadlock the wait --
        field-observed: the short name stopped resolving after the reboot, the
        TCP gate never opened, the ssh probe never ran, and the wait burned
        its full timeout while VS Code (using the alias) connected fine. ssh
        enforces its own ConnectTimeout, so the extra TCP dial bought nothing.

        Without -SshTarget, falls back to the TCP-only stability window:
        $StableProbes consecutive successful connects. Returns $true when
        ready, $false on timeout. Never throws; no output (caller narrates).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$VmHost,
        [int]$Port = 22,
        [int]$TimeoutSec = 300,
        [int]$ProbeIntervalSec = 2,
        [int]$StableProbes = 3,
        # ~/.ssh/config Host alias (or user@host) for the restart-proof probe.
        [string]$SshTarget = "",
        # Pre-reboot /proc/sys/kernel/random/boot_id; readiness = it changed.
        [string]$BaselineBootId = "",
        # No-baseline fallback: a boot id we can't compare still proves a fresh
        # boot when the VM's uptime is under this many seconds.
        [int]$FreshBootMaxUptimeSec = 600
    )
    $deadline = (Get-Date).AddSeconds($TimeoutSec)
    $streak = 0
    while ((Get-Date) -lt $deadline) {
        if ($SshTarget) {
            # Restart proof over SSH -- attempted every interval, no TCP gate
            # (see above). accept-new: the post-reboot host key may not be in
            # known_hosts yet (Set-HostSshConfig's accept probe can race the
            # way down). EAP pinned to Continue so 5.1 doesn't turn native
            # stderr into a terminating NativeCommandError.
            $prevEAP = $ErrorActionPreference; $ErrorActionPreference = "Continue"
            try {
                $probe = @(& ssh -n -o BatchMode=yes -o StrictHostKeyChecking=accept-new -o ConnectTimeout=5 `
                             $SshTarget "cat /proc/sys/kernel/random/boot_id /proc/uptime" 2>$null)
            } catch { $probe = @() }
            finally { $ErrorActionPreference = $prevEAP }
            if ($LASTEXITCODE -eq 0 -and $probe.Count -ge 2) {
                $bootId = "$($probe[0])".Trim()
                $uptime = 0.0
                $null = [double]::TryParse(("$($probe[1])" -split '\s+')[0],
                    [System.Globalization.NumberStyles]::Float,
                    [System.Globalization.CultureInfo]::InvariantCulture, [ref]$uptime)
                if ($BaselineBootId) {
                    if ($bootId -and $bootId -ne $BaselineBootId) { return $true }
                } elseif ($uptime -gt 0 -and $uptime -lt $FreshBootMaxUptimeSec) {
                    return $true
                }
            }
        } else {
            $up = $false
            $client = $null
            try {
                $client = New-Object System.Net.Sockets.TcpClient
                $async = $client.BeginConnect($VmHost, $Port, $null, $null)
                if ($async.AsyncWaitHandle.WaitOne(2000) -and $client.Connected) { $up = $true }
            } catch { $up = $false }
            finally { if ($client) { try { $client.Close() } catch { } } }
            if ($up) {
                $streak++
                if ($streak -ge $StableProbes) { return $true }
            } else {
                $streak = 0
            }
        }
        Start-Sleep -Seconds $ProbeIntervalSec
    }
    return $false
}

function Get-ConstructConfigProjectsDir {
    <#
        Returns the live projects directory: config\projects when it exists
        (initializing if needed), legacy fallback otherwise.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptsDir)

    $configDir = Get-ConstructConfigDir
    $projDir   = Join-Path $configDir "projects"
    if (Test-Path -LiteralPath $projDir) { return $projDir }
    # Fallback: try initializing, then check again.
    $null = Initialize-ConstructConfigStore -ScriptsDir $ScriptsDir
    if (Test-Path -LiteralPath $projDir) { return $projDir }
    # Last resort: the legacy shipped projects/ folder.
    $legacy = Join-Path $ScriptsDir "projects"
    if (Test-Path -LiteralPath $legacy) { return $legacy }
    return $projDir
}

# ── Git availability ─────────────────────────────────────────────────────────

function Test-ConstructGitAvailable {
    <# Returns $true when git.exe is on PATH. #>
    return ($null -ne (Get-Command git -ErrorAction SilentlyContinue))
}

function Ensure-ConstructGit {
    <#
        Make sure git is installed on the host. Follows the Ensure-Ffmpeg winget
        pattern: check PATH, else try winget --id Git.Git, else print a manual
        hint. -AutoMode attempts silently and returns $false on failure (caller
        aborts loudly). Never throws.
    #>
    [CmdletBinding()]
    param([switch]$AutoMode)

    if (Test-ConstructGitAvailable) { return $true }

    if (Get-Command winget -ErrorAction SilentlyContinue) {
        if (-not $AutoMode) {
            Write-Host "==> Installing git (winget --id Git.Git)..." -ForegroundColor Cyan
        }
        try {
            & winget install --id Git.Git -e --silent `
                --accept-package-agreements --accept-source-agreements --scope user 2>&1 | Out-Null
        } catch {
            if (-not $AutoMode) {
                Write-Warning "winget could not install git: $($_.Exception.Message)"
            }
        }
        # Refresh PATH for this session (winget updates PATH for new sessions).
        $machPath = [Environment]::GetEnvironmentVariable("PATH", "Machine")
        $userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
        if ($machPath -and $userPath) { $env:PATH = "$userPath;$machPath" }
        if (Test-ConstructGitAvailable) {
            if (-not $AutoMode) { Write-Host "    git installed." -ForegroundColor Green }
            return $true
        }
    }

    if (-not $AutoMode) {
        Write-Warning "git is not installed and could not be installed automatically."
        Write-Host "    Install it from https://git-scm.com/ or run:  winget install --id Git.Git" -ForegroundColor DarkGray
    }
    return $false
}

# ── Git repo initialisation ──────────────────────────────────────────────────

function Set-ConstructConfigRepoHardening {
    <#
        .SYNOPSIS
        Make the config repo's commits hermetic and line-ending-stable (idempotent).
        The config dir is a machine-local bookkeeping repo created by `git init`, so
        it inherits the user's GLOBAL git settings. commit.gpgsign=true (a verified-
        commits setup) or a failing global core.hooksPath makes every headless
        `git commit` fail, leaving a cleanly auto-merged merge uncommitted -- the
        phantom "unresolved merge" the panel reports. The $gitArgs prefix already
        disables both per-invocation for the ENGINE; this persists them repo-locally
        so the user's own commits (e.g. resolving in VS Code) behave the same, and
        pins LF so canonical-LF profiles never round-trip through CRLF. Also ignores
        the machine-local bookkeeping files (.gitattributes + the .migrated sentinel)
        via .git/info/exclude, so they don't clutter `git status` and -- crucially --
        can't trip git's untracked-overwrite guard, which would make `git merge`
        refuse and surface as a phantom "merge conflict". Best-effort.
    #>
    param([Parameter(Mandatory)][string]$ConfigDir)
    $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    try {
        & git -C $ConfigDir config commit.gpgsign false 2>$null | Out-Null
        # Empty hooksPath bypasses any inherited (global) hooks so the user's own
        # manual commits in this repo can't be broken by a global pre-commit hook.
        & git -C $ConfigDir config core.hooksPath "" 2>$null | Out-Null
        & git -C $ConfigDir config core.autocrlf false 2>$null | Out-Null
        $ga = Join-Path $ConfigDir ".gitattributes"
        if (-not (Test-Path -LiteralPath $ga)) {
            [System.IO.File]::WriteAllText($ga, "* text=auto eol=lf`n")
        }
        # Ignore the local bookkeeping files (idempotent append).
        $exDir = Join-Path (Join-Path $ConfigDir ".git") "info"
        $exFile = Join-Path $exDir "exclude"
        $cur = ""
        if (Test-Path -LiteralPath $exFile) { $cur = [System.IO.File]::ReadAllText($exFile) }
        $have = @($cur -split "`r?`n" | ForEach-Object { $_.Trim() })
        $missing = @(@(".gitattributes", ".migrated", ".sync.lock") | Where-Object { $have -notcontains $_ })
        if ($missing.Count -gt 0) {
            if (-not (Test-Path -LiteralPath $exDir)) { New-Item -ItemType Directory -Path $exDir -Force | Out-Null }
            $prefix = ""
            if ($cur -and -not $cur.EndsWith("`n")) { $prefix = "`n" }
            [System.IO.File]::AppendAllText($exFile, $prefix + ($missing -join "`n") + "`n")
        }
    } catch { }
    $ErrorActionPreference = $prev
}

function Initialize-ConstructConfigRepo {
    <#
        Lazy git init per D1: create a git repo in the config dir with main + vm
        branches if it does not exist yet. Optionally seeds from -SeedDir (shipped
        projects). Returns $true when the repo is ready. Idempotent.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [string]$SeedDir
    )

    if (-not (Test-ConstructGitAvailable)) { return $false }

    $gitDir = Join-Path $ConfigDir ".git"
    if (Test-Path -LiteralPath $gitDir) {
        # git refuses EVERY operation on a repo it distrusts -- classically
        # "dubious ownership" when the config dir belongs to another account
        # (a different-admin elevated console). This function used to swallow
        # that and return $true, so the sync tick "ran" while each git call
        # inside it silently no-opped. Probe first: rev-parse --git-dir
        # succeeds on any healthy repo regardless of branch state, so a
        # failure here means the repo is unusable -- fail init loudly with
        # git's own diagnostic so callers (and the provisioning guard) see
        # repo-init-failed instead of a clean-looking no-op tick.
        # git's diagnostic goes to a temp FILE, not 2>&1: Windows PowerShell 5.1
        # turns native stderr into ErrorRecords, and under SilentlyContinue the
        # merged records are discarded -- the message would vanish exactly when
        # it is needed. File redirection is applied regardless of EAP.
        $probeErrFile = [System.IO.Path]::GetTempFileName()
        try {
            $probeCode = 0
            # 'Continue', not 'SilentlyContinue': 5.1 discards native-stderr
            # records under SilentlyContinue even when 2>-redirected (proven in
            # the field by an empty diagnostic); the redirect keeps the console
            # clean either way.
            $prev = $ErrorActionPreference; $ErrorActionPreference = "Continue"
            try {
                $null = & git -C $ConfigDir rev-parse --git-dir 2>$probeErrFile
                $probeCode = $LASTEXITCODE
            } finally {
                $ErrorActionPreference = $prev
            }
            if ($probeCode -ne 0) {
                $probeText = ""
                try { $probeText = ((Get-Content -LiteralPath $probeErrFile -ErrorAction SilentlyContinue) -join " ").Trim() } catch { }
                Write-Warning "Config repo at $ConfigDir is unusable (git rev-parse exited $probeCode)$(if ($probeText) { ": $probeText" })"
                return $false
            }
        } finally {
            Remove-Item -LiteralPath $probeErrFile -ErrorAction SilentlyContinue
        }
        # Ensure the vm branch exists, and re-apply the repo-local hardening
        # every run so a repo created before this fix is repaired
        # (signing/hooks off, LF pinned).
        $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        try {
            $null = & git -C $ConfigDir rev-parse --verify refs/heads/vm 2>$null
            if ($LASTEXITCODE -ne 0) {
                & git -C $ConfigDir branch vm 2>$null | Out-Null
            }
        } catch { }
        Set-ConstructConfigRepoHardening -ConfigDir $ConfigDir
        $ErrorActionPreference = $prev
        return $true
    }

    # Ensure directories.
    foreach ($d in @("projects","manifest","bases")) {
        $p = Join-Path $ConfigDir $d
        if (-not (Test-Path -LiteralPath $p)) {
            New-Item -ItemType Directory -Path $p -Force | Out-Null
        }
    }

    # Seed from shipped projects if given.
    if ($SeedDir -and (Test-Path -LiteralPath $SeedDir)) {
        $projDir = Join-Path $ConfigDir "projects"
        foreach ($f in @(Get-ChildItem -LiteralPath $SeedDir -Filter *.json -File -ErrorAction SilentlyContinue)) {
            $lower = $f.BaseName.ToLowerInvariant()
            if ($script:RESERVED_PROFILE_NAMES -contains $lower) { continue }
            if ($f.BaseName -like '*.sample') { continue }
            $dest = Join-Path $projDir $f.Name
            if (-not (Test-Path -LiteralPath $dest)) {
                Copy-Item -LiteralPath $f.FullName -Destination $dest -Force
            }
        }
    }

    $gitArgs = @("-c", "user.name=The Construct", "-c", "user.email=construct@construct.local", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=")

    $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    try {
        & git -C $ConfigDir init 2>$null | Out-Null
        # Harden BEFORE the initial add so core.autocrlf=false is in effect and the
        # .gitattributes is versioned by the initial commit.
        Set-ConstructConfigRepoHardening -ConfigDir $ConfigDir
        & git -C $ConfigDir @gitArgs add -A 2>$null | Out-Null
        # Exclude reserved names from the initial commit (D1/D5: NEVER
        # default.json / project.schema.json in the repo).
        foreach ($rn in $script:RESERVED_PROFILE_NAMES) {
            & git -C $ConfigDir reset HEAD -- "projects/$rn.json" 2>$null | Out-Null
        }
        & git -C $ConfigDir @gitArgs commit --allow-empty -m "initial config" 2>$null | Out-Null
        & git -C $ConfigDir branch -M main 2>$null | Out-Null
        & git -C $ConfigDir branch vm 2>$null | Out-Null
    } catch { }
    $ErrorActionPreference = $prev

    return (Test-Path -LiteralPath $gitDir)
}

# ── Canonical JSON serializer ────────────────────────────────────────────────
# Hand-rolled to byte-match JSON.stringify(sanitizeProfile(name,obj),null,2)+"\n"
# from extension/src/projects.js. ConvertTo-Json is NOT used (wrong indent,
# escaping and key order). Replicates JSON.stringify's escape rules exactly:
# \b \t \n \f \r shortcuts, other chars < 0x20 as \u00xx (lowercase hex, 4
# digits), non-ASCII emitted raw (UTF-8). Empty array [] and empty object {}
# are inline. 2-space indent, LF line endings, trailing newline.

function ConvertTo-ConstructJsonString {
    <# Escape a string value exactly as JSON.stringify does. Pure. #>
    [CmdletBinding()]
    param([AllowEmptyString()][AllowNull()][string]$Value)
    if ($null -eq $Value) { return '""' }
    $sb = New-Object System.Text.StringBuilder
    $null = $sb.Append('"')
    for ($i = 0; $i -lt $Value.Length; $i++) {
        $c = $Value[$i]
        $code = [int]$c
        if     ($c -eq '"')  { $null = $sb.Append('\"') }
        elseif ($c -eq '\')  { $null = $sb.Append('\\') }
        elseif ($code -eq 8)  { $null = $sb.Append('\b') }
        elseif ($code -eq 9)  { $null = $sb.Append('\t') }
        elseif ($code -eq 10) { $null = $sb.Append('\n') }
        elseif ($code -eq 12) { $null = $sb.Append('\f') }
        elseif ($code -eq 13) { $null = $sb.Append('\r') }
        elseif ($code -lt 32) {
            # Other control chars: \u00xx with lowercase hex, 4 digits.
            $null = $sb.Append(('\u{0:x4}' -f $code))
        }
        else {
            # Non-ASCII emitted raw (UTF-8) just like JSON.stringify.
            $null = $sb.Append($c)
        }
    }
    $null = $sb.Append('"')
    return $sb.ToString()
}

function ConvertTo-ConstructJsonValue {
    <#
        Serialize an arbitrary PS value to JSON matching JSON.stringify(v,null,2)
        output exactly. Recursive. $Depth is the current indent depth (number of
        2-space levels). Pure.
    #>
    [CmdletBinding()]
    param($Value, [int]$Depth = 0)

    if ($null -eq $Value) { return "null" }
    if ($Value -is [bool]) {
        return $(if ($Value) { "true" } else { "false" })
    }
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal] -or $Value -is [float]) {
        return "$Value"
    }
    if ($Value -is [string]) {
        return (ConvertTo-ConstructJsonString -Value $Value)
    }
    if ($Value -is [array] -or $Value -is [System.Collections.IList]) {
        $arr = @($Value)
        if ($arr.Count -eq 0) { return "[]" }
        $indent    = "  " * ($Depth + 1)
        $endIndent = "  " * $Depth
        $lines = New-Object System.Collections.Generic.List[string]
        for ($i = 0; $i -lt $arr.Count; $i++) {
            $val = ConvertTo-ConstructJsonValue -Value $arr[$i] -Depth ($Depth + 1)
            if ($i -lt ($arr.Count - 1)) {
                $lines.Add("$indent$val,")
            } else {
                $lines.Add("$indent$val")
            }
        }
        return ("[`n" + ($lines -join "`n") + "`n$endIndent]")
    }
    # Object (PSCustomObject or hashtable or ordered dictionary).
    $keys = $null
    if ($Value -is [System.Collections.IDictionary]) {
        $keys = @($Value.Keys)
    } elseif ($Value -is [pscustomobject]) {
        $keys = @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    } else {
        # Fallback: try PSObject properties.
        $keys = @($Value.PSObject.Properties | ForEach-Object { $_.Name })
    }
    if ($null -eq $keys -or $keys.Count -eq 0) { return "{}" }

    # Replicate JavaScript's JSON.stringify key ordering: integer-like keys
    # (canonical array-index strings: non-negative integers whose string form
    # round-trips, i.e. [string][uint32]$k -ceq $k and [uint32]$k -lt 2^32-1)
    # sort first in ascending numeric order, then remaining keys in insertion
    # order. This matters for free-form maps like sdks and tests.
    $intKeys = New-Object System.Collections.Generic.List[object]
    $strKeys = New-Object System.Collections.Generic.List[string]
    foreach ($k in $keys) {
        $asUint = 0
        $isArrayIndex = $false
        if ($k -match '^\d+$') {
            try {
                $asUint = [uint32]$k
                # Must be < 2^32-1 (4294967295) and round-trip.
                if ($asUint -lt 4294967295 -and [string]$asUint -ceq "$k") {
                    $isArrayIndex = $true
                }
            } catch { }
        }
        if ($isArrayIndex) {
            $intKeys.Add(@{ Key = $k; Num = $asUint })
        } else {
            $strKeys.Add($k)
        }
    }
    if ($intKeys.Count -gt 0) {
        $intKeys = @($intKeys | Sort-Object { $_.Num })
        $keys = @(@($intKeys | ForEach-Object { $_.Key }) + @($strKeys))
    }
    $indent    = "  " * ($Depth + 1)
    $endIndent = "  " * $Depth
    $lines = New-Object System.Collections.Generic.List[string]
    for ($i = 0; $i -lt $keys.Count; $i++) {
        $k = $keys[$i]
        $v = $null
        if ($Value -is [System.Collections.IDictionary]) {
            $v = $Value[$k]
        } else {
            $v = $Value.$k
        }
        $keyStr = ConvertTo-ConstructJsonString -Value $k
        $valStr = ConvertTo-ConstructJsonValue -Value $v -Depth ($Depth + 1)
        $sep = if ($i -lt ($keys.Count - 1)) { "," } else { "" }
        $lines.Add("$indent$keyStr`: $valStr$sep")
    }
    return ("{`n" + ($lines -join "`n") + "`n$endIndent}")
}

# ── Profile sanitisation (mirrors extension/src/projects.js sanitizeProfile) ─

function Invoke-ConstructSanitizeProfile {
    <#
        Coerce an arbitrary object into a schema-valid project profile, mirroring
        sanitizeProfile in extension/src/projects.js exactly: same key order, same
        coercion rules, same MCP type inference, same stripping of unknown keys.
        Returns the cleaned PSCustomObject, or $null if name is empty.
    #>
    [CmdletBinding()]
    param(
        [AllowEmptyString()][AllowNull()][string]$Name,
        $Object
    )

    $nm = if ($null -ne $Name) { "$Name".Trim() } else { "" }
    if (-not $nm) { return $null }
    $o = $Object
    if ($null -eq $o -or $o -isnot [pscustomobject]) {
        $o = New-Object pscustomobject
    }

    $MCP_AGENTS = @("claude", "claude-code", "codex", "opencode")

    # Helper: non-empty string after trim.
    $nonEmpty = { param($v) if ($v -is [string] -and $v.Trim() -ne "") { $v } else { $null } }

    # Helper: is plain object (PSCustomObject, not array/primitive).
    $isObj = { param($v) $null -ne $v -and $v -is [pscustomobject] }

    # sanitizeRepos
    $repos = @()
    $rawRepos = $null
    if ($o.PSObject.Properties.Name -contains 'repos') { $rawRepos = $o.repos }
    if ($rawRepos -is [array] -or $rawRepos -is [System.Collections.IList]) {
        foreach ($r in @($rawRepos)) {
            if ($null -eq $r -or -not (& $isObj $r)) { continue }
            $url = & $nonEmpty $r.url
            if (-not $url) { continue }
            $entry = [ordered]@{ url = $url }
            if ($r.PSObject.Properties.Name -contains 'directory') {
                $dir = & $nonEmpty $r.directory
                if ($dir) { $entry['directory'] = $dir }
            }
            $repos += [pscustomobject]$entry
        }
    }

    # sanitizeSdks (free-form map, author key order preserved)
    $sdks = [ordered]@{}
    $rawSdks = $null
    if ($o.PSObject.Properties.Name -contains 'sdks') { $rawSdks = $o.sdks }
    if (& $isObj $rawSdks) {
        foreach ($p in $rawSdks.PSObject.Properties) {
            $v = $p.Value
            if ($v -is [string]) {
                if ($v.Trim() -ne "") { $sdks[$p.Name] = $v }
            } elseif ($v -is [array] -or $v -is [System.Collections.IList]) {
                $arr = @($v | Where-Object { $_ -is [string] -and $_.Trim() -ne "" })
                if ($arr.Count -gt 0) { $sdks[$p.Name] = $arr }
            }
        }
    }

    # sanitizeMcp
    $mcp = @()
    $rawMcp = $null
    if ($o.PSObject.Properties.Name -contains 'mcp') { $rawMcp = $o.mcp }
    if ($rawMcp -is [array] -or $rawMcp -is [System.Collections.IList]) {
        foreach ($m in @($rawMcp)) {
            if ($null -eq $m -or -not (& $isObj $m)) { continue }
            $mcpName = & $nonEmpty $m.name
            if (-not $mcpName) { continue }

            # Type: explicit "stdio"/"http", else inferred. Case-sensitive to
            # match JS ===.
            $type = $null
            if ($m.PSObject.Properties.Name -contains 'type') {
                if ($m.type -ceq "stdio" -or $m.type -ceq "http") { $type = $m.type }
            }
            if (-not $type) {
                if (& $nonEmpty $m.command) { $type = "stdio" }
                elseif (& $nonEmpty $m.url) { $type = "http" }
            }

            $entry = [ordered]@{ name = $mcpName; type = $type }

            # strMap helper
            $strMap = {
                param($src)
                if ($null -eq $src -or -not (& $isObj $src)) { return $null }
                $map = [ordered]@{}
                foreach ($p2 in $src.PSObject.Properties) {
                    if ($p2.Value -is [string]) { $map[$p2.Name] = $p2.Value }
                }
                if ($map.Count -gt 0) { return [pscustomobject]$map }
                return $null
            }

            if ($type -eq "stdio") {
                $cmd = & $nonEmpty $m.command
                if (-not $cmd) { continue }
                $entry['command'] = $cmd
                if ($m.PSObject.Properties.Name -contains 'args' -and
                    ($m.args -is [array] -or $m.args -is [System.Collections.IList])) {
                    $args2 = @($m.args | Where-Object { $_ -is [string] })
                    if ($args2.Count -gt 0) { $entry['args'] = $args2 }
                }
                $env2 = & $strMap $m.env
                if ($null -ne $env2) { $entry['env'] = $env2 }
            } elseif ($type -eq "http") {
                $url2 = & $nonEmpty $m.url
                if (-not $url2) { continue }
                $entry['url'] = $url2
                $headers = & $strMap $m.headers
                if ($null -ne $headers) { $entry['headers'] = $headers }
                $bt = & $nonEmpty $m.bearerTokenEnvVar
                if ($bt) { $entry['bearerTokenEnvVar'] = $bt }
            } else {
                continue   # could not determine a valid server type
            }

            # agents (case-sensitive to match JS ===)
            if ($m.PSObject.Properties.Name -contains 'agents' -and
                ($m.agents -is [array] -or $m.agents -is [System.Collections.IList])) {
                $agents = @($m.agents | Where-Object { $MCP_AGENTS -ccontains $_ })
                if ($agents.Count -gt 0) { $entry['agents'] = $agents }
            }
            # enabled
            if ($m.PSObject.Properties.Name -contains 'enabled' -and $m.enabled -is [bool]) {
                $entry['enabled'] = $m.enabled
            }

            $mcp += [pscustomobject]$entry
        }
    }

    # sanitizeStringArray (hostPackages / provisionCommands): inline because
    # returning @() from a scriptblock via & unwraps to $null in PowerShell.
    $rawHP = $null; $rawPC = $null
    if ($o.PSObject.Properties.Name -contains 'hostPackages')      { $rawHP = $o.hostPackages }
    if ($o.PSObject.Properties.Name -contains 'provisionCommands') { $rawPC = $o.provisionCommands }
    # Force through @() to re-wrap scalars that PS 5.1 ConvertFrom-Json may
    # have unwrapped from single-element JSON arrays.
    if ($null -ne $rawHP) {
        $hostPackages = @(@($rawHP) | Where-Object { $_ -is [string] -and $_.Trim() -ne "" })
    } else {
        $hostPackages = @()
    }
    if ($null -ne $rawPC) {
        $provisionCommands = @(@($rawPC) | Where-Object { $_ -is [string] -and $_.Trim() -ne "" })
    } else {
        $provisionCommands = @()
    }

    # tests: opaque object, pass through if plain object, else empty.
    $tests = [ordered]@{}
    if ($o.PSObject.Properties.Name -contains 'tests') {
        $rawTests = $o.tests
        if (& $isObj $rawTests) {
            foreach ($tp in $rawTests.PSObject.Properties) {
                $tests[$tp.Name] = $tp.Value
            }
        }
    }

    # Build the output in FIXED key order (D3).
    $result = [ordered]@{
        name              = $nm
        repos             = $repos
        sdks              = [pscustomobject]$sdks
        mcp               = $mcp
        hostPackages      = $hostPackages
        provisionCommands = $provisionCommands
        tests             = [pscustomobject]$tests
    }
    return [pscustomobject]$result
}

function ConvertTo-ConstructCanonicalJson {
    <#
        THE canonical byte form of a profile: sanitize, then serialize with the
        hand-rolled JSON serializer. Byte-matches JS canonicalProfileJson for any
        valid profile. Returns the JSON string (with trailing LF), or $null when
        name is empty. LF line endings throughout (no CRLF).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name,
        [Parameter(Mandatory)]$Object
    )

    $clean = Invoke-ConstructSanitizeProfile -Name $Name -Object $Object
    if ($null -eq $clean) { return $null }
    $json = ConvertTo-ConstructJsonValue -Value $clean -Depth 0
    # Ensure LF line endings (no CRLF), trailing newline.
    $json = $json.Replace("`r`n", "`n")
    return "$json`n"
}

# ── Profile validation ───────────────────────────────────────────────────────
# Mirrors validateProfile in extension/src/projects.js exactly: same error
# messages, same strictness rules. Returns @{Ok=[bool]; Errors=[string[]]}.

function Test-ConstructProfile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Name,
        [Parameter(Mandatory)]$Object
    )

    $errors = New-Object System.Collections.Generic.List[string]
    $MCP_AGENTS = @("claude", "claude-code", "codex", "opencode")

    $str = { param($v) $v -is [string] -and $v.Trim() -ne "" }
    $isObj = { param($v) $null -ne $v -and $v -is [pscustomobject] }
    $strMapOk = { param($v) (& $isObj $v) -and @($v.PSObject.Properties | Where-Object { $_.Value -isnot [string] }).Count -eq 0 }

    if (-not (& $isObj $Object)) {
        return [pscustomobject]@{ Ok = $false; Errors = @("profile is not a JSON object") }
    }

    $KNOWN = @("name", "repos", "sdks", "mcp", "hostPackages", "provisionCommands", "tests")
    foreach ($p in $Object.PSObject.Properties) {
        if ($KNOWN -notcontains $p.Name) {
            $errors.Add("unknown key `"$($p.Name)`"")
        }
    }

    # name
    $hasName = $Object.PSObject.Properties.Name -contains 'name'
    if (-not $hasName -or -not (& $str $Object.name)) {
        $errors.Add('"name" must be a non-empty string')
    } else {
        $want = if ($null -ne $Name) { "$Name".Trim() } else { "" }
        if ($want -and $Object.name -ne $want) {
            $errors.Add("`"name`" is `"$($Object.name)`" but the profile file is `"$want`"")
        }
    }

    # repos
    if ($Object.PSObject.Properties.Name -contains 'repos') {
        $rp = $Object.repos
        if (-not ($rp -is [array] -or $rp -is [System.Collections.IList])) {
            $errors.Add('"repos" must be an array')
        } else {
            for ($i = 0; $i -lt @($rp).Count; $i++) {
                $r = @($rp)[$i]
                if (-not (& $isObj $r)) { $errors.Add("repos[$i] must be an object"); continue }
                foreach ($rk in $r.PSObject.Properties) {
                    if ($rk.Name -ne "url" -and $rk.Name -ne "directory") {
                        $errors.Add("repos[$i] unknown key `"$($rk.Name)`"")
                    }
                }
                if (-not (& $str $r.url)) { $errors.Add("repos[$i].url must be a non-empty string") }
                if ($r.PSObject.Properties.Name -contains 'directory' -and -not (& $str $r.directory)) {
                    $errors.Add("repos[$i].directory must be a non-empty string")
                }
            }
        }
    }

    # sdks
    if ($Object.PSObject.Properties.Name -contains 'sdks') {
        $sk = $Object.sdks
        if (-not (& $isObj $sk)) {
            $errors.Add('"sdks" must be an object')
        } else {
            foreach ($sp in $sk.PSObject.Properties) {
                $v = $sp.Value
                $okStr = & $str $v
                # An EMPTY array is valid (JS: Array.isArray(v) && v.every(str)
                # is vacuously true for []; project.schema.json has no minItems).
                $okArr = ($v -is [array] -or $v -is [System.Collections.IList]) -and
                         @($v | Where-Object { -not (& $str $_) }).Count -eq 0
                if (-not $okStr -and -not $okArr) {
                    $errors.Add("sdks.$($sp.Name) must be a non-empty string or an array of non-empty strings")
                }
            }
        }
    }

    # mcp
    if ($Object.PSObject.Properties.Name -contains 'mcp') {
        $mp = $Object.mcp
        if (-not ($mp -is [array] -or $mp -is [System.Collections.IList])) {
            $errors.Add('"mcp" must be an array')
        } else {
            for ($i = 0; $i -lt @($mp).Count; $i++) {
                $m = @($mp)[$i]
                if (-not (& $isObj $m)) { $errors.Add("mcp[$i] must be an object with `"name`" plus `"command`" (stdio) or `"url`" (http); see docs/projects.md"); continue }
                if (-not (& $str $m.name)) { $errors.Add("mcp[$i].name must be a non-empty string") }

                $type = $null
                $hasType = $m.PSObject.Properties.Name -contains 'type'
                if ($hasType) {
                    if ($m.type -ceq "stdio" -or $m.type -ceq "http") { $type = $m.type }
                    elseif ($null -ne $m.type) { $errors.Add("mcp[$i].type must be `"stdio`" or `"http`""); continue }
                }
                if (-not $type) {
                    if (& $str $m.command) { $type = "stdio" }
                    elseif (& $str $m.url) { $type = "http" }
                }
                if (-not $type) { $errors.Add("mcp[$i] needs a `"command`" (stdio) or `"url`" (http)"); continue }

                $common  = @("name","type","agents","enabled")
                $allowed = if ($type -eq "stdio") { $common + @("command","args","env") }
                           else { $common + @("url","headers","bearerTokenEnvVar") }
                foreach ($mk in $m.PSObject.Properties) {
                    if ($allowed -notcontains $mk.Name) {
                        $errors.Add("mcp[$i] unknown key `"$($mk.Name)`" for a $type server")
                    }
                }

                if ($type -eq "stdio") {
                    if (-not (& $str $m.command)) { $errors.Add("mcp[$i].command must be a non-empty string") }
                    if ($m.PSObject.Properties.Name -contains 'args') {
                        $argsOk = ($m.args -is [array] -or $m.args -is [System.Collections.IList]) -and
                                  @($m.args | Where-Object { $_ -isnot [string] }).Count -eq 0
                        if (-not $argsOk) { $errors.Add("mcp[$i].args must be an array of strings") }
                    }
                    if ($m.PSObject.Properties.Name -contains 'env') {
                        if (-not (& $strMapOk $m.env)) { $errors.Add("mcp[$i].env must be an object of string values") }
                    }
                } else {
                    if (-not (& $str $m.url)) { $errors.Add("mcp[$i].url must be a non-empty string") }
                    if ($m.PSObject.Properties.Name -contains 'headers') {
                        if (-not (& $strMapOk $m.headers)) { $errors.Add("mcp[$i].headers must be an object of string values") }
                    }
                    if ($m.PSObject.Properties.Name -contains 'bearerTokenEnvVar') {
                        if (-not (& $str $m.bearerTokenEnvVar)) {
                            $errors.Add("mcp[$i].bearerTokenEnvVar must be a non-empty string")
                        }
                    }
                }

                if ($m.PSObject.Properties.Name -contains 'agents') {
                    $agOk = ($m.agents -is [array] -or $m.agents -is [System.Collections.IList]) -and
                            @($m.agents).Count -gt 0 -and
                            @($m.agents | Where-Object { $MCP_AGENTS -cnotcontains $_ }).Count -eq 0
                    if (-not $agOk) {
                        $errors.Add("mcp[$i].agents must be a non-empty array from $($MCP_AGENTS -join '/')")
                    }
                }
                if ($m.PSObject.Properties.Name -contains 'enabled' -and $m.enabled -isnot [bool]) {
                    $errors.Add("mcp[$i].enabled must be a boolean")
                }
            }
        }
    }

    # hostPackages / provisionCommands
    foreach ($key in @("hostPackages", "provisionCommands")) {
        if ($Object.PSObject.Properties.Name -contains $key) {
            $val = $Object.$key
            $ok = ($val -is [array] -or $val -is [System.Collections.IList]) -and
                  @($val | Where-Object { -not (& $str $_) }).Count -eq 0
            if (-not $ok) { $errors.Add("`"$key`" must be an array of non-empty strings") }
        }
    }

    # tests
    if ($Object.PSObject.Properties.Name -contains 'tests') {
        if (-not (& $isObj $Object.tests)) { $errors.Add('"tests" must be an object') }
    }

    return [pscustomobject]@{
        Ok     = ($errors.Count -eq 0)
        Errors = @($errors)
    }
}

# ── SSH to the VM ────────────────────────────────────────────────────────────
# Mirrors Get-AgentUsage.ps1's connection pattern: prefer the explicit root key
# ~/.ssh/agent_vm_ed25519, else the agent-vm Host alias. BatchMode-style
# non-interactive flags so a password prompt never stalls the tick.

function Invoke-ConstructVmSsh {
    <#
        Run a single command on the VM over SSH and return the exit code + output.
        Never throws: returns Code=-1 + empty Output when ssh is not on PATH or
        the connection fails.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$VmHost,
        [Parameter(Mandatory)][string]$Command
    )

    $sshExe = Get-Command ssh -ErrorAction SilentlyContinue
    if (-not $sshExe) {
        return [pscustomobject]@{ Code = -1; Output = "" }
    }

    # Build connection args: prefer the explicit key the provisioner wrote.
    $keyPath = Join-Path $HOME ".ssh/agent_vm_ed25519"
    if (Test-Path -LiteralPath $keyPath) {
        $target  = "root@$VmHost"
        $sshOpts = @(
            "-i", $keyPath,
            "-o", "IdentitiesOnly=yes",
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=15"
        )
    } else {
        # Fall back to the Host alias in ~/.ssh/config.
        $alias = ($VmHost -split '\.')[0]
        $target  = $alias
        $sshOpts = @(
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", "BatchMode=yes",
            "-o", "ConnectTimeout=15"
        )
    }

    # Transport the command base64-encoded (mktemp + base64 -d | bash), exactly
    # the Get-AgentUsage.ps1 Invoke-RemoteCollector pattern. Under WinPS 5.1 the
    # Legacy native-argument passing strips embedded double quotes and splits the
    # argument, silently corrupting multi-line scripts passed as a bare ssh arg.
    $scriptLf = ($Command -replace "`r`n", "`n")
    $b64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($scriptLf))
    $remoteCmd = "f=`$(mktemp) && printf %s '$b64' | base64 -d > `"`$f`" && bash `"`$f`"; rc=`$?; rm -f `"`$f`"; exit `$rc"

    $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    try {
        $output = & ssh @sshOpts $target $remoteCmd 2>$null
        $code = $LASTEXITCODE
        if ($null -eq $code) { $code = -1 }
        $outStr = if ($null -ne $output) { ($output -join "`n") } else { "" }
        return [pscustomobject]@{ Code = $code; Output = $outStr }
    } catch {
        return [pscustomobject]@{ Code = -1; Output = "" }
    } finally {
        $ErrorActionPreference = $prev
    }
}

function Get-ConstructVmProjects {
    <#
        Read the PROJECTS list from /etc/construct/config.env on the VM.
        Returns a string array of project names, or $null when unreachable.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$VmHost)

    $result = Invoke-ConstructVmSsh -VmHost $VmHost -Command 'cat /etc/construct/config.env 2>/dev/null'
    if ($result.Code -ne 0) { return $null }
    foreach ($line in ($result.Output -split "`n")) {
        if ($line -match '^\s*PROJECTS\s*=\s*"?([^"]*)"?\s*$') {
            $val = $matches[1].Trim()
            if (-not $val) { return @() }
            return @($val -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        }
    }
    return $null
}

# ── VM store read/write ──────────────────────────────────────────────────────
# The same wire format as the JS engine: name<TAB>base64 lines + END sentinel
# for reading; a guarded bash script for writing.

function Read-ConstructVmStore {
    <#
        Read all /opt/construct/projects/*.json from the VM as a hashtable of
        name -> content (string). Returns $null when unreachable or the sentinel
        is missing.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$VmHost,
        [scriptblock]$SshInvoker
    )

    # The read script emits a NOSTORE marker when /opt/construct/projects does
    # not exist, so the caller can disambiguate "empty store" (all files deleted)
    # from "fresh VM where the store dir was never created" (D13).
    $readScript = @'
set -u
store='/opt/construct/projects'
if [ ! -d "$store" ]; then
  printf 'NOSTORE\n'
fi
if [ -d "$store" ]; then
  for f in "$store"/*.json; do
    [ -f "$f" ] || continue
    name="$(basename "$f" .json)"
    b64="$(base64 < "$f" | tr -d '\n')"
    printf '%s\t%s\n' "$name" "$b64"
  done
fi
printf 'END\n'
'@

    $result = $null
    if ($null -ne $SshInvoker) {
        $result = & $SshInvoker $readScript
    } else {
        $result = Invoke-ConstructVmSsh -VmHost $VmHost -Command $readScript
    }

    if ($null -eq $result -or $result.Code -ne 0) { return $null }

    $lines = $result.Output -split "`n"
    $sawEnd = $false
    $sawNoStore = $false
    $store = @{}
    foreach ($line in $lines) {
        if ($line -eq "END") { $sawEnd = $true; continue }
        if ($line -eq "NOSTORE") { $sawNoStore = $true; continue }
        $parts = $line -split "`t", 2
        if ($parts.Count -lt 2) { continue }
        $name = $parts[0].Trim()
        if (-not $name) { continue }
        try {
            $bytes = [Convert]::FromBase64String($parts[1])
            $content = [System.Text.Encoding]::UTF8.GetString($bytes)
            $store[$name] = $content
        } catch { continue }
    }
    if (-not $sawEnd) { return $null }
    # Return a PSObject with the store hashtable and whether the store dir existed.
    return [pscustomobject]@{ Files = $store; StoreDirExists = (-not $sawNoStore) }
}

function Write-ConstructVmStore {
    <#
        Guarded write-back to the VM store. Each operation writes only when the
        current content matches the expected base64 (or is absent when expected
        absent). Returns a PSObject with Done and Skipped string arrays, or $null
        on failure.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$VmHost,
        [Parameter(Mandatory)][array]$Ops,
        [scriptblock]$SshInvoker
    )

    if ($Ops.Count -eq 0) {
        return [pscustomobject]@{ Done = @(); Skipped = @() }
    }

    # Build a bash script that performs guarded writes.
    $sb = New-Object System.Text.StringBuilder
    $null = $sb.AppendLine("set -u")
    $null = $sb.AppendLine("store='/opt/construct/projects'")
    $null = $sb.AppendLine("mkdir -p ""`$store""")

    foreach ($op in $Ops) {
        # Escape the profile name for safe embedding in bash single quotes,
        # mirroring the JS buildWriteStoreScript pattern: replace ' with '\''
        $q = $op.Name -replace "'", "'\''"
        # Use single quotes around the name portion to prevent bash metacharacter
        # expansion ($, backtick, double-quote). $store is still expanded via
        # double quotes. Matches JS: const file = '"$store"' + "/'" + safeName + ".json'"
        $file = "`"`$store`"/'$q.json'"
        if ($op.Action -eq "delete") {
            if ($null -ne $op.Expect -and $op.Expect -ne "") {
                $null = $sb.AppendLine("cur=`$(base64 < $file 2>/dev/null | tr -d '\n' || true)")
                $null = $sb.AppendLine("if [ ""`$cur"" = '$($op.Expect)' ]; then rm -f $file; printf '%s\tdone\n' '$q'; else printf '%s\tskipped\n' '$q'; fi")
            } else {
                $null = $sb.AppendLine("rm -f $file; printf '%s\tdone\n' '$q'")
            }
        } else {
            $contentB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($op.Content))
            if ($null -ne $op.Expect -and $op.Expect -ne "") {
                $null = $sb.AppendLine("cur=`$(base64 < $file 2>/dev/null | tr -d '\n' || true)")
                $null = $sb.AppendLine("if [ ""`$cur"" = '$($op.Expect)' ]; then printf '%s' '$contentB64' | base64 -d > $file; printf '%s\tdone\n' '$q'; else printf '%s\tskipped\n' '$q'; fi")
            } elseif ($op.ExpectAbsent) {
                $null = $sb.AppendLine("if [ ! -f $file ]; then printf '%s' '$contentB64' | base64 -d > $file; printf '%s\tdone\n' '$q'; else printf '%s\tskipped\n' '$q'; fi")
            } else {
                $null = $sb.AppendLine("printf '%s' '$contentB64' | base64 -d > $file; printf '%s\tdone\n' '$q'")
            }
        }
    }
    $null = $sb.AppendLine("printf 'END\n'")

    $bashScript = $sb.ToString()

    $result = $null
    if ($null -ne $SshInvoker) {
        $result = & $SshInvoker $bashScript
    } else {
        $result = Invoke-ConstructVmSsh -VmHost $VmHost -Command $bashScript
    }

    if ($null -eq $result -or $result.Code -ne 0) { return $null }

    $lines = $result.Output -split "`n"
    $sawEnd = $false
    $done = @(); $skipped = @()
    foreach ($line in $lines) {
        if ($line -eq "END") { $sawEnd = $true; continue }
        $parts = $line -split "`t", 2
        if ($parts.Count -lt 2) { continue }
        if ($parts[1].Trim() -eq "done") { $done += $parts[0] }
        else { $skipped += $parts[0] }
    }
    if (-not $sawEnd) { return $null }
    return [pscustomobject]@{ Done = $done; Skipped = $skipped }
}

# ── The sync tick (D6) ───────────────────────────────────────────────────────

# ── Cross-process sync lock ──────────────────────────────────────────────────
# Serializes whole sync ticks across every engine that can touch the config
# repo: each VS Code window runs its own extension host, and this PowerShell
# engine runs during provisions -- none share a process. Two concurrent ticks
# interleave read-store -> commit -> merge -> write-back, and a tick holding a
# stale store read commits spurious deletions of files the other tick just
# added. Same file name and stale rule as the JS engine (SYNC_LOCK_FILE in
# extension/src/configsync.js must match).

$script:CONSTRUCT_SYNC_LOCK_FILE = ".sync.lock"
$script:CONSTRUCT_SYNC_LOCK_STALE_SEC = 300

function Lock-ConstructConfigSync {
    <#
        Try to take the cross-process sync lock: atomic CreateNew of
        <ConfigDir>\.sync.lock. A lock older than $StaleSeconds belongs to a
        crashed/killed process and is broken. Returns an ownership TOKEN
        (string) when acquired, $null when another live engine holds it. Pass
        the token to Unlock-ConstructConfigSync -- release is a no-op unless
        the lock file still carries it, so a holder that outlived the stale
        threshold and was broken cannot delete the next holder's lock.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [int]$StaleSeconds = $script:CONSTRUCT_SYNC_LOCK_STALE_SEC
    )
    $lockPath = Join-Path $ConfigDir $script:CONSTRUCT_SYNC_LOCK_FILE
    $token = [guid]::NewGuid().ToString("N")
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        try {
            $fsm = [System.IO.File]::Open($lockPath, [System.IO.FileMode]::CreateNew,
                                          [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
            try {
                $bytes = [System.Text.Encoding]::UTF8.GetBytes('{"token":"' + $token + '","pid":' + $PID + ',"at":"' + (Get-Date -Format o) + '"}')
                $fsm.Write($bytes, 0, $bytes.Length)
            } finally { $fsm.Dispose() }
            return $token
        } catch [System.IO.IOException] {
            # Already exists (or transient IO): stale-check the holder.
            # -Force: on non-Windows pwsh a dotfile counts as Hidden and a plain
            # Get-Item refuses it ("could not find item") even though it exists.
            $st = $null
            try { $st = Get-Item -LiteralPath $lockPath -Force -ErrorAction Stop } catch { continue } # vanished -- retry
            $age = ((Get-Date) - $st.LastWriteTime).TotalSeconds
            if ($age -le $StaleSeconds) { return $null }   # live holder
            try { Remove-Item -LiteralPath $lockPath -Force -ErrorAction Stop } catch { }
            # retry the create once; if a rival re-created it first, report busy
        } catch {
            return $null
        }
    }
    return $null
}

function Unlock-ConstructConfigSync {
    <#
        Release the sync lock, but only if it is still ours: the file must
        exist and carry the given token. A missing/unreadable/foreign-token
        file is left alone.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [AllowEmptyString()][AllowNull()][string]$Token
    )
    if (-not $Token) { return }
    $lockPath = Join-Path $ConfigDir $script:CONSTRUCT_SYNC_LOCK_FILE
    try {
        $cur = [System.IO.File]::ReadAllText($lockPath) | ConvertFrom-Json
        if ($cur.token -cne $Token) { return }   # not ours -- leave it
        Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
    } catch { }   # already gone or unreadable -- leave it
}

function Invoke-ConstructConfigSync {
    <#
        Full D6 sync tick, serialized by the cross-process lock shared with the
        VS Code engine. Waits up to $LockWaitSeconds for a concurrent tick to
        finish (the provisioning pre-wipe sync should run, not silently skip),
        then gives up with LockBusy=$true rather than racing. Everything else
        is delegated to Invoke-ConstructConfigSyncLocked.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][string]$VmHost,
        [ValidateSet("ours","theirs")]
        [string]$AutoResolve,
        [switch]$SeedOnly,
        [scriptblock]$SshReadInvoker,
        [scriptblock]$SshWriteInvoker,
        [int]$LockWaitSeconds = 90
    )

    if (-not (Test-Path -LiteralPath $ConfigDir)) {
        New-Item -ItemType Directory -Path $ConfigDir -Force | Out-Null
    }
    $deadline = (Get-Date).AddSeconds($LockWaitSeconds)
    $lockToken = Lock-ConstructConfigSync -ConfigDir $ConfigDir
    while (-not $lockToken -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        $lockToken = Lock-ConstructConfigSync -ConfigDir $ConfigDir
    }
    if (-not $lockToken) {
        # VmReadOk mirrors configsync.js: $true/$false once the VM-store read was
        # attempted, $null when the tick never got that far. Provisioning treats
        # $false (and Ran=$false with profiles to seed) as fatal -- silent skips
        # are how fresh installs end up with an empty store and zero repos.
        return [pscustomobject]@{
            Ok = $true; Ran = $false; LockBusy = $true; Conflict = $false; Blocked = $false
            Reason = "lock-busy"; SkippedInvalid = @(); Merged = $false; Seeded = $false
            Warnings = @("Sync lock held by another engine (a VS Code window?); tick skipped.")
            WriteBack = $null; VmReadOk = $null
        }
    }
    try {
        $inner = @{}
        foreach ($k in $PSBoundParameters.Keys) {
            if ($k -ne 'LockWaitSeconds') { $inner[$k] = $PSBoundParameters[$k] }
        }
        return Invoke-ConstructConfigSyncLocked @inner
    } finally {
        Unlock-ConstructConfigSync -ConfigDir $ConfigDir -Token $lockToken
    }
}

function Invoke-ConstructConfigSyncLocked {
    <#
        The core D6 sync tick body: commit host changes, read the VM store,
        commit a VM snapshot, merge, guarded write-back, advance vm ref. With
        -SeedOnly (D13) just seed the VM with main profiles. Degraded mode (no
        git): additive seed only. Callers go through Invoke-ConstructConfigSync
        (the lock); tests may call this directly to bypass it.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][string]$VmHost,
        [ValidateSet("ours","theirs")]
        [string]$AutoResolve,
        [switch]$SeedOnly,
        [scriptblock]$SshReadInvoker,
        [scriptblock]$SshWriteInvoker
    )

    $warnings = New-Object System.Collections.Generic.List[string]
    $skippedInvalid = @()

    $gitArgs = @("-c", "user.name=The Construct", "-c", "user.email=construct@construct.local", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=")
    $hasGit = Test-ConstructGitAvailable
    $projDir = Join-Path $ConfigDir "projects"
    if (-not (Test-Path -LiteralPath $projDir)) {
        New-Item -ItemType Directory -Path $projDir -Force | Out-Null
    }

    # Read local profiles from disk.
    $localProfiles = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $projDir -Filter *.json -File -ErrorAction SilentlyContinue)) {
        $bname = $f.BaseName
        if ($script:RESERVED_PROFILE_NAMES -contains $bname.ToLowerInvariant()) { continue }
        try {
            $content = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
            $localProfiles[$bname] = $content
        } catch { }
    }

    # ── Degraded mode (no git) ────────────────────────────────────────────────
    if (-not $hasGit) {
        # Additive seed only: write profiles absent on the VM store.
        $vmStoreResult = Read-ConstructVmStore -VmHost $VmHost -SshInvoker $SshReadInvoker
        if ($null -eq $vmStoreResult) {
            $warnings.Add("VM unreachable; skipped VM sync (degraded / no git).")
            return [pscustomobject]@{
                Ok = $true; Ran = $true; Conflict = $false; Blocked = $false
                Reason = ""; SkippedInvalid = @(); Merged = $false; Seeded = $false
                Warnings = @($warnings); WriteBack = $null; VmReadOk = $false
            }
        }
        $vmStoreFiles = $vmStoreResult.Files
        $seedOps = @()
        foreach ($name in $localProfiles.Keys) {
            if (-not $vmStoreFiles.ContainsKey($name)) {
                # Canonicalize the content before seeding (D3).
                $seedContent = $localProfiles[$name]
                try {
                    $seedObj = $seedContent | ConvertFrom-Json
                    $seedCanon = ConvertTo-ConstructCanonicalJson -Name $name -Object $seedObj
                    if ($null -ne $seedCanon) { $seedContent = $seedCanon }
                } catch { }
                $seedOps += @{
                    Name = $name; Action = "write"; Content = $seedContent
                    Expect = $null; ExpectAbsent = $true
                }
            }
        }
        $wb = $null
        if ($seedOps.Count -gt 0) {
            $wb = Write-ConstructVmStore -VmHost $VmHost -Ops $seedOps -SshInvoker $SshWriteInvoker
        }
        return [pscustomobject]@{
            Ok = $true; Ran = $true; Conflict = $false; Blocked = $false
            Reason = "degraded-no-git"; SkippedInvalid = @(); Merged = $false
            Seeded = ($seedOps.Count -gt 0); Warnings = @($warnings)
            WriteBack = $wb; VmReadOk = $true
        }
    }

    # ── Ensure repo is initialised ────────────────────────────────────────────
    $repoReady = Initialize-ConstructConfigRepo -ConfigDir $ConfigDir
    if (-not $repoReady) {
        $warnings.Add("Could not initialise config repo.")
        return [pscustomobject]@{
            Ok = $false; Ran = $false; Conflict = $false; Blocked = $false
            Reason = "repo-init-failed"; SkippedInvalid = @(); Merged = $false
            Seeded = $false; Warnings = @($warnings); WriteBack = $null; VmReadOk = $null
        }
    }

    # ── Step 1: Check for ongoing merge/conflict ──────────────────────────────
    $mergeHead = Join-Path $ConfigDir ".git/MERGE_HEAD"
    if (Test-Path -LiteralPath $mergeHead) {
        $unmergedAtStart = @()
        $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        try {
            $unmergedAtStart = @(& git -C $ConfigDir diff --name-only --diff-filter=U 2>$null)
        } catch { }
        $ErrorActionPreference = $prev

        if ($AutoResolve -and $unmergedAtStart.Count -gt 0) {
            $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
            try {
                & git -C $ConfigDir checkout "--$AutoResolve" -- . 2>$null | Out-Null
                & git -C $ConfigDir add -A 2>$null | Out-Null
            } catch { }
            $ErrorActionPreference = $prev
        }

        $unmergedAfterResolve = @()
        $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        try {
            $unmergedAfterResolve = @(& git -C $ConfigDir diff --name-only --diff-filter=U 2>$null)
        } catch { }
        $ErrorActionPreference = $prev

        if ($unmergedAfterResolve.Count -eq 0) {
            # Post-merge validation gate (D7): even after AutoResolve, the
            # merged content must validate before we commit. For a merge left
            # uncommitted by the gate (clean line-merge into invalid JSON),
            # checkout --ours/--theirs touches nothing if there are no
            # unmerged paths, so the invalid merged content would be committed
            # without this re-validation.
            $autoGateOk = $true
            $autoGateReason = ""
            foreach ($af in @(Get-ChildItem -LiteralPath $projDir -Filter *.json -File -ErrorAction SilentlyContinue)) {
                $abname = $af.BaseName
                if ($script:RESERVED_PROFILE_NAMES -contains $abname.ToLowerInvariant()) { continue }
                try {
                    $araw = [System.IO.File]::ReadAllText($af.FullName, [System.Text.Encoding]::UTF8)
                    $aobj = $araw | ConvertFrom-Json
                    $av = Test-ConstructProfile -Name $abname -Object $aobj
                    if (-not $av.Ok) {
                        $autoGateOk = $false
                        $autoGateReason = "Invalid merged profile '$abname' after auto-resolve: $($av.Errors -join '; ')"
                    }
                } catch {
                    $autoGateOk = $false
                    $autoGateReason = "Unparseable merged profile '$abname' after auto-resolve"
                }
            }
            if ($autoGateOk) {
                $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
                try {
                    & git -C $ConfigDir @gitArgs add -A 2>$null | Out-Null
                    $commitMsg = if ($AutoResolve) { "auto-resolve ($AutoResolve)" } else { "merge vm" }
                    & git -C $ConfigDir @gitArgs commit -m $commitMsg 2>$null | Out-Null
                } catch { }
                $ErrorActionPreference = $prev
            }
            if (Test-Path -LiteralPath $mergeHead) {
                $reason = if (-not $autoGateOk) { $autoGateReason } elseif ($AutoResolve) { "auto-resolve-failed" } else { "merge-commit-failed" }
                return [pscustomobject]@{
                    Ok = $false; Ran = $false; Conflict = $true; Blocked = $true
                    Reason = $reason; SkippedInvalid = @()
                    Merged = $false; Seeded = $false; Warnings = @($warnings)
                    WriteBack = $null; VmReadOk = $null
                }
            }
        } else {
            return [pscustomobject]@{
                Ok = $false; Ran = $false; Conflict = $true; Blocked = $false
                Reason = "merge-in-progress"; SkippedInvalid = @()
                Merged = $false; Seeded = $false; Warnings = @($warnings)
                WriteBack = $null; VmReadOk = $null
            }
        }
    }

    # ── Step 2: Commit host-side working-tree changes ─────────────────────────
    $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    try {
        & git -C $ConfigDir add -A -- projects/ 2>$null | Out-Null
        $diffIndex = & git -C $ConfigDir diff --cached --name-only 2>$null
        if ($diffIndex) {
            # Validate changed profile files before committing.
            # Also exclude reserved names (D1/D5): default.json and
            # project.schema.json must NEVER be tracked in the config repo.
            $invalidPaths = @()
            foreach ($d in @($diffIndex)) {
                $d = "$d".Trim()
                if (-not ($d -like 'projects/*.json')) { continue }
                $bname = [System.IO.Path]::GetFileNameWithoutExtension([System.IO.Path]::GetFileName($d))
                if ($script:RESERVED_PROFILE_NAMES -contains $bname.ToLowerInvariant()) {
                    $invalidPaths += $d
                    $warnings.Add("Reserved name '$bname' in projects/ (unstaged): NEVER tracked in the config repo.")
                    continue
                }
                $fp = Join-Path $ConfigDir $d
                if (-not (Test-Path -LiteralPath $fp)) { continue }   # deletions are ok
                try {
                    $raw = [System.IO.File]::ReadAllText($fp, [System.Text.Encoding]::UTF8)
                    $obj = $raw | ConvertFrom-Json
                    $v = Test-ConstructProfile -Name $bname -Object $obj
                    if (-not $v.Ok) {
                        $invalidPaths += $d
                        $warnings.Add("Invalid host file $d (skipped commit): $($v.Errors -join '; ')")
                    }
                } catch {
                    $invalidPaths += $d
                    $warnings.Add("Unparseable host file $d (skipped commit): $($_.Exception.Message)")
                }
            }
            foreach ($ip in $invalidPaths) {
                & git -C $ConfigDir reset HEAD -- $ip 2>$null | Out-Null
            }
            # Re-check if anything is still staged.
            $remaining = & git -C $ConfigDir diff --cached --name-only 2>$null
            if ($remaining) {
                & git -C $ConfigDir @gitArgs commit -m "host sync" 2>$null | Out-Null
            }
        }
    } catch { }
    $ErrorActionPreference = $prev

    # ── Step 3: Read the VM store ─────────────────────────────────────────────
    $vmStoreResult = Read-ConstructVmStore -VmHost $VmHost -SshInvoker $SshReadInvoker
    if ($null -eq $vmStoreResult) {
        $warnings.Add("VM unreachable; skipped VM side.")
        return [pscustomobject]@{
            Ok = $true; Ran = $true; Conflict = $false; Blocked = $false
            Reason = ""; SkippedInvalid = @(); Merged = $false; Seeded = $false
            Warnings = @($warnings); WriteBack = $null; VmReadOk = $false
        }
    }
    $vmStore = $vmStoreResult.Files
    $vmStoreDirExists = $vmStoreResult.StoreDirExists

    # ── SeedOnly / fresh-VM path (D13) ────────────────────────────────────────
    # D13 disambiguation: take the fresh-VM seed path when the VM store is empty
    # AND (the store dir does not exist OR vm branch has 0 profiles OR -SeedOnly).
    # Without the NOSTORE marker, a plain tick after a VM wipe would commit an
    # empty vm snapshot and the merge would DELETE every host profile from main.
    $vmStoreEmpty = $vmStore.Count -eq 0
    if ($SeedOnly -or $vmStoreEmpty) {
        $vmTreeEmpty = $true
        $freshVm = $false

        if (-not $vmStoreDirExists) {
            # Store dir doesn't exist -> definitely a fresh/wiped VM.
            $freshVm = $true
        }

        $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        try {
            $vmTreeFiles = & git -C $ConfigDir ls-tree --name-only vm -- projects/ 2>$null
            if ($vmTreeFiles) { $vmTreeEmpty = $false }
        } catch { }
        $ErrorActionPreference = $prev

        if (-not $freshVm -and $vmStoreEmpty -and ($vmTreeEmpty -or $SeedOnly)) {
            $freshVm = $true
        }

        if ($freshVm) {
            $seedOps = @()
            foreach ($f in @(Get-ChildItem -LiteralPath $projDir -Filter *.json -File -ErrorAction SilentlyContinue)) {
                $bname = $f.BaseName
                if ($script:RESERVED_PROFILE_NAMES -contains $bname.ToLowerInvariant()) { continue }
                try {
                    $content = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
                    # Canonicalize before seeding (D3).
                    try {
                        $seedObj2 = $content | ConvertFrom-Json
                        $seedCanon2 = ConvertTo-ConstructCanonicalJson -Name $bname -Object $seedObj2
                        if ($null -ne $seedCanon2) { $content = $seedCanon2 }
                    } catch { }
                    $seedOps += @{
                        Name = $bname; Action = "write"; Content = $content
                        Expect = $null; ExpectAbsent = $true
                    }
                } catch { }
            }
            $wb = $null
            if ($seedOps.Count -gt 0) {
                $wb = Write-ConstructVmStore -VmHost $VmHost -Ops $seedOps -SshInvoker $SshWriteInvoker
            }
            # Advance vm ref to main so the next tick starts from a common base.
            $prev2 = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
            try {
                & git -C $ConfigDir update-ref refs/heads/vm refs/heads/main 2>$null | Out-Null
            } catch { }
            $ErrorActionPreference = $prev2
            return [pscustomobject]@{
                Ok = $true; Ran = $true; Conflict = $false; Blocked = $false
                Reason = ""; SkippedInvalid = @(); Merged = $false
                Seeded = $true; Warnings = @($warnings); WriteBack = $wb; VmReadOk = $true
            }
        }
    }

    # ── Step 4/5: Validate VM files and commit VM snapshot ────────────────────
    $validVm = @{}
    foreach ($name in @($vmStore.Keys)) {
        $lower = "$name".ToLowerInvariant()
        if ($script:RESERVED_PROFILE_NAMES -contains $lower) {
            # Reserved names are dead files by design (default always resolves to
            # the shipped copy) and pre-v2 provisions seeded default.json into the
            # store, so a leftover is the normal state on upgraded VMs -- skip it
            # quietly instead of warning on every tick.
            Write-Verbose "Reserved name '$name' in VM store ignored."
            continue
        }
        try {
            $obj = $vmStore[$name] | ConvertFrom-Json
            $v = Test-ConstructProfile -Name $name -Object $obj
            if (-not $v.Ok) {
                $skippedInvalid += @{ Name = $name; Reason = ($v.Errors -join '; ') }
                $warnings.Add("Invalid VM file '$name': $($v.Errors -join '; ')")
                continue
            }
            $validVm[$name] = $vmStore[$name]
        } catch {
            $skippedInvalid += @{ Name = $name; Reason = "Unparseable JSON" }
            $warnings.Add("Unparseable VM file '$name'.")
        }
    }

    # Mass-deletion guard (mirrors syncTickLocked in configsync.js): the store
    # EXISTS but yielded zero valid profiles while the vm branch has some. Far
    # more likely a half-provisioned store or all-invalid files than a genuine
    # delete-everything -- propagating it would wipe main. Skip the VM side with
    # a warning; individual deletions still propagate normally.
    if ($validVm.Count -eq 0) {
        $vmTipCount = 0
        $prevMd = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        try {
            $vmTipCount = @(& git -C $ConfigDir ls-tree --name-only vm -- projects/ 2>$null |
                            Where-Object { $_ }).Count
        } catch { }
        $ErrorActionPreference = $prevMd
        if ($vmTipCount -gt 0) {
            $warnings.Add("VM store has no valid profiles but the vm branch has ${vmTipCount}; refusing to propagate a mass deletion (delete profiles individually if intended).")
            # ...but do NOT stall (mirrors syncTickLocked in configsync.js): an
            # existing-but-empty store is, in the field, a rebuilt VM whose dir
            # provisioning recreated before the first seed -- returning here on
            # every tick left it empty forever. Re-seed main's profiles with
            # expect-absent guards: only files missing from the VM are written
            # (invalid files stay put with their warning), main is untouched,
            # and the vm ref is NOT advanced.
            $reseedOps = @()
            foreach ($f in @(Get-ChildItem -LiteralPath $projDir -Filter *.json -File -ErrorAction SilentlyContinue)) {
                $rsName = $f.BaseName
                if ($script:RESERVED_PROFILE_NAMES -contains $rsName.ToLowerInvariant()) { continue }
                try {
                    $rsContent = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
                    try {
                        $rsObj = $rsContent | ConvertFrom-Json
                        $rsCanon = ConvertTo-ConstructCanonicalJson -Name $rsName -Object $rsObj
                        if ($null -ne $rsCanon) { $rsContent = $rsCanon }
                    } catch { }
                    $reseedOps += @{
                        Name = $rsName; Action = "write"; Content = $rsContent
                        Expect = $null; ExpectAbsent = $true
                    }
                } catch { }
            }
            $reseedWb = $null
            $reseedDone = 0
            if ($reseedOps.Count -gt 0) {
                $reseedWb = Write-ConstructVmStore -VmHost $VmHost -Ops $reseedOps -SshInvoker $SshWriteInvoker
                if ($reseedWb -and $reseedWb.PSObject.Properties['Done']) { $reseedDone = @($reseedWb.Done).Count }
                if ($null -eq $reseedWb) { $warnings.Add("Re-seed write-back to the VM store failed.") }
                elseif ($reseedDone -gt 0) { Write-Verbose "Re-seeded $reseedDone profile(s) into the emptied VM store." }
            }
            return [pscustomobject]@{
                Ok = $true; Ran = $true; Conflict = $false; Blocked = $false
                Reason = ""; SkippedInvalid = @($skippedInvalid); Merged = $false
                Seeded = ($reseedDone -gt 0); Warnings = @($warnings); WriteBack = $reseedWb
                VmReadOk = $true
            }
        }
    }

    # Temp-index VM commit (D6 step 5).
    $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    $vmCommitted = $false
    try {
        $tmpIndex = Join-Path ([System.IO.Path]::GetTempPath()) ("construct-idx-" + [guid]::NewGuid().ToString("N"))
        $savedIndex = $env:GIT_INDEX_FILE
        try {
            $env:GIT_INDEX_FILE = $tmpIndex
            & git -C $ConfigDir read-tree vm 2>$null | Out-Null

            # Names read from the VM but SKIPPED (invalid) or RESERVED must NOT be
            # read as deletions: an invalid file is skipped and 'never enters the
            # repo' (spec section 6.2), so the vm branch keeps its last agreed-valid
            # copy for that name rather than committing a spurious deletion that the
            # merge would propagate to main and wipe a previously-synced profile.
            # Only names genuinely absent from the VM read are deletions.
            $preserveNames = @{}
            foreach ($si in $skippedInvalid) { $preserveNames[$si.Name] = $true }
            foreach ($name in @($vmStore.Keys)) {
                if ($script:RESERVED_PROFILE_NAMES -contains ("$name".ToLowerInvariant())) { $preserveNames[$name] = $true }
            }

            # Remove projects/* entries from the temp index EXCEPT the preserved ones,
            # so the tree is rebuilt from the fresh VM read without losing skipped names.
            $existingEntries = & git -C $ConfigDir ls-files --cached -- "projects/" 2>$null
            if ($existingEntries) {
                foreach ($entry in @($existingEntries)) {
                    $base = "$entry".Substring("projects/".Length)
                    if ($base.EndsWith(".json")) { $entryName = $base.Substring(0, $base.Length - 5) } else { $entryName = $base }
                    if ($preserveNames.ContainsKey($entryName)) { continue }
                    & git -C $ConfigDir update-index --force-remove -- "$entry" 2>$null | Out-Null
                }
            }

            # Add valid VM files. Each file is canonicalized (D3 'canonical JSON
            # everywhere') before being committed to the vm branch, keeping the
            # raw-bytes guard expect (write-back uses the RAW bytes read this tick).
            foreach ($name in $validVm.Keys) {
                $rawContent = $validVm[$name]
                # Canonicalize: parse, sanitize+serialize to the canonical byte form.
                $canonContent = $rawContent
                try {
                    $vmObj = $rawContent | ConvertFrom-Json
                    $canon = ConvertTo-ConstructCanonicalJson -Name $name -Object $vmObj
                    if ($null -ne $canon) { $canonContent = $canon }
                } catch { }   # if parse fails, use raw (the validator already accepted it)
                $contentBytes = [System.Text.Encoding]::UTF8.GetBytes($canonContent)
                # Write blob via a temp file (piping to --stdin is unreliable across PS versions).
                $tmpFile = Join-Path ([System.IO.Path]::GetTempPath()) ("construct-blob-" + [guid]::NewGuid().ToString("N"))
                [System.IO.File]::WriteAllBytes($tmpFile, $contentBytes)
                $sha = & git -C $ConfigDir hash-object -w $tmpFile 2>$null
                Remove-Item -LiteralPath $tmpFile -Force -ErrorAction SilentlyContinue
                if ($sha) {
                    $sha = "$sha".Trim()
                    & git -C $ConfigDir update-index --add --cacheinfo "100644,$sha,projects/$name.json" 2>$null | Out-Null
                }
            }

            $newTree = (& git -C $ConfigDir write-tree 2>$null)
            if ($newTree) {
                $newTree = "$newTree".Trim()
                # Restore the real index before comparing trees (rev-parse reads .git, not the index).
                $vmTipTree = $null
                $origIdx = $env:GIT_INDEX_FILE
                if ($null -eq $savedIndex) {
                    Remove-Item Env:\GIT_INDEX_FILE -ErrorAction SilentlyContinue
                } else {
                    $env:GIT_INDEX_FILE = $savedIndex
                }
                $vmTipTree = (& git -C $ConfigDir rev-parse "vm^{tree}" 2>$null)
                $env:GIT_INDEX_FILE = $origIdx
                if ($vmTipTree) { $vmTipTree = "$vmTipTree".Trim() }
                if ($newTree -ne $vmTipTree) {
                    $vmTip = $null
                    $origIdx2 = $env:GIT_INDEX_FILE
                    if ($null -eq $savedIndex) {
                        Remove-Item Env:\GIT_INDEX_FILE -ErrorAction SilentlyContinue
                    } else {
                        $env:GIT_INDEX_FILE = $savedIndex
                    }
                    $vmTip = (& git -C $ConfigDir rev-parse vm 2>$null)
                    $env:GIT_INDEX_FILE = $origIdx2
                    if ($vmTip) { $vmTip = "$vmTip".Trim() }
                    $newCommit = (& git -C $ConfigDir @gitArgs commit-tree $newTree -p $vmTip -m "vm sync" 2>$null)
                    if ($newCommit) {
                        $newCommit = "$newCommit".Trim()
                        & git -C $ConfigDir update-ref refs/heads/vm $newCommit 2>$null | Out-Null
                        $vmCommitted = $true
                    }
                }
            }
        } finally {
            if ($null -eq $savedIndex) {
                Remove-Item Env:\GIT_INDEX_FILE -ErrorAction SilentlyContinue
            } else {
                $env:GIT_INDEX_FILE = $savedIndex
            }
            if (Test-Path -LiteralPath $tmpIndex) {
                Remove-Item -LiteralPath $tmpIndex -Force -ErrorAction SilentlyContinue
            }
        }
    } catch {
        $warnings.Add("VM commit failed: $($_.Exception.Message)")
    }
    $ErrorActionPreference = $prev

    # ── Step 6: Merge vm into main ────────────────────────────────────────────
    $merged = $false
    $conflict = $false
    $blocked = $false
    $blockedReason = ""
    $needsMerge = $true

    $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    try {
        $mainTree = (& git -C $ConfigDir rev-parse "main^{tree}" 2>$null)
        $vmTree   = (& git -C $ConfigDir rev-parse "vm^{tree}" 2>$null)
        if ($mainTree) { $mainTree = "$mainTree".Trim() }
        if ($vmTree)   { $vmTree   = "$vmTree".Trim() }

        if ($mainTree -eq $vmTree) { $needsMerge = $false }
        if ($needsMerge) {
            & git -C $ConfigDir merge-base --is-ancestor vm main 2>$null
            if ($LASTEXITCODE -eq 0) { $needsMerge = $false }
        }

        if ($needsMerge) {
            & git -C $ConfigDir merge --no-ff --no-commit vm 2>$null | Out-Null
            $mergeExitCode = $LASTEXITCODE

            if ($mergeExitCode -ne 0) {
                $conflictFiles = @(& git -C $ConfigDir diff --name-only --diff-filter=U 2>$null)
                if ($conflictFiles.Count -gt 0) {
                    if ($AutoResolve) {
                        & git -C $ConfigDir checkout "--$AutoResolve" -- . 2>$null | Out-Null
                        & git -C $ConfigDir add -A 2>$null | Out-Null
                    } else {
                        $conflict = $true
                    }
                }
            }

            if (-not $conflict) {
                # Post-merge validation gate.
                $gateOk = $true
                $gateReason = ""
                foreach ($f in @(Get-ChildItem -LiteralPath $projDir -Filter *.json -File -ErrorAction SilentlyContinue)) {
                    $bname = $f.BaseName
                    if ($script:RESERVED_PROFILE_NAMES -contains $bname.ToLowerInvariant()) { continue }
                    try {
                        $raw = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
                        $obj = $raw | ConvertFrom-Json
                        $v = Test-ConstructProfile -Name $bname -Object $obj
                        if (-not $v.Ok) {
                            $gateOk = $false
                            $gateReason = "Invalid merged profile '$bname': $($v.Errors -join '; ')"
                        }
                    } catch {
                        $gateOk = $false
                        $gateReason = "Unparseable merged profile '$bname'"
                    }
                }
                if ($gateOk) {
                    & git -C $ConfigDir @gitArgs commit -m "merge vm" 2>$null | Out-Null
                    if ($LASTEXITCODE -eq 0) {
                        $merged = $true
                    } else {
                        $blocked = $true
                        $blockedReason = "merge-commit-failed"
                    }
                } else {
                    $blocked = $true
                    $blockedReason = $gateReason
                }
            }
        }
    } catch {
        $warnings.Add("Merge step failed: $($_.Exception.Message)")
    }
    $ErrorActionPreference = $prev

    if ($conflict -or $blocked) {
        return [pscustomobject]@{
            Ok = (-not $conflict -and -not $blocked); Ran = $true
            Conflict = $conflict; Blocked = $blocked
            Reason = $(if ($blocked) { $blockedReason } else { "conflict" })
            SkippedInvalid = $skippedInvalid; Merged = $false; Seeded = $false
            Warnings = @($warnings); WriteBack = $null; VmReadOk = $true
        }
    }

    # ── Step 7: Guarded write-back ────────────────────────────────────────────
    # Build a set of VM names that were skipped as invalid or reserved (D6.4):
    # these must NOT be written to or deleted on the VM store — invalid files
    # are SKIPPED with a warning, not destroyed.
    $skippedVmNames = @{}
    foreach ($si in $skippedInvalid) { $skippedVmNames[$si.Name] = $true }
    foreach ($name in @($vmStore.Keys)) {
        if ($script:RESERVED_PROFILE_NAMES -contains "$name".ToLowerInvariant()) {
            $skippedVmNames[$name] = $true
        }
    }

    $mainFiles = @{}
    foreach ($f in @(Get-ChildItem -LiteralPath $projDir -Filter *.json -File -ErrorAction SilentlyContinue)) {
        $bname = $f.BaseName
        if ($script:RESERVED_PROFILE_NAMES -contains $bname.ToLowerInvariant()) { continue }
        try {
            $mainFiles[$bname] = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
        } catch { }
    }

    $writeOps = @()
    foreach ($name in $mainFiles.Keys) {
        if ($skippedVmNames.ContainsKey($name)) { continue }
        $mainContent = $mainFiles[$name]
        $vmContent = if ($vmStore.ContainsKey($name)) { $vmStore[$name] } else { $null }
        if ($mainContent -ne $vmContent) {
            if ($null -ne $vmContent) {
                $expectB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($vmContent))
                $writeOps += @{
                    Name = $name; Action = "write"; Content = $mainContent
                    Expect = $expectB64; ExpectAbsent = $false
                }
            } else {
                $writeOps += @{
                    Name = $name; Action = "write"; Content = $mainContent
                    Expect = $null; ExpectAbsent = $true
                }
            }
        }
    }
    foreach ($name in @($vmStore.Keys)) {
        if ($skippedVmNames.ContainsKey($name)) { continue }
        if (-not $mainFiles.ContainsKey($name)) {
            $expectB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($vmStore[$name]))
            $writeOps += @{
                Name = $name; Action = "delete"; Expect = $expectB64; ExpectAbsent = $false
            }
        }
    }

    $wb = $null
    if ($writeOps.Count -gt 0) {
        $wb = Write-ConstructVmStore -VmHost $VmHost -Ops $writeOps -SshInvoker $SshWriteInvoker
    }

    # ── Step 8: Advance vm ref (D6.8) ───────────────────────────────────────────
    # Advance vm to main ONLY when the merge committed AND write-back actually
    # ran successfully (writeOps empty means nothing to write = success; or
    # Write-ConstructVmStore returned non-null). Without this guard, a failed
    # write-back causes the next tick to re-read the stale VM content as a
    # fresh vm-side change, silently reverting the host's committed edit.
    $writeBackRan = ($writeOps.Count -eq 0) -or ($null -ne $wb)
    if (($merged -or -not $needsMerge) -and $writeBackRan) {
        $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        try {
            & git -C $ConfigDir update-ref refs/heads/vm refs/heads/main 2>$null | Out-Null
        } catch { }
        $ErrorActionPreference = $prev
    }

    return [pscustomobject]@{
        Ok = $true; Ran = $true; Conflict = $false; Blocked = $false
        Reason = ""; SkippedInvalid = $skippedInvalid; Merged = $merged
        Seeded = $false; Warnings = @($warnings); WriteBack = $wb; VmReadOk = $true
    }
}

# ── Import from remote config repo / local dir ──────────────────────────────

function Update-ConstructStagingClone {
    <#
        Clone (or fetch + hard-reset) a remote config repo into the D2 staging
        cache: <LOCALAPPDATA||TEMP>\The-Construct\cache\config-remotes\<slug>.
        -NoFetch skips the network round-trip when the clone already exists
        (used by the per-candidate interactive import, which refreshes once
        up front). Returns the clone directory path; throws when the clone
        cannot be created.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$SourceRepo,
        [switch]$NoFetch
    )

    $cacheBase = $null
    if ($env:LOCALAPPDATA) { $cacheBase = $env:LOCALAPPDATA }
    elseif ($env:TEMP) { $cacheBase = $env:TEMP }
    else { $cacheBase = [System.IO.Path]::GetTempPath() }
    $stagingRoot = Join-Path (Join-Path $cacheBase "The-Construct") "cache/config-remotes"
    # Slug: replace non-alnum/dot/dash/underscore with - (same rule as JS).
    $slug = ($SourceRepo -replace '[^A-Za-z0-9._-]', '-')
    $cloneDir = Join-Path $stagingRoot $slug

    # Fail CLOSED: a fetch/reset that fails must NOT silently fall back to stale
    # cached content (which could import an out-of-date profile). Every git step
    # is checked via $LASTEXITCODE and any failure throws — the caller then aborts
    # the import rather than proceeding with the old clone. -NoFetch is the only
    # sanctioned way to reuse an existing clone without a network round-trip.
    $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    $err = $null
    try {
        if (Test-Path -LiteralPath (Join-Path $cloneDir ".git")) {
            if (-not $NoFetch) {
                & git -C $cloneDir fetch origin 2>$null | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "git fetch failed for config repo '$SourceRepo'." }
                $defaultBranch = & git -C $cloneDir symbolic-ref refs/remotes/origin/HEAD 2>$null
                if ($LASTEXITCODE -ne 0 -or -not $defaultBranch) { $defaultBranch = "refs/remotes/origin/main" }
                $defaultBranch = "$defaultBranch".Trim()
                & git -C $cloneDir reset --hard $defaultBranch 2>$null | Out-Null
                if ($LASTEXITCODE -ne 0) { throw "git reset --hard '$defaultBranch' failed for config repo '$SourceRepo'." }
            }
        } else {
            if (-not (Test-Path -LiteralPath $stagingRoot)) {
                New-Item -ItemType Directory -Path $stagingRoot -Force | Out-Null
            }
            & git clone $SourceRepo $cloneDir 2>$null | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "git clone failed for config repo '$SourceRepo'." }
        }
    } catch {
        $err = $_.Exception.Message
    }
    $ErrorActionPreference = $prev

    if ($err) { throw $err }
    if (-not (Test-Path -LiteralPath (Join-Path $cloneDir ".git"))) {
        throw "Failed to clone/fetch config repo '$SourceRepo'."
    }
    return $cloneDir
}

function Get-ConstructImportCandidates {
    <#
        D16 candidate discovery: files matching projects/*.json when that
        subdir exists, else top-level *.json; reserved names and *.sample
        always excluded. Returns FileInfo objects (may be empty).
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$SourceDir)

    $scanDir = $SourceDir
    $srcProjDir = Join-Path $SourceDir "projects"
    if (Test-Path -LiteralPath $srcProjDir) { $scanDir = $srcProjDir }
    return @(Get-ChildItem -LiteralPath $scanDir -Filter *.json -File -ErrorAction SilentlyContinue |
             Where-Object { $script:RESERVED_PROFILE_NAMES -notcontains $_.BaseName.ToLowerInvariant() -and
                            $_.BaseName -notlike '*.sample' })
}

function Register-ConstructConfigRemote {
    <#
        Record a linked remote config repo in manifest/remotes.json
        ([{url}, ...], D1). Idempotent: an already-present URL is a no-op.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][string]$RemoteUrl
    )

    $manifDir = Join-Path $ConfigDir "manifest"
    if (-not (Test-Path -LiteralPath $manifDir)) {
        New-Item -ItemType Directory -Path $manifDir -Force | Out-Null
    }
    $remotesFile = Join-Path $manifDir "remotes.json"
    $remotes = @()
    if (Test-Path -LiteralPath $remotesFile) {
        try { $remotes = @((Get-Content -LiteralPath $remotesFile -Raw | ConvertFrom-Json)) } catch { $remotes = @() }
    }
    foreach ($r in $remotes) {
        if ($r.url -eq $RemoteUrl) { return }
    }
    $remotes += [pscustomobject]@{ url = $RemoteUrl }
    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    $json = ConvertTo-ConstructJsonValue -Value $remotes -Depth 0
    [System.IO.File]::WriteAllText($remotesFile, "$json`n", $utf8NoBom)
}

function Test-ConstructRenameTarget {
    <#
        Validate a proposed rename target for an interactive import collision
        (D17): the new name must be non-empty and filename-safe, must not be a
        reserved name (default / project.schema -- D1/D5: never written, never
        committed), and projects/<NewName>.json must not already exist -- an
        import never silently overwrites. The one exception: when the existing
        file is a SAME-PROVENANCE import (manifest remoteUrl + pathInRemote
        both match), the rename is an update of our own earlier import, not an
        overwrite. Returns @{Ok:[bool]; Reason:[string]}.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][AllowEmptyString()][string]$NewName,
        [string]$RemoteUrl,
        [string]$PathInRemote
    )

    $nm = "$NewName".Trim()
    if (-not $nm) {
        return [pscustomobject]@{ Ok = $false; Reason = "Profile name must be non-empty." }
    }
    if ($nm -match '[\\/:*?"<>|]' -or $nm -match '[\x00-\x1f]') {
        return [pscustomobject]@{ Ok = $false; Reason = "Profile name '$nm' contains filename-unsafe characters." }
    }
    if ($script:RESERVED_PROFILE_NAMES -contains $nm.ToLowerInvariant()) {
        return [pscustomobject]@{ Ok = $false; Reason = "'$nm' is a reserved name -- choose another." }
    }
    $destFile = Join-Path (Join-Path $ConfigDir "projects") "$nm.json"
    if (Test-Path -LiteralPath $destFile) {
        # Allowed only as a same-provenance update (manifest matches).
        if ($RemoteUrl) {
            $manifFile = Join-Path (Join-Path $ConfigDir "manifest") "$nm.json"
            if (Test-Path -LiteralPath $manifFile) {
                try {
                    $manifObj = Get-Content -LiteralPath $manifFile -Raw | ConvertFrom-Json
                    if ($manifObj.remoteUrl -eq $RemoteUrl -and $manifObj.pathInRemote -eq $PathInRemote) {
                        return [pscustomobject]@{ Ok = $true; Reason = "" }
                    }
                } catch { }
            }
        }
        return [pscustomobject]@{ Ok = $false; Reason = "A profile named '$nm' already exists -- choose another." }
    }
    return [pscustomobject]@{ Ok = $true; Reason = "" }
}

function Import-ConstructConfigAs {
    <#
        Import ONE upstream profile file under a DIFFERENT local name (the
        interactive rename path of D17). Validates the target name first via
        Test-ConstructRenameTarget (reserved names refused, existing files
        never silently overwritten), injects the new name, runs the
        validate + canonicalize gate, writes projects/ + manifest/ + bases/,
        and commits. Returns @{Ok:[bool]; Name:[string]; Error:[string]}.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][string]$SourceFile,
        [Parameter(Mandatory)][string]$NewName,
        [string]$RemoteUrl,
        [string]$PathInRemote,
        [string]$CloneDir
    )

    $nm = "$NewName".Trim()
    $tv = Test-ConstructRenameTarget -ConfigDir $ConfigDir -NewName $nm -RemoteUrl $RemoteUrl -PathInRemote $PathInRemote
    if (-not $tv.Ok) {
        return [pscustomobject]@{ Ok = $false; Name = $nm; Error = $tv.Reason }
    }
    if (-not (Test-Path -LiteralPath $SourceFile)) {
        return [pscustomobject]@{ Ok = $false; Name = $nm; Error = "Source file '$SourceFile' not found." }
    }

    $projDir  = Join-Path $ConfigDir "projects"
    $manifDir = Join-Path $ConfigDir "manifest"
    $basesDir = Join-Path $ConfigDir "bases"
    foreach ($d in @($projDir, $manifDir, $basesDir)) {
        if (-not (Test-Path -LiteralPath $d)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
        }
    }

    try {
        $raw = [System.IO.File]::ReadAllText($SourceFile, [System.Text.Encoding]::UTF8)
        $obj = $raw | ConvertFrom-Json
    } catch {
        return [pscustomobject]@{ Ok = $false; Name = $nm; Error = "Cannot parse '$SourceFile': $($_.Exception.Message)" }
    }

    # Inject the new name, then run the D17 validate + canonicalize gate.
    if ($obj -isnot [pscustomobject]) {
        return [pscustomobject]@{ Ok = $false; Name = $nm; Error = "'$SourceFile' is not a JSON object." }
    }
    if ($obj.PSObject.Properties.Name -contains 'name') { $obj.name = $nm }
    else { $obj | Add-Member -NotePropertyName 'name' -NotePropertyValue $nm -Force }

    $v = Test-ConstructProfile -Name $nm -Object $obj
    if (-not $v.Ok) {
        return [pscustomobject]@{ Ok = $false; Name = $nm; Error = "Invalid profile: $($v.Errors -join '; ')" }
    }
    $canonical = ConvertTo-ConstructCanonicalJson -Name $nm -Object $obj
    if ($null -eq $canonical) {
        return [pscustomobject]@{ Ok = $false; Name = $nm; Error = "Could not canonicalize '$nm'." }
    }

    $utf8NoBom = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText((Join-Path $projDir "$nm.json"), $canonical, $utf8NoBom)

    if ($RemoteUrl) {
        # Provenance (D1): ref + baseCommit + baseBlobSha from the staging clone.
        $manifRef = ""; $manifBaseCommit = ""; $manifBaseBlobSha = ""
        $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        try {
            if ($CloneDir -and (Test-Path -LiteralPath (Join-Path $CloneDir ".git"))) {
                $manifBaseCommit = "$(& git -C $CloneDir rev-parse HEAD 2>$null)".Trim()
                $rawRef = "$(& git -C $CloneDir symbolic-ref --short HEAD 2>$null)".Trim()
                if ($rawRef) { $manifRef = $rawRef }
                $manifBaseBlobSha = "$(& git -C $CloneDir hash-object -- $SourceFile 2>$null)".Trim()
            }
        } catch { }
        $ErrorActionPreference = $prev

        $manifEntry = [ordered]@{
            remoteUrl    = $RemoteUrl
            ref          = $manifRef
            pathInRemote = $PathInRemote
            importedAs   = $nm
            baseCommit   = $manifBaseCommit
            baseBlobSha  = $manifBaseBlobSha
        }
        $manifJson = ConvertTo-ConstructJsonValue -Value ([pscustomobject]$manifEntry) -Depth 0
        [System.IO.File]::WriteAllText((Join-Path $manifDir "$nm.json"), "$manifJson`n", $utf8NoBom)
        [System.IO.File]::WriteAllText((Join-Path $basesDir "$nm.json"), $canonical, $utf8NoBom)
    }

    if (Test-ConstructGitAvailable) {
        $srcBase = [System.IO.Path]::GetFileNameWithoutExtension($SourceFile)
        $gitArgs = @("-c", "user.name=The Construct", "-c", "user.email=construct@construct.local", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=")
        $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        try {
            & git -C $ConfigDir add -A 2>$null | Out-Null
            & git -C $ConfigDir @gitArgs commit -m "import: $nm (renamed from $srcBase)" 2>$null | Out-Null
        } catch { }
        $ErrorActionPreference = $prev
    }

    return [pscustomobject]@{ Ok = $true; Name = $nm; Error = "" }
}

function Import-ConstructConfigs {
    <#
        Import project profiles from a remote git repo or a local directory into
        the config store. D16 discovery: files matching projects/*.json if that
        subdir exists, else top-level *.json; always exclude reserved names and
        *.sample. CLI collisions THROW. Returns @{Imported; Errors}.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [string]$SourceRepo,
        [string]$SourceDir,
        [string[]]$Names,
        [switch]$NoFetch
    )

    $projDir  = Join-Path $ConfigDir "projects"
    $manifDir = Join-Path $ConfigDir "manifest"
    $basesDir = Join-Path $ConfigDir "bases"
    foreach ($d in @($projDir, $manifDir, $basesDir)) {
        if (-not (Test-Path -LiteralPath $d)) {
            New-Item -ItemType Directory -Path $d -Force | Out-Null
        }
    }

    $srcDir = $null
    $remoteUrl = $null

    if ($SourceRepo) {
        $remoteUrl = $SourceRepo
        # Clone/fetch to the D2 staging cache.
        $srcDir = Update-ConstructStagingClone -SourceRepo $SourceRepo -NoFetch:$NoFetch
    } elseif ($SourceDir) {
        if (-not (Test-Path -LiteralPath $SourceDir)) {
            throw "Source directory '$SourceDir' does not exist."
        }
        $srcDir = $SourceDir
    } else {
        throw "Either -SourceRepo or -SourceDir must be specified."
    }

    # D16 candidate discovery.
    $candidates = @(Get-ConstructImportCandidates -SourceDir $srcDir)

    if ($Names -and $Names.Count -gt 0) {
        $candidates = @($candidates | Where-Object { $Names -contains $_.BaseName })
    }

    $imported = @()
    $errors   = @()

    foreach ($f in $candidates) {
        $name = $f.BaseName

        try {
            $raw = [System.IO.File]::ReadAllText($f.FullName, [System.Text.Encoding]::UTF8)
            $obj = $raw | ConvertFrom-Json
        } catch {
            $errors += "Cannot parse '$($f.Name)': $($_.Exception.Message)"
            continue
        }

        $v = Test-ConstructProfile -Name $name -Object $obj
        if (-not $v.Ok) {
            $errors += "Invalid profile '$name': $($v.Errors -join '; ')"
            continue
        }

        $canonical = ConvertTo-ConstructCanonicalJson -Name $name -Object $obj
        if ($null -eq $canonical) {
            $errors += "Could not canonicalize '$name'."
            continue
        }

        $destFile  = Join-Path $projDir "$name.json"
        $manifFile = Join-Path $manifDir "$name.json"
        $baseFile  = Join-Path $basesDir "$name.json"

        if (Test-Path -LiteralPath $destFile) {
            $hasManifest = Test-Path -LiteralPath $manifFile
            if ($hasManifest -and $remoteUrl) {
                try {
                    $manifObj = Get-Content -LiteralPath $manifFile -Raw | ConvertFrom-Json
                    if ($manifObj.remoteUrl -eq $remoteUrl) {
                        # 3-way merge: base = stored base, ours = local, theirs = upstream.
                        $baseContent = ""
                        if (Test-Path -LiteralPath $baseFile) {
                            $baseContent = [System.IO.File]::ReadAllText($baseFile, [System.Text.Encoding]::UTF8)
                        }
                        $oursContent = [System.IO.File]::ReadAllText($destFile, [System.Text.Encoding]::UTF8)
                        $theirsContent = $canonical

                        $tmpOurs   = Join-Path ([System.IO.Path]::GetTempPath()) ("merge-ours-" + [guid]::NewGuid().ToString("N"))
                        $tmpBase   = Join-Path ([System.IO.Path]::GetTempPath()) ("merge-base-" + [guid]::NewGuid().ToString("N"))
                        $tmpTheirs = Join-Path ([System.IO.Path]::GetTempPath()) ("merge-theirs-" + [guid]::NewGuid().ToString("N"))
                        $utf8NoBom = New-Object System.Text.UTF8Encoding $false
                        [System.IO.File]::WriteAllText($tmpOurs, $oursContent, $utf8NoBom)
                        [System.IO.File]::WriteAllText($tmpBase, $baseContent, $utf8NoBom)
                        [System.IO.File]::WriteAllText($tmpTheirs, $theirsContent, $utf8NoBom)

                        $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
                        $mergedContent = & git merge-file -p $tmpOurs $tmpBase $tmpTheirs 2>$null
                        $mergeResult = $LASTEXITCODE
                        $ErrorActionPreference = $prev

                        Remove-Item -LiteralPath $tmpOurs, $tmpBase, $tmpTheirs -Force -ErrorAction SilentlyContinue

                        if ($mergeResult -eq 0 -or $mergeResult -eq $null) {
                            # Pipeline capture of 'git merge-file -p' drops the
                            # trailing LF, so restore it. Then run the D17
                            # canonical+validate gate: parse, validate, re-serialize.
                            $mergedStr = if ($mergedContent) { ($mergedContent -join "`n") } else { $oursContent }
                            if ($mergedStr -and -not $mergedStr.EndsWith("`n")) {
                                $mergedStr = "$mergedStr`n"
                            }
                            # D17 gate: merged text must parse, validate, and
                            # re-canonicalize; otherwise treat as conflict.
                            $mergeGateOk = $false
                            try {
                                $mergedObj = $mergedStr | ConvertFrom-Json
                                $mergedV = Test-ConstructProfile -Name $name -Object $mergedObj
                                if ($mergedV.Ok) {
                                    $mergedCanon = ConvertTo-ConstructCanonicalJson -Name $name -Object $mergedObj
                                    if ($null -ne $mergedCanon) {
                                        $mergedStr = $mergedCanon
                                        $mergeGateOk = $true
                                    }
                                }
                            } catch { }
                            if (-not $mergeGateOk) {
                                $errors += "3-way merge for '$name' produced invalid JSON; treat as conflict."
                                continue
                            }
                            [System.IO.File]::WriteAllText($destFile, $mergedStr, $utf8NoBom)
                        } else {
                            $errors += "3-way merge conflict for '$name'; resolve manually."
                            continue
                        }
                    } else {
                        throw "Name collision: '$name' exists with different provenance (existing: $($manifObj.remoteUrl), import: $remoteUrl)."
                    }
                } catch {
                    if ($_.Exception.Message -match "Name collision") { throw }
                    $errors += "Failed to process manifest for '$name': $($_.Exception.Message)"
                    continue
                }
            } else {
                throw "Name collision: a profile named '$name' already exists. Rename one to avoid ambiguity (suggestion: '$name-2')."
            }
        } else {
            $utf8NoBom = New-Object System.Text.UTF8Encoding $false
            [System.IO.File]::WriteAllText($destFile, $canonical, $utf8NoBom)
        }

        # Write/update provenance manifest + base.
        if ($remoteUrl) {
            $relPath = if (Test-Path -LiteralPath (Join-Path $srcDir "projects")) {
                "projects/$($f.Name)"
            } else { $f.Name }

            # Full D1 provenance: ref, baseCommit (staging clone HEAD sha),
            # baseBlobSha (git hash-object of the imported blob).
            $manifRef = ""
            $manifBaseCommit = ""
            $manifBaseBlobSha = ""
            $prev2 = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
            try {
                if ($null -ne $srcDir -and (Test-Path -LiteralPath (Join-Path $srcDir ".git"))) {
                    $manifBaseCommit = "$(& git -C $srcDir rev-parse HEAD 2>$null)".Trim()
                    $rawRef = "$(& git -C $srcDir symbolic-ref --short HEAD 2>$null)".Trim()
                    if ($rawRef) { $manifRef = $rawRef }
                    $manifBaseBlobSha = "$(& git -C $srcDir hash-object -- $f.FullName 2>$null)".Trim()
                }
            } catch { }
            $ErrorActionPreference = $prev2

            $manifEntry = [ordered]@{
                remoteUrl    = $remoteUrl
                ref          = $manifRef
                pathInRemote = $relPath
                importedAs   = $name
                baseCommit   = $manifBaseCommit
                baseBlobSha  = $manifBaseBlobSha
            }
            $utf8NoBom = New-Object System.Text.UTF8Encoding $false
            $manifJson = ConvertTo-ConstructJsonValue -Value ([pscustomobject]$manifEntry) -Depth 0
            [System.IO.File]::WriteAllText($manifFile, "$manifJson`n", $utf8NoBom)
            [System.IO.File]::WriteAllText($baseFile, $canonical, $utf8NoBom)
        }

        $imported += $name
    }

    # Commit the import.
    if ($imported.Count -gt 0 -and (Test-ConstructGitAvailable)) {
        $gitArgs = @("-c", "user.name=The Construct", "-c", "user.email=construct@construct.local", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=")
        $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
        try {
            & git -C $ConfigDir add -A 2>$null | Out-Null
            & git -C $ConfigDir @gitArgs commit -m "import: $($imported -join ', ')" 2>$null | Out-Null
        } catch { }
        $ErrorActionPreference = $prev
    }

    # Write/update remotes manifest.
    if ($remoteUrl) {
        Register-ConstructConfigRemote -ConfigDir $ConfigDir -RemoteUrl $remoteUrl
    }

    return [pscustomobject]@{
        Imported = $imported
        Errors   = $errors
    }
}

# ── Push upstream ────────────────────────────────────────────────────────────

function Push-ConstructConfigUpstream {
    <#
        Per-remote push-back (D19): copy local versions of tracked files into the
        staging clone at their pathInRemote, commit on a timestamped branch, push.
        Returns @{Ok; Branch; Output}.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$ConfigDir,
        [Parameter(Mandatory)][string]$RemoteUrl
    )

    $manifDir = Join-Path $ConfigDir "manifest"
    $projDir  = Join-Path $ConfigDir "projects"

    $tracked = @()
    foreach ($f in @(Get-ChildItem -LiteralPath $manifDir -Filter *.json -File -ErrorAction SilentlyContinue)) {
        if ($f.Name -eq "remotes.json") { continue }
        try {
            $entry = Get-Content -LiteralPath $f.FullName -Raw | ConvertFrom-Json
            if ($entry.remoteUrl -eq $RemoteUrl) {
                $tracked += @{
                    Name = $f.BaseName
                    PathInRemote = $entry.pathInRemote
                }
            }
        } catch { continue }
    }

    if ($tracked.Count -eq 0) {
        return [pscustomobject]@{ Ok = $false; Branch = ""; Output = "No tracked files for '$RemoteUrl'." }
    }

    $cacheBase = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA }
                 elseif ($env:TEMP) { $env:TEMP }
                 else { [System.IO.Path]::GetTempPath() }
    $stagingRoot = Join-Path (Join-Path $cacheBase "The-Construct") "cache/config-remotes"
    $slug = ($RemoteUrl -replace '[^A-Za-z0-9._-]', '-')
    $cloneDir = Join-Path $stagingRoot $slug

    if (-not (Test-Path -LiteralPath (Join-Path $cloneDir ".git"))) {
        return [pscustomobject]@{ Ok = $false; Branch = ""; Output = "Staging clone not found for '$RemoteUrl'. Run an import first." }
    }

    # Pre-validate all tracked PathInRemote values before touching the clone.
    # This containment check runs BEFORE the git try/catch (which uses
    # SilentlyContinue) so the throw propagates to the caller.
    $resolvedCloneDir = [System.IO.Path]::GetFullPath($cloneDir)
    if (-not $resolvedCloneDir.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $resolvedCloneDir += [System.IO.Path]::DirectorySeparatorChar
    }
    foreach ($t in $tracked) {
        $dest = Join-Path $cloneDir $t.PathInRemote
        $resolvedDest = [System.IO.Path]::GetFullPath($dest)
        if (-not $resolvedDest.StartsWith($resolvedCloneDir, [System.StringComparison]::Ordinal)) {
            throw "Path traversal blocked: PathInRemote '$($t.PathInRemote)' for profile '$($t.Name)' escapes the clone directory."
        }
    }

    $gitArgs = @("-c", "user.name=The Construct", "-c", "user.email=construct@construct.local", "-c", "commit.gpgsign=false", "-c", "core.hooksPath=")
    $branchName = "construct-config-update-" + (Get-Date -Format "yyyyMMdd-HHmm")

    $prev = $ErrorActionPreference; $ErrorActionPreference = "SilentlyContinue"
    try {
        & git -C $cloneDir fetch origin 2>$null | Out-Null
        $defaultBranch = & git -C $cloneDir symbolic-ref refs/remotes/origin/HEAD 2>$null
        if (-not $defaultBranch) { $defaultBranch = "refs/remotes/origin/main" }
        $defaultBranch = "$defaultBranch".Trim()
        & git -C $cloneDir reset --hard $defaultBranch 2>$null | Out-Null
        & git -C $cloneDir checkout -b $branchName 2>$null | Out-Null

        foreach ($t in $tracked) {
            $src = Join-Path $projDir "$($t.Name).json"
            if (-not (Test-Path -LiteralPath $src)) { continue }
            $dest = Join-Path $cloneDir $t.PathInRemote
            $destDir = Split-Path -Parent $dest
            if (-not (Test-Path -LiteralPath $destDir)) {
                New-Item -ItemType Directory -Path $destDir -Force | Out-Null
            }
            Copy-Item -LiteralPath $src -Destination $dest -Force
        }

        & git -C $cloneDir add -A 2>$null | Out-Null
        & git -C $cloneDir @gitArgs commit -m "construct config update" 2>$null | Out-Null
        $pushOutput = & git -C $cloneDir push origin $branchName 2>&1
    } catch { }
    $ErrorActionPreference = $prev

    $outStr = if ($pushOutput) { "$pushOutput" } else { "" }
    return [pscustomobject]@{
        Ok     = ($LASTEXITCODE -eq 0)
        Branch = $branchName
        Output = $outStr
    }
}
