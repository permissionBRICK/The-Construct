# One lifecycle run at a time on this PC. Reprovision, reinstall/redownload, the T3 Code update, the
# Construct update and the host conversion share this installation and its workspace, so two of them
# running together collide. The outermost entry script holds
# %LOCALAPPDATA%\The-Construct\lifecycle-running.json for its whole run; the Companion and the VS Code
# panel read the same file to lock their lifecycle buttons for every instance. A script called inside
# that run (Auto-Install -> Provision, Update-T3Code -> Provision) finds its owner's token in
# CONSTRUCT_LIFECYCLE_RUN and neither acquires nor releases.

function Get-ConstructLifecycleRunPath {
    $base = $env:LOCALAPPDATA
    if (-not $base) { $base = $env:TEMP }
    if (-not $base) { $base = [System.IO.Path]::GetTempPath() }
    return (Join-Path (Join-Path $base 'The-Construct') 'lifecycle-running.json')
}

# The marker while its owner process is alive; $null for no marker, an unreadable one or a dead owner.
function Read-ConstructLifecycleRun {
    param([string]$Path = (Get-ConstructLifecycleRunPath))
    try { return (ConvertFrom-ConstructLifecycleRun ([System.IO.File]::ReadAllText($Path))) } catch { return $null }
}

# Every field read through PSObject.Properties: a caller may run under Set-StrictMode -Version Latest.
function ConvertFrom-ConstructLifecycleRun([string]$Text) {
    try { $run = $Text | ConvertFrom-Json } catch { return $null }
    if ($run -isnot [pscustomobject]) { return $null }
    $field = { param($name) $p = $run.PSObject.Properties[$name]; if ($p -and $null -ne $p.Value) { "$($p.Value)" } else { '' } }
    $ownerPid = 0
    if (-not [int]::TryParse((& $field 'pid'), [ref]$ownerPid) -or $ownerPid -le 0) { return $null }
    if (-not (Get-Process -Id $ownerPid -ErrorAction SilentlyContinue)) { return $null }
    return [pscustomobject]@{ pid = $ownerPid; instance = (& $field 'instance'); action = (& $field 'action'); startedAt = (& $field 'startedAt'); token = (& $field 'token') }
}

function Format-ConstructLifecycleRun($Run) {
    $labels = @{ reprovision = 'Reprovision'; reinstall = 'Reinstall'; redownload = 'Redownload'
                 updateT3 = 'T3 Code update'; updateConstruct = 'Construct update'; convertToHost = 'Host conversion' }
    $label = if ($labels.ContainsKey($Run.action)) { $labels[$Run.action] } else { $Run.action }
    if ($Run.instance) { return "$label of '$($Run.instance)'" }
    return $label
}

# Check, reclaim and create (and release, and hand over) run under an exclusive handle on a lock file beside
# the marker, so two scripts starting together cannot both judge a stale marker and remove each other's.
# Readers (the Companion, the panel) only read the marker. The lock file stays; deleting it would race.
function Invoke-ConstructLifecycleRunLocked([string]$Path, [scriptblock]$Body) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
    $deadline = (Get-Date).AddSeconds(15)
    while ($true) {
        try { $lock = [System.IO.File]::Open("$Path.lock", 'OpenOrCreate', 'ReadWrite', 'None'); break }
        catch [System.IO.IOException] {
            if ((Get-Date) -gt $deadline) { throw "Another Construct run is starting at the same time. Wait until it finishes, then try again." }
            Start-Sleep -Milliseconds 100
        }
    }
    try { & $Body } finally { $lock.Dispose() }
}

# Claims the run. $true: this script owns the marker and calls Exit-ConstructLifecycleRun in a finally.
# $false: an outer script of this run owns it. Throws while another run holds it. -AdoptToken: the token
# of the run a script handed to its relaunched (elevated) copy, which takes the marker over.
function Enter-ConstructLifecycleRun {
    param([Parameter(Mandatory = $true)][string]$Action, [string]$Instance = '', [string]$AdoptToken = '')
    $path = Get-ConstructLifecycleRunPath
    Invoke-ConstructLifecycleRunLocked $path {
        $current = Read-ConstructLifecycleRun -Path $path
        # Test seam: a pause inside the locked section, to prove two contenders are serialized.
        if ($global:ConstructLifecycleRunTestPause) { & $global:ConstructLifecycleRunTestPause }
        if ($current) {
            if ($env:CONSTRUCT_LIFECYCLE_RUN -and $current.token -eq $env:CONSTRUCT_LIFECYCLE_RUN) { return $false }
            if ($AdoptToken -and $current.token -eq $AdoptToken) {
                Write-ConstructLifecycleRun -Path $path -Run ([ordered]@{ pid = $PID; instance = $current.instance; action = $current.action; startedAt = $current.startedAt; token = $current.token })
                $env:CONSTRUCT_LIFECYCLE_RUN = $current.token
                return $true
            }
            throw "Another Construct run is in progress: $(Format-ConstructLifecycleRun $current) (process $($current.pid)). Wait until it finishes, then try again."
        }
        # No marker, or a stale one (dead owner, unreadable): replaced under the lock.
        Write-ConstructLifecycleRun -Path $path -Run ([ordered]@{ pid = $PID; instance = $Instance; action = $Action; startedAt = (Get-Date).ToUniversalTime().ToString('o'); token = [guid]::NewGuid().ToString('N') })
        $env:CONSTRUCT_LIFECYCLE_RUN = (Read-ConstructLifecycleRun -Path $path).token
        return $true
    }
}

# Written beside the marker and moved into place, so a reader never sees half of it.
function Write-ConstructLifecycleRun([string]$Path, $Run) {
    $temporary = "$Path.$PID.tmp"
    try {
        [System.IO.File]::WriteAllText($temporary, ($Run | ConvertTo-Json -Compress), (New-Object System.Text.UTF8Encoding $false))
        if ([System.IO.File]::Exists($Path)) { [System.IO.File]::Replace($temporary, $Path, [NullString]::Value) } else { [System.IO.File]::Move($temporary, $Path) }
    } finally { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
}

# The owner relaunches itself (elevation) and exits: the marker names the new process until it adopts it.
function Move-ConstructLifecycleRun([int]$ProcessId) {
    $path = Get-ConstructLifecycleRunPath
    $token = $env:CONSTRUCT_LIFECYCLE_RUN
    Remove-Item Env:CONSTRUCT_LIFECYCLE_RUN -ErrorAction SilentlyContinue
    Invoke-ConstructLifecycleRunLocked $path {
        $run = Read-ConstructLifecycleRun -Path $path
        if ($token -and $run -and $run.token -eq $token) {
            Write-ConstructLifecycleRun -Path $path -Run ([ordered]@{ pid = $ProcessId; instance = $run.instance; action = $run.action; startedAt = $run.startedAt; token = $run.token })
        }
    }
}

function Exit-ConstructLifecycleRun {
    $token = $env:CONSTRUCT_LIFECYCLE_RUN
    Remove-Item Env:CONSTRUCT_LIFECYCLE_RUN -ErrorAction SilentlyContinue
    $path = Get-ConstructLifecycleRunPath
    Invoke-ConstructLifecycleRunLocked $path {
        $run = Read-ConstructLifecycleRun -Path $path
        if ($token -and $run -and $run.token -eq $token) { Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue }
    }
}
