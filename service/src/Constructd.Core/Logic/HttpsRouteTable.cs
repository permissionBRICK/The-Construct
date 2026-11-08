using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

/// <summary>
/// WHICH FORWARD A NAME ON THE SHARED HTTPS PORT REACHES (docs/https-forwards.md). The gateway routes by this
/// table and the API advertises from it, so a printed link is always one the gateway serves.
///
/// <para>Order, first match wins:</para>
/// <list type="number">
/// <item>the vault's web host serves the vault pages (the gateway checks it before the table);</item>
/// <item>a VM's own public name serves that VM's main forward, the newest one when several are main. A VM's own
///   name is reserved even while it has no main forward;</item>
/// <item><c>&lt;vm&gt;-&lt;name&gt;</c> serves that forward. A name a VM or the vault already uses, or that an older
///   forward already has, is skipped: that forward keeps its own port address.</item>
/// </list>
/// <para>Without a per-VM name pattern there are no names to route; only the vault is served.</para>
/// </summary>
public sealed class HttpsRouteTable
{
    private readonly Func<string, string> _vmHost;
    private readonly string? _pattern;
    private readonly string _publicHost;

    private HttpsRouteTable(IReadOnlyDictionary<string, PortForward> hosts, string? vaultHost, Func<string, string> vmHost,
        string? pattern, string publicHost)
    {
        Hosts = hosts;
        VaultHost = vaultHost;
        _vmHost = vmHost;
        _pattern = pattern;
        _publicHost = publicHost;
    }

    /// <summary>Host name (case-insensitive) → the forward it reaches.</summary>
    public IReadOnlyDictionary<string, PortForward> Hosts { get; }

    /// <summary>The vault's web host on the shared port, when it has one.</summary>
    public string? VaultHost { get; }

    /// <summary>An empty table: nothing but the vault.</summary>
    public static HttpsRouteTable Empty(string? vaultHost = null) =>
        new(new Dictionary<string, PortForward>(StringComparer.OrdinalIgnoreCase), vaultHost, n => n, null, string.Empty);

    /// <param name="forwards">All forwards of the host; only open HTTPS forwards of the listed VMs are routed.</param>
    /// <param name="primaryVmNames">The primary VMs that exist (not being deleted).</param>
    /// <param name="pattern">The per-VM name pattern (<c>Constructd:PublicHostPattern</c>); empty means no names.</param>
    /// <param name="publicHost">The service's own public host, the pattern's fallback.</param>
    /// <param name="vaultHost">The vault's web host, reserved for the vault pages.</param>
    public static HttpsRouteTable Build(IEnumerable<PortForward> forwards, IEnumerable<string> primaryVmNames,
        string? pattern, string publicHost, string? vaultHost)
    {
        ArgumentNullException.ThrowIfNull(forwards);
        ArgumentNullException.ThrowIfNull(primaryVmNames);
        if (string.IsNullOrWhiteSpace(pattern)) return Empty(vaultHost);

        string VmHost(string vm) => PublicHostPatternRules.Resolve(pattern, publicHost, vm);
        var vms = new HashSet<string>(primaryVmNames, StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(vms.Select(VmHost), StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(vaultHost)) reserved.Add(vaultHost);

        var https = forwards
            .Where(f => f.Https && f.PublicPort is not null && vms.Contains(f.VmName))
            .OrderBy(f => f.Created).ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
        var hosts = new Dictionary<string, PortForward>(StringComparer.OrdinalIgnoreCase);
        foreach (var forward in https.Where(f => f.Main))
        {
            var host = VmHost(forward.VmName);
            if (string.Equals(host, vaultHost, StringComparison.OrdinalIgnoreCase)) continue;
            hosts[host] = forward;
        }

        foreach (var forward in https)
        {
            var host = HttpsForwardNames.Host(pattern, publicHost, forward.VmName, HttpsForwardNames.Effective(forward));
            if (host is null || reserved.Contains(host) || hosts.ContainsKey(host)) continue;
            hosts[host] = forward;
        }

        return new(hosts, vaultHost, VmHost, pattern, publicHost);
    }

    /// <summary>
    /// The host the API advertises for <paramref name="forward"/>: the VM's own name when this forward is the
    /// main one routed there, else its <c>&lt;vm&gt;-&lt;name&gt;</c> host when that reaches it, else null (it keeps
    /// its port address).
    /// </summary>
    public string? HostOf(PortForward forward)
    {
        ArgumentNullException.ThrowIfNull(forward);
        if (string.IsNullOrWhiteSpace(_pattern)) return null;
        var own = _vmHost(forward.VmName);
        if (forward.Main && Hosts.TryGetValue(own, out var main) && main.Id == forward.Id) return own;
        return NamedHostOf(forward);
    }

    /// <summary>The forward's <c>&lt;vm&gt;-&lt;name&gt;</c> host when the table routes it to this forward, else null.</summary>
    public string? NamedHostOf(PortForward forward)
    {
        ArgumentNullException.ThrowIfNull(forward);
        var named = HttpsForwardNames.Host(_pattern, _publicHost, forward.VmName, HttpsForwardNames.Effective(forward));
        return named is not null && Hosts.TryGetValue(named, out var routed) && routed.Id == forward.Id ? named : null;
    }
}
