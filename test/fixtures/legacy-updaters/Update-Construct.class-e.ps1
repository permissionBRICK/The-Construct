#Requires -Version 5.1
<#
    Self-update for the control panel's "Update Construct": re-download the repo in
    place, record the update marker, and reinstall the control-panel extension. Does
    NOT rebuild the VM. Launched by the panel; also runnable by hand. -Repo/-Ref pick
    the source (default: the canonical repo / main).

    Result signal: when the panel launches this, it passes a path (via the
    CONSTRUCT_UPDATE_RESULT env var - an env var, not a parameter, so an OLDER copy of
    this script simply ignores it instead of erroring on an unknown argument) that we
    write "ok"/"fail" to at the end. The panel polls it and, on "ok", RELOADS the VS Code
    window so the refreshed panel loads automatically (a detached console can't reload VS
    Code). On success with a result path we therefore DON'T pause (the reload is the
    feedback); on failure we pause and tell the user to reopen VS Code. Run by hand (no
    result path) it pauses on success too so the output stays readable. -ResultFile is
    still accepted for compatibility and takes precedence over the env var.
#>
[CmdletBinding()]
param(
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main",
    [switch]$SkipCompanion,
    [string]$ResultFile = ""
)
$ErrorActionPreference = "Stop"
$release = $null
if (-not $ResultFile) { $ResultFile = $env:CONSTRUCT_UPDATE_RESULT }
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

$ok = $false
try {
    $base = if ($env:LOCALAPPDATA) { $env:LOCALAPPDATA } else { $env:TEMP }
    $slug = ($Repo + "-" + $Ref) -replace '[^A-Za-z0-9._-]', '-'
    $work = Join-Path $base (Join-Path "The-Construct" $slug)
    $zip  = Join-Path $base "construct-download.zip"
    if (-not (Test-Path -LiteralPath $work)) { New-Item -ItemType Directory -Path $work -Force | Out-Null }

    Write-Host "==> Downloading $Repo ($Ref) ..." -ForegroundColor Cyan
    $oldPP = $ProgressPreference; $ProgressPreference = "SilentlyContinue"
    try {
        if ($Repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { throw 'Invalid Construct repository.' }
        if ($Ref -eq 'main') {
            $release = Invoke-RestMethod -Uri "https://github.com/$Repo/releases/latest/download/manifest.json" -UseBasicParsing -TimeoutSec 30
            if ($release.schemaVersion -ne 1 -or $release.repository -cne $Repo -or $release.ref -cne 'refs/heads/main' -or
                $release.commit -cnotmatch '^[0-9a-f]{40}$' -or $release.releaseTag -cne ('host-' + $release.commit) -or
                $release.sourceAsset -cne ('construct-source-' + $release.commit + '.zip') -or $release.sourceSha256 -cnotmatch '^[0-9a-f]{64}$' -or
                $release.sourceSizeBytes -le 0 -or $release.sourceSizeBytes -gt 1GB -or
                $release.payloadAsset -cne ('construct-host-' + $release.commit.Substring(0,7) + '-win-x64.zip') -or
                $release.payloadSha256 -cnotmatch '^[0-9a-f]{64}$' -or $release.payloadSizeBytes -le 0 -or $release.payloadSizeBytes -gt 1GB) {
                throw 'No complete published Construct release is available.'
            }
            $url = "https://github.com/$Repo/releases/download/$($release.releaseTag)/$($release.sourceAsset)"
            Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing -TimeoutSec 300
            if ((Get-Item -LiteralPath $zip).Length -ne $release.sourceSizeBytes -or
                (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $release.sourceSha256) { throw 'Construct source checksum mismatch.' }
        } else {
            # Explicit development branches bypass the published main stream.
            Invoke-WebRequest -Uri "https://codeload.github.com/$Repo/zip/refs/heads/$Ref" -OutFile $zip -UseBasicParsing -TimeoutSec 300
        }
    }
    finally { $ProgressPreference = $oldPP }
    Expand-Archive -LiteralPath $zip -DestinationPath $work -Force
    # Keep the verified ZIP until the external source manifest has been recorded.

    $expectedRootName = (($Repo -split '/')[-1] + '-' + (($Ref -replace '/', '-')))
    $root = Get-Item -LiteralPath (Join-Path $work $expectedRootName) -ErrorAction SilentlyContinue
    if (-not $root) {
        $root = Get-ChildItem -LiteralPath $work -Directory |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
    }
    if (-not $root) { throw "Downloaded archive looked empty: $work" }

    try { . (Join-Path $root.FullName "lib\AgentVm.Common.ps1") }
    catch { Write-Warning "Could not load helpers: $($_.Exception.Message)" }

    if ($release) {
        if (Get-Command Remove-ConstructStaleSourceFiles -ErrorAction SilentlyContinue) {
            $stale = Remove-ConstructStaleSourceFiles -Root $root.FullName -Zip $zip
            if ($stale -gt 0) { Write-Host "    removed $stale file(s) the release no longer ships" -ForegroundColor DarkGray }
        }
        try { [void](Write-ConstructSourceManifest -Zip $zip -Commit $release.commit) }
        catch { Write-Warning "Could not record the source manifest ($($_.Exception.GetType().Name)); the host cache is unavailable until the next update." }
    }
    Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

    # Reinstall the control-panel extension (repackage + code --install-extension). Both a
    # MISSING helper (the dot-source above failed) and a falsey return are real failures -
    # otherwise the panel would reload into the OLD panel thinking the update succeeded.
    if (-not (Get-Command Install-ControlPanelExtension -ErrorAction SilentlyContinue)) {
        throw "Update helpers didn't load, so the control-panel extension couldn't be reinstalled."
    }
    if (-not [bool](Install-ControlPanelExtension -SourceRoot $root.FullName)) {
        throw "The control-panel extension didn't install."
    }

    # Advance the local marker only after the new panel installed successfully.
    if (Get-Command Set-ConstructInstalledMarker -ErrorAction SilentlyContinue) {
        $markerArgs = @{ Root = $root.FullName; Repo = $Repo; Ref = $Ref }
        if ($release) { $markerArgs.Commit = $release.commit }
        $sha = Set-ConstructInstalledMarker @markerArgs
        Write-Host "==> Updated Construct files in $($root.FullName)" -ForegroundColor Green
        if ($sha) { Write-Host "    installed commit: $sha" -ForegroundColor DarkGray }
    } else {
        Write-Warning "Refreshed the files but couldn't record the update marker (helpers unavailable)."
    }
    try { . (Join-Path $root.FullName 'lib/Construct.Companion.ps1'); Invoke-ConstructCompanionInstallHook -ScriptsDir $root.FullName -SkipCompanion:$SkipCompanion }
    catch { Write-Warning 'Could not load Companion installer helpers; continuing Construct update.' }
    $ok = $true
} catch {
    Write-Warning "Update failed: $($_.Exception.Message)"
}

# Signal the panel (if it launched us) so it can reload on success / warn on failure.
if ($ResultFile) {
    try { Set-Content -LiteralPath $ResultFile -Value $(if ($ok) { "ok" } else { "fail" }) -Encoding ASCII -Force } catch { }
}

Write-Host ""
if ($ok) {
    if ($ResultFile) {
        # Launched by the VS Code panel or the T3 Code Desktop app: no pause, the console
        # closes by itself (it's launched without -NoExit). Open VS Code windows reload
        # on their own once they see the new install marker.
        Write-Host "Update complete. Open VS Code windows reload the refreshed panel automatically." -ForegroundColor Green
    } else {
        Write-Host "Update complete. Reload/restart VS Code to pick up the refreshed panel." -ForegroundColor Cyan
        if (-not [Console]::IsInputRedirected) { Read-Host "Press Enter to close" | Out-Null }
    }
} else {
    Write-Host "The update did not complete. Please reopen VS Code, then try the update again." -ForegroundColor Yellow
    if (-not [Console]::IsInputRedirected) { Read-Host "Press Enter to close" | Out-Null }
    exit 1
}
