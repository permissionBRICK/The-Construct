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
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

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

        await using var stream = await HappyEyeballs.ConnectAsync(new DnsEndPoint("engine.example", port),
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("2001:db8::7"), IPAddress.Loopback }), ConnectAsync, TimeSpan.FromMilliseconds(25), timeout.Token);
        using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        await abandoned.Task.WaitAsync(timeout.Token);
    }

    [Fact]
    public async Task Stalled_first_addresses_in_both_families_still_reach_a_later_address()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var stalled = new[]
        {
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var badV6 = IPAddress.Parse("2001:db8::7");
        var badV4 = IPAddress.Parse("198.51.100.7");

        async Task<Socket> ConnectAsync(IPEndPoint endpoint, CancellationToken ct)
        {
            if (endpoint.Address.Equals(badV6) || endpoint.Address.Equals(badV4))
            {
                var slot = endpoint.Address.Equals(badV6) ? stalled[0] : stalled[1];
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { slot.TrySetResult(); }
                throw new SocketException((int)SocketError.TimedOut);
            }

            var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(endpoint, ct);
            return socket;
        }

        await using var stream = await HappyEyeballs.ConnectAsync(new DnsEndPoint("engine.example", port),
            (_, _) => Task.FromResult(new[] { badV6, badV4, IPAddress.Loopback }), ConnectAsync, TimeSpan.FromMilliseconds(20), timeout.Token);
        using var accepted = await listener.AcceptTcpClientAsync(timeout.Token);
        await Task.WhenAll(stalled.Select(s => s.Task)).WaitAsync(timeout.Token);
    }
}
