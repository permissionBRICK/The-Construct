#Requires -Version 5.1
. (Join-Path $PSScriptRoot 'Construct.Runtime.ps1')
# Dot-sourcing performs no installation. Seams may be replaced individually in tests.
function Throw-ConstructCompanionError {
    param([string]$Message)
    $failure=New-Object InvalidOperationException($Message)
    $failure.Data['constructCompanionDiagnostic']=$true
    throw $failure
}

function New-ConstructCompanionSeams {
    return @{
        CheckUser = {
            if ($env:OS -ne 'Windows_NT') { Throw-ConstructCompanionError 'Companion installation requires Windows.' }
            $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
            if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { Throw-ConstructCompanionError 'Run the Companion installer from a non-elevated PowerShell window.' }
        }
        Native = { param($Exe, [string[]]$Arguments)
            if (-not (Get-Command $Exe -ErrorAction SilentlyContinue)) { return @{exitCode=127; output=@()} }
            # PS 5.1 represents native stderr as ErrorRecords; capture it without
            # letting ErrorActionPreference=Stop abort before we collect the exit code.
            $priorPreference=$ErrorActionPreference
            try {
                $ErrorActionPreference='Continue'
                $output = @(& $Exe @Arguments 2>&1)
                return @{exitCode=$LASTEXITCODE; output=$output}
            } finally { $ErrorActionPreference=$priorPreference }
        }
        Json = { param($Uri)
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Invoke-RestMethod -Uri $Uri -UseBasicParsing -TimeoutSec 30 -Headers @{'User-Agent'='ConstructCompanion'}
        }
        Download = { param($Uri, $Destination, $ScriptsDir)
            if (-not (Get-Command Receive-ConstructBinary -ErrorAction SilentlyContinue)) { . (Join-Path $ScriptsDir 'lib/AgentVm.Common.ps1') }
            Receive-ConstructBinary -Uri $Uri -OutFile $Destination
        }
        Quit = { param($Endpoint, $Reason)
            # Never follow a redirect carrying the bearer credential or expose HTTP errors.
            try {
                Invoke-RestMethod -Uri ("http://127.0.0.1:{0}/v1/quit" -f $Endpoint.port) -Method Post `
                    -Headers @{Authorization=('Bearer ' + $Endpoint.token)} -ContentType 'application/json' `
                    -Body (@{reason=$Reason} | ConvertTo-Json -Compress) -UseBasicParsing -TimeoutSec 5 -MaximumRedirection 0 | Out-Null
            } catch { Throw-ConstructCompanionError 'Companion quit request failed.' }
        }
        Alive = { param($ProcessId) $null -ne (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue) }
        # Processes running from the installation folder (a Companion the endpoint cannot reach).
        Running = { param($Directory)
            $prefix=([IO.Path]::GetFullPath($Directory)).TrimEnd('\')+'\'
            @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $p=$null; try { $p=$_.Path } catch { }; $p -and $p.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { @{name=$_.ProcessName+'.exe'; id=$_.Id} })
        }
        # Processes holding any file of the folder open (Windows Restart Manager, no elevation).
        Holders = { param($Directory) Get-ConstructCompanionFileHolders $Directory }
        Sleep = { param($Milliseconds) Start-Sleep -Milliseconds $Milliseconds }
        Start = { param($Exe, [string[]]$Arguments)
            $info = New-Object Diagnostics.ProcessStartInfo
            $info.FileName=$Exe; $info.WorkingDirectory=Split-Path $Exe -Parent
            $info.UseShellExecute=$false; $info.CreateNoWindow=$true
            # The only argument is a fixed flag, never user input.
            $info.Arguments=$Arguments -join ' '
            $process=[Diagnostics.Process]::Start($info); $process.Dispose()
        }
        Registry = { param($Operation, $Path, $Name, $Value)
            $subkey=$Path.Substring('HKCU:\'.Length)
            $key=$null
            try {
                switch ($Operation) {
                    'read' {
                        $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subkey)
                        if ($key -and $key.GetValueNames() -contains $Name) {
                            return @{exists=$true; value=$key.GetValue($Name,$null,[Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); kind=$key.GetValueKind($Name).ToString()}
                        }
                        return @{exists=$false}
                    }
                    'write' {
                        $key=[Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($subkey)
                        $kind=[Microsoft.Win32.RegistryValueKind]::String; $data=$Value
                        if ($Value -is [hashtable]) { $kind=[Microsoft.Win32.RegistryValueKind]$Value.kind; $data=$Value.value }
                        # RegistryKey accepts the empty name for the default value on PS 5.1.
                        $key.SetValue($Name,$data,$kind)
                    }
                    'remove' {
                        $key=[Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($subkey,$true)
                        if ($key) { $key.DeleteValue($Name,$false) }
                    }
                    'removeTree' { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($subkey,$false) }
                }
            } finally { if ($key) { $key.Dispose() } }
        }
        Move = { param($From,$To) [IO.Directory]::Move($From,$To) }
    }
}

function Get-ConstructCompanionPaths {
    param([string]$LocalAppData)
    if (-not $LocalAppData) { $LocalAppData=$env:LOCALAPPDATA }
    if (-not $LocalAppData) { $LocalAppData=$env:TEMP }
    if (-not $LocalAppData) { Throw-ConstructCompanionError 'No per-user application directory is available.' }
    return @{install=(Join-Path (Join-Path $LocalAppData 'Programs') 'ConstructCompanion'); state=(Join-Path (Join-Path $LocalAppData 'The-Construct') 'companion')}
}

function Read-ConstructCompanionJson {
    param([string]$Path)
    if (Test-Path -LiteralPath $Path) {
        try { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) }
        catch { Throw-ConstructCompanionError 'Cannot read Companion JSON file; check its format and permissions.' }
    }
    return $null
}

function Resolve-ConstructCompanionSource {
    param([string]$ScriptsDir, [ValidateSet('auto','local','release')][string]$Source='auto', [hashtable]$Seams)
    $settings=Read-ConstructCompanionJson (Join-Path $ScriptsDir '.construct-settings.json')
    $repo='permissionBRICK/The-Construct'
    if ($settings.constructRepo) { $repo=[string]$settings.constructRepo }
    if ($repo -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$') { Throw-ConstructCompanionError 'Invalid Companion release repository.' }
    $local=$false
    if ($Source -eq 'local' -and (Test-Path -LiteralPath (Join-Path $ScriptsDir 'companion/Construct.Companion.sln'))) {
        $sdks=& $Seams.Native 'dotnet' @('--list-sdks')
        $local=$sdks.exitCode -eq 0 -and @($sdks.output | Where-Object { $_ -is [string] -and $_ -match '^10\.' }).Count -gt 0
    }
    if ($Source -eq 'local' -and -not $local) { Throw-ConstructCompanionError 'Local Companion build requires the solution and a .NET 10 SDK.' }
    if ($local) {
        $commit=[string]$settings.installedCommit
        if (Test-Path -LiteralPath (Join-Path $ScriptsDir '.git')) {
            $git=& $Seams.Native 'git' @('-C',$ScriptsDir,'rev-parse','HEAD')
            if ($git.exitCode -ne 0) { Throw-ConstructCompanionError ('Cannot determine the checkout commit (git exit '+[int]$git.exitCode+'). Check Git availability and run git rev-parse HEAD in the scripts directory.') }
            $commits=@($git.output | Where-Object { $_ -is [string] -and $_ -cmatch '^[0-9a-f]{40}$' })
            $commit=''; if ($commits.Count -eq 1) { $commit=$commits[0] }
        }
        if ($commit -notmatch '^[0-9a-f]{40}$') { Throw-ConstructCompanionError 'Cannot determine the local Companion commit; record the Construct update marker first.' }
        return @{source='local-build'; commit=$commit; packageVersion=([DateTimeOffset]::UtcNow.ToString('yyyy.MM.dd')+'+'+$commit.Substring(0,7)); releaseTag=$null}
    }
    # Every main commit publishes its Companion next to its host release, so the installed
    # Construct commit names its Companion directly: one manifest download, no release
    # listing and no GitHub API rate limit. The listing is only the fallback (a commit whose
    # Companion release is still building, or an install without a commit marker).
    $found=$null; $tag=''
    $installedCommit=[string]$settings.installedCommit
    if ($installedCommit -cnotmatch '^[0-9a-f]{40}$') {
        $marker=Join-Path $ScriptsDir '.construct-revision'
        if (Test-Path -LiteralPath $marker) { $installedCommit=([IO.File]::ReadAllText($marker)).Trim().ToLowerInvariant() }
    }
    if ($installedCommit -cmatch '^[0-9a-f]{40}$') {
        # The host release of that commit names the Companion it installs (companionReleaseTag:
        # its own build, or the newest one when no Companion file changed). Older host releases
        # have no pointer; then the Companion built from the same commit is tried.
        $candidates=@()
        try {
            $hostManifest=& $Seams.Json "https://github.com/$repo/releases/download/host-$installedCommit/manifest.json"
            $pointer=[string]$hostManifest.companionReleaseTag
            if ($pointer -cmatch '^companion-[0-9a-f]{40}$') { $candidates+=$pointer }
        } catch { }
        if ($candidates -notcontains "companion-$installedCommit") { $candidates+="companion-$installedCommit" }
        foreach ($candidate in $candidates) {
            try {
                $found=& $Seams.Json "https://github.com/$repo/releases/download/$candidate/manifest.json"
                Assert-ConstructCompanionManifest $found $repo $candidate
                $tag=$candidate; break
            } catch { $found=$null; $tag='' }
        }
    }
    if (-not $found) {
        # Paginate: host and Companion releases share this repository.
        $releases=@(); $page=1
        do {
            $batch=@(& $Seams.Json "https://api.github.com/repos/$repo/releases?per_page=100&page=$page")
            # Windows PowerShell 5.1 returns a JSON array as one object; unwrap it.
            if ($batch.Count -eq 1 -and $batch[0] -is [Array]) { $batch=@($batch[0]) }
            $releases+=@($batch | Where-Object { -not $_.draft -and -not $_.prerelease -and $_.tag_name -cmatch '^companion-[0-9a-f]{40}$' })
            if ($batch.Count -eq 100 -and $page -ge 20) { Throw-ConstructCompanionError 'Companion release discovery reached its 20-page limit; reduce unrelated repository releases or use -Source local.' }
            $page++
        } while ($batch.Count -eq 100)
        $release=$releases | Sort-Object { [DateTimeOffset]$_.published_at } -Descending | Select-Object -First 1
        if (-not $release) { Throw-ConstructCompanionError 'No published Companion release is available.' }
        $tag=[string]$release.tag_name
        $found=& $Seams.Json "https://github.com/$repo/releases/download/$tag/manifest.json"
        Assert-ConstructCompanionManifest $found $repo $tag
    }
    $base="https://github.com/$repo/releases/download/$tag"
    $payload=Select-ConstructReleasePayload $found $Seams.Native
    return @{source=$payload.source; commit=$found.commit; packageVersion=$found.packageVersion; releaseTag=$tag; manifest=$found; payload=$payload; payloadUri="$base/$($payload.asset)"}
}

function Assert-ConstructCompanionManifest {
    param($Manifest,[string]$Repository,[string]$Tag)
    if ($Tag -cnotmatch '^companion-[0-9a-f]{40}$' -or $Manifest.schemaVersion -ne 1 -or $Manifest.ipcApiVersion -ne 1 -or
        $Manifest.commit -cne $Tag.Substring(10) -or $Manifest.repository -cne $Repository -or $Manifest.releaseTag -cne $Tag -or
        $Manifest.ref -cne 'refs/heads/main' -or $Manifest.exe -cne 'app\ConstructCompanion.exe' -or
        $Manifest.payloadAsset -cne ('construct-companion-'+$Tag.Substring(10,7)+'-win-x64.zip') -or
        $Manifest.payloadSha256 -cnotmatch '^[0-9a-f]{64}$' -or $Manifest.sumsSha256 -cnotmatch '^[0-9a-f]{64}$' -or
        $Manifest.packageVersion -cnotmatch ('^\d{4}\.\d{2}\.\d{2}\+'+$Tag.Substring(10,7)+'$')) { Throw-ConstructCompanionError 'Invalid Companion release manifest.' }
    try { Assert-ConstructFrameworkDependentManifest $Manifest ('construct-companion-'+$Tag.Substring(10,7)+'-win-x64-fdd.zip') }
    catch { Throw-ConstructCompanionError 'Invalid Companion framework-dependent manifest.' }
    if ($null -ne $Manifest.payloadSizeBytes -and (($Manifest.payloadSizeBytes -isnot [int] -and $Manifest.payloadSizeBytes -isnot [long]) -or $Manifest.payloadSizeBytes -le 0 -or $Manifest.payloadSizeBytes -gt 1GB)) { Throw-ConstructCompanionError 'Invalid Companion payload size.' }
    if ($null -ne $Manifest.payloadUncompressedSizeBytes -and (($Manifest.payloadUncompressedSizeBytes -isnot [int] -and $Manifest.payloadUncompressedSizeBytes -isnot [long]) -or $Manifest.payloadUncompressedSizeBytes -le 0 -or $Manifest.payloadUncompressedSizeBytes -gt 1GB)) { Throw-ConstructCompanionError 'Invalid Companion uncompressed size.' }
    $date=[DateTimeOffset]::MinValue
    if (-not [DateTimeOffset]::TryParse([string]$Manifest.builtAt,[ref]$date)) { Throw-ConstructCompanionError 'Invalid Companion manifest build date.' }
}

function Expand-ConstructCompanionPayload {
    param([string]$Zip,[string]$Destination,$Manifest,$Payload)
    if (-not $Payload) { $Payload=@{sha256=$Manifest.payloadSha256;sizeBytes=$Manifest.payloadSizeBytes;sumsSha256=$Manifest.sumsSha256;uncompressedSizeBytes=$Manifest.payloadUncompressedSizeBytes} }
    if ($null -ne $Payload.sizeBytes -and (Get-Item -LiteralPath $Zip).Length -ne $Payload.sizeBytes) { Throw-ConstructCompanionError 'Companion payload size mismatch.' }
    if ((Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash -ne $Payload.sha256) { Throw-ConstructCompanionError 'Companion payload checksum mismatch.' }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive=[IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        Assert-ConstructArchiveLengths $archive $Payload.uncompressedSizeBytes
        $seen=@{}
        foreach ($entry in $archive.Entries) {
            $name=$entry.FullName.Replace('\','/')
            if ($name.EndsWith('/') -or $name -notmatch '^(app/.+|SHA256SUMS)$' -or $name -match '(^|/)(\.|\.\.)(/|$)|[:\x00-\x1f]' -or
                $name -match '(^|/)[^/]*[. ](/|$)' -or $seen.ContainsKey($name)) { Throw-ConstructCompanionError 'Unsafe Companion archive layout.' }
            $seen[$name]=$true
        }
        # Validate inflated lengths before creating any destination files.
        $hashes=@{}
        foreach ($entry in $archive.Entries) { $hashes[$entry.FullName.Replace('\','/')]=Expand-ConstructBoundedEntry $entry $null -Hash }
        if ($hashes['SHA256SUMS'] -ne $Payload.sumsSha256) { Throw-ConstructCompanionError 'Companion file list checksum mismatch.' }
        $reader=New-Object IO.StreamReader($archive.GetEntry('SHA256SUMS').Open())
        try {
            $covered=@{}
            while ($null -ne ($line=$reader.ReadLine())) {
                if ($line -cnotmatch '^([0-9a-f]{64})  (app/.+)$' -or $covered.ContainsKey($Matches[2]) -or $hashes[$Matches[2]] -ne $Matches[1]) { Throw-ConstructCompanionError 'Companion file checksum mismatch.' }
                $covered[$Matches[2]]=$true
            }
            if ($covered.Count -ne $hashes.Count-1) { Throw-ConstructCompanionError 'Unlisted Companion payload file.' }
        } finally { $reader.Dispose() }
        foreach ($entry in $archive.Entries) {
            $target=Join-Path $Destination $entry.FullName.Replace('\','/')
            [IO.Directory]::CreateDirectory((Split-Path $target -Parent)) | Out-Null
            Expand-ConstructBoundedEntry $entry $target
        }
    } finally { $archive.Dispose() }
    $sums=Join-Path $Destination 'SHA256SUMS'
    if (-not (Test-Path -LiteralPath $sums) -or (Get-FileHash -LiteralPath $sums -Algorithm SHA256).Hash -ne $Payload.sumsSha256) { Throw-ConstructCompanionError 'Companion file list checksum mismatch.' }
    $checked=@{}
    foreach ($line in [IO.File]::ReadAllLines($sums)) {
        if ($line -cnotmatch '^([0-9a-f]{64})  (app/.+)$') { Throw-ConstructCompanionError 'Invalid Companion checksum list.' }
        $hash=$Matches[1]; $name=$Matches[2]
        if (-not $seen.ContainsKey($name) -or $checked.ContainsKey($name)) { Throw-ConstructCompanionError 'Invalid Companion checksum entry.' }
        $file=Join-Path $Destination $name
        if ((Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $hash) { Throw-ConstructCompanionError 'Companion file checksum mismatch.' }
        $checked[$name]=$true
    }
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $Destination 'app') -File -Recurse) {
        $name='app/'+$file.FullName.Substring((Join-Path $Destination 'app').Length+1).Replace('\','/')
        if (-not $checked.ContainsKey($name)) { Throw-ConstructCompanionError 'Unlisted Companion payload file.' }
    }
}

# Moves a folder's contents item by item rather than renaming the folder: a folder held open
# (a console whose current directory it is, an Explorer window, a scanner) blocks the rename
# but not the files inside. Each item retries for ten seconds (antivirus scans and closing
# handles hold freshly extracted files); a failure then names the holders. The emptied source
# folder is removed when possible; a held empty folder is harmless and reused next time.
function Move-ConstructCompanionTree {
    param([string]$From,[string]$To,[hashtable]$Seams)
    [IO.Directory]::CreateDirectory($To) | Out-Null
    foreach ($item in @(Get-ChildItem -LiteralPath $From -Force)) {
        $target=Join-Path $To $item.Name
        for ($attempt=1; ; $attempt++) {
            try { & $Seams.Move $item.FullName $target; break }
            catch {
                if ($attempt -lt 20) { & $Seams.Sleep 500; continue }
                $message=$item.Name+': '+(Get-ConstructCompanionSafeMessage $_.Exception)
                $holders=@(); try { $holders=@(& $Seams.Holders $item.FullName) } catch { $holders=@() }
                if ($holders.Count -gt 0) { $message+='; held by '+(@($holders | ForEach-Object { [string]$_.name+' (pid '+[string]$_.id+')' }) -join ', ') }
                throw (New-Object Exception $message)
            }
        }
    }
    try { [IO.Directory]::Delete($From) } catch { }
}
function Test-ConstructCompanionFolderHasContent {
    param([string]$Path)
    return (Test-Path -LiteralPath $Path) -and @(Get-ChildItem -LiteralPath $Path -Force | Select-Object -First 1).Count -gt 0
}
function Get-ConstructCompanionFileHolders {
    param([string]$Directory)
    if ($env:OS -ne 'Windows_NT' -or -not (Test-Path -LiteralPath $Directory)) { return @() }
    try {
        if (-not ('ConstructCompanionRestartManager' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class ConstructCompanionRestartManager {
    [StructLayout(LayoutKind.Sequential)] public struct RM_UNIQUE_PROCESS { public int dwProcessId; public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] public struct RM_PROCESS_INFO {
        public RM_UNIQUE_PROCESS Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
        public int ApplicationType; public uint AppStatus; public uint TSSessionId; [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
    }
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);
    [DllImport("rstrtmgr.dll")] static extern int RmEndSession(uint pSessionHandle);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications, IntPtr rgApplications, uint nServices, string[] rgsServiceNames);
    [DllImport("rstrtmgr.dll")] static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo, [In, Out] RM_PROCESS_INFO[] rgAffectedApps, ref uint lpdwRebootReasons);
    public static List<KeyValuePair<string, int>> Holders(string[] files) {
        var result = new List<KeyValuePair<string, int>>();
        uint session; if (RmStartSession(out session, 0, Guid.NewGuid().ToString("N")) != 0) return result;
        try {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, IntPtr.Zero, 0, null) != 0) return result;
            uint needed, count = 0, reasons = 0;
            var status = RmGetList(session, out needed, ref count, null, ref reasons);
            if (needed == 0) return result;
            var infos = new RM_PROCESS_INFO[needed]; count = needed;
            if (RmGetList(session, out needed, ref count, infos, ref reasons) != 0) return result;
            for (var i = 0; i < count; i++) result.Add(new KeyValuePair<string, int>(infos[i].strAppName, infos[i].Process.dwProcessId));
            return result;
        } finally { RmEndSession(session); }
    }
}
'@
        }
        $files=@(Get-ChildItem -LiteralPath $Directory -File -Recurse -Force | ForEach-Object { $_.FullName })
        if ($files.Count -eq 0) { return @() }
        $seen=@{}; $holders=@()
        foreach ($pair in [ConstructCompanionRestartManager]::Holders([string[]]$files)) {
            $name=[string]$pair.Key; $id=[int]$pair.Value
            try { $process=Get-Process -Id $id -ErrorAction Stop; $name=$process.ProcessName+'.exe' } catch { }
            if (-not $seen.ContainsKey($id)) { $seen[$id]=$true; $holders+=@{name=$name; id=$id} }
        }
        return $holders
    } catch { return @() }
}
# A Companion started outside the endpoint handshake (or still shutting down) keeps its files
# open; wait for it briefly and refuse clearly instead of failing the move. Never kills.
function Wait-ConstructCompanionProcessesExit {
    param([string]$InstallDir,[hashtable]$Seams)
    for ($i=0; $i -lt 60; $i++) {
        $running=@(); try { $running=@(& $Seams.Running $InstallDir) } catch { $running=@() }
        if ($running.Count -eq 0) { return }
        & $Seams.Sleep 250
    }
    $list=@($running | ForEach-Object { [string]$_.name+' (pid '+[string]$_.id+')' }) -join ', '
    Throw-ConstructCompanionError ('The Companion is still running ('+$list+') and its endpoint is not reachable; close it from the tray and retry. No process was killed.')
}
# One line, no secrets: move/registry/start errors carry paths and Windows error text only.
function Get-ConstructCompanionSafeMessage {
    param($Exception)
    $text=[string]$Exception.Message
    if ($Exception.InnerException -and $Exception.InnerException.Message) { $text=[string]$Exception.InnerException.Message }
    $text=($text -replace '\s+',' ').Trim()
    if ($text.Length -gt 200) { $text=$text.Substring(0,200)+'...' }
    if (-not $text) { $text=$Exception.GetType().Name }
    return $text
}

function Stop-ConstructCompanionForInstall {
    param([string]$StateDir,[hashtable]$Seams,[ValidateSet('update','user')][string]$Reason='update')
    $endpoint=Read-ConstructCompanionJson (Join-Path $StateDir 'endpoint.json')
    if ($endpoint) {
        if ($endpoint.v -ne 1 -or $endpoint.ipcApiVersion -ne 1 -or ($endpoint.port -isnot [int] -and $endpoint.port -isnot [long]) -or $endpoint.port -lt 1 -or $endpoint.port -gt 65535 -or
            ($endpoint.pid -isnot [int] -and $endpoint.pid -isnot [long]) -or $endpoint.pid -le 0 -or $endpoint.pid -gt [int]::MaxValue -or $endpoint.token -cnotmatch '^[0-9a-fA-F]{64}$') { Throw-ConstructCompanionError 'Invalid Companion endpoint; close the Companion from its tray menu and retry.' }
        if (-not (& $Seams.Alive $endpoint.pid)) { return }
        $timer=[Diagnostics.Stopwatch]::StartNew()
        & $Seams.Quit $endpoint $Reason
        # Poll count also bounds injected clocks; wall time includes the HTTP request.
        for ($i=0; $i -lt 60 -and $timer.Elapsed.TotalSeconds -lt 15; $i++) {
            if (-not (& $Seams.Alive $endpoint.pid)) { return }
            & $Seams.Sleep ([Math]::Min(250,[Math]::Max(1,15000-[int]$timer.Elapsed.TotalMilliseconds)))
        }
        Throw-ConstructCompanionError 'Companion did not exit within 15 seconds; close it from the tray and retry. No process was killed.'
    }
}

function Get-ConstructCompanionRegistrations {
    param([string]$InstallDir,[bool]$Autostart=$true)
    $exe=Join-Path $InstallDir 'ConstructCompanion.exe'
    return @(
        @{path='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'; name='ConstructCompanion'; value=('"'+$exe+'" --background'); enabled=$Autostart},
        @{path='HKCU:\Software\Classes\construct'; name=''; value='URL:Construct Protocol'; enabled=$true},
        @{path='HKCU:\Software\Classes\construct'; name='URL Protocol'; value=''; enabled=$true},
        @{path='HKCU:\Software\Classes\construct\shell\open\command'; name=''; value=('"'+$exe+'" --uri "%1"'); enabled=$true},
        @{path='HKCU:\Software\Classes\AppUserModelId\PermissionBrick.TheConstruct'; name='DisplayName'; value='Construct Companion'; enabled=$true},
        @{path='HKCU:\Software\Classes\AppUserModelId\PermissionBrick.TheConstruct'; name='IconUri'; value=$exe; enabled=$true}
    )
}

function Install-ConstructCompanion {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ScriptsDir,[ValidateSet('auto','local','release')][string]$Source='auto',
        [switch]$Force,[switch]$SkipCompanion,[string]$LocalAppData,[hashtable]$Seams=(New-ConstructCompanionSeams))
    $ErrorActionPreference='Stop'
    if ($SkipCompanion) { return 'skipped' }
    $settings=Read-ConstructCompanionJson (Join-Path $ScriptsDir '.construct-settings.json')
    if ($settings.companion -is [bool] -and -not $settings.companion) { return 'skipped' }
    & $Seams.CheckUser
    $paths=Get-ConstructCompanionPaths $LocalAppData
    [IO.Directory]::CreateDirectory($paths.state) | Out-Null
    # Serialize update/uninstall in this user profile, without leaving a live process.
    $lock=$null; $work=$null
    try {
        $lock=[IO.File]::Open((Join-Path $paths.state 'install.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $plan=Resolve-ConstructCompanionSource $ScriptsDir $Source $Seams
        $installed=Read-ConstructCompanionJson (Join-Path $paths.install 'install.json')
        if (-not $Force -and $installed.commit -eq $plan.commit -and (Test-Path -LiteralPath (Join-Path $paths.install 'ConstructCompanion.exe'))) { return 'unchanged' }
        $previous=$paths.install+'.previous'
        if (Test-ConstructCompanionFolderHasContent $previous) { Throw-ConstructCompanionError 'A previous Companion installation needs recovery; see docs/companion.md.' }
        $parent=Split-Path $paths.install -Parent
        [IO.Directory]::CreateDirectory($parent) | Out-Null
        $work=Join-Path $parent ('companion-stage-'+[guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($work) | Out-Null
        $app=Join-Path $work 'app'
        if ($plan.source -eq 'local-build') {
            $result=& $Seams.Native 'dotnet' @('publish',(Join-Path $ScriptsDir 'companion/src/Construct.Companion/Construct.Companion.csproj'),'-c','Release','-r','win-x64','--self-contained','true',('-p:InformationalVersion=1.0.0+'+$plan.commit),'-p:IncludeSourceRevisionInInformationalVersion=false','-nodeReuse:false','-p:UseSharedCompilation=false','--artifacts-path',(Join-Path $work 'artifacts'),'-o',$app)
            if ($result.exitCode -ne 0) {
                # Dependency diagnostics can include credentials (for example NuGet
                # source URLs). Expose their error codes, never arbitrary output.
                $codes=@(foreach ($line in $result.output) {
                    foreach ($match in [regex]::Matches([string]$line,'(?i)\berror\s+((?:MSB|NETSDK|NU|CS)\d{3,5})\s*:')) { $match.Groups[1].Value.ToUpperInvariant() }
                }) | Sort-Object -Unique | Select-Object -First 10
                $detail=''; if ($codes) { $detail='; diagnostics: '+($codes -join ', ') }
                Throw-ConstructCompanionError ('Companion local publish failed (dotnet exit '+[int]$result.exitCode+$detail+'). Run dotnet publish companion/src/Construct.Companion -c Release -r win-x64 --self-contained true in the scripts directory to inspect full build output locally.')
            }
        } else {
            $zip=Join-Path $work 'payload.zip'
            & $Seams.Download $plan.payloadUri $zip $ScriptsDir
            $unpacked=Join-Path $work 'unpacked'
            Expand-ConstructCompanionPayload $zip $unpacked $plan.manifest $plan.payload
            $app=Join-Path $unpacked 'app'
        }
        if (-not (Test-Path -LiteralPath (Join-Path $app 'ConstructCompanion.exe')) -or -not (Test-Path -LiteralPath (Join-Path $app 'media/panel.html'))) { Throw-ConstructCompanionError 'Companion publish output is missing its executable or media.' }
        $record=[ordered]@{schemaVersion=1; commit=$plan.commit; packageVersion=$plan.packageVersion; source=$plan.source; releaseTag=$plan.releaseTag; installedAt=[DateTimeOffset]::UtcNow.ToString('o'); ipcApiVersion=1}
        [IO.File]::WriteAllText((Join-Path $app 'install.json'),($record | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
        $preferences=Read-ConstructCompanionJson (Join-Path $paths.state 'settings.json')
        $autostart=-not ($preferences.autostart -is [bool] -and -not $preferences.autostart)
        $registrations=Get-ConstructCompanionRegistrations $paths.install $autostart
        $saved=@(); foreach ($entry in $registrations) { $saved+=@{entry=$entry; prior=(& $Seams.Registry 'read' $entry.path $entry.name $null)} }
        Stop-ConstructCompanionForInstall $paths.state $Seams
        Wait-ConstructCompanionProcessesExit $paths.install $Seams
        $backedUp=$false; $moved=$false; $registered=$false; $step='backing up the installed files'
        try {
            if (Test-ConstructCompanionFolderHasContent $paths.install) { Move-ConstructCompanionTree $paths.install $previous $Seams; $backedUp=$true }
            $step='moving the new files into place'
            Move-ConstructCompanionTree $app $paths.install $Seams; $moved=$true
            $step='registering the app'; $registered=$true
            foreach ($entry in $registrations) {
                if ($entry.enabled) { & $Seams.Registry 'write' $entry.path $entry.name $entry.value }
                else { & $Seams.Registry 'remove' $entry.path $entry.name $null }
            }
            $step='starting the app'
            & $Seams.Start (Join-Path $paths.install 'ConstructCompanion.exe') @('--background')
        } catch {
            $detail=Get-ConstructCompanionSafeMessage $_.Exception
            try {
                if ($registered) {
                    foreach ($item in $saved) {
                        if ($item.prior.exists) { & $Seams.Registry 'write' $item.entry.path $item.entry.name $item.prior }
                        else { & $Seams.Registry 'remove' $item.entry.path $item.entry.name $null }
                    }
                }
                if ($moved) { Get-ChildItem -LiteralPath $paths.install -Force | Remove-Item -Recurse -Force }
                if ($backedUp) { Move-ConstructCompanionTree $previous $paths.install $Seams }
            } catch { Throw-ConstructCompanionError ('Companion update failed while '+$step+' ('+$detail+') and rollback is incomplete; preserve .previous and see docs/companion.md.') }
            # Leave the user with a running Companion whenever the previous files came back.
            $restarted=$false
            if ($backedUp) { try { & $Seams.Start (Join-Path $paths.install 'ConstructCompanion.exe') @('--background'); $restarted=$true } catch { } }
            $outcome=if ($restarted) { 'the previous files and registrations were restored and the previous Companion was started again.' } else { 'the previous files and registrations were restored. Start the Companion again manually.' }
            Throw-ConstructCompanionError ('Companion update failed while '+$step+' ('+$detail+'); '+$outcome)
        }
        # A successful process start commits the swap; cleanup cannot roll back a running app.
        if ($backedUp) {
            try { Remove-Item -LiteralPath $previous -Recurse -Force }
            catch { Write-Warning 'Companion installed, but .previous cleanup failed; remove it after closing the app before the next update.' }
        }
        return 'installed'
    } finally {
        try { if ($work -and (Test-Path -LiteralPath $work)) { Remove-Item -LiteralPath $work -Recurse -Force } }
        finally { if ($lock) { $lock.Dispose() } }
    }
}

function Uninstall-ConstructCompanion {
    [CmdletBinding()]
    param([string]$LocalAppData,[hashtable]$Seams=(New-ConstructCompanionSeams))
    $ErrorActionPreference='Stop'
    & $Seams.CheckUser
    $paths=Get-ConstructCompanionPaths $LocalAppData
    [IO.Directory]::CreateDirectory($paths.state) | Out-Null
    $lock=[IO.File]::Open((Join-Path $paths.state 'install.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        Stop-ConstructCompanionForInstall $paths.state $Seams 'user'
        & $Seams.Registry 'remove' 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'ConstructCompanion' $null
        & $Seams.Registry 'removeTree' 'HKCU:\Software\Classes\construct' '' $null
        & $Seams.Registry 'removeTree' 'HKCU:\Software\Classes\AppUserModelId\PermissionBrick.TheConstruct' '' $null
        if (Test-Path -LiteralPath $paths.install) { Remove-Item -LiteralPath $paths.install -Recurse -Force }
    } finally { $lock.Dispose() }
}

function Test-ConstructCompanionElevated {
    if ($env:OS -ne 'Windows_NT') { return $false }
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Invoke-ConstructCompanionInstallHook {
    [CmdletBinding()]
    param([string]$ScriptsDir,[switch]$SkipCompanion,[switch]$SkipWhenElevated)
    # An elevated reprovision is the installer's own child; the non-elevated client pre-step owns the Companion.
    if (-not $SkipCompanion -and $SkipWhenElevated -and (Test-ConstructCompanionElevated)) { return }
    try {
        $outcome = Install-ConstructCompanion -ScriptsDir $ScriptsDir -SkipCompanion:$SkipCompanion
        switch ([string]$outcome) {
            'installed' { Write-Host '==> Construct Companion installed and started.' -ForegroundColor Green }
            'unchanged' {
                $build=''
                try {
                    if (Get-Command Get-ConstructCompanionPaths -ErrorAction SilentlyContinue) {
                        $current=[string](Read-ConstructCompanionJson (Join-Path (Get-ConstructCompanionPaths).install 'install.json')).commit
                        if ($current -cmatch '^[0-9a-f]{40}$') { $build=' (build '+$current.Substring(0,7)+': no Companion change since)' }
                    }
                } catch { $build='' }
                Write-Host ('==> Construct Companion already current'+$build+'.') -ForegroundColor DarkGray
            }
            'skipped'   { Write-Host '==> Construct Companion skipped.' -ForegroundColor DarkGray }
            default     { if ($outcome) { Write-Host ('==> Construct Companion: ' + $outcome) -ForegroundColor DarkGray } }
        }
    }
    catch {
        $reason='An unexpected dependency failure occurred. Retry Install-ConstructCompanion in a non-elevated PowerShell window for diagnostics.'
        if ($_.Exception.Data['constructCompanionDiagnostic']) { $reason=$_.Exception.Message }
        Write-Warning ('Companion installation failed (continuing VM/extension installation): '+$reason)
    }
}
