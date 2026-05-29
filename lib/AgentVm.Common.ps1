#Requires -Version 5.1
<#
.SYNOPSIS
    Shared helpers for the Windows agent-VM scripts (Auto-Install.ps1 and
    Create-AgentVM.ps1): an interactive arrow-key menu, a destructive-action
    confirmation, Hyper-V / virtualization validation, and Hyper-V VM teardown.

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

        Unless -NoPrompt, the user is prompted for each value with the resolved
        default offered on Enter. The final values are saved back to -Dir.
        Returns @{ Name = <string>; Email = <string> } (either may be "").
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Dir,
        [string]$Name,
        [string]$Email,
        [switch]$NoPrompt
    )

    $saved   = Read-ConstructSettings -Dir $Dir
    $hostGit = Get-HostGitIdentity

    $defName  = if     ($Name)                              { $Name }
                elseif ($saved -and $saved.gitUserName)     { [string]$saved.gitUserName }
                elseif ($hostGit.Name)                      { $hostGit.Name }
                else                                        { "" }
    $defEmail = if     ($Email)                             { $Email }
                elseif ($saved -and $saved.gitEmail)        { [string]$saved.gitEmail }
                elseif ($hostGit.Email)                     { $hostGit.Email }
                else                                        { "" }

    $resName  = $defName
    $resEmail = $defEmail

    if (-not $NoPrompt) {
        Write-Step "Git identity (applied as the VM's global git config)"
        $hint = if ($defName)  { " (press Enter for '$defName')" }  else { " (leave blank to skip)" }
        $ans  = Read-Host "    Git user name$hint"
        if (-not [string]::IsNullOrWhiteSpace($ans)) { $resName = $ans.Trim() }
        $hint = if ($defEmail) { " (press Enter for '$defEmail')" } else { " (leave blank to skip)" }
        $ans  = Read-Host "    Git email$hint"
        if (-not [string]::IsNullOrWhiteSpace($ans)) { $resEmail = $ans.Trim() }
    }

    Save-ConstructSettings -Dir $Dir -Values @{ gitUserName = $resName; gitEmail = $resEmail }
    return @{ Name = $resName; Email = $resEmail }
}
