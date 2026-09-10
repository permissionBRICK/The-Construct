using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;

namespace Constructd.Windows.Console;

public sealed class HyperVInteractiveConsole(IProcessRunner runner, ConstructdOptions options,
    IConsoleSessionStore sessions, IClock clock) : IInteractiveConsole
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, ConsoleConnection> connections = new();
    public static readonly string Script = """
        $ErrorActionPreference = 'Stop'
        $p = [Console]::In.ReadToEnd() | ConvertFrom-Json
        $prefix = 'Construct:'
        function Remove-ConsoleAccount($account) {
            $parts = $account.Description.Split(':')
            if ($parts.Count -eq 2) {
                Revoke-VMConnectAccess -VMId ([Guid]$parts[1]) -UserName $account.SID.Value -ErrorAction SilentlyContinue
            }
            Remove-LocalUser -SID $account.SID
        }
        if ($p.action -eq 'sweep') {
            Get-LocalUser | Where-Object { $_.Name -match '^cvc[0-9a-f]{17}$' -and ($_.Description -like ($prefix + '*')) -and ($_.AccountExpires -and $_.AccountExpires -lt (Get-Date)) } | ForEach-Object {
                Remove-ConsoleAccount $_
            }
            '{}' ; exit 0
        }
        if ($p.sid -notmatch '^[0-9a-f]{32}$') { throw 'Invalid session' }
        $name = 'cvc' + $p.sid.Substring(0,17)
        $user = Get-LocalUser -Name $name -ErrorAction SilentlyContinue
        if ($user -and -not $user.Description.StartsWith($prefix)) { throw 'Account collision' }
        if ($p.action -eq 'remove') {
            if ($user) { Remove-ConsoleAccount $user }
            '{}' ; exit 0
        }
        $expiry = [DateTimeOffset]::Parse($p.expiresAt).LocalDateTime.AddSeconds(30)
        if ($p.action -eq 'renew') {
            if (-not $user) { throw 'Account missing' }
            Set-LocalUser -Name $name -AccountExpires $expiry
            '{}' ; exit 0
        }
        $vm = Get-VM -Name $p.vm -ErrorAction Stop
        # Read the local VMConnect certificate without sending credentials. The authenticated host
        # API delivers this fingerprint to the gateway, so renewal/rotation needs no guest hardcoding.
        $tcp = New-Object Net.Sockets.TcpClient
        try {
            if (-not $tcp.ConnectAsync('127.0.0.1',2179).Wait(5000)) { throw 'VMConnect unavailable' }
            $stream = $tcp.GetStream(); $stream.ReadTimeout = 5000; $stream.WriteTimeout = 5000
            $blob = [Text.Encoding]::Unicode.GetBytes(([string]$vm.Id) + [char]0)
            $packet = New-Object IO.MemoryStream
            $binary = New-Object IO.BinaryWriter($packet)
            $binary.Write([uint32](18 + $blob.Length)); $binary.Write([uint32]0)
            $binary.Write([uint32]2); $binary.Write([uint32]0)
            $binary.Write([uint16](([string]$vm.Id).Length + 1)); $binary.Write($blob)
            $bytes = $packet.ToArray(); $stream.Write($bytes,0,$bytes.Length)
            $binary.Dispose(); $packet.Dispose()
            $ssl = New-Object Net.Security.SslStream($stream,$false,({$true} -as [Net.Security.RemoteCertificateValidationCallback]))
            $ssl.AuthenticateAsClient($env:COMPUTERNAME)
            $cert = New-Object Security.Cryptography.X509Certificates.X509Certificate2($ssl.RemoteCertificate)
            $sha = [Security.Cryptography.SHA256]::Create()
            $fingerprint = 'sha256:' + [BitConverter]::ToString($sha.ComputeHash($cert.RawData)).Replace('-',':').ToLowerInvariant()
            $sha.Dispose(); $cert.Dispose(); $ssl.Dispose()
        } finally { $tcp.Dispose() }

        if ($user) { throw 'Account already exists' }
        try {
            $user = New-LocalUser -Name $name -Password (ConvertTo-SecureString $p.password -AsPlainText -Force) -Description ($prefix + $vm.Id) -AccountExpires $expiry -UserMayNotChangePassword
            # No group membership, host administration, or host RDP access is granted.
            Grant-VMConnectAccess -VMId $vm.Id -UserName ($env:COMPUTERNAME + '\' + $name)
            @{ vmId = [string]$vm.Id; username = $name; domain = $env:COMPUTERNAME; certificateFingerprint = $fingerprint } | ConvertTo-Json -Compress
        } catch {
            if ($user) { Remove-LocalUser -SID $user.SID -ErrorAction SilentlyContinue }
            throw 'Console account setup failed'
        }
        """;
    private async Task<string> RunAsync(object payload, CancellationToken ct)
    {
        if (!options.BrowserConsoleEnabled) throw new ConsoleTransportException();
        try
        {
            var r = await runner.RunAsync(options.PowerShellPath,
                ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                    Convert.ToBase64String(Encoding.Unicode.GetBytes(Script))],
                JsonSerializer.Serialize(payload), TimeSpan.FromSeconds(20), null, ct);
            if (!r.Succeeded || r.StandardOutput.Length > 4096) throw new ConsoleTransportException();
            return r.StandardOutput;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new ConsoleTransportException(); }
    }
    public async Task<ConsoleConnection> ConnectAsync(ConsoleSession session, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (connections.TryGetValue(session.Id, out var existing)) return existing;
            var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) + "aA1!";
            using var doc = JsonDocument.Parse(await RunAsync(new { action = "create", sid = session.Id,
                vm = session.VmName, expiresAt = session.ExpiresAt, password }, ct));
            var r = doc.RootElement;
            var connection = new ConsoleConnection { VmId = r.GetProperty("vmId").GetString()!,
                Username = r.GetProperty("username").GetString()!, Domain = r.GetProperty("domain").GetString()!, Password = password, CertificateFingerprint = r.GetProperty("certificateFingerprint").GetString()! };
            connections.Add(session.Id, connection);
            return connection;
        }
        finally { gate.Release(); }
    }
    public async Task RenewAsync(ConsoleSession session, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (connections.ContainsKey(session.Id))
                await RunAsync(new { action = "renew", sid = session.Id, expiresAt = session.ExpiresAt }, ct);
        }
        finally { gate.Release(); }
    }
    public async Task RemoveAsync(string sessionId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await RemoveCoreAsync(sessionId, ct); }
        finally { gate.Release(); }
    }
    private async Task RemoveCoreAsync(string sid, CancellationToken ct)
    {
        if (!connections.ContainsKey(sid)) return;
        await RunAsync(new { action = "remove", sid }, ct);
        connections.Remove(sid);
    }
    public async Task ReconcileAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var sid in connections.Keys.ToArray())
                if (sessions.Get(sid, clock.UtcNow) is null) await RemoveCoreAsync(sid, ct);
            await RunAsync(new { action = "sweep" }, ct);
        }
        finally { gate.Release(); }
    }
}
