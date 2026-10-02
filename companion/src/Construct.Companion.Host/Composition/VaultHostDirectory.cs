using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.State;
using Construct.Companion.Core.Vault;
using Construct.Companion.Host.Dispatch;
namespace Construct.Companion.Host.Composition;

// The key vault's view of this user's hosts: host administration's enrolment and whoami detection, the
// instance registry, and T3 pairing over the instance's own SSH transport (exactly as Open T3 Code runs it).
// Both are resolved lazily: they depend on the runtime registry, which depends on the vault.
internal sealed class VaultHostDirectory(Func<HostAdministration> hosts, Func<CompanionInstances> instances, IRemoteApi api, IStateFileSystem files, ITokenStore tokens) : IVaultHostDirectory
{
    public IReadOnlyList<VaultHostRef> Hosts()
    {
        var result = new List<VaultHostRef>();
        foreach (var host in hosts().List())
            try { result.Add(new(host.Slug, new Uri(host.Url).Host, new RemoteHostClient(api, files, tokens, host.Url, host.Auth == "token" ? RemoteAuthentication.Token : RemoteAuthentication.Negotiate))); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or UriFormatException) { /* not a usable host address */ }
        return result;
    }
    public IReadOnlyList<VaultHostInstance> Instances(string slug) =>
        instances().Registry.List().Select(VaultHosts.Instance).OfType<VaultHostInstance>().Where(i => i.Slug == slug).ToArray();
    public Task<string?> CheckUserAsync(string slug, CancellationToken cancellationToken) => hosts().VaultUserProblemAsync(slug, cancellationToken);
    public Task<ProcessResult> RunT3PairingAsync(string instance, CancellationToken cancellationToken)
    {
        var entry = instances().Get(instance);
        return entry.Ssh.RunRemoteScriptAsync(T3Code.BuildPairingScript(entry.Definition), TimeSpan.FromSeconds(90), cancellationToken);
    }
}
