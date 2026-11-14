using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Runtime;
namespace Construct.Companion.Fakes;

public sealed class FakeForwardTransport : IForwardTransport
{
    public FakeSshTransport Ssh { get; } = new();
    public bool IsRemote { get; set; }
    public string Capability { get; set; } = "supported";
    public JsonObject View { get; set; } = new() { ["owner"] = true, ["requests"] = new JsonArray(), ["acks"] = new JsonArray(), ["closes"] = new JsonArray() };
    public List<(string Id, JsonObject Doc)> Acks { get; } = [];
    public bool Released { get; private set; }
    public Task<string> CheckCapabilityAsync(CancellationToken cancellationToken) => Task.FromResult(Capability);
    public IRunningProcess SpawnWatch(CancellationToken cancellationToken) => Ssh.SpawnWatch("watch", cancellationToken);
    public Task<JsonObject?> ReadAsync(CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(View.Copy());
    public Task WriteAckAsync(string id, JsonObject document, CancellationToken cancellationToken)
    { Acks.Add((id, document.Copy())); View["acks"] = RuntimeJson.List(View.Array("acks").Where(a => a.Str("id") != id)); View.Array("acks").Add(document.Copy()); return Task.CompletedTask; }
    public Task SweepAsync(string sub, string id, CancellationToken cancellationToken)
    { var key = sub == "close" ? "closes" : sub; View[key] = RuntimeJson.List(View.Array(key).Where(n => (sub == "close" ? RuntimeJson.Text(n) : n.Str("id")) != id)); return Task.CompletedTask; }
    public Task CloseAsync(string id, CancellationToken cancellationToken)
    { View["requests"] = RuntimeJson.List(View.Array("requests").Where(n => n.Str("id") != id)); return Task.CompletedTask; }
    public Task ReleaseAsync(CancellationToken cancellationToken) { Released = true; return Task.CompletedTask; }
    public IRunningProcess SpawnTunnel(TunnelSpec spec, CancellationToken cancellationToken) => Ssh.SpawnTunnel(spec, cancellationToken);
    public Task<bool> ProbePortAsync(int port, string bindHost, CancellationToken cancellationToken) => Ssh.ProbePortAsync(port, bindHost, cancellationToken);
}
