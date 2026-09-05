param([string]$DownloadBase)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../lib/AgentVm.Common.ps1')
function Assert($ok, $message) { if (-not $ok) { throw $message } }
# Execute the provisioner's actual identity-selection block. Any prompt fails.
$source = Get-Content (Join-Path $PSScriptRoot '../Provision-AgentVM.ps1') -Raw
$start = $source.IndexOf('$gitIdentity = @{ Name = ""; Email = "" }')
$end = $source.IndexOf('Ensure-Tar', $start)
$script:fixtureDir = $PSScriptRoot
$identityBlock = [scriptblock]::Create($source.Substring($start, $end - $start).Replace('$PSScriptRoot', '$script:fixtureDir'))
function Read-ConstructSettings { [pscustomobject]@{gitUserName='Saved Name';gitEmail='saved@example.test';gitCredentialStore=$false} }
function Get-HostGitIdentity { @{Name='Host Name';Email='host@example.test'} }
function Save-ConstructSettings { param($Dir,$Values) $script:saved=$Values }
function Select-Projects { 'default' }
function Write-Ok($message) {}
function Read-Host { throw 'Unexpected interactive prompt' }
function Invoke-TuiConfirm { throw 'Unexpected confirmation' }
$Action='provision'
$Auto=$false; $NonInteractive=$true
& $identityBlock
Assert ($script:saved.gitUserName -eq 'Saved Name') 'Saved Git name was not reused'
Assert ($script:saved.gitEmail -eq 'saved@example.test') 'Saved Git email was not reused'
Assert ($script:saved.gitCredentialStore -eq $false) 'Saved credential choice changed'
$Auto=$true; $NonInteractive=$false
& $identityBlock
Write-Host 'PASS: noninteractive and automatic reprovision reuse all three saved Git choices'
$root = Join-Path ([IO.Path]::GetTempPath()) ('t3-host-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $root | Out-Null
try {
    if ($DownloadBase) {
        $file = Join-Path $root 'download.bin'
        Receive-ConstructBinary -Uri "$DownloadBase/redirect" -OutFile $file
        Assert ((Get-Item $file).Length -eq 8388608) 'Streamed download has the wrong length'
        Assert ((Get-FileHash $file -Algorithm SHA256).Hash.ToLowerInvariant() -eq 'ad97f87076920684e2ca66fc44e5d322797dc9d64706b174e51b5d0828937043') 'Streamed download is corrupt'
        $failed=$false
        try { Receive-ConstructBinary -Uri "$DownloadBase/missing" -OutFile $file } catch { $failed=$true }
        Assert $failed 'HTTP errors must fail before installing'
        Write-Host 'PASS: streamed download, redirects, content integrity and HTTP errors'
    }
    $info=New-T3DesktopStartInfo -FilePath 'C:\Program Files\T3 Code\T3 Code.exe' -WorkingDirectory 'C:\Program Files\T3 Code'
    Assert (-not $info.UseShellExecute -and $info.CreateNoWindow) 'Desktop must launch without a console'
    Assert ($info.EnvironmentVariables['ELECTRON_NO_ATTACH_CONSOLE'] -eq '1') 'Electron must not attach to the provisioning console'
    if ($env:OS -eq 'Windows_NT') {
        # Real Win32 process launch using the same helper as the provisioner. The
        # probe has a console subsystem, so inherited console attachment is observable.
        $probe = Join-Path $root 'probe.exe'
        $probeSource = @'
using System;
using System.IO;
using System.Runtime.InteropServices;
public class Probe {
    [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
    public static void Main() {
        File.WriteAllText("result.txt", GetConsoleWindow().ToInt64() + ":" + Environment.GetEnvironmentVariable("ELECTRON_NO_ATTACH_CONSOLE"));
        System.Threading.Thread.Sleep(1500);
        File.WriteAllText("survived.txt", "ok");
    }
}
'@
        Add-Type -TypeDefinition $probeSource -OutputAssembly $probe -OutputType ConsoleApplication
        Start-T3DesktopDetached -FilePath $probe -WorkingDirectory $root
        $result=Join-Path $root 'result.txt'
        $deadline=[DateTime]::UtcNow.AddSeconds(15)
        while (-not (Test-Path $result) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        Assert ((Get-Content $result -Raw) -eq '0:1') 'Child inherited a console or lost Electron isolation'
        $survived=Join-Path $root 'survived.txt'
        while (-not (Test-Path $survived) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 50 }
        Assert (Test-Path $survived) 'Disposing the process handle must leave the child running'
        Write-Host 'PASS: native Windows detached launch has no console and remains alive'
    }
} finally { Remove-Item $root -Recurse -Force }
