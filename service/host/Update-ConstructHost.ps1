#requires -Version 5.1
<# Independent SYSTEM scheduled task. Never invokes the installer except -AclOnly.
   Dot-source with -LibraryOnly for tests; service control is isolated in named functions. #>
[CmdletBinding()]
param([string]$Handoff, [switch]$Resume, [switch]$Rollback, [switch]$LibraryOnly)
$ErrorActionPreference = 'Stop'

function Read-UpdateJson([string]$Path) {
    if (Test-Path -LiteralPath $Path) { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) }
    return $null
}
function Write-UpdateJson([string]$Path, $Value) {
    $temp = $Path + '.' + [Guid]::NewGuid().ToString('n') + '.tmp'
    [IO.File]::WriteAllText($temp, (ConvertTo-Json -InputObject $Value -Depth 30), (New-Object Text.UTF8Encoding($false)))
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp, $Path, [NullString]::Value) }
    else { [IO.File]::Move($temp, $Path) }
}
function Test-UpdateTerminal($Record, [string]$UpdateId) { return ($Record -and (-not $UpdateId -or $Record.updateId -eq $UpdateId) -and $Record.outcome -in @('succeeded','rolledBack','rolledBackWithDatabase')) }
function Get-UpdateAuthority($Record, $Fence, [string]$UpdateId) {
    if ($Record -and $Record.updateId -and $Record.updateId -ne $UpdateId) { $Record=$null }
    if (Test-UpdateTerminal $Record) { return 'already-terminal' }
    if ($Fence -and $Fence.updateId -eq $UpdateId) {
        switch ($Fence.disposition) {
            'closed' { return 'superseded' }
            'commitOnly' { return 'commitOnly' }
            'rollbackAuthorized' { return 'rollback' }
            default { throw 'Invalid recovery fence.' }
        }
    }
    return 'apply'
}
function Test-UpdatePath([string]$Path) {
    if (-not $Path -or $Path.Length -gt 240 -or $Path -match '[\\:*?"<>|\x00-\x1f]' -or $Path.StartsWith('/')) { return $false }
    foreach ($part in $Path.Split('/')) {
        if (-not $part -or $part -eq '.' -or $part -eq '..' -or $part -match '[ .]$' -or $part -match '^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)') { return $false }
    }
    return $true
}
function Test-UpdatePreserved([string]$Path) {
    return ($Path -match '(^|/)(appsettings\.Production\.json|install\.json|settings\.json|projects|keys|\.git|\.construct-tools|data|media|iso)(/|$)' -or $Path -match '\.db')
}
function Assert-UpdateNoLinks([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        if ((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Update path contains a link.' }
        $current = Split-Path $current -Parent
    }
}
function Test-UpdateUnder([string]$Path, [string]$Root) {
    if (-not $Root) { return $false }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $base = [IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar)
    return ($full.Equals($base, [StringComparison]::OrdinalIgnoreCase) -or $full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase))
}
function Get-UpdateTarget([string]$Path, $H, $Settings) {
    if (-not (Test-UpdatePath $Path) -or (Test-UpdatePreserved $Path)) { throw 'Preserved or unsafe update target.' }
    if ($Path.StartsWith('service/')) { $root = $H.publishDir; $relative = $Path.Substring(8) }
    elseif ($Path.StartsWith('scripts/')) { $root = $H.scriptsDir; $relative = $Path.Substring(8) }
    else { throw 'Not an installation file.' }
    $target = [IO.Path]::GetFullPath((Join-Path $root $relative))
    if (-not (Test-UpdateUnder $target $root)) { throw 'Target escaped its root.' }
    foreach ($protected in @($H.dataDir, $Settings.Constructd.Iso.CacheDir, $Settings.Constructd.HostAdmin.Media.RootDir,
        (Join-Path $H.scriptsDir '.construct-tools'), (Join-Path $H.scriptsDir 'keys'), (Join-Path $H.scriptsDir 'projects'))) {
        if ($protected -and (Test-UpdateUnder $target $protected)) { throw 'Target intersects preserved data.' }
    }
    if ($Path.StartsWith('scripts/') -and (Test-UpdateUnder $target $H.publishDir)) { throw 'Scripts entry intersects service publish directory.' }
    Assert-UpdateNoLinks $target
    return $target
}
function Test-UpdateManifest([string]$StagedPath, [string]$Commit) {
    Assert-UpdateNoLinks $StagedPath
    $m = Read-UpdateJson (Join-Path $StagedPath 'manifest.json')
    $v = Read-UpdateJson (Join-Path $StagedPath 'verified.json')
    if (-not $m -or -not $v -or $m.schemaVersion -ne 1 -or $m.commit -ne $Commit -or $v.commit -ne $Commit -or
        $m.ref -ne 'refs/heads/main' -or $m.releaseTag -ne ('host-' + $Commit)) { throw 'Staged identity mismatch.' }
    if ((Get-FileHash (Join-Path $StagedPath 'package.zip') -Algorithm SHA256).Hash -ne $m.payloadSha256) { throw 'Payload hash mismatch.' }
    $extracted = Join-Path $StagedPath 'extracted'
    $sums = Join-Path $extracted 'SHA256SUMS'
    if ((Get-FileHash $sums -Algorithm SHA256).Hash -ne $m.sumsSha256) { throw 'Hash list mismatch.' }
    $listed = @{}
    foreach ($line in [IO.File]::ReadAllLines($sums)) {
        if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid hash list.' }
        $hash = $Matches[1]; $path = $Matches[2]
        if (-not (Test-UpdatePath $path) -or $listed.ContainsKey($path)) { throw 'Unsafe or duplicate package path.' }
        $listed[$path] = $hash
    }
    if ($v.files.Count -ne $listed.Count) { throw 'Verification coverage mismatch.' }
    foreach ($file in $v.files) {
        if (-not $listed.ContainsKey($file.path) -or $listed[$file.path] -ne $file.sha256) { throw 'Verification record mismatch.' }
    }
    $actual = @(Get-ChildItem -LiteralPath $extracted -Recurse -File -Force)
    if ($actual.Count -ne ($listed.Count + 1)) { throw 'Package contains unlisted files.' }
    foreach ($path in $listed.Keys) {
        $file = Join-Path $extracted $path; Assert-UpdateNoLinks $file
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $listed[$path]) { throw 'File hash mismatch.' }
    }
    if ($m.updaterPath -ne 'updater/Update-ConstructHost.ps1' -or $listed[$m.updaterPath] -ne $m.updaterSha256) { throw 'Updater hash mismatch.' }
    return $m
}
function Get-UpdateRollbackMode($Manifest, [int]$PreviousSchema) {
    if ($Manifest.database.minReadableBy -gt $PreviousSchema -or @($Manifest.database.breakingMigrations).Count -gt 0) { return 'database' }
    return 'binary'
}
function Assert-UpdateBackup([string]$Backup) {
    if (-not (Test-Path -LiteralPath (Join-Path $Backup 'backup-complete.json'))) { throw 'Backup incomplete.' }
    $files = @(Read-UpdateJson (Join-Path $Backup 'files.json'))
    foreach ($file in $files) {
        if (-not (Test-UpdatePath $file.path)) { throw 'Unsafe backup path.' }
        $path = Join-Path $Backup $file.path; Assert-UpdateNoLinks $path
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Backup hash mismatch.' }
    }
    return ,$files
}
function Stop-UpdateService([string]$Name) {
    if ((Get-Service -Name $Name).Status -ne 'Stopped') { Stop-Service -Name $Name -ErrorAction Stop }
    (Get-Service -Name $Name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(120))
}
function Start-UpdateService([string]$Name) {
    if ((Get-Service -Name $Name).Status -ne 'Running') { Start-Service -Name $Name -ErrorAction Stop }
    (Get-Service -Name $Name).WaitForStatus('Running', [TimeSpan]::FromSeconds(120))
}
function Test-UpdateHealth($H, [string]$Commit, [int]$Schema, [int]$TimeoutSeconds, [scriptblock]$OnAttempt) {
    # The callback is implemented in C#: PS scriptblock callbacks can lack a runspace on TLS threads.
    if (-not ('ConstructUpdateTls' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Net;
using System.Net.Security;
public static class ConstructUpdateTls {
    public static void Pin(HttpWebRequest request, string thumbprint) {
        request.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
            cert != null && String.Equals(cert.GetCertHashString(), thumbprint, StringComparison.OrdinalIgnoreCase);
    }
}
'@
    }
    $uri = [Uri]$H.healthUrl
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne '127.0.0.1' -or $H.certificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') { throw 'Invalid health endpoint.' }
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        if ($OnAttempt) { & $OnAttempt }
        try {
            $req = [Net.HttpWebRequest]::Create($uri); $req.Proxy = $null; $req.AllowAutoRedirect = $false; $req.Timeout = 5000
            [ConstructUpdateTls]::Pin($req, $H.certificateThumbprint)
            $req.Headers['Authorization'] = 'UpdateHandoff ' + $H.healthToken
            $response = $req.GetResponse()
            try { $reader = New-Object IO.StreamReader($response.GetResponseStream()); try { $body = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() } } finally { $response.Dispose() }
            if ($body.status -eq 'maintenance' -and $body.commit -eq $Commit -and $body.schemaVersion -ge $Schema) {
                $env:DOTNET_ENVIRONMENT = 'Production'
                $db = @(& $H.adminCliPath admin db check --json 2>$null)
                if ($LASTEXITCODE -eq 0) {
                    $check = ($db -join "`n") | ConvertFrom-Json
                    if ($check.status -eq 'ok' -and $check.schemaVersion -eq $body.schemaVersion) { return $true }
                }
            }
        } catch { } # No exception or response can expose the one-time credential.
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    return $false
}
function Invoke-ConstructHostUpdate([string]$HandoffPath, [bool]$IsResume, [bool]$WantRollback) {
    $h = Read-UpdateJson $HandoffPath
    if (-not $h -or $h.updateId -notmatch '^[0-9a-f]{32}$') { throw 'Invalid handoff.' }
    $root = Join-Path $h.dataDir 'updates'
    foreach ($path in @($HandoffPath,$root,$h.publishDir,$h.scriptsDir,$h.dataDir,$h.stagedPath)) { Assert-UpdateNoLinks $path }
    $lock = [IO.File]::Open((Join-Path $root 'updater.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
    $admin = $null
    try {
        $recordPath = Join-Path $root 'last-update.json'; $fencePath = Join-Path $root 'fence.json'
        $r = Read-UpdateJson $recordPath
        if ($r -and $r.updateId -ne $h.updateId) { $r = $null }
        $authority = Get-UpdateAuthority $r (Read-UpdateJson $fencePath) $h.updateId
        if ($authority -eq 'already-terminal') { return 0 }
        if ($authority -eq 'superseded') { return 4 }
        if ($WantRollback -and $authority -ne 'rollback') { throw 'Rollback requires an administrative fence.' }
        $admin = [IO.File]::Open((Join-Path $h.dataDir 'admin.lock'), 'OpenOrCreate', 'ReadWrite', 'None')
        $backup = Join-Path $root ('backup-' + $h.updateId)
        if (-not $r) {
            $r = [pscustomobject]@{updateId=$h.updateId;commit=$h.commit;previousCommit=$h.previousCommit;phase='stop';phaseAt=[DateTimeOffset]::UtcNow.ToString('o');outcome=$null;error=$null;backupPath=$backup;backupComplete=$false;replaceStarted=$false;stagedPath=$h.stagedPath;healthAttempts=0;lostJobs=@();manualSteps=@()}
        }
        function Set-Phase([string]$Phase) {
            $a = Get-UpdateAuthority (Read-UpdateJson $recordPath) (Read-UpdateJson $fencePath) $h.updateId
            if ($a -eq 'superseded' -or $a -eq 'already-terminal' -or ($a -eq 'commitOnly' -and $Phase -ne 'commit')) { throw 'Update is fenced.' }
            $r.phase = $Phase; $r.phaseAt = [DateTimeOffset]::UtcNow.ToString('o'); Write-UpdateJson $recordPath $r
            [IO.File]::AppendAllText((Join-Path $root 'updater.log'), ($r.phaseAt + ' phase=' + $Phase + [Environment]::NewLine), (New-Object Text.UTF8Encoding($false)))
        }
        $stopAttempted=$false
        try {
            $manifest = Test-UpdateManifest $h.stagedPath $h.commit
            $verified = Read-UpdateJson (Join-Path $h.stagedPath 'verified.json')
            if ($verified.updateId -ne $h.updateId) { throw 'Verification update mismatch.' }
            $settings = Read-UpdateJson (Join-Path $h.publishDir 'appsettings.Production.json')
            if (-not $settings) { throw 'Production settings missing.' }
            $previous = Read-UpdateJson (Join-Path $h.publishDir 'install.json')
            $newFiles = @($verified.files | Where-Object { $_.path.StartsWith('service/') -or $_.path.StartsWith('scripts/') })
            foreach ($file in $newFiles) { Get-UpdateTarget $file.path $h $settings | Out-Null }
            if ($authority -eq 'commitOnly') {
                foreach ($file in $newFiles) { if ((Get-FileHash (Get-UpdateTarget $file.path $h $settings) -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Installation mixed.' } }
            } else {
                Set-Phase 'stop'; $stopAttempted=$true; Stop-UpdateService $h.serviceName
                Set-Phase 'backup'
                if ($r.replaceStarted -and -not (Test-Path (Join-Path $backup 'backup-complete.json'))) { throw 'Incomplete backup after replacement.' }
                if (Test-Path (Join-Path $backup 'backup-complete.json')) { $backupFiles = Assert-UpdateBackup $backup; $previous = Read-UpdateJson (Join-Path $backup 'previous-install.json') }
                else {
                    if (Test-Path -LiteralPath $backup) { Remove-Item -LiteralPath $backup -Recurse -Force }
                    [IO.Directory]::CreateDirectory($backup) | Out-Null
                    $copyFiles = @()
                    if ($previous) { $copyFiles = @($previous.files) }
                    else {
                        foreach ($pair in @(@('service',$h.publishDir), @('scripts',$h.scriptsDir))) {
                            foreach ($file in Get-ChildItem -LiteralPath $pair[1] -Recurse -File -Force) {
                                $relative = $file.FullName.Substring($pair[1].TrimEnd([IO.Path]::DirectorySeparatorChar).Length+1).Replace('\','/')
                                $path = $pair[0] + '/' + $relative
                                if (Test-UpdatePreserved $path) { continue }
                                if ($pair[0] -eq 'scripts' -and ((Test-UpdateUnder $file.FullName $h.publishDir) -or (Test-UpdateUnder $file.FullName $h.dataDir))) { continue }
                                # Preserved roots are skipped, not handed to the copying primitive.
                                if (($settings.Constructd.Iso.CacheDir -and (Test-UpdateUnder $file.FullName $settings.Constructd.Iso.CacheDir)) -or
                                    ($settings.Constructd.HostAdmin.Media.RootDir -and (Test-UpdateUnder $file.FullName $settings.Constructd.HostAdmin.Media.RootDir))) { continue }
                                $copyFiles += [pscustomobject]@{path=$path}
                            }
                        }
                    }
                    foreach ($file in $copyFiles) {
                        $source = Get-UpdateTarget $file.path $h $settings
                        $dest = Join-Path $backup $file.path; [IO.Directory]::CreateDirectory((Split-Path $dest -Parent)) | Out-Null
                        [IO.File]::Copy($source,$dest,$true)
                    }
                    # Preserve the install ledger separately; it is never a payload target.
                    if ($previous) { Copy-Item -LiteralPath (Join-Path $h.publishDir 'install.json') -Destination (Join-Path $backup 'previous-install.json') }
                    foreach ($suffix in @('', '-wal', '-shm')) {
                        $db = $settings.Constructd.DatabasePath + $suffix
                        if (Test-Path -LiteralPath $db) { Copy-Item -LiteralPath $db -Destination (Join-Path $backup ('constructd.db' + $suffix)) }
                    }
                    $backupFiles = @(Get-ChildItem -LiteralPath $backup -Recurse -File | ForEach-Object {
                        [pscustomobject]@{path=$_.FullName.Substring($backup.Length+1).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
                    })
                    Write-UpdateJson (Join-Path $backup 'files.json') @($backupFiles)
                    Write-UpdateJson (Join-Path $backup 'backup-complete.json') @{updateId=$h.updateId;previousSchema=$h.previousSchemaVersion}
                }
                $r.backupComplete=$true; Write-UpdateJson $recordPath $r
                if ($authority -eq 'rollback') { throw 'Administrative rollback requested.' }
                Set-Phase 'replace'; $r.replaceStarted=$true; Write-UpdateJson $recordPath $r
                foreach ($file in $newFiles) {
                    $dest = Get-UpdateTarget $file.path $h $settings; [IO.Directory]::CreateDirectory((Split-Path $dest -Parent)) | Out-Null
                    [IO.File]::Copy((Join-Path (Join-Path $h.stagedPath 'extracted') $file.path),$dest,$true)
                }
                if ($previous) { foreach ($file in $previous.files) {
                    if ($file.path -notin $newFiles.path) { $dest=Get-UpdateTarget $file.path $h $settings; if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force } }
                } }
                & (Join-Path $h.scriptsDir 'service/host/Install-ConstructHost.ps1') -ScriptsDir $h.scriptsDir -PublishDir $h.publishDir -DataDir $h.dataDir -AclOnly
                Set-Phase 'start'; Start-UpdateService $h.serviceName
                Set-Phase 'health'
                if (-not (Test-UpdateHealth $h $h.commit $manifest.database.schemaVersion $h.healthTimeoutSeconds {$r.healthAttempts++; Write-UpdateJson $recordPath $r})) { throw 'Update health failed.' }
            }
            Set-Phase 'commit'
            Write-UpdateJson (Join-Path $h.publishDir 'install.json') @{commit=$h.commit;packageVersion=$manifest.packageVersion;installedAt=[DateTimeOffset]::UtcNow.ToString('o');previousCommit=$h.previousCommit;updateId=$h.updateId;files=$newFiles}
            $r.outcome='succeeded'; Write-UpdateJson $recordPath $r
            & schtasks.exe /Delete /TN Construct-HostUpdate /F 2>$null | Out-Null
            # Pruning only after successful health and a durable terminal outcome.
            Get-ChildItem -LiteralPath $root -Directory -Filter 'backup-*' | Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'backup-complete.json') } | Sort-Object LastWriteTimeUtc -Descending | Select-Object -Skip 2 | ForEach-Object { Remove-Item -LiteralPath $_.FullName -Recurse -Force }
            Get-ChildItem -LiteralPath $root -Directory | Where-Object { $_.Name -match '^[0-9a-f]{32}$' -and $_.Name -ne $h.updateId } | ForEach-Object { Assert-UpdateNoLinks $_.FullName; Remove-Item -LiteralPath $_.FullName -Recurse -Force }
            return 0
        } catch {
            # A terminal result is immutable even if cleanup failed afterwards.
            if (Test-UpdateTerminal (Read-UpdateJson $recordPath) $h.updateId) { return 0 }
            $a = Get-UpdateAuthority $r (Read-UpdateJson $fencePath) $h.updateId
            try {
                if (-not $r.replaceStarted -and $a -ne 'commitOnly') {
                    # No installation bytes changed: recover the old service without restoring files/DB.
                    if ($stopAttempted) {
                        Start-UpdateService $h.serviceName
                        if (-not (Test-UpdateHealth $h $h.previousCommit $h.previousSchemaVersion $h.healthTimeoutSeconds {$r.healthAttempts++; Write-UpdateJson $recordPath $r})) { throw 'Old service health failed.' }
                    }
                    $r.outcome='applyFailed'; $r.error='update-failed-before-replace'; Write-UpdateJson $recordPath $r
                    Write-UpdateJson $fencePath @{updateId=$h.updateId;disposition='closed';actor='updater';at=[DateTimeOffset]::UtcNow.ToString('o')}
                    return 1
                }
                if ($a -eq 'commitOnly' -or -not $r.backupComplete -or -not $manifest) { throw 'Manual repair required.' }
                $backupFiles = Assert-UpdateBackup $backup
                Set-Phase 'rollback'; Stop-UpdateService $h.serviceName
                foreach ($file in $backupFiles) {
                    if ($file.path.StartsWith('service/') -or $file.path.StartsWith('scripts/')) {
                        $dest=Get-UpdateTarget $file.path $h $settings; [IO.Directory]::CreateDirectory((Split-Path $dest -Parent)) | Out-Null
                        [IO.File]::Copy((Join-Path $backup $file.path),$dest,$true)
                    }
                }
                foreach ($file in $newFiles) {
                    if ($file.path -notin $backupFiles.path) { $dest=Get-UpdateTarget $file.path $h $settings; if(Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force } }
                }
                $mode=Get-UpdateRollbackMode $manifest $h.previousSchemaVersion
                if ($mode -eq 'database') {
                    if (-not (Test-Path (Join-Path $backup 'constructd.db'))) { throw 'Database backup missing.' }
                    foreach ($suffix in @('', '-wal', '-shm')) {
                        $dest=$settings.Constructd.DatabasePath + $suffix; $source=Join-Path $backup ('constructd.db' + $suffix)
                        if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Force }
                        if (Test-Path -LiteralPath $source) { [IO.File]::Copy($source,$dest,$true) }
                    }
                }
                foreach ($file in $backupFiles) {
                    if ($file.path.StartsWith('service/') -or $file.path.StartsWith('scripts/')) {
                        if ((Get-FileHash (Get-UpdateTarget $file.path $h $settings) -Algorithm SHA256).Hash -ne $file.sha256) { throw 'Restored installation mismatch.' }
                    }
                }
                $oldLedger=Join-Path $backup 'previous-install.json'; $ledger=Join-Path $h.publishDir 'install.json'
                if (Test-Path $oldLedger) { [IO.File]::Copy($oldLedger,$ledger,$true) }
                elseif (Test-Path $ledger) { Remove-Item -LiteralPath $ledger -Force }
                Start-UpdateService $h.serviceName
                $expectedSchema=$h.previousSchemaVersion
                if ($mode -eq 'database') { $expectedSchema=$h.previousSchemaVersion }
                if (-not (Test-UpdateHealth $h $h.previousCommit $expectedSchema $h.healthTimeoutSeconds {$r.healthAttempts++; Write-UpdateJson $recordPath $r})) { throw 'Rollback health failed.' }
                $r.outcome='rolledBack'; if ($mode -eq 'database') { $r.outcome='rolledBackWithDatabase' }
                $r.error='update-failed'; Write-UpdateJson $recordPath $r
                Write-UpdateJson $fencePath @{updateId=$h.updateId;disposition='closed';actor='updater';at=[DateTimeOffset]::UtcNow.ToString('o')}
                return 0
            } catch {
                $r.outcome='recoveryFailed'; $r.error='manual-recovery-required'
                $r.manualSteps=@('Keep the service in maintenance. Inspect this record and backup/files.json.', 'Repair from the complete backup, or repair the staged installation. Do not restore a database after commitOnly or a terminal outcome.', 'Use the admin update resolve API to commit, abort or close after verifying the installed files.')
                Write-UpdateJson $recordPath $r
                & schtasks.exe /Change /TN Construct-HostUpdate /Disable 2>$null | Out-Null
                return 1
            }
        }
    } finally { if ($admin) { $admin.Dispose() }; $lock.Dispose() }
}
if (-not $LibraryOnly) {
    try { exit (Invoke-ConstructHostUpdate $Handoff ([bool]$Resume) ([bool]$Rollback)) }
    catch { [Console]::Error.WriteLine('Host update could not run; inspect the local recovery record.'); exit 1 }
}
