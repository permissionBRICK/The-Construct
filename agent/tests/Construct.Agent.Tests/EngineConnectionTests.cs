using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Construct.Agent.Engines;
using Construct.Agent.Tests.Support;

namespace Construct.Agent.Tests;

/// <summary>
/// How the agent reaches an engine: one pinned handler per engine whose connections every call reuses, and a connect
/// that races the address families (RFC 8305), so an engine that listens on IPv4 only answers at once under a name that
/// also has an IPv6 address.
/// </summary>
public sealed class EngineConnectionTests
{
    [Fact]
    public async Task Calls_to_one_engine_share_its_pinned_handler_and_reuse_its_connections()
    {
        using var engine = new EngineHost();
        var signIn = await engine.AddUserAsync("alice");
        await using var laptop = await TestAgent.StartAsync();
        var engineId = await laptop.AddEngineAsync(engine, signIn);
        await laptop.PostAsync("api/device/setup", new { engine = engineId, deviceName = "laptop", vaultMode = "available" });
        await laptop.GetAsync($"api/vault/engines/{engineId}/mode");
        var (handler, before) = laptop.Host.Agent.SharedHandler(engineId)!.Value;

        for (var i = 0; i < 5; i++)
        {
            await laptop.GetAsync($"api/vault/engines/{engineId}/mode");
            await laptop.GetAsync("api/vault/secrets");
        }

        var (same, after) = laptop.Host.Agent.SharedHandler(engineId)!.Value;
        Assert.Same(handler, same);
        // Ten calls to the engine and its store, one after the other, on the connection already open (the event stream
        // holds one of its own and may connect again meanwhile).
        Assert.InRange(after - before, 0, 2);
    }

    [Fact]
    public async Task An_IPv6_address_that_does_not_answer_does_not_hold_up_the_IPv4_one()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var abandoned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
        {
            if (endpoint.AddressFamily == AddressFamily.InterNetworkV6)
            {
                // A refused IPv6 connect on Windows: SYN retries, no answer for seconds.
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { abandoned.TrySetResult(); }
                throw new SocketException((int)SocketError.TimedOut);
            }

            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(endpoint, ct);
            return socket;
        }

        var clock = Stopwatch.StartNew();
        await using var stream = await HappyEyeballs.ConnectAsync(new DnsEndPoint("engine.example", port),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("2001:db8::7"), IPAddress.Loopback }), ConnectAsync, HappyEyeballs.Stagger, CancellationToken.None);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"Connecting took {clock.Elapsed}.");
        using var accepted = await listener.AcceptTcpClientAsync();
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
