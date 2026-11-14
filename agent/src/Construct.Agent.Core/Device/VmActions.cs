using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.State;

namespace Construct.Agent.Device;

public sealed record OpenResult(string What, string Alias, string? Kind = null);

public sealed record ShareView(string Unc, string? Letter);

/// <summary>
/// A one-time T3 Code pairing link for T3 Desktop (11.4): <c>pairUrl</c> (the forwarded one), every link the VM
/// offers in its order, and the scope set it carries (<c>administrative</c> on Construct's T3 build, else <c>standard</c>).
/// </summary>
public sealed record T3PairingLink(string PairUrl, IReadOnlyList<T3PairingLinkEntry> Links, string Scopes);

public sealed record T3PairingLinkEntry(string Kind, string PairUrl);

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

    /// <summary>
    /// Mints a pairing link for T3 Desktop on one of the user's own running VMs, as <c>Get-ConstructT3PairingLink.ps1</c>
    /// did over SSH: the label names the VM (<c>construct-t3-desktop</c> for the default one), ten minutes, the
    /// administrative scopes where the VM's T3 offers them. The link carries a one-time token: it is returned to the
    /// caller only, never logged or put into a message.
    /// </summary>
    public async Task<T3PairingLink> T3PairingAsync(string engine, string vm, CancellationToken ct)
    {
        links.Target(engine, vm);
        if (!System.Text.RegularExpressions.Regex.IsMatch(vm, "^[a-z0-9][a-z0-9-]{0,62}$")) throw new AgentFailure(400, "invalid-request", "Not a VM name.");
        var ssh = links.Transport(engine, vm);
        var label = vm == "agent-vm" ? "construct-t3-desktop" : "construct-t3-desktop-" + vm;
        var pairing = await ssh.RunRemoteScriptAsync(DesktopPairingScript(label), TimeSpan.FromSeconds(90), ct).ConfigureAwait(false);
        switch (pairing.Code)
        {
            case 0:
                break;
            case 2:
                throw new AgentFailure(409, "t3-not-installed", "T3 Code is not installed on the VM.");
            case 7:
                throw new AgentFailure(409, "t3-forward-not-ready", "T3 Code's port forward is not ready yet; try again in a moment.");
            default:
                throw new AgentFailure(502, "t3-no-link", "T3 Code did not return a pairing link.");
        }

        var stdout = pairing.Stdout ?? "";
        var scopes = System.Text.RegularExpressions.Regex.Match(stdout, @"(?m)^CONSTRUCT_PAIRING_SCOPES=(administrative|standard)\s*$") is { Success: true } m ? m.Groups[1].Value : "standard";
        var json = System.Text.RegularExpressions.Regex.Replace(stdout, @"(?m)^CONSTRUCT_PAIRING_SCOPES=\w+\s*$", "").Trim();
        var offered = T3Code.ExtractPairLinks(json).Select(l => new T3PairingLinkEntry(l.Kind, l.PairUrl)).ToList();
        var url = offered.FirstOrDefault(l => l.Kind == "forwarded")?.PairUrl ?? T3Code.ExtractPairUrl(json);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new AgentFailure(502, "t3-no-link", "T3 Code did not return a pairing link.");
        return new T3PairingLink(url, offered, scopes);
    }

    /// <summary>The old Desktop script's guest side (<c>Get-ConstructT3PairingLink.ps1</c> before the agent), with the shared pairing helper.</summary>
    private static string DesktopPairingScript(string label) => """
        set -uo pipefail
        CONFIG_FILE=/etc/construct/config.env
        cfgget() {
          _v="$(sed -n "s/^$1=//p" "$CONFIG_FILE" 2>/dev/null | head -1)"
          case "$_v" in
            "'"*"'") _v="${_v#\'}"; _v="${_v%\'}"; _v="${_v//\'\\\'\'/\'}" ;;
          esac
          printf '%s' "$_v"
        }
        command -v t3 >/dev/null 2>&1 || { echo "t3 is not installed on the VM" >&2; exit 2; }
        T3CODE_PORT="$(cfgget T3CODE_PORT)"; T3CODE_PORT="${T3CODE_PORT:-5177}"
        ext="$(cfgget CONSTRUCT_EXTERNAL_HOST)"

        """ + GuestScripts.Render("construct-t3-pairing-base") + """

        base="$(t3base "${ext:-$(hostname).mshome.net}")" || exit 7
        scopes=standard
        extra=""
        if t3 auth pairing create --help 2>&1 | grep -q -- '--scopes'; then
          scopes=administrative; extra="--scopes administrative"
        fi
        out="$(t3pair "$base" --json --ttl 10m --label '
        """ + label + """
        ' $extra --log-level none)" || exit 3
        printf 'CONSTRUCT_PAIRING_SCOPES=%s\n' "$scopes"
        printf '%s\n' "$out"

        """;

    public async Task<ShareView> ShareAsync(string engine, string vm, CancellationToken ct)
    {
        var mapper = ShareOf(engine, vm);
        var (unc, _, _) = await ReadShareAsync(engine, vm, ct).ConfigureAwait(false);
        return new(unc, await mapper.MappedAsync(unc, ct).ConfigureAwait(false));
    }

    /// <summary>Maps the VM's workspace share with the credential the VM generated (<c>bin/setup-smb-share.sh</c>).</summary>
    public async Task<ShareView> MapShareAsync(string engine, string vm, CancellationToken ct)
    {
        var mapper = ShareOf(engine, vm);
        var (unc, user, password) = await ReadShareAsync(engine, vm, ct).ConfigureAwait(false);
        if (password is null) throw new AgentFailure(409, "share-off", "The VM has no workspace share (SMB_SHARE).");
        return new(unc, await mapper.MapAsync(unc, user, password, ct).ConfigureAwait(false));
    }

    public async Task UnmapShareAsync(string engine, string vm, CancellationToken ct)
    {
        var mapper = ShareOf(engine, vm);
        var (unc, _, _) = await ReadShareAsync(engine, vm, ct).ConfigureAwait(false);
        await mapper.UnmapAsync(unc, ct).ConfigureAwait(false);
    }

    private IShareMapper ShareOf(string engine, string vm)
    {
        var mapper = platform.Shares ?? throw NotAvailable.Feature("The workspace share");
        var target = links.Target(engine, vm);
        // Only a VM on this machine's engine: its share answers on the VM's own name, not through a host.
        if (target.Address is not { Kind: "direct" }) throw new AgentFailure(409, "share-local-only", "The workspace share is for a VM on this PC.");
        return mapper;
    }

    /// <summary>The share's UNC path (its name from SMB_SHARE_NAME) and its credential; the password is null when the share is off.</summary>
    private async Task<(string Unc, string User, string? Password)> ReadShareAsync(string engine, string vm, CancellationToken ct)
    {
        var result = await links.Transport(engine, vm).RunRemoteScriptAsync(
            "grep -E '^(SMB_SHARE|SMB_USER|SMB_PASSWORD|SMB_SHARE_NAME)=' /etc/construct/config.env || true", TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
        if (result.Code != 0) throw new AgentFailure(502, "vm-unreachable", "The VM did not answer over SSH.");
        var values = ParseConfig(result.Stdout);
        var unc = Unc(links.Target(engine, vm).Address?.Host ?? vm, values.GetValueOrDefault("SMB_SHARE_NAME") is { Length: > 0 } name ? name : "repo");
        var user = values.GetValueOrDefault("SMB_USER") is { Length: > 0 } u ? u : "dev";
        var password = values.GetValueOrDefault("SMB_SHARE") == "false" ? null : values.GetValueOrDefault("SMB_PASSWORD") is { Length: > 0 } p ? p : null;
        return (unc, user, password);
    }

    /// <summary>The share on the VM's direct address, as the engine named it (7.2).</summary>
    private static string Unc(string host, string share) => $@"\\{host}\{share}";

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
