# Reviewed local transport from 8ef673b; text fixture, never dot-source this file.
function New-RepoArchive {
    $repoDir = $PSScriptRoot
    $tarPath = Join-Path $env:TEMP "construct-repo.tar.gz"
    if (Test-Path $tarPath) { Remove-Item $tarPath -Force }

    # Exclude .git (unless -IncludeGit), ISOs, the host-only settings file, and the
    # saved config backup (.construct-backup holds plaintext secrets and must never
    # be uploaded into the VM's repo copy).
    $names = @(Get-ChildItem -Force -LiteralPath $repoDir |
               Where-Object { ($IncludeGit -or $_.Name -ne ".git") -and $_.Extension -ne ".iso" -and $_.Name -ne ".construct-settings.json" -and $_.Name -ne ".construct-backup" }).Name
    Write-Step "Packing repo ($repoDir) -> $tarPath"
    & tar.exe -czf $tarPath -C $repoDir @names
    if ($LASTEXITCODE -ne 0) { throw "tar failed packing the repo (exit $LASTEXITCODE)." }
    Write-Ok "Created $([math]::Round((Get-Item $tarPath).Length / 1KB)) KB archive"
    return $tarPath
}

# Upload the archive via SCP (remove any stale copy owned by root from a previous run).
Write-Step "Uploading repo archive to $RemoteArchive"
Invoke-Ssh -Sudo -Command "rm -f $RemoteArchive"
Invoke-Scp -LocalPath $archivePath -RemotePath "/tmp/construct-repo.tar.gz"
Write-Ok "Uploaded"

# Unpack into /opt/construct/repo.
Write-Step "Unpacking repo on the VM"
Invoke-Ssh -Sudo -Command "mkdir -p /opt/construct && rm -rf /opt/construct/repo && mkdir -p /opt/construct/repo && tar -xzf $RemoteArchive -C /opt/construct/repo && chown -R ${SeedUser}:${SeedUser} /opt/construct"
Write-Ok "Repo in place at /opt/construct/repo"

# ── -Action export:
