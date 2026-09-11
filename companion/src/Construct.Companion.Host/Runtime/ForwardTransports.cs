using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Runtime;

namespace Construct.Companion.Host.Runtime;

public sealed class LocalForwardTransport(ISshTransport ssh, string claimId, string directory = ForwardProtocol.SpoolDirectory) : IForwardTransport
{
    public bool IsRemote => false;
    private bool ownsClaim;
    public async Task<string> CheckCapabilityAsync(CancellationToken cancellationToken)
    {
        var result = await ssh.RunRemoteScriptAsync(ForwardProtocol.CapabilityScript(directory), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return result.Code != 0 ? "unanswered" : ForwardProtocol.ParseCapability(result.Stdout) ? "supported" : "unsupported";
    }
    public IRunningProcess SpawnWatch(CancellationToken cancellationToken) => ssh.SpawnWatch(ForwardProtocol.WatchScript(directory), cancellationToken);
    public async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
    {
        var r = await ssh.RunRemoteScriptAsync(ForwardProtocol.ReconcileScript(claimId, directory), TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (r.Code != 0) return null;
        var view = ForwardProtocol.ParseDump(r.Stdout);
        ownsClaim = view.Str("owner") == "self";
        view["absent"] = view.Str("owner") == "absent";
        view["owner"] = ownsClaim;
        return view;
    }
    public Task WriteAckAsync(string id, JsonObject document, CancellationToken cancellationToken) => RunCheckedAsync(ForwardProtocol.AckScript(id, document, directory), cancellationToken);
    public Task SweepAsync(string sub, string id, CancellationToken cancellationToken) => RunCheckedAsync(ForwardProtocol.RemoveScript([(sub, id)], directory), cancellationToken);
    public Task CloseAsync(string id, CancellationToken cancellationToken) => RunCheckedAsync(ForwardProtocol.RemoveScript([("requests", id), ("acks", id), ("close", id)], directory), cancellationToken);
    public async Task ReleaseAsync(CancellationToken cancellationToken)
    {
        if (!ownsClaim) return;
        await RunCheckedAsync(ForwardProtocol.ReleaseScript(claimId, directory), cancellationToken).ConfigureAwait(false); ownsClaim = false;
    }
    private async Task RunCheckedAsync(string script, CancellationToken cancellationToken)
    {
        var r = await ssh.RunRemoteScriptAsync(script, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        if (r.Code != 0) throw new InvalidOperationException("Forward spool operation failed.");
    }
    public IRunningProcess SpawnTunnel(TunnelSpec spec, CancellationToken cancellationToken) => ssh.SpawnTunnel(spec, cancellationToken);
    public Task<bool> ProbePortAsync(int port, string bindHost, CancellationToken cancellationToken) => ssh.ProbePortAsync(port, bindHost, cancellationToken);
}

// The credential comes from the enrolled user's token store (or current-user Negotiate), never a VM token.
public sealed class RemoteForwardTransport(ISshTransport ssh, IRemoteApi api, Uri host, string vmName,
    Func<string, bool> verifyPin, RemoteAuthentication authentication, Secret? userCredential, bool network = false) : IForwardTransport
{
    private Dictionary<string, string> targets = [];
    public bool IsRemote => true;
    public Task<string> CheckCapabilityAsync(CancellationToken cancellationToken) => Task.FromResult("supported");
    public IRunningProcess SpawnWatch(CancellationToken cancellationToken) => throw new InvalidOperationException("Remote forwards use polling.");
    public async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
    {
        var self = Uri.EscapeDataString(vmName);
        if (await SendAsync("GET", $"vms/{self}/forwards", null, cancellationToken).ConfigureAwait(false) is not JsonArray entries) return null;
        if (network)
        {
            if (await SendAsync("GET", $"vms/{self}/forwards?via={self}", null, cancellationToken).ConfigureAwait(false) is not JsonArray via) return null;
            var seen = entries.OfType<JsonObject>().Select(e => e.Str("id")).ToHashSet();
            foreach (var entry in via.OfType<JsonObject>().Where(e => seen.Add(e.Str("id")))) entries.Add(entry.DeepClone());
        }
        var read = RemoteForwardList.Read(entries);
        targets = read.Array("requests").OfType<JsonObject>().Where(r => r["destination"] is JsonObject)
            .ToDictionary(r => r.Str("id"), r => r["destination"].Str("vmName"));
        foreach (var pending in read.Array("pending").OfType<JsonObject>()) targets[pending.Str("id")] = pending.Str("child");
        read["owner"] = true; return read;
    }
    public async Task WriteAckAsync(string id, JsonObject document, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["status"] = document.Str("status") };
        if (document["localPort"] is not null) body["localPort"] = document["localPort"]!.DeepClone();
        foreach (var key in new[] { "hostLabel", "message" }) if (document.Str(key).Length > 0) body[key] = document.Str(key);
        await SendAsync("POST", Route(id) + "/ack", body, cancellationToken).ConfigureAwait(false);
    }
    public Task SweepAsync(string sub, string id, CancellationToken cancellationToken) => Task.CompletedTask;
    public async Task CloseAsync(string id, CancellationToken cancellationToken) => await SendAsync("DELETE", Route(id), null, cancellationToken).ConfigureAwait(false);
    public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    private string Route(string id)
    {
        if (!ForwardProtocol.IsSafeId(id)) throw new ArgumentException("Unusable forward id.");
        return $"vms/{Uri.EscapeDataString(targets.GetValueOrDefault(id, vmName))}/forwards/{Uri.EscapeDataString(id)}";
    }
    private async Task<JsonNode?> SendAsync(string method, string path, JsonObject? body, CancellationToken cancellationToken)
    {
        if (authentication == RemoteAuthentication.None || authentication == RemoteAuthentication.Token && userCredential is null)
            throw new InvalidOperationException("Forward access requires a user credential.");
        var response = await api.SendAsync(new RemoteRequest(method, new Uri(host.AbsoluteUri.TrimEnd('/') + "/api/v1/" + path), verifyPin,
            body is null ? null : JsonSerializer.SerializeToElement(body), authentication, userCredential), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is < 200 or >= 300) throw new InvalidOperationException("Forward service operation failed.");
        return response.Body is { } json ? JsonNode.Parse(json.GetRawText()) : null;
    }
    public IRunningProcess SpawnTunnel(TunnelSpec spec, CancellationToken cancellationToken) => ssh.SpawnTunnel(spec, cancellationToken);
    public Task<bool> ProbePortAsync(int port, string bindHost, CancellationToken cancellationToken) => ssh.ProbePortAsync(port, bindHost, cancellationToken);
}
