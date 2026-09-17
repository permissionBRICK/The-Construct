using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;

namespace Constructd.Proxmox;

/// <summary>One TCP connection and qm proxy per Construct session. No PVE login leaves the node.</summary>
public sealed class ProxmoxInteractiveConsole : IInteractiveConsole, IAsyncDisposable, IDisposable
{
    private readonly ConstructdOptions options;
    private readonly IConsoleSessionStore sessions;
    private readonly IClock clock;
    private readonly IStreamingProcessRunner streaming;
    private readonly ProxmoxCommands commands;
    private readonly IPAddress address;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, Proxy> proxies = new();
    private bool disposed;

    public ProxmoxInteractiveConsole(IProcessRunner runner, ConstructdOptions options,
        IConsoleSessionStore sessions, IClock clock, IStreamingProcessRunner streaming)
    {
        options.Proxmox.ValidateConsolePorts(options.SshForwardPorts, options.AppForwardPorts);
        this.options = options; this.sessions = sessions; this.clock = clock; this.streaming = streaming;
        commands = new(runner, options);
        address = IPAddress.Parse(options.ListenAddress);
    }

    public async Task<ConsoleConnection> ConnectAsync(ConsoleSession session, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (disposed || !options.BrowserConsoleEnabled || sessions.Get(session.Id, clock.UtcNow) is null)
                throw new ConsoleTransportException();
            if (proxies.TryGetValue(session.Id, out var existing))
            {
                if (existing.Completion.IsCompleted) throw new ConsoleTransportException();
                return existing.Connection;
            }
            var id = await commands.RequireAsync(session.VmName, ct);
            ct.ThrowIfCancellationRequested();
            var remaining = session.ExpiresAt - clock.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new ConsoleTransportException();
            var listener = Listen();
            IStreamingProcess? process = null;
            try
            {
                // Classic VNC uses only the first eight password bytes. All eight characters
                // carry randomness; qm applies this ticket with set_password and a +30s expiry.
                var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(8))[..8];
                process = streaming.Start(options.Proxmox.QmPath, ["vncproxy", ProxmoxCommands.Number(id)],
                    new Dictionary<string, string> { ["LC_PVE_TICKET"] = password });
                var connection = new ConsoleConnection { Protocol = "vnc", VmId = ProxmoxCommands.Number(id),
                    Host = string.IsNullOrWhiteSpace(options.PublicHost) ? ProxmoxDriver.ResolveNode(options) : options.PublicHost,
                    Port = ((IPEndPoint)listener.LocalEndpoint).Port, Password = password,
                    Username = "", Domain = "", CertificateFingerprint = "" };
                proxies.Add(session.Id, new Proxy(connection, listener, process, remaining));
                return connection;
            }
            catch
            {
                listener.Stop();
                if (process is not null) await process.DisposeAsync();
                throw;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new ConsoleTransportException(); }
        finally { gate.Release(); }
    }

    private TcpListener Listen()
    {
        for (var port = options.Proxmox.ConsolePorts.Start; port <= options.Proxmox.ConsolePorts.End; port++)
        {
            if (proxies.Values.Any(p => p.Connection.Port == port)) continue;
            var listener = new TcpListener(address, port);
            try { listener.Start(1); return listener; }
            catch (SocketException) { listener.Stop(); }
        }
        throw new ConsoleTransportException();
    }

    public async Task RenewAsync(ConsoleSession session, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (!proxies.TryGetValue(session.Id, out var proxy)) return;
            var remaining = session.ExpiresAt - clock.UtcNow;
            if (remaining <= TimeSpan.Zero || sessions.Get(session.Id, clock.UtcNow) is null || !proxy.Renew(remaining))
            {
                await RemoveCoreAsync(session.Id);
                throw new ConsoleTransportException();
            }
        }
        finally { gate.Release(); }
    }

    public async Task RemoveAsync(string sessionId, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { await RemoveCoreAsync(sessionId); }
        finally { gate.Release(); }
    }

    private async Task RemoveCoreAsync(string id)
    {
        if (proxies.Remove(id, out var proxy)) await proxy.DisposeAsync();
    }

    public async Task ReconcileAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            foreach (var (id, proxy) in proxies.ToArray())
                if (proxy.Completion.IsCompleted || sessions.Get(id, clock.UtcNow) is null)
                {
                    // A consumed or failed endpoint must never be reopened with this session ID.
                    sessions.Remove(id);
                    await RemoveCoreAsync(id);
                }
        }
        finally { gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync();
        try
        {
            disposed = true;
            foreach (var id in proxies.Keys.ToArray()) await RemoveCoreAsync(id);
        }
        finally { gate.Release(); }
    }
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class Proxy : IAsyncDisposable
    {
        public ConsoleConnection Connection { get; }
        public Task Completion { get; }
        private readonly CancellationTokenSource stop = new();

        public Proxy(ConsoleConnection connection, TcpListener listener, IStreamingProcess process, TimeSpan lifetime)
        {
            Connection = connection;
            stop.CancelAfter(lifetime);
            Completion = RunAsync(listener, process);
        }

        public bool Renew(TimeSpan lifetime)
        {
            if (Completion.IsCompleted || stop.IsCancellationRequested) return false;
            stop.CancelAfter(lifetime);
            return true;
        }

        private async Task RunAsync(TcpListener listener, IStreamingProcess process)
        {
            TcpClient? client = null;
            Task? up = null, down = null;
            var accept = listener.AcceptTcpClientAsync(stop.Token).AsTask();
            try
            {
                if (await Task.WhenAny(accept, process.Exited) != accept) return;
                client = await accept;
                listener.Stop(); // No second TCP connection, even while this stream is active.
                client.NoDelay = true;
                var stream = client.GetStream();
                up = stream.CopyToAsync(process.StandardInput, stop.Token);
                down = process.StandardOutput.CopyToAsync(stream, stop.Token);
                await Task.WhenAny(up, down, process.Exited);
            }
            catch { } // No dependency output, passwords, or raw socket errors reach logs.
            finally
            {
                await stop.CancelAsync();
                listener.Stop();
                // Accept can complete while the process exits. Still take ownership of its socket.
                try { client ??= await accept; } catch { }
                client?.Dispose();
                try { await process.DisposeAsync(); } catch { }
                foreach (var task in new[] { up, down })
                    if (task is not null) try { await task; } catch { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync();
            await Completion;
            stop.Dispose();
        }
    }
}
