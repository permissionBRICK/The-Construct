using System.Net;
using System.Net.Sockets;
using System.Text;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using Constructd.Proxmox;
using Constructd.Windows.Process;

namespace Constructd.Tests.Proxmox;

public sealed class ProxmoxInteractiveConsoleTests
{
    // This fixture only echoes binary bytes on local pipes. It never invokes qm or a host.
    internal sealed class FixtureRunner : IStreamingProcessRunner
    {
        public string Script = """
            import os
            ticket = os.environ['LC_PVE_TICKET'].encode('ascii')
            os.write(2, ticket * 32768)
            os.write(1, ticket)
            while data := os.read(0, 65536):
                if data == b'quit':
                    break
                os.write(1, data)
            """;
        public string? FileName;
        public string[] Arguments = [];
        public Dictionary<string, string> Environment = new();
        public IStreamingProcess? Process;
        public bool Fail;
        public int Starts;
        public IStreamingProcess Start(string fileName, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string> environment)
        {
            Starts++; FileName = fileName; Arguments = [.. arguments]; Environment = new(environment);
            if (Fail) throw new InvalidOperationException("SECRET-DEPENDENCY " + environment["LC_PVE_TICKET"]);
            return Process = new StreamingProcessRunner().Start("python3", ["-c", Script], environment);
        }
    }

    private sealed class World : IAsyncDisposable
    {
        public readonly MutableClock Clock = new();
        public readonly InMemoryConsoleSessionStore Sessions = new();
        public readonly FixtureRunner Runner = new();
        public readonly RecordingProcessRunner Commands = new()
        { Default = new(0, """[{"type":"qemu","node":"pve1","name":"child","vmid":101}]""", "", false) };
        public readonly ConstructdOptions Options;
        public readonly ProxmoxInteractiveConsole Console;
        public World()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            Options = new() { ListenAddress = "127.0.0.1", PublicHost = "pve.test",
                Proxmox = new() { Node = "pve1", QmPath = "/fixture/qm", ConsolePorts = new(port, port) } };
            Console = new(Commands, Options, Sessions, Clock, Runner);
        }
        public ConsoleSession Session() => Sessions.TryCreate("child", "owner", 1, 1, TimeSpan.FromSeconds(60), Clock.UtcNow)!;
        public ValueTask DisposeAsync() => Console.DisposeAsync();
    }

    internal static async Task<TcpClient> ConnectAsync(ConsoleConnection connection)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, connection.Port!.Value).WaitAsync(TimeSpan.FromSeconds(5));
            var greeting = new byte[8];
            await client.GetStream().ReadExactlyAsync(greeting).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(connection.Password, Encoding.ASCII.GetString(greeting));
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    private static async Task RefusedAsync(int port)
    {
        using var client = new TcpClient();
        await Assert.ThrowsAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
    }

    [Fact]
    public async Task Argv_environment_binary_bridge_single_use_and_removal()
    {
        await using var world = new World();
        var session = world.Session();
        var connection = await world.Console.ConnectAsync(session, default);
        Assert.Same(connection, await world.Console.ConnectAsync(session, default));
        Assert.Equal("vnc", connection.Protocol); Assert.Equal("101", connection.VmId);
        Assert.Equal("pve.test", connection.Host); Assert.Equal("", connection.Username);
        Assert.Equal("", connection.Domain); Assert.Equal("", connection.CertificateFingerprint);
        Assert.Equal(8, connection.Password.Length);
        Assert.Equal("/fixture/qm", world.Runner.FileName);
        Assert.Equal(new[] { "vncproxy", "101" }, world.Runner.Arguments);
        Assert.Equal(connection.Password, world.Runner.Environment["LC_PVE_TICKET"]);
        Assert.DoesNotContain(connection.Password, string.Join(' ', world.Runner.Arguments));
        Assert.DoesNotContain(connection.Password, connection.ToString());
        using var client = await ConnectAsync(connection);
        var bytes = Enumerable.Range(0, 256).Select(n => (byte)n).ToArray();
        await client.GetStream().WriteAsync(bytes);
        var reply = new byte[bytes.Length];
        await client.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(bytes, reply);
        await RefusedAsync(connection.Port!.Value);
        await world.Console.RemoveAsync(session.Id, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(world.Runner.Process!.Exited.IsCompleted);
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]));
        await RefusedAsync(connection.Port.Value);
        var next = await world.Console.ConnectAsync(world.Session(), default);
        Assert.Equal(connection.Port, next.Port);
        Assert.NotEqual(connection.Password, next.Password);
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("crashed")]
    [InlineData("disconnected")]
    public async Task Reconcile_reaps_unavailable_sessions_and_releases_ports(string reason)
    {
        await using var world = new World();
        var session = world.Session();
        var connection = await world.Console.ConnectAsync(session, default);
        using var client = await ConnectAsync(connection);
        if (reason == "expired") world.Clock.Advance(TimeSpan.FromMinutes(2));
        else if (reason == "revoked") world.Sessions.Remove(session.Id);
        else
        {
            if (reason == "crashed") await client.GetStream().WriteAsync("quit"u8.ToArray());
            else client.Dispose();
            await world.Runner.Process!.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        }
        for (var attempt = 0; attempt < 100 && world.Sessions.Get(session.Id, world.Clock.UtcNow) is not null; attempt++)
        { await world.Console.ReconcileAsync(default); await Task.Delay(10); }
        await world.Console.ReconcileAsync(default);
        Assert.Null(world.Sessions.Get(session.Id, world.Clock.UtcNow));
        Assert.True(world.Runner.Process!.Exited.IsCompleted);
        await RefusedAsync(connection.Port!.Value);
        await Assert.ThrowsAsync<ConsoleTransportException>(() => world.Console.ConnectAsync(session, default));
        Assert.Equal(connection.Port, (await world.Console.ConnectAsync(world.Session(), default)).Port);
    }

    [Fact]
    public async Task Renew_extends_deadline_and_expiry_kills_active_stream_without_reconcile()
    {
        await using var world = new World();
        var session = world.Session();
        var connection = await world.Console.ConnectAsync(session, default);
        using var client = await ConnectAsync(connection);
        await world.Console.RenewAsync(session with { ExpiresAt = world.Clock.UtcNow.AddMilliseconds(200) }, default);
        await world.Console.RenewAsync(session with { ExpiresAt = world.Clock.UtcNow.AddSeconds(30) }, default);
        await Task.Delay(300);
        Assert.False(world.Runner.Process!.Exited.IsCompleted);
        await world.Console.RenewAsync(session with { ExpiresAt = world.Clock.UtcNow.AddMilliseconds(100) }, default);
        await world.Runner.Process.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, await client.GetStream().ReadAsync(new byte[1]));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Idle_listener_closes_on_deadline_or_process_exit(bool crash)
    {
        await using var world = new World();
        if (crash) world.Runner.Script = "pass";
        var session = world.Session();
        var connection = await world.Console.ConnectAsync(session, default);
        if (!crash) await world.Console.RenewAsync(session with { ExpiresAt = world.Clock.UtcNow.AddMilliseconds(100) }, default);
        await world.Runner.Process!.Exited.WaitAsync(TimeSpan.FromSeconds(5));
        // Reconcile waits for all pipes and pending accepts to be disposed.
        await world.Console.RemoveAsync(session.Id, default).WaitAsync(TimeSpan.FromSeconds(5));
        await RefusedAsync(connection.Port!.Value);
    }

    [Fact]
    public async Task Start_failure_is_redacted_and_port_is_reusable()
    {
        await using var world = new World(); world.Runner.Fail = true;
        var session = world.Session();
        var error = await Assert.ThrowsAsync<ConsoleTransportException>(() => world.Console.ConnectAsync(session, default));
        Assert.DoesNotContain("SECRET-DEPENDENCY", error.ToString());
        Assert.DoesNotContain(world.Runner.Environment["LC_PVE_TICKET"], error.ToString());
        await RefusedAsync(world.Options.Proxmox.ConsolePorts.Start);
        world.Runner.Fail = false;
        var connection = await world.Console.ConnectAsync(session, default);
        using var client = await ConnectAsync(connection);
    }

    [Fact]
    public async Task Disabled_expired_cancelled_and_exhausted_requests_start_no_proxy()
    {
        await using var world = new World();
        var session = world.Session();
        world.Options.BrowserConsoleEnabled = false;
        await Assert.ThrowsAsync<ConsoleTransportException>(() => world.Console.ConnectAsync(session, default));
        world.Options.BrowserConsoleEnabled = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => world.Console.ConnectAsync(session, new(true)));
        await Assert.ThrowsAsync<ConsoleTransportException>(() => world.Console.ConnectAsync(session with { ExpiresAt = world.Clock.UtcNow }, default));
        Assert.Equal(0, world.Runner.Starts);
        await world.Console.ConnectAsync(session, default);
        await Assert.ThrowsAsync<ConsoleTransportException>(() => world.Console.ConnectAsync(world.Session(), default));
        Assert.Equal(1, world.Runner.Starts);
    }
}
