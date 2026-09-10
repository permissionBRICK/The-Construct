#Requires -Version 5.1
#Requires -RunAsAdministrator
<# Guided, resumable first host installation. All choices arrive from the UI before UAC.
   Never creates, deletes, stops, renames or reprovisions the existing guest. #>
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$PlanB64)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$utf8 = New-Object Text.UTF8Encoding($false)
$plan = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($PlanB64)) | ConvertFrom-Json
$root = Join-Path $env:ProgramData 'ConstructHost'
$journalPath = Join-Path $root 'conversion.json'
$result = $null
$conversionLock = $null

function Write-JsonFile($Path, $Value) {
    $temp = $Path + '.tmp'
    [IO.File]::WriteAllText($temp, ($Value | ConvertTo-Json -Depth 12), $utf8)
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp, $Path, [System.Management.Automation.Language.NullString]::Value) }
    else { [IO.File]::Move($temp, $Path) }
}
function Step($Message) { Write-Host "`n==> $Message" -ForegroundColor Cyan }
function Assert-NoLinks([string]$Path) {
    $next = [IO.Path]::GetFullPath($Path)
    while ($next) {
        if ((Test-Path -LiteralPath $next) -and ((Get-Item -LiteralPath $next -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'A conversion path contains a link or junction.' }
        $next = Split-Path -Parent $next
    }
}
function Protect-Directory($Path) {
    Assert-NoLinks $Path
    [IO.Directory]::CreateDirectory($Path) | Out-Null
    $acl = New-Object Security.AccessControl.DirectorySecurity
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($sid in @('S-1-5-18','S-1-5-32-544')) {
        $identity = New-Object Security.Principal.SecurityIdentifier($sid)
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Path -AclObject $acl
}
function Invoke-Admin([string[]]$Arguments) {
    $output = & $exe admin @Arguments --json
    if ($LASTEXITCODE -ne 0) { throw ('Host administration failed: ' + ($Arguments | Select-Object -First 2) -join ' ') }
    ($output -join "`n") | ConvertFrom-Json
}
function Quote-Argument([string]$Value) {
    # Windows argv quoting, including backslashes before quotes and the closing quote.
    '"' + [regex]::Replace([regex]::Replace($Value, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1') + '"'
}
function Invoke-Guest([string]$Command, [string]$InputText = '') {
    $argv = @('-F','NUL','-o','BatchMode=yes','-o','IdentitiesOnly=yes','-o','StrictHostKeyChecking=yes','-o',('UserKnownHostsFile="' + $plan.knownHosts.Replace('\','/') + '"'),
        '-o','ConnectTimeout=15','-o','ServerAliveInterval=15','-o','ServerAliveCountMax=3','-i',$plan.keyPath,'-p',([string]$plan.sshPort),('root@' + $plan.sshHost),$Command)
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = 'ssh.exe'; $start.UseShellExecute = $false
    $start.Arguments = ($argv | ForEach-Object { Quote-Argument $_ }) -join ' '
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $out = $process.StandardOutput.ReadToEndAsync(); $err = $process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($InputText); $process.StandardInput.Close()
        if (-not $process.WaitForExit(120000)) { $process.Kill(); throw 'Guest enrollment timed out.' }
        $text = $out.GetAwaiter().GetResult(); $null = $err.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw 'Could not verify or enroll the guest over its existing SSH connection.' }
        $text.Trim()
    } finally { $process.Dispose() }
}
function Expand-VerifiedPackage($Zip, $Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    $seen = @{}; $total = 0L
    try {
        foreach ($entry in $archive.Entries) {
            $rel = $entry.FullName.Replace('\','/')
            if ($rel.EndsWith('/')) { continue }
            if ($rel -notmatch '^(service|scripts|updater)/' -and $rel -ne 'SHA256SUMS') { throw 'Unexpected package path.' }
            if ($rel -match '(^|/)\.\.?(/|$)|:|^/' -or $seen.ContainsKey($rel)) { throw 'Unsafe package path.' }
            $total += $entry.Length
            if ($total -gt 1GB) { throw 'Host package exceeds extraction limit.' }
            $seen[$rel] = $true
            $target = Join-Path $Destination $rel
            Assert-NoLinks $target
            [IO.Directory]::CreateDirectory((Split-Path -Parent $target)) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally { $archive.Dispose() }
}
try {
    if ($plan.id -notmatch '^[a-f0-9]{32}$' -or $plan.name -notmatch '^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$' -or
        $plan.name.StartsWith('construct-') -or $plan.publicHost -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$' -or
        [string]::IsNullOrWhiteSpace($plan.adminUser) -or $plan.machineId -notmatch '^[a-f0-9]{32}$') { throw 'Invalid conversion plan.' }
    Assert-NoLinks $root
    $service = Get-Service constructd -ErrorAction SilentlyContinue
    $journal = if (Test-Path -LiteralPath $journalPath) { Get-Content -Raw -LiteralPath $journalPath | ConvertFrom-Json } else { $null }
    if (($service -or (Test-Path -LiteralPath $root)) -and (-not $journal -or $journal.id -ne $plan.id -or $journal.owner -ne $plan.adminUser)) {
        throw 'A host installation already exists. Use Host administration to manage it; conversion will not overwrite it.'
    }
    if ($journal -and $journal.result) { Write-JsonFile $plan.resultPath $journal.result; exit 0 }
    Step 'Verifying the existing VM and SSH identity'
    $vm = Get-VM -Name $plan.vmName
    if ($vm.Name.ToLowerInvariant() -ne $plan.name -or $vm.State -ne 'Running') { throw 'The selected local VM must be running.' }
    if ($journal -and $journal.vmId -ne $vm.Id.ToString()) { throw 'The Hyper-V VM was replaced since conversion began.' }
    if ((Invoke-Guest 'cat /etc/machine-id') -ne $plan.machineId) { throw 'The SSH guest identity changed.' }
    # Cross-check the SSH destination against the selected VM's reported addresses.
    $addresses = @((Get-VMNetworkAdapter -VM $vm).IPAddresses)
    $resolved = @([Net.Dns]::GetHostAddresses($plan.sshHost) | ForEach-Object { $_.ToString() })
    if (-not @($resolved | Where-Object { $addresses -contains $_ }).Count) { throw 'The SSH destination does not match the selected Hyper-V VM.' }
    $switches = @((Get-VMNetworkAdapter -VM $vm).SwitchName | Where-Object { $_ } | Select-Object -Unique)
    if ($switches.Count -ne 1) { throw 'Automatic conversion requires one VM network switch.' }
    $disks = @(Get-VMHardDiskDrive -VM $vm | ForEach-Object { Get-VHD -Path $_.Path })
    if ($disks.Count -eq 0) { throw 'The VM has no disk to adopt.' }
    if (-not $journal) {
        # Fail before writing the host if its published name does not name this PC.
        $local = @(Get-NetIPAddress | ForEach-Object IPAddress)
        $public = @([Net.Dns]::GetHostAddresses($plan.publicHost) | ForEach-Object { $_.ToString() })
        if (-not @($public | Where-Object { $local -contains $_ }).Count) { throw 'The host address must resolve to this Windows PC.' }
        if (Get-NetTCPConnection -State Listen -LocalPort 7462 -ErrorAction SilentlyContinue) { throw 'Port 7462 is already in use.' }
        Protect-Directory $root
        $journal = [pscustomobject]@{id=$plan.id;owner=$plan.adminUser;vmId=$vm.Id.ToString();installed=$false;result=$null}
        Write-JsonFile $journalPath $journal
    }
    $conversionLock = [IO.File]::Open((Join-Path $root 'conversion.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    # Read again under the lock: another window may just have completed this run.
    $journal = Get-Content -Raw -LiteralPath $journalPath | ConvertFrom-Json
    if ($journal.result) { Write-JsonFile $plan.resultPath $journal.result; exit 0 }
    if ($journal.installed -and $journal.publicHost -ne $plan.publicHost) { throw 'Resume with the address used for this host installation.' }
    $scripts = Join-Path $root 'scripts'
    $publish = Join-Path $scripts 'service\publish'
    $data = Join-Path $root 'data'
    $exe = Join-Path $publish 'Constructd.Api.exe'
    if (-not $journal.installed) {
        Step 'Downloading the verified Construct host release'
        $headers = @{'User-Agent'='Construct-Host-Conversion/1'}
        $releases = Invoke-RestMethod -Uri 'https://api.github.com/repos/permissionBRICK/The-Construct/releases?per_page=100' -Headers $headers -TimeoutSec 30
        $release = $releases | Where-Object { $_.tag_name -match '^host-[0-9a-f]{40}$' -and -not $_.draft -and -not $_.prerelease } | Sort-Object published_at -Descending | Select-Object -First 1
        if (-not $release) { throw 'No published Construct host package is available.' }
        $base = 'https://github.com/permissionBRICK/The-Construct/releases/download/' + $release.tag_name + '/'
        $manifest = Invoke-RestMethod -Uri ($base + 'manifest.json') -Headers $headers -TimeoutSec 30
        if ($manifest.repository -ne 'permissionBRICK/The-Construct' -or $manifest.releaseTag -ne $release.tag_name -or
            $manifest.commit -ne $release.tag_name.Substring(5) -or $manifest.payloadAsset -notmatch '^construct-host-[a-f0-9]{7}-win-x64\.zip$' -or
            $manifest.payloadSha256 -notmatch '^[a-f0-9]{64}$' -or $manifest.features -notcontains 'local-vm-adoption-v1') {
            throw 'The published host package does not yet support guided conversion. Update Construct after the host release finishes.'
        }
        $zip = Join-Path $root 'package.zip'; $stage = Join-Path $root 'package'
        $web = New-Object Net.WebClient
        try { $web.DownloadFile(($base + $manifest.payloadAsset), $zip) } finally { $web.Dispose() }
        if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ne $manifest.payloadSha256) { throw 'Host package checksum mismatch.' }
        Expand-VerifiedPackage $zip $stage
        [IO.Directory]::CreateDirectory($scripts) | Out-Null
        Copy-Item -Path (Join-Path $stage 'scripts\*') -Destination $scripts -Recurse -Force
        [IO.Directory]::CreateDirectory($publish) | Out-Null
        Copy-Item -Path (Join-Path $stage 'service\*') -Destination $publish -Recurse -Force
        [IO.Directory]::CreateDirectory((Join-Path $scripts 'keys')) | Out-Null
        Copy-Item -LiteralPath (Join-Path $plan.scriptsDir 'keys\bootstrap_ed25519.pub') -Destination (Join-Path $scripts 'keys\bootstrap_ed25519.pub') -Force
        Step 'Reusing the Ubuntu source and installing the host service'
        $ubuntu = if ($plan.ubuntu -in @('22.04','24.04')) { $plan.ubuntu } else { '24.04' }
        $isoBase = 'https://releases.ubuntu.com/' + $ubuntu + '/'
        $listing = Invoke-WebRequest -Uri $isoBase -UseBasicParsing -TimeoutSec 30
        $match = [regex]::Match($listing.Content, 'ubuntu-[0-9.]+-live-server-amd64\.iso')
        if (-not $match.Success) { throw 'Could not discover the Ubuntu source ISO.' }
        $isoUrl = $isoBase + $match.Value
        $sums = Invoke-WebRequest -Uri ($isoBase + 'SHA256SUMS') -UseBasicParsing -TimeoutSec 30
        $checksum = [regex]::Match($sums.Content, ('(?m)^([a-fA-F0-9]{64})\s+\*?' + [regex]::Escape($match.Value) + '\r?$'))
        if (-not $checksum.Success) { throw 'The Ubuntu release did not provide a checksum for its source ISO.' }
        $isoSha = $checksum.Groups[1].Value.ToLowerInvariant()
        $isoPath = ''; $cached = Join-Path $plan.scriptsDir $match.Value
        if (Test-Path -LiteralPath $cached) {
            if ((Get-FileHash -LiteralPath $cached -Algorithm SHA256).Hash -ne $isoSha) { throw 'The cached Ubuntu ISO failed its checksum verification.' }
            [IO.Directory]::CreateDirectory($data) | Out-Null
            $isoPath = Join-Path $data $match.Value
            Copy-Item -LiteralPath $cached -Destination $isoPath -Force
        }
        $install = Join-Path $scripts 'service\host\Install-ConstructHost.ps1'
        $installArgs = @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass','-File',$install,
            '-ScriptsDir',$scripts,'-PublishDir',$publish,'-DataDir',$data,'-PublicHost',$plan.publicHost,
            '-AdminUser',$plan.adminUser,'-SwitchName',$switches[0],'-IsoSourceUrl',$isoUrl,'-IsoSha256',$isoSha,'-SkipIsoBuild','-SkipAdminToken','-SkipPrereqs','-NoStart')
        if ($isoPath) { $installArgs += @('-IsoSourcePath',$isoPath) }
        if ($plan.keepAwake) { $installArgs += '-KeepHostAwake' } else { $installArgs += '-SkipPowerSettings' }
        & powershell.exe @installArgs
        if ($LASTEXITCODE -ne 0) { throw 'Host installation failed; fix the reported issue and retry conversion.' }
        Write-JsonFile (Join-Path $publish 'install.json') @{commit=$manifest.commit;packageVersion=$manifest.packageVersion;installedAt=[DateTimeOffset]::UtcNow.ToString('o');files=@()}
        $journal | Add-Member -NotePropertyName publicHost -NotePropertyValue $plan.publicHost -Force
        $journal.installed = $true; Write-JsonFile $journalPath $journal
        Remove-Item -LiteralPath $zip
        Remove-Item -LiteralPath $stage -Recurse
    }
    # Only this conversion's service can reach here. Stopping it never stops Hyper-V VMs.
    $service = Get-Service constructd -ErrorAction SilentlyContinue
    if ($service -and $service.Status -ne 'Stopped') { Stop-Service constructd; (Get-Service constructd).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(30)) }
    Step 'Adopting the existing VM without rebuilding it'
    # Installation can take minutes. Re-read identity and resources at the handoff,
    # so a concurrent local reinstall cannot substitute another VM under this name.
    $vm = Get-VM -Name $plan.vmName
    if ($vm.Id.ToString() -ne $journal.vmId -or $vm.State -ne 'Running' -or
        (Invoke-Guest 'cat /etc/machine-id') -ne $plan.machineId) { throw 'The selected VM changed while the host was being installed.' }
    $disks = @(Get-VMHardDiskDrive -VM $vm | ForEach-Object { Get-VHD -Path $_.Path })
    if ($disks.Count -eq 0) { throw 'The selected VM no longer has a disk.' }
    $adopt = Invoke-Admin @('vms','adopt',$plan.name,'--owner',$plan.adminUser,'--cpu',([string]$vm.ProcessorCount),
        '--ram-mb',([string][int]($vm.MemoryStartup / 1MB)),'--disk-gb',([string][int][Math]::Ceiling(($disks | Measure-Object Size -Sum).Sum / 1GB)),
        '--incarnation',$vm.Id.ToString())
    $settings = Get-Content -Raw -LiteralPath (Join-Path $publish 'appsettings.Production.json') | ConvertFrom-Json
    $cert = Get-Item ('Cert:\LocalMachine\My\' + $settings.Constructd.CertThumbprint)
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $pin = ([BitConverter]::ToString($sha.ComputeHash($cert.RawData))).Replace('-',':') } finally { $sha.Dispose() }
    $pem = "-----BEGIN CERTIFICATE-----`n" + [Convert]::ToBase64String($cert.RawData,[Base64FormattingOptions]::InsertLineBreaks) + "`n-----END CERTIFICATE-----`n"
    Start-Service constructd
    Step 'Verifying service access and enrolling the running guest'
    . (Join-Path $scripts 'lib\AgentVm.Remote.ps1')
    $api = @{BaseUrl='https://127.0.0.1:7462';Pin=$pin}
    $ready = $false
    for ($n=0; $n -lt 30; $n++) {
        try { $null = Invoke-ConstructApi @api -Path '/health'; $ready=$true; break } catch { Start-Sleep -Seconds 2 }
    }
    if (-not $ready) { throw 'Host service did not become healthy.' }
    $files = @{}
    foreach ($file in @('construct','construct-vm.sh','construct-expose.sh','construct-idle-report.sh')) { $files[$file] = [IO.File]::ReadAllText((Join-Path $scripts ('bin\' + $file))) }
    $payload = @{name=$plan.name;owner=$plan.adminUser;machineId=$plan.machineId;serviceUrl=('https://' + $plan.publicHost + ':7462');
        publicHost=$plan.publicHost;sshPort=$adopt.sshPort;vmToken=$adopt.vmToken;certificate=$pem;files=$files}
    $guestCode = [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $scripts 'bin\adopt-host.py')))
    $command = 'python3 -c "import base64;exec(base64.b64decode(' + "'" + $guestCode + "'" + '))"'
    Write-Host (Invoke-Guest $command ($payload | ConvertTo-Json -Depth 8 -Compress))
    $admin = Invoke-Admin @('tokens','issue',$plan.adminUser,'--label',('conversion-' + $plan.id))
    # RSA public key belongs to the initiating VS Code session, even if UAC uses
    # another administrator. No plaintext token is written to the handoff file.
    $rsa = New-Object Security.Cryptography.RSACryptoServiceProvider
    try {
        $rsa.FromXmlString($plan.publicKeyXml)
        $encrypted = [Convert]::ToBase64String($rsa.Encrypt([Text.Encoding]::UTF8.GetBytes($admin.token), $true))
    } finally { $rsa.Dispose() }
    $result = @{ok=$true;id=$plan.id;name=$plan.name;owner=$plan.adminUser;url=$payload.serviceUrl;publicHost=$plan.publicHost;
        sshPort=$adopt.sshPort;fingerprint=$pin;encryptedToken=$encrypted}
    $journal.result = $result; Write-JsonFile $journalPath $journal
    Write-JsonFile $plan.resultPath $result
    Step 'Host installed and VM adopted. VS Code will finish connecting automatically.'
    exit 0
} catch {
    Write-Host ('Conversion stopped: ' + $_.Exception.Message) -ForegroundColor Red
    if ($plan.resultPath) { Write-JsonFile $plan.resultPath @{ok=$false;id=$plan.id;error=$_.Exception.Message;hostInstalled=($journal -and $journal.installed)} }
    Write-Host 'The existing VM and its disks have been preserved. Retry from Construct Settings after correcting the reported issue.'
    exit 1
} finally {
    if ($conversionLock) { $conversionLock.Dispose() }
}
