# Streaming ISO downloads without BITS or Invoke-WebRequest's per-buffer progress overhead.
function Receive-ConstructIso {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][uri]$Uri,
        [Parameter(Mandatory)][string]$OutFile,
        [ValidateRange(1,32)][int]$Streams = 8,
        [ValidateRange(1,300)][int]$IdleTimeoutSeconds = 5,
        [ValidateRange(0,20)][int]$Retries = 6,
        [ValidatePattern('^(?:[a-fA-F0-9]{64})?$')][string]$ExpectedSha256 = ''
    )
    if (-not ('Construct.Download.Transfer' -as [type])) {
        Add-Type -AssemblyName System.Net.Http
        $source = Join-Path $PSScriptRoot 'Construct.Download.cs'
        if ($PSVersionTable.PSVersion.Major -le 5) { Add-Type -Path $source -ReferencedAssemblies System.Net.Http }
        else { Add-Type -Path $source -IgnoreWarnings -WarningAction SilentlyContinue }
    }
    $transfer = $null
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $previousBytes = 0L; $previousTime = 0.0; $lastLine = -10.0
    try {
        Write-Host "Downloading ISO with up to $Streams streams (idle timeout ${IdleTimeoutSeconds}s)..."
        $transfer = [Construct.Download.Transfer]::new($Uri.AbsoluteUri, $OutFile, $Streams, $IdleTimeoutSeconds, $Retries, $ExpectedSha256)
        while ($true) {
            $done = $transfer.Downloaded; $total = $transfer.Total; $elapsed = $clock.Elapsed.TotalSeconds
            $speed = [Math]::Max(0, ($done - $previousBytes) / [Math]::Max(0.001, $elapsed - $previousTime))
            $percent = if ($total -gt 0) { [Math]::Min(100, 100.0 * $done / $total) } else { 0 }
            $size = if ($total -gt 0) { '{0:N0} / {1:N0} MiB ({2:N1}%)' -f ($done / 1MB), ($total / 1MB), $percent } else { '{0:N0} MiB' -f ($done / 1MB) }
            $eta = if ($total -gt $done -and $speed -gt 0) { ' | ETA {0:N0}s' -f (($total - $done) / $speed) } else { '' }
            $status = '{0} | {1:N1} MiB/s | {2} streams | {3} retries{4}' -f $size, ($speed / 1MB), $transfer.ActiveStreams, $transfer.Retries, $eta
            Write-Progress -Id 73 -Activity $transfer.Phase -Status $status -PercentComplete ([int]$percent)
            # Plain output also works in detached/redirected consoles where Write-Progress is hidden.
            if ($elapsed - $lastLine -ge 2 -or $transfer.Completion.IsCompleted) { Write-Host ($transfer.Phase + ': ' + $status); $lastLine = $elapsed }
            $previousBytes = $done; $previousTime = $elapsed
            if ($transfer.Completion.IsCompleted) { break }
            Start-Sleep -Milliseconds 250
        }
        $null = $transfer.Completion.GetAwaiter().GetResult()
        Write-Host ('Downloaded {0:N0} MiB in {1:N1}s: {2}' -f ($transfer.Downloaded / 1MB), $clock.Elapsed.TotalSeconds, $OutFile)
    } finally {
        if ($transfer) { $transfer.Dispose() }
        Write-Progress -Id 73 -Activity 'ISO download' -Completed
    }
}
