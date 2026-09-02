using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// What kind of console a backend offers (<c>docs/drivers.md</c> §3.1/§4: <c>vmconnect</c>,
/// <c>none</c>, or a URL). It is a kind rather than a flag because a client has to <em>do</em>
/// something different for each: launch VMConnect, open a browser, or offer nothing at all.
/// </summary>
public enum ConsoleKind
{
    /// <summary>No console this client can open (a headless or remote-only backend).</summary>
    None,

    /// <summary>Hyper-V's own <c>vmconnect.exe</c>, on the machine the VM runs on.</summary>
    VmConnect,

    /// <summary>A web console; the address is in <see cref="DriverConsole.Url"/> (Proxmox noVNC, …).</summary>
    Url,
}

/// <summary>
/// The console capability as one value, so the kind and the address cannot contradict each other:
/// a <see cref="ConsoleKind.Url"/> console always has a URL, and no other kind ever does.
/// </summary>
public sealed record DriverConsole
{
    private DriverConsole(ConsoleKind kind, string? url)
    {
        Kind = kind;
        Url = url;
    }

    public ConsoleKind Kind { get; }

    /// <summary>Set only for <see cref="ConsoleKind.Url"/>.</summary>
    public string? Url { get; }

    public static DriverConsole None { get; } = new(ConsoleKind.None, null);

    public static DriverConsole VmConnect { get; } = new(ConsoleKind.VmConnect, null);

    public static DriverConsole At(string url) =>
        string.IsNullOrWhiteSpace(url)
            ? throw new ArgumentException("A URL console needs a URL.", nameof(url))
            : new DriverConsole(ConsoleKind.Url, url);

    /// <summary>
    /// The PowerShell contract's <c>Capabilities.Console</c> value — <c>'vmconnect'</c>, <c>'none'</c>,
    /// an empty value, or a URL — mapped onto this type. Anything unrecognized that is not a URL is
    /// <see cref="None"/>: a client that cannot tell what to open must be told there is nothing.
    /// </summary>
    public static DriverConsole Parse(string? value)
    {
        var text = value?.Trim();

        if (string.IsNullOrEmpty(text) || string.Equals(text, "none", StringComparison.OrdinalIgnoreCase))
        {
            return None;
        }

        if (string.Equals(text, "vmconnect", StringComparison.OrdinalIgnoreCase))
        {
            return VmConnect;
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            ? At(uri.ToString())
            : None;
    }
}

/// <summary>Capability flags of a driver (plan §4.2).</summary>
public sealed record DriverCapabilities(bool Checkpoints, bool Suspend, DriverConsole Console);

/// <summary>
/// The hypervisor seam. This mirrors the PowerShell driver contract of plan §4.2
/// (<c>New-Vm</c>/<c>Remove-Vm</c>/<c>Start-Vm</c>/<c>Stop-Vm</c>/<c>Get-VmState</c>/
/// <c>Get-VmEndpoint</c>/<c>Wait-VmReachable</c> + capability flags); <c>docs/drivers.md</c>, which
/// writes that contract down, lands with the driver extraction batch (B4).
///
/// The Windows implementation (B7) invokes <c>Create-AgentVM.ps1</c> and the Hyper-V cmdlets through
/// PowerShell; a future Proxmox driver maps the same operations onto its REST API. Nothing above
/// this interface may reference Hyper-V, PowerShell or Windows.
///
/// Explicitly NOT part of this contract: ISO building (<see cref="IIsoBuilder"/>), in-guest
/// provisioning (client-side) and client config.
///
/// Implementations should keep secrets out of their exceptions (no command lines with seed
/// credentials), but the service does not rely on it: an exception from this interface is reduced to
/// its type (<see cref="Logic.SafeError"/>) before anything — job state, the audit trail, the database
/// or the log — records it.
/// </summary>
public interface IHypervisorDriver
{
    DriverCapabilities Capabilities { get; }

    /// <summary>Creates the VM and starts the unattended install. Progress lines flow to the job.</summary>
    Task CreateVmAsync(VmDescriptor descriptor, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Removes the VM including its disk chain. Missing VMs are not an error.</summary>
    Task RemoveVmAsync(string name, IProgress<string>? progress, CancellationToken cancellationToken);

    Task StartAsync(string name, CancellationToken cancellationToken);

    Task StopAsync(string name, CancellationToken cancellationToken);

    /// <summary>Suspends to disk (Hyper-V <c>Save-VM</c>), freeing host RAM. Requires <see cref="DriverCapabilities.Suspend"/>.</summary>
    Task SaveAsync(string name, CancellationToken cancellationToken);

    Task<VmState> GetStateAsync(string name, CancellationToken cancellationToken);

    /// <summary>The driver-native endpoint, or <c>null</c> when the VM has none yet.</summary>
    Task<Endpoint?> GetEndpointAsync(string name, CancellationToken cancellationToken);

    /// <summary>Polls until SSH answers. Returns false on timeout (replaces the raw socket poll in <c>Create-AgentVM.ps1</c>).</summary>
    Task<bool> WaitReachableAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>Ejects the install ISO once the unattended install is done.</summary>
    Task DetachInstallMediaAsync(string name, CancellationToken cancellationToken);
}
