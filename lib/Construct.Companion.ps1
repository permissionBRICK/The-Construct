# The Companion install hook the old Update-Construct.ps1 (updater classes E and F) calls after the download
# (docs/plans/construct-platform-design.md 13.2). The Companion is gone; nothing else is left in this file.
function Invoke-ConstructCompanionInstallHook {
    # The Construct device agent replaces the Companion (docs/plans/construct-platform-design.md 13.2): the
    # updater's marker step has stopped the Companion and started the agent, so there is nothing to install.
    [CmdletBinding()]
    param([string]$ScriptsDir,[switch]$SkipCompanion,[switch]$SkipWhenElevated)
    Write-Host '==> Construct Companion: unchanged (the Construct agent takes its place).' -ForegroundColor DarkGray
    return 'unchanged'
}
