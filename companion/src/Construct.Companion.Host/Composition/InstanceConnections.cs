using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Host.Composition;

// Real ssh/argv transports per instance; the fake registers FakeInstanceConnections instead.
public sealed class InstanceConnections(IProcessRunner runner, IPortProbe ports, IFileSystem files,
    IRemoteApi api, ITokenStore tokens, RuntimeClaimId claim) : IInstanceConnections
{
    public ISshTransport Ssh(JsonObject instance)
    {
        var cfg = new SshConfiguration(StateJson.String(instance["vmHost"]), StateJson.String(instance["hostAlias"]), KeyName: StateJson.String(instance["keyName"]), SshPort: Instances.CoercePort(instance["sshPort"]) ?? 22, ConnectTimeout: 8);
        var keyPath = files.GetRoot(FileSystemRoot.UserProfile) is { Length: > 0 } root ? Path.Combine(root, ".ssh", cfg.KeyName) : null;
        return new ProcessSshTransport(runner, ports, cfg, HostProcesses.SshExecutable(files), keyPath);
    }
    public async Task<IForwardTransport> ForwardsAsync(JsonObject instance, ISshTransport ssh, CancellationToken ct)
    {
        if (!Instances.IsRemoteBackend(StateJson.Text(instance["backend"]))) return new LocalForwardTransport(ssh, claim.Value);
        var service = instance["service"]!.AsObject(); var url = RemoteHost.NormalizeServiceUrl(StateJson.String(service["url"])); RemoteHost.AssertTransportSafe(url);
        var pin = RemoteHost.ReadPin(files, url); var secure = new Uri(url).Scheme == "https";
        if (secure && pin.Length == 0) throw new InvalidOperationException("Enroll this host to confirm its certificate.");
        var authentication = StateJson.Text(service["auth"]) == "token" ? RemoteAuthentication.Token : RemoteAuthentication.Negotiate;
        var token = authentication == RemoteAuthentication.Token ? await tokens.ReadAsync(RemoteHost.HostSlug(url), ct) : null;
        if (authentication == RemoteAuthentication.Token && token is null) throw new InvalidOperationException("The enrolled user's credential is unavailable.");
        var client = new RemoteHostClient(api, files, tokens, url, authentication);
        var health = await client.HealthAsync(ct); var network = (health?["apiFeatures"] as JsonArray ?? []).Select(StateJson.String).Contains("network");
        return new RemoteForwardTransport(ssh, api, new Uri(url), StateJson.String(instance["vmName"]), actual => !secure || RemoteHost.FingerprintsMatch(pin, actual), authentication, token, network);
    }
}
