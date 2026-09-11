#Requires -Version 5.1
<#
    One-line web installer for The Construct:
        irm https://raw.githubusercontent.com/permissionBRICK/The-Construct/main/install.ps1 | iex
    Downloads the repo to %LOCALAPPDATA%\The-Construct and runs Auto-Install.ps1
    (which self-elevates, builds the autoinstall ISO, and creates + provisions the VM).
    Pass -Repo/-Ref to install from a fork/branch. Any other arguments pass straight
    through to Auto-Install.ps1 (e.g. -ConfigRepo, -ConfigDir, -Action add-config).
#>
param(
    [string]$Repo = "permissionBRICK/The-Construct",
    [string]$Ref  = "main"
)
$ErrorActionPreference = "Stop"
$release = $null
try { Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass -Force } catch { }
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

# Stable per-repo/ref folder (not a temp dir) so the ISOs Auto-Install writes survive re-runs.
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

# GitHub archives unpack to a single <name>-<ref> folder. Run the real .ps1 file (not
# iex) so Auto-Install's self-elevation ($PSCommandPath) and $PSScriptRoot resolve.
$expectedRootName = (($Repo -split '/')[-1] + '-' + (($Ref -replace '/', '-')))
$root = Get-Item -LiteralPath (Join-Path $work $expectedRootName) -ErrorAction SilentlyContinue
if (-not $root) {
    # GitHub may normalize unusual ref names differently. Prefer the directory
    # refreshed by this download, never an arbitrary stale sibling from an older run.
    $root = Get-ChildItem -LiteralPath $work -Directory |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}
if (-not $root) { throw "Downloaded archive looked empty: $work" }
$auto = Join-Path $root.FullName "Auto-Install.ps1"
if (-not (Test-Path -LiteralPath $auto)) { throw "Auto-Install.ps1 not found in $($root.FullName)." }

if ($release) {
    try {
        . (Join-Path $root.FullName 'lib/AgentVm.Common.ps1')
        [void](Write-ConstructSourceManifest -Zip $zip -Commit $release.commit)
    } catch {
        Write-Warning "Could not record the source manifest ($($_.Exception.GetType().Name)); the host cache is unavailable until the next update."
    }
}
Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue

Write-Host "==> Launching Auto-Install.ps1" -ForegroundColor Cyan
# Forward the repo/ref PAIR only when explicitly set (fork/mirror), so the marker is
# accurate. The pair travels together: setting either forwards both. Any other args
# ($args, populated because there is no [CmdletBinding()]) pass straight through.
$fwd = @()
if ($PSBoundParameters.ContainsKey('Repo') -or $PSBoundParameters.ContainsKey('Ref')) { $fwd += '-Repo', $Repo, '-Ref', $Ref }
$fwd += $args
& $auto @fwd
