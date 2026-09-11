using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Runtime;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Runtime;
using static Construct.Companion.Tests.Runtime.ProcessSupervisorTests;
namespace Construct.Companion.Tests.Runtime;

public sealed class ForwarderRuntimeTests
{
    [Fact]
    public async Task GuestSpoolRequestTunnelAckCloseSweepAndRelease()
    {
        var clock = new FakeClock(); var ssh = new FakeSshTransport(); var closed = false; var acked = false; var swept = false; var released = false;
        var doc = new JsonObject { ["v"] = 1, ["id"] = "web", ["vmPort"] = 5173 };
        var expectedAck = ForwardProtocol.AckDocument("web", new() { ["localPort"] = 5173 });
        ssh.ScriptHandler = (script, token) =>
        {
            if (script == ForwardProtocol.CapabilityScript()) return Task.FromResult(new ProcessResult(0, "SPOOL=1\n"));
            if (script == ForwardProtocol.ReconcileScript("cc-12345678"))
            {
                var dump = "OWNER=self\n" + (closed ? swept ? "" : Encode("C", new JsonObject { ["v"] = 1, ["id"] = "web" }) : Encode("R", doc) + (acked ? Encode("A", expectedAck) : ""));
                return Task.FromResult(new ProcessResult(0, dump));
            }
            if (script == ForwardProtocol.AckScript("web", expectedAck)) { acked = true; return Task.FromResult(new ProcessResult(0)); }
            if (script == ForwardProtocol.RemoveScript([("close", "web")])) { swept = true; return Task.FromResult(new ProcessResult(0)); }
            if (script == ForwardProtocol.ReleaseScript("cc-12345678")) { released = true; return Task.FromResult(new ProcessResult(0)); }
            throw new InvalidOperationException("Unexpected script.");
        };
        var transport = new LocalForwardTransport(ssh, "cc-12345678");
        await using var forwarder = new Forwarder("agent-vm", transport, new SshProcessSupervisor(clock), new PortReservations(), clock);
        Assert.Equal("supported", await forwarder.StartAsync());
        var reconcile = forwarder.ReconcileAsync(); await Eventually(() => ssh.Tunnels.Count == 1);
        Assert.False(acked); clock.Advance(TimeSpan.FromMilliseconds(1200)); await reconcile.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(acked); Assert.Equal("http://localhost:5173/", forwarder.Snapshot.Array("items")[0].Str("url"));
        closed = true; ssh.Spool.SignalForwardChange(ssh.Watches.Single().Process);
        await forwarder.ReconcileAsync(); Assert.True(ssh.Tunnels.Single().Process.Stopped); Assert.True(swept); Assert.Empty(forwarder.Snapshot.Array("items"));
        await forwarder.DisposeAsync(); Assert.Empty(forwarder.Snapshot.Array("items")); Assert.True(released); Assert.True(ssh.Watches.Single().Process.Stopped); Assert.Equal(0, clock.PendingDelays);
    }
    private static string Encode(string kind, JsonObject doc) => kind + " " + doc.Str("id") + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(doc.ToJsonString())) + "\n";
    [Theory]
    [InlineData("unsupported")]
    [InlineData("unanswered")]
    public async Task UnsupportedAndUnansweredGuestsNeverStartWatchers(string outcome)
    {
        var transport = new FakeForwardTransport { Capability = outcome }; var clock = new FakeClock();
        await using var forwarder = new Forwarder("dev", transport, new SshProcessSupervisor(clock), new PortReservations(), clock);
        Assert.Equal(outcome, await forwarder.StartAsync()); Assert.Empty(transport.Ssh.Watches); Assert.Empty(transport.Ssh.Tunnels); Assert.Equal(0, clock.PendingDelays);
    }
    [Fact]
    public async Task CancellationDuringCapabilityCannotSpawnAnything()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ssh = new FakeSshTransport { ScriptHandler = (_, _) => { entered.TrySetResult(); return finish.Task; } };
        var clock = new FakeClock(); var forwarder = new Forwarder("dev", new LocalForwardTransport(ssh, "cc-12345678"), new SshProcessSupervisor(clock), new PortReservations(), clock);
        var start = forwarder.StartAsync(); await entered.Task; var dispose = forwarder.DisposeAsync().AsTask(); finish.SetResult(new(0, "SPOOL=1"));
        Assert.Equal("stood-down", await start); await dispose; Assert.Empty(ssh.Watches); Assert.Empty(ssh.Tunnels);
    }
    [Fact]
    public async Task TwoInstancesCannotReserveTheSamePreferredPort()
    {
        var clock = new FakeClock(); var ports = new PortReservations(); var one = RequestTransport("one"); var two = RequestTransport("two");
        await using var a = new Forwarder("dev", one, new SshProcessSupervisor(clock), ports, clock);
        await using var b = new Forwarder("build", two, new SshProcessSupervisor(clock), ports, clock);
        await a.StartAsync(); await b.StartAsync(); var ar = a.ReconcileAsync(); var br = b.ReconcileAsync();
        await Eventually(() => one.Ssh.Tunnels.Count == 1 && two.Ssh.Tunnels.Count == 1);
        Assert.NotEqual(one.Ssh.Tunnels[0].Spec.LocalPort, two.Ssh.Tunnels[0].Spec.LocalPort);
        clock.Advance(TimeSpan.FromMilliseconds(1200)); await Task.WhenAll(ar, br).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(one.Acks); Assert.Single(two.Acks);
    }
    internal static FakeForwardTransport RequestTransport(string id = "web")
    {
        var t = new FakeForwardTransport(); t.View.Array("requests").Add(new JsonObject { ["id"] = id, ["vmPort"] = 5173, ["label"] = "", ["target"] = "client" }); return t;
    }
    [Fact]
    public async Task HostLabelRebindKeepsPortAndOnlyAcksAfterSettle()
    {
        var clock = new FakeClock(); var t = RequestTransport();
        await using var f = new Forwarder("dev", t, new SshProcessSupervisor(clock), new PortReservations(), clock);
        await f.StartAsync(); var initial = f.ReconcileAsync(); await Eventually(() => t.Ssh.Tunnels.Count == 1);
        clock.Advance(TimeSpan.FromMilliseconds(1200)); await initial;
        using var rebindCancellation = new CancellationTokenSource();
        var rebind = f.SetHostLabelAsync("pc", rebindCancellation.Token); await Eventually(() => t.Ssh.Tunnels.Count == 2);
        Assert.True(t.Ssh.Tunnels[0].Process.Stopped); Assert.Single(t.Acks); rebindCancellation.Cancel();
        Assert.Equal(t.Ssh.Tunnels[0].Spec.LocalPort, t.Ssh.Tunnels[1].Spec.LocalPort); Assert.Equal("0.0.0.0", t.Ssh.Tunnels[1].Spec.BindHost);
        clock.Advance(TimeSpan.FromMilliseconds(1200)); await rebind; await f.ReconcileAsync(); Assert.Equal("pc", t.Acks[^1].Doc.Str("hostLabel"));
        await f.SetHostLabelAsync("other-pc"); await f.ReconcileAsync(); Assert.Equal(2, t.Ssh.Tunnels.Count); Assert.Equal("other-pc", t.Acks[^1].Doc.Str("hostLabel"));
    }
    [Fact]
    public async Task OwnershipLossKillsTunnelsAndRejectsClose()
    {
        var clock = new FakeClock(); var t = RequestTransport();
        await using var f = new Forwarder("dev", t, new SshProcessSupervisor(clock), new PortReservations(), clock);
        await f.StartAsync(); var r = f.ReconcileAsync(); await Eventually(() => t.Ssh.Tunnels.Count == 1); clock.Advance(TimeSpan.FromMilliseconds(1200)); await r;
        t.View["owner"] = false; await f.ReconcileAsync(); Assert.True(t.Ssh.Tunnels[0].Process.Stopped); Assert.False(await f.CloseAsync("web"));
    }
    [Fact]
    public async Task RemoteChildListsAndAcksUseUserCredentialAndChildRoute()
    {
        var api = new FakeRemoteApi(); var token = new Secret("fixture-user-credential"); var ssh = new FakeSshTransport();
        api.Responses.Enqueue(new(200, JsonSerializer.SerializeToElement(new object[0])));
        api.Responses.Enqueue(new(200, JsonDocument.Parse("""[{"id":"child-web","vmPort":80,"destination":{"vmName":"child","via":"primary","connectAddress":"10.0.0.2","connectPort":8080}}]""").RootElement));
        api.Responses.Enqueue(new(200)); api.Responses.Enqueue(new(204));
        var transport = new RemoteForwardTransport(ssh, api, new Uri("https://host/"), "primary", pin => pin == api.Fingerprint, RemoteAuthentication.Token, token, true);
        var view = await transport.ReadAsync(CancellationToken.None); Assert.Single(view!.Array("requests"));
        Assert.EndsWith("/api/v1/vms/primary/forwards?via=primary", api.Requests[1].Url.AbsoluteUri);
        await transport.WriteAckAsync("child-web", ForwardProtocol.AckDocument("child-web", new() { ["localPort"] = 80 }), CancellationToken.None);
        await transport.CloseAsync("child-web", CancellationToken.None);
        Assert.EndsWith("/api/v1/vms/child/forwards/child-web/ack", api.Requests[2].Url.AbsoluteUri);
        Assert.EndsWith("/api/v1/vms/child/forwards/child-web", api.Requests[3].Url.AbsoluteUri);
        Assert.All(api.Requests, r => { Assert.Same(token, r.Token); Assert.Equal(RemoteAuthentication.Token, r.Authentication); Assert.DoesNotContain(token.Reveal(), r.ToString()); });
        Assert.Empty(ssh.Scripts);
    }
    [Fact]
    public async Task FailedViaReadDoesNotReturnIncompleteWorld()
    {
        var api = new FakeRemoteApi(); api.Responses.Enqueue(new(200, JsonSerializer.SerializeToElement(new object[0]))); api.Responses.Enqueue(new(503));
        var transport = new RemoteForwardTransport(new FakeSshTransport(), api, new Uri("https://host/"), "primary", _ => true, RemoteAuthentication.Negotiate, null, true);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ReadAsync(CancellationToken.None));
    }
}
