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

    Write-Step "Checking Hyper-V installation"

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
        Write-Host "    Please reboot, then re-run this script." -ForegroundColor Yellow
        Read-Host "Press Enter to reboot now (or Ctrl+C to cancel)"
        Restart-Computer -Force
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
        starts selected.

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

    Write-Host ""
    Write-Host "  Select project configs to install" -ForegroundColor Cyan
    Write-Host "  (Up/Down to move, Space to toggle, Enter to activate a row)" -ForegroundColor DarkGray
    Write-Host ""

    # Start on the first profile, or on "Continue" when there's nothing to toggle.
    $cursor      = if ($names.Count -eq 0) { 1 } else { 0 }
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

            # Rows = one per profile, then the two action buttons.
            $rowCount = $names.Count + 2
            $idxOpen  = $names.Count        # "Open projects config folder"
            $idxCont  = $names.Count + 1    # "Continue"
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
        Write-Step "Git identity (applied as the VM's global git config)"
        $hint = if ($defName)  { " (press Enter for '$defName')" }  else { " (leave blank to skip)" }
        $ans  = Read-Host "    Git user name$hint"
        if (-not [string]::IsNullOrWhiteSpace($ans)) { $resName = $ans.Trim() }
        $hint = if ($defEmail) { " (press Enter for '$defEmail')" } else { " (leave blank to skip)" }
        $ans  = Read-Host "    Git email$hint"
        if (-not [string]::IsNullOrWhiteSpace($ans)) { $resEmail = $ans.Trim() }

        Write-Host "    Store git credentials on the VM so pushes/pulls don't re-prompt?" -ForegroundColor White
        Write-Host "      WARNING: credentials are saved in PLAINTEXT (~/.git-credentials) and are" -ForegroundColor Yellow
        Write-Host "      readable by anything on the VM -- including the AI agents, so a prompt-" -ForegroundColor Yellow
        Write-Host "      injection attack could exfiltrate them." -ForegroundColor Yellow
        $credHint = if ($defCred) { "[Y/n]" } else { "[y/N]" }
        $ans = Read-Host "    Store git credentials? $credHint"
        if (-not [string]::IsNullOrWhiteSpace($ans)) { $resCred = ($ans.Trim() -match '^(y|yes)$') }
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

    Write-Step "Git credentials for cloning project repos"
    Write-Host "    The selected projects clone repos from: $($hostList -join ', ')" -ForegroundColor White
    Write-Host "    Enter credentials to use for the clone, or press Enter to skip" -ForegroundColor White
    Write-Host "    (skip if the repos are public or you'll authenticate another way)." -ForegroundColor DarkGray
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
    $ans = Read-Host "   Continue with the reinstall and lose this work? [y/N]"
    return ($ans.Trim().ToLowerInvariant() -match '^(y|yes)$')
}
