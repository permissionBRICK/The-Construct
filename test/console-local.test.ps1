$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/../lib/AgentVm.Remote.ps1"
. "$PSScriptRoot/../lib/AgentVm.Console.ps1"
function Assert($test, $message) { if (-not $test) { throw $message } }
$account = Get-ConstructConsoleAccountName 'work-vm'
Assert ($account -match '^cvl[0-9a-f]{17}$') 'SAM account length and prefix'
Assert ($account -eq (Get-ConstructConsoleAccountName 'WORK-VM')) 'stable case-insensitive name'
Assert ($account -ne (Get-ConstructConsoleAccountName 'other-vm')) 'instance isolation'
$passwords = @(1..20 | ForEach-Object { New-ConstructConsolePassword })
Assert (@($passwords | Select-Object -Unique).Count -eq 20) 'fresh random passwords'
Assert (@($passwords | Where-Object { $_.Length -ne 40 -or $_ -cnotmatch '[A-Z]' -or $_ -cnotmatch '[a-z]' -or $_ -notmatch '[0-9]' }).Count -eq 0) 'complexity'
Assert ((Get-ConstructRemoteTokenPath -Slug 'console-work-vm' -StoreDir '/tmp') -eq '/tmp/console-work-vm.token') 'slug token path'
try { Get-ConstructRemoteTokenPath -Slug '../escape' -StoreDir '/tmp'; throw 'accepted bad slug' } catch { Assert ($_.Exception.Message -notmatch 'accepted bad slug') 'slug validation' }
$env:COMPUTERNAME = 'TESTHOST'
$script:stored = 'old-password'; $script:changed = @()
$seams = @{
    InstanceName='work-vm'; VmName='Work-VM'
    VmLookup={ param($n) Assert ($n -eq 'Work-VM') 'VM target'; [pscustomobject]@{Id=[Guid]'11111111-2222-3333-4444-555555555555'; State='Running'} }
    GrantLookup={ param($id) [pscustomobject]@{UserName="TESTHOST\$account"} }
    ReadToken={ param($slug) Assert ($slug -eq 'console-work-vm') 'read shared store'; $script:stored }
    Store={ param($slug,$pw) $script:stored=$pw }
    ChangePassword={ param($name,$old,$new) Assert ($old -eq $script:stored) 'rotate current password'; $script:changed+=,$new }
    Fingerprint={ param($id) 'sha256:' + ((@('ab')*32) -join ':') }
    HostAddress={ param($n) '192.168.1.1' }
}
$r = Invoke-ConstructConsoleHandoff @seams | ConvertFrom-Json
Assert ($r.username -eq $account -and $r.password -eq $script:stored -and $r.rotated) 'handoff emits saved rotated credential'
Assert ($script:changed.Count -eq 1) 'one rotation per click'
$r2 = Invoke-ConstructConsoleHandoff @seams | ConvertFrom-Json
Assert ($r.password -ne $r2.password) 'next click rotates again'
$seams.ReadToken={ '' }; $r=Invoke-ConstructConsoleHandoff @seams | ConvertFrom-Json
Assert ($r.setupRequired -and $r.reason -eq 'no-credential') 'missing credential requests setup'
$seams.ReadToken={ $script:stored }; $seams.GrantLookup={ @() }; $r=Invoke-ConstructConsoleHandoff @seams | ConvertFrom-Json
Assert ($r.reason -eq 'grant-missing') 'missing grant requests setup'
$seams.GrantLookup={ [pscustomobject]@{UserName="TESTHOST\$account"} }
$seams.ChangePassword={ throw 'wrong old password' }; $r=Invoke-ConstructConsoleHandoff @seams | ConvertFrom-Json
Assert ($r.reason -eq 'credential-out-of-sync') 'wrong password requests repair'
$seams.ChangePassword={ throw (New-Object Runtime.InteropServices.COMException('password restriction', -2147023571)) }
$r=Invoke-ConstructConsoleHandoff @seams | ConvertFrom-Json
Assert (-not $r.rotated -and $r.password -eq $script:stored) 'minimum age policy reuses old password'
$seams.Fingerprint={ throw 'tcp refused' }; $r=Invoke-ConstructConsoleHandoff @seams | ConvertFrom-Json
Assert ($r.error -eq 'vmconnect-unreachable') 'fingerprint error is fixed text'
$seams.VmLookup={ [pscustomobject]@{Id=[Guid]::Empty;State='Off'} }; $r=Invoke-ConstructConsoleHandoff @seams | ConvertFrom-Json
Assert ($r.error -eq 'vm-not-running') 'offline VM'
foreach ($file in @('lib/AgentVm.Console.ps1','Set-AgentVmConsoleAccess.ps1','lib/AgentVm.Remote.ps1','lib/AgentVm.Cleanup.ps1','Auto-Install.ps1')) {
    $errors=$null; [void][Management.Automation.Language.Parser]::ParseFile((Join-Path "$PSScriptRoot/.." $file),[ref]$null,[ref]$errors)
    Assert ($errors.Count -eq 0) "PowerShell parses: $file"
}
Write-Host 'Local console account, password, slug, rotation and handoff tests passed.'


# Real token-format/file operations with a protection double. Production always uses DPAPI.
function Test-ConstructDpapiAvailable { $true }
function Protect-ConstructTokenBytes { param([byte[]]$Bytes) return ,([byte[]]@($Bytes | ForEach-Object { $_ -bxor 87 })) }
function Unprotect-ConstructTokenBytes { param([byte[]]$Bytes) return ,([byte[]]@($Bytes | ForEach-Object { $_ -bxor 87 })) }
$storeDir = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N'))
try {
    $tokenPath = Save-ConstructRemoteToken -Slug console-work-vm -Token 'PRIVATE-token' -StoreDir $storeDir
    Assert ((Get-ConstructRemoteToken -Slug console-work-vm -StoreDir $storeDir) -eq 'PRIVATE-token') 'slug DPAPI format round trip'
    Assert ([IO.File]::ReadAllText($tokenPath) -notmatch 'PRIVATE-token') 'token is not plaintext on disk'
    Remove-ConstructRemoteToken -Slug console-work-vm -StoreDir $storeDir | Out-Null
    Assert (-not (Test-Path $tokenPath)) 'slug removal deletes only its file'
} finally { if (Test-Path $storeDir) { Remove-Item -LiteralPath $storeDir -Recurse -Force } }

# Setup/removal run against cmdlet doubles. Assert the Windows argument shapes and ownership fence.
$script:user=$null; $script:token=''; $script:grants=@(); $script:revoked=@(); $script:rules=@(); $script:created=0
$script:vmId=[Guid]'11111111-2222-3333-4444-555555555555'
function Get-VM { param($Name) [pscustomobject]@{Id=$script:vmId} }
function Get-LocalUser { param($Name,$ErrorAction) $script:user }
function New-LocalUser {
    param($Name,$Password,$Description,[switch]$PasswordNeverExpires,[switch]$AccountNeverExpires)
    Assert ($Description.Length -le 48) 'description fits SAM field'
    Assert ($PasswordNeverExpires -and $AccountNeverExpires) 'persistent account'
    $script:created++; $script:user=[pscustomobject]@{Name=$Name;Description=$Description}; $script:user
}
function Set-LocalUser { param($Name,$Password,$UserMayChangePassword,$Description) if ($Description) { $script:user.Description=$Description } }
function Get-ConstructRemoteToken { param($Slug) $script:token }
function Save-ConstructRemoteToken { param($Slug,$Token) $script:token=$Token }
function Remove-ConstructRemoteToken { param($Slug) $script:token='' }
function Get-VMConnectAccess { param($VMId,$UserName) if ($script:grants -contains "$VMId") { [pscustomobject]@{UserName=$UserName} } }
function Grant-VMConnectAccess { param($VMId,$UserName) Assert ($UserName -eq "TESTHOST\$account") 'exact console account grant'; $script:grants+="$VMId" }
function Revoke-VMConnectAccess { param($VMId,$UserName,$ErrorAction) $script:revoked+="$VMId"; $script:grants=@($script:grants | Where-Object { $_ -ne "$VMId" }) }
function Remove-LocalUser { param($Name) $script:user=$null }
function Get-VMNetworkAdapter { param($VMName) [pscustomobject]@{SwitchName='Default Switch'} }
function Get-NetFirewallRule { param($Name,$ErrorAction) }
function Remove-NetFirewallRule { [CmdletBinding()]param([Parameter(ValueFromPipeline)]$InputObject) }
function New-NetFirewallRule { param($Name,$DisplayName,$Direction,$Action,$Protocol,$LocalPort,$InterfaceAlias) Assert ($LocalPort -eq 2179 -and $InterfaceAlias -eq 'vEthernet (Default Switch)') 'firewall scoped to switch'; $script:rules+=$Name }
Set-ConstructConsoleAccess -InstanceName work-vm -VmName Work-VM
$firstToken=$script:token
Set-ConstructConsoleAccess -InstanceName work-vm -VmName Work-VM
Assert ($script:created -eq 1 -and $script:token -eq $firstToken) 'setup reuses existing account and credential'
Assert ($script:grants.Count -eq 1) 'setup does not duplicate grants'
Set-ConstructConsoleAccess -InstanceName work-vm -VmName Work-VM -Reset
Assert ($firstToken -ne $script:token) 'repair resets out-of-sync credential'
$script:vmId=[Guid]'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee'
Set-ConstructConsoleAccess -InstanceName work-vm -VmName Work-VM
Assert ($script:revoked -contains '11111111-2222-3333-4444-555555555555') 'reinstall revokes previous VM GUID'
Assert ($script:grants.Count -eq 1 -and $script:grants[0] -eq "$script:vmId") 'reinstall grants new VM only'
Remove-ConstructConsoleAccount -InstanceName work-vm
Assert (-not $script:user -and -not $script:token -and $script:grants.Count -eq 0) 'remove revokes grant and deletes account and credential'
$script:user=[pscustomobject]@{Name=$account;Description='Unrelated user'}
try { Set-ConstructConsoleAccess -InstanceName work-vm -VmName Work-VM; throw 'accepted collision' } catch { Assert ($_.Exception.Message -eq 'Console account name collision') 'unrelated account cannot be reused' }
Write-Host 'Local console setup, idempotence, reset, reinstall and removal tests passed.'

# A local TLS peer checks the actual VMConnect preconnection packet and certificate hash.
Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
public static class ConsoleTlsFixture {
    public static int Port;
    public static string Fingerprint;
    public static string VmId;
    public static Task Run;
    public static void Start() {
        var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        Fingerprint = "sha256:" + BitConverter.ToString(SHA256.HashData(cert.RawData)).Replace('-', ':').ToLowerInvariant();
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Run = Task.Run(async () => {
            try {
                using (var tcp = await listener.AcceptTcpClientAsync()) {
                    using (var stream = tcp.GetStream()) {
                        stream.ReadTimeout = 5000;
                        var reader = new BinaryReader(stream, Encoding.Unicode, true);
                        uint length = reader.ReadUInt32();
                        if (reader.ReadUInt32() != 0 || reader.ReadUInt32() != 2 || reader.ReadUInt32() != 0) throw new Exception("Bad preconnection header");
                        ushort chars = reader.ReadUInt16();
                        var blob = reader.ReadBytes(chars * 2);
                        if (length != 18 + blob.Length) throw new Exception("Bad packet length");
                        VmId = Encoding.Unicode.GetString(blob).TrimEnd('\0');
                        using (var tls = new SslStream(stream, false)) {
                            await tls.AuthenticateAsServerAsync(cert, false, SslProtocols.Tls12, false);
                        }
                    }
                }
            } finally { listener.Stop(); cert.Dispose(); key.Dispose(); }
        });
    }
}
'@
[ConsoleTlsFixture]::Start()
$fingerprint = Get-ConstructVmConnectFingerprint -VmId '11111111-2222-3333-4444-555555555555' -Port ([ConsoleTlsFixture]::Port)
Assert ([ConsoleTlsFixture]::Run.Wait(7000)) 'TLS handshake finishes'
Assert ($fingerprint -ceq [ConsoleTlsFixture]::Fingerprint) 'fingerprint hashes certificate DER'
Assert ([ConsoleTlsFixture]::VmId -eq '11111111-2222-3333-4444-555555555555') 'preconnection packet carries exact VM GUID'
Write-Host 'Local VMConnect packet and TLS fingerprint tests passed.'
