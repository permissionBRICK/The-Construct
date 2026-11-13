using System.Text.Json.Nodes;
using Construct.Companion.Core.State;

namespace Construct.Agent.Device;

public sealed record OpenResult(string What, string Alias, string? Kind = null);

public sealed record ShareView(string Unc, string? Letter);

/// <summary>
/// The "open" actions of a VM (11.2: T3 Code, VS Code, an SSH terminal) and its workspace share, as the
/// Companion's dispatcher ran them (<c>openAgentWeb</c>, <c>connect</c>, <c>Mount-RepoShare</c>), without
/// prompts: T3 Code opens the first link the VM offers (its order: the user's proxy, the forward, direct).
/// </summary>
public sealed class VmActions(VmLinks links, IDevicePlatform platform)
{
    public const string Workspace = "/root/repos";

    public async Task<OpenResult> OpenAsync(string engine, string vm, string what, CancellationToken ct)
    {
        var target = links.Target(engine, vm);
        switch (what)
        {
            case "vscode":
                links.WriteAlias(target);
                await platform.OpenAsync("vscode://vscode-remote/ssh-remote+" + Uri.EscapeDataString(target.Alias) +
                    string.Join('/', Workspace.Split('/').Select(Uri.EscapeDataString)), ct).ConfigureAwait(false);
                return new(what, target.Alias);
            case "ssh":
                links.WriteAlias(target);
                await platform.OpenTerminalAsync(target.Alias, ct).ConfigureAwait(false);
                return new(what, target.Alias);
            case "t3":
            {
                var ssh = links.Transport(engine, vm);
                var pairing = await ssh.RunRemoteScriptAsync(T3Code.BuildPairingScript(new JsonObject { ["name"] = vm }), TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
                if (pairing.Code == 7) throw new AgentFailure(409, "t3-forward-not-ready", "T3 Code's port forward is not ready yet; try again in a moment.");
                var link = pairing.Code == 0 ? T3Code.ExtractPairLinks(pairing.Stdout).FirstOrDefault() : null;
                var url = link?.PairUrl ?? (pairing.Code == 0 ? T3Code.ExtractPairUrl(pairing.Stdout) : "");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                    throw new AgentFailure(502, "t3-no-link", "T3 Code did not return a pairing link.");
                // The pairing link carries a one-time token: it goes to the browser, never into an answer or a log.
                await platform.OpenAsync(uri.AbsoluteUri, ct).ConfigureAwait(false);
                return new(what, target.Alias, link?.Kind ?? "direct");
            }

            default:
                throw new AgentFailure(400, "invalid-request", "Open takes t3, vscode or ssh.");
        }
    }

    public async Task<ShareView> ShareAsync(string engine, string vm, CancellationToken ct)
    {
        var (mapper, unc) = ShareOf(engine, vm);
        return new(unc, await mapper.MappedAsync(unc, ct).ConfigureAwait(false));
    }

    /// <summary>Maps the VM's workspace share with the credential the VM generated (<c>bin/setup-smb-share.sh</c>).</summary>
    public async Task<ShareView> MapShareAsync(string engine, string vm, CancellationToken ct)
    {
        var (mapper, _) = ShareOf(engine, vm);
        var result = await links.Transport(engine, vm).RunRemoteScriptAsync(
            "grep -E '^(SMB_SHARE|SMB_USER|SMB_PASSWORD|SMB_SHARE_NAME)=' /etc/construct/config.env || true", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (result.Code != 0) throw new AgentFailure(502, "vm-unreachable", "The VM did not answer over SSH.");
        var values = ParseConfig(result.Stdout);
        if (values.GetValueOrDefault("SMB_SHARE") == "false" || values.GetValueOrDefault("SMB_PASSWORD") is not { Length: > 0 } password)
            throw new AgentFailure(409, "share-off", "The VM has no workspace share (SMB_SHARE).");
        var unc = Unc(vm, values.GetValueOrDefault("SMB_SHARE_NAME") is { Length: > 0 } name ? name : "repo");
        return new(unc, await mapper.MapAsync(unc, values.GetValueOrDefault("SMB_USER") is { Length: > 0 } user ? user : "dev", password, ct).ConfigureAwait(false));
    }

    public async Task UnmapShareAsync(string engine, string vm, CancellationToken ct)
    {
        var (mapper, unc) = ShareOf(engine, vm);
        await mapper.UnmapAsync(unc, ct).ConfigureAwait(false);
    }

    private (IShareMapper Mapper, string Unc) ShareOf(string engine, string vm)
    {
        var mapper = platform.Shares ?? throw NotAvailable.Feature("The workspace share");
        var target = links.Target(engine, vm);
        // Only a VM on this machine's engine: its share answers on the VM's own name, not through a host.
        if (target.Address is not { Kind: "direct" }) throw new AgentFailure(409, "share-local-only", "The workspace share is for a VM on this PC.");
        return (mapper, Unc(vm, "repo"));
    }

    private static string Unc(string vm, string share) => $@"\\{vm}.mshome.net\{share}";

    /// <summary><c>KEY=value</c> lines of config.env, with config-set.sh's single quotes undone.</summary>
    public static Dictionary<string, string> ParseConfig(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var at = line.IndexOf('=');
            if (at <= 0) continue;
            var value = line[(at + 1)..].TrimEnd('\r');
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1].Replace("'\\''", "'", StringComparison.Ordinal);
            values[line[..at]] = value;
        }

        return values;
    }
}
