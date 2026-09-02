using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging;

namespace Constructd.Windows.Forwards;

/// <summary>
/// A host port-proxy rule could not be applied. Safe by construction: it names the VM and the public
/// port, both of which the caller already knows, and never the netsh output.
/// </summary>
public sealed class PortForwardException : Exception, IConstructdError
{
    public PortForwardException(string vmName, int publicPort, string? detail = null)
        : base($"Could not apply the host port forward for VM '{vmName}' on port {publicPort}.")
    {
        VmName = vmName;
        PublicPort = publicPort;
        Detail = detail;
    }

    private PortForwardException(string message, string? detail)
        : base(message)
    {
        VmName = "-";
        Detail = detail;
    }

    public string VmName { get; }

    public int PublicPort { get; }

    /// <summary>
    /// How it failed, in this service's own words (which netsh call, and its exit code or timeout).
    /// Never netsh's output.
    /// </summary>
    public string? Detail { get; }

    /// <summary>The host's own rule list could not be read, so nothing can be reconciled against it.</summary>
    public static PortForwardException CannotEnumerate(string? detail) =>
        new("Could not read the host's port-proxy rules.", detail);
}

/// <summary>
/// <see cref="IPortForwardManager"/> on <c>netsh interface portproxy</c> (plan §4.4, §4.6).
///
/// The bookkeeping — the two <see cref="PortAllocator"/> ranges, the per-VM gate, the durable-before-live
/// ordering, the per-VM cap — is the same as the in-memory manager's, because it is the part that has to
/// hold whatever materializes the forward. What this adds is the host side: a v4tov4 rule per
/// <see cref="ForwardTarget.Host"/> forward, and a reconciliation pass that makes the host's actual
/// rules match the store again after a reboot, a crash, or a VM's DHCP address changing.
///
/// <see cref="ForwardTarget.Client"/> forwards are only recorded here: they are opened on the user's PC
/// by the extension (plan §4.6), so materializing them on the host would be exactly the LAN exposure
/// the client target exists to avoid.
/// </summary>
public sealed class NetshPortForwardManager : IPortForwardManager
{
    /// <summary>netsh answers in milliseconds; a longer wait means something is wrong, not slow.</summary>
    private static readonly TimeSpan NetshTimeout = TimeSpan.FromSeconds(30);

    private readonly IClock _clock;
    private readonly IVmRepository _vms;
    private readonly IForwardStore _store;
    private readonly IHypervisorDriver _driver;
    private readonly IProcessRunner _processes;
    private readonly IHostAddressResolver _addresses;
    private readonly ITcpTableReader _tcpTable;
    private readonly ConstructdOptions _options;
    private readonly ILogger<NetshPortForwardManager> _logger;

    private readonly PortAllocator _sshPorts;
    private readonly PortAllocator _appPorts;
    private readonly string _listenAddress;

    private readonly ConcurrentDictionary<string, int> _sshForwards = new(StringComparer.OrdinalIgnoreCase);

    // One gate per VM: allocation, adding a forward and tearing all of them down are state transitions
    // of the same VM and must not interleave (the in-memory manager documents the same rule).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _vmGates = new(StringComparer.OrdinalIgnoreCase);

    public NetshPortForwardManager(
        IClock clock,
        IVmRepository vms,
        IForwardStore store,
        IHypervisorDriver driver,
        IProcessRunner processes,
        IHostAddressResolver addresses,
        ITcpTableReader tcpTable,
        ConstructdOptions options,
        ILogger<NetshPortForwardManager> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _clock = clock;
        _vms = vms;
        _store = store;
        _driver = driver;
        _processes = processes;
        _addresses = addresses;
        _tcpTable = tcpTable;
        _options = options;
        _logger = logger;

        _sshPorts = new PortAllocator(options.SshForwardPorts.Start, options.SshForwardPorts.End);
        _appPorts = new PortAllocator(options.AppForwardPorts.Start, options.AppForwardPorts.End);
        _listenAddress = ArgumentGuard.IPv4(options.ListenAddress, "Constructd:ListenAddress");

        // Two independent allocators over overlapping ranges would each consider a shared port free and
        // hand it to a different VM, and the second netsh rule would silently replace the first. There
        // is no configuration in which that is what the admin meant, so it is refused at startup.
        if (_sshPorts.Start <= _appPorts.End && _appPorts.Start <= _sshPorts.End)
        {
            throw new InvalidOperationException(
                $"Constructd:SshForwardPorts ({_sshPorts.Start}-{_sshPorts.End}) and " +
                $"Constructd:AppForwardPorts ({_appPorts.Start}-{_appPorts.End}) overlap. They are " +
                "allocated independently, so an overlap hands the same public port to two VMs.");
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The port is written onto the VM record — its durable home — <em>before</em> the netsh rule is
    /// created, so a crash between the two leaves an allocation with no rule (which
    /// <see cref="ReconcileAsync"/> repairs) and never a live rule that no allocation accounts for and
    /// that a later VM could be handed as well.
    /// </remarks>
    public async Task<int> AllocateSshForwardAsync(string vmName, CancellationToken cancellationToken)
    {
        var name = ArgumentGuard.VmName(vmName);
        var gate = GateFor(name);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vm = await _vms.GetAsync(name, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Unknown VM '{name}'.");

            if (_sshForwards.TryGetValue(name, out var known))
            {
                return known;
            }

            // Already allocated durably (a previous attempt, or a restart): adopt it rather than
            // handing the VM a second port.
            if (vm.SshForwardPort is int stored)
            {
                Reserve(stored);
                _sshForwards[name] = stored;
                await MaterializeSshAsync(name, stored, cancellationToken).ConfigureAwait(false);
                return stored;
            }

            var port = _sshPorts.Allocate();
            try
            {
                await _vms.UpdateAsync(vm with { SshForwardPort = port }, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                _sshPorts.Release(port);
                throw;
            }

            _sshForwards[name] = port;
            await MaterializeSshAsync(name, port, cancellationToken).ConfigureAwait(false);
            return port;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> ReleaseSshForwardAsync(string vmName, CancellationToken cancellationToken)
    {
        var name = ArgumentGuard.VmName(vmName);
        var gate = GateFor(name);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vm = await _vms.GetAsync(name, cancellationToken).ConfigureAwait(false);
            var storedPort = vm?.SshForwardPort;

            var released = _sshForwards.TryRemove(name, out var port);
            if (released)
            {
                await DeleteRuleAsync(port, cancellationToken).ConfigureAwait(false);
                Release(port);
            }

            if (storedPort is int durable)
            {
                if (durable != port)
                {
                    await DeleteRuleAsync(durable, cancellationToken).ConfigureAwait(false);
                }

                Release(durable);
                await _vms.UpdateAsync(vm! with { SshForwardPort = null }, cancellationToken).ConfigureAwait(false);
                released = true;
            }

            return released;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<AddForwardResult> TryAddForwardAsync(
        string vmName,
        int vmPort,
        ForwardTarget target,
        string label,
        int maxForwards,
        CancellationToken cancellationToken)
    {
        var name = ArgumentGuard.VmName(vmName);
        ArgumentGuard.Port(vmPort, "vm port");

        // Counting, checking that the VM is still there, allocating and storing behind the VM's gate,
        // so concurrent requests cannot exceed the cap, share a public port, or slip a forward past a
        // teardown that is already running.
        var gate = GateFor(name);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vm = await _vms.GetAsync(name, cancellationToken).ConfigureAwait(false);
            if (vm is null || vm.Deleting)
            {
                return AddForwardResult.VmUnavailable;
            }

            var existing = await _store.CountByVmAsync(name, cancellationToken).ConfigureAwait(false);
            if (existing >= maxForwards)
            {
                return AddForwardResult.LimitReached;
            }

            int? publicPort = target == ForwardTarget.Host ? _appPorts.Allocate() : null;
            var forward = new PortForward(
                Id: Guid.NewGuid().ToString("n"),
                VmName: name,
                VmPort: vmPort,
                PublicPort: publicPort,
                Target: target,
                Label: label,
                Created: _clock.UtcNow);

            try
            {
                await _store.AddAsync(forward, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Never leak the port when the state could not be recorded.
                if (publicPort is int failed)
                {
                    _appPorts.Release(failed);
                }

                throw;
            }

            if (publicPort is int port)
            {
                try
                {
                    var address = await ResolveVmAddressAsync(name, port, cancellationToken).ConfigureAwait(false);
                    await AddRuleAsync(name, port, address.Address, vmPort, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // The record only exists to describe a live rule; without one it would be a forward
                    // the user can see and nothing can reach, and it would hold a port forever.
                    await _store.RemoveAsync(forward.Id, cancellationToken).ConfigureAwait(false);
                    _appPorts.Release(port);
                    throw;
                }
            }

            return AddForwardResult.Added(forward);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> RemoveForwardAsync(string vmName, string id, CancellationToken cancellationToken)
    {
        var forward = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (forward is null || !string.Equals(forward.VmName, vmName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!await _store.RemoveAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await ReleasePublicPortAsync(forward, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<int> RemoveAllForwardsAsync(string vmName, CancellationToken cancellationToken)
    {
        var name = ArgumentGuard.VmName(vmName);

        // Under the VM's gate, so an add cannot land between the enumeration and the removals.
        var gate = GateFor(name);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var removed = 0;
            foreach (var forward in await _store.ListAsync(name, cancellationToken).ConfigureAwait(false))
            {
                if (await _store.RemoveAsync(forward.Id, cancellationToken).ConfigureAwait(false))
                {
                    await ReleasePublicPortAsync(forward, cancellationToken).ConfigureAwait(false);
                    removed++;
                }
            }

            return removed;
        }
        finally
        {
            gate.Release();
        }
    }

    public Task<IReadOnlyList<PortForward>> ListAsync(string? vmName, CancellationToken cancellationToken) =>
        _store.ListAsync(vmName, cancellationToken);

    /// <summary>
    /// Makes the host's rules agree with the store again.
    ///
    /// Three kinds of drift, all repaired here: a stored forward with no rule (the service died between
    /// the durable write and the netsh call, or somebody flushed portproxy), a rule pointing at an
    /// address the VM no longer has (Hyper-V's NAT DHCP handed it a new one), and a rule <em>in our own
    /// port ranges</em> that the store knows nothing about (a VM removed while the service was down).
    /// Rules outside the configured ranges, or on another listen address, are left alone — the host may
    /// have port proxies that are none of this service's business.
    ///
    /// It also re-reserves every allocated port in its allocator, which is what stops a new VM being
    /// handed a port that is already in use after a restart.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var rules = await ShowRulesAsync(cancellationToken).ConfigureAwait(false);
        var existing = rules
            .Where(rule => string.Equals(rule.ListenAddress, _listenAddress, StringComparison.Ordinal))
            .GroupBy(rule => rule.ListenPort)
            .ToDictionary(group => group.Key, group => group.First());

        // Every port the store accounts for — including one whose VM address cannot be resolved right
        // now, so a temporary DNS failure never makes reconciliation delete a live rule.
        var known = new HashSet<int>();
        var wanted = new List<(string VmName, int PublicPort, string ConnectAddress, int ConnectPort)>();

        foreach (var vm in await _vms.ListAsync(owner: null, cancellationToken).ConfigureAwait(false))
        {
            if (vm.SshForwardPort is not int port)
            {
                continue;
            }

            known.Add(port);
            _sshForwards[vm.Name] = port;
            Reserve(port);

            // Against the SSH range specifically, not the union: a port that now falls in the APP
            // range is not one this allocator can account for, and re-materializing it would leave a
            // live rule on a port _appPorts still considers free and will hand to somebody else.
            if (!IsSshPort(port))
            {
                // Allocated under a wider (or different) range that the admin has since narrowed. It is
                // grandfathered: left exactly as it is, and no netsh call is made for it either way —
                // "this service only touches rules inside its configured ranges" has to hold for the
                // rules it would add just as much as for the ones it would delete.
                WarnOutOfRange("the SSH forward of VM", vm.Name, port, _sshPorts);
                continue;
            }

            var endpoint = await TryResolveAsync(vm.Name, cancellationToken).ConfigureAwait(false);
            if (endpoint is { } ssh)
            {
                wanted.Add((vm.Name, port, ssh.Address, ssh.SshPort));
            }
        }

        foreach (var forward in await _store.ListAsync(vmName: null, cancellationToken).ConfigureAwait(false))
        {
            if (forward.PublicPort is not int port)
            {
                continue;
            }

            known.Add(port);
            Reserve(port);

            if (forward.Target != ForwardTarget.Host)
            {
                continue;
            }

            if (!IsAppPort(port))
            {
                WarnOutOfRange("a host forward of VM", forward.VmName, port, _appPorts);
                continue;
            }

            var endpoint = await TryResolveAsync(forward.VmName, cancellationToken).ConfigureAwait(false);
            if (endpoint is { } vmAddress)
            {
                wanted.Add((forward.VmName, port, vmAddress.Address, forward.VmPort));
            }
        }

        var repaired = 0;

        foreach (var (vmName, publicPort, connectAddress, connectPort) in wanted)
        {
            if (existing.TryGetValue(publicPort, out var rule))
            {
                if (string.Equals(rule.ConnectAddress, connectAddress, StringComparison.Ordinal) &&
                    rule.ConnectPort == connectPort)
                {
                    continue;
                }

                // netsh has no "update"; the pair is the update. If the delete does not take, the add
                // would land on top of a rule still pointing at the old address — so this fails loudly
                // rather than leaving the forward wrong and reporting it repaired.
                if (!await DeleteRuleAsync(publicPort, cancellationToken).ConfigureAwait(false))
                {
                    throw Fail(vmName, publicPort, "netsh delete left the stale rule in place");
                }
            }

            await AddRuleAsync(vmName, publicPort, connectAddress, connectPort, cancellationToken).ConfigureAwait(false);
            repaired++;
        }

        foreach (var rule in existing.Values)
        {
            if (known.Contains(rule.ListenPort) || !IsOurs(rule.ListenPort))
            {
                continue;
            }

            _logger.LogWarning(
                "Removing an unknown port-proxy rule on {Address}:{Port} — it is inside a configured range " +
                "but no VM or forward accounts for it.",
                _listenAddress,
                rule.ListenPort);

            // The whole point of this sweep is that the rule stops existing: it is inside our range,
            // nothing accounts for it, and it is exposed on the LAN. Counting a delete that netsh
            // refused would report the host as reconciled while the rule is still live and forwarding.
            if (!await DeleteRuleAsync(rule.ListenPort, cancellationToken).ConfigureAwait(false))
            {
                throw Fail(string.Empty, rule.ListenPort, "netsh delete left an unaccounted-for rule in place");
            }

            repaired++;
        }

        return repaired;
    }

    /// <summary>
    /// Live connections through any of the VM's public ports — its SSH forward plus every host-target
    /// forward. A client tunnel rides the SSH connection, so it is counted there (plan §4.6).
    /// </summary>
    public async Task<int> CountActiveConnectionsAsync(string vmName, CancellationToken cancellationToken)
    {
        var ports = new HashSet<int>();

        var vm = await _vms.GetAsync(vmName, cancellationToken).ConfigureAwait(false);
        if (vm?.SshForwardPort is int sshPort)
        {
            ports.Add(sshPort);
        }

        foreach (var forward in await _store.ListAsync(vmName, cancellationToken).ConfigureAwait(false))
        {
            if (forward.PublicPort is int publicPort)
            {
                ports.Add(publicPort);
            }
        }

        if (ports.Count == 0)
        {
            return 0;
        }

        return TcpConnectionCounter.CountEstablished(_tcpTable.Read(), ports);
    }

    // ── host rules ───────────────────────────────────────────────────────────────────────────────

    private async Task MaterializeSshAsync(string vmName, int publicPort, CancellationToken cancellationToken)
    {
        var endpoint = await ResolveVmAddressAsync(vmName, publicPort, cancellationToken).ConfigureAwait(false);
        await AddRuleAsync(vmName, publicPort, endpoint.Address, endpoint.SshPort, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The VM's own address and SSH port, from the driver's endpoint. Resolved at apply time: the
    /// address is DHCP-assigned and changes across reboots.
    /// </summary>
    private async Task<(string Address, int SshPort)> ResolveVmAddressAsync(
        string vmName,
        int publicPort,
        CancellationToken cancellationToken)
    {
        var endpoint = await _driver.GetEndpointAsync(vmName, cancellationToken).ConfigureAwait(false);
        if (endpoint is null)
        {
            throw Fail(vmName, publicPort, "the driver reports no endpoint for the VM yet");
        }

        var address = await _addresses.ResolveIPv4Async(endpoint.SshHost, cancellationToken).ConfigureAwait(false);
        if (address is null)
        {
            throw Fail(vmName, publicPort, $"the VM's endpoint host does not resolve to an IPv4 address ({endpoint.SshHost})");
        }

        return (address.ToString(), ArgumentGuard.Port(endpoint.SshPort, "vm ssh port"));
    }

    /// <summary>
    /// Reconciliation's variant: it repairs what it can and reports the rest, so one unreachable VM
    /// must not stop the pass — and must not get its live rule deleted either (see <c>known</c>).
    /// </summary>
    private async Task<(string Address, int SshPort)?> TryResolveAsync(string vmName, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = await _driver.GetEndpointAsync(vmName, cancellationToken).ConfigureAwait(false);
            var address = endpoint is null
                ? null
                : await _addresses.ResolveIPv4Async(endpoint.SshHost, cancellationToken).ConfigureAwait(false);

            if (endpoint is null || address is null)
            {
                _logger.LogWarning(
                    "No IPv4 address for {Vm} while reconciling port forwards; leaving its rules as they are.",
                    vmName);
                return null;
            }

            return (address.ToString(), ArgumentGuard.Port(endpoint.SshPort, "vm ssh port"));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Could not resolve an address for {Vm} while reconciling port forwards: {Error}",
                vmName,
                SafeError.Describe(ex));

            return null;
        }
    }

    private async Task AddRuleAsync(
        string vmName,
        int publicPort,
        string connectAddress,
        int connectPort,
        CancellationToken cancellationToken)
    {
        string[] arguments =
        [
            "interface",
            "portproxy",
            "add",
            "v4tov4",
            $"listenaddress={_listenAddress}",
            $"listenport={ArgumentGuard.Port(publicPort, "public port")}",
            $"connectaddress={ArgumentGuard.IPv4(connectAddress, "vm address")}",
            $"connectport={ArgumentGuard.Port(connectPort, "vm port")}",
        ];

        var result = await RunNetshAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw Fail(vmName, publicPort, Describe("netsh add", result));
        }
    }

    /// <summary>
    /// Deletes one rule and reports whether netsh actually did it.
    ///
    /// The failure is swallowed rather than thrown because <em>teardown</em> is best effort by design: a
    /// rule that is already gone, and a netsh that refuses, must not stop a VM from being deleted or a
    /// forward from being released — the alternative is a VM that cannot be removed because of a rule
    /// that may not even exist. Callers for whom the deletion is the point — reconciliation — check the
    /// result instead of assuming it, so a rule that survived is never counted as repaired.
    /// </summary>
    private async Task<bool> DeleteRuleAsync(int publicPort, CancellationToken cancellationToken)
    {
        string[] arguments =
        [
            "interface",
            "portproxy",
            "delete",
            "v4tov4",
            $"listenaddress={_listenAddress}",
            $"listenport={ArgumentGuard.Port(publicPort, "public port")}",
        ];

        try
        {
            var result = await RunNetshAsync(arguments, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                _logger.LogWarning(
                    "Could not delete the port-proxy rule on {Address}:{Port}: {Reason}.",
                    _listenAddress,
                    publicPort,
                    Describe("netsh delete", result));
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "Deleting the port-proxy rule on {Address}:{Port} failed: {Error}",
                _listenAddress,
                publicPort,
                SafeError.Describe(ex));
            return false;
        }
    }

    private async Task<IReadOnlyList<PortProxyRule>> ShowRulesAsync(CancellationToken cancellationToken)
    {
        var result = await RunNetshAsync(
            ["interface", "portproxy", "show", "v4tov4"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            var reason = Describe("netsh show", result);
            _logger.LogError("Could not read the host's port-proxy rules: {Reason}", reason);
            throw PortForwardException.CannotEnumerate(reason);
        }

        return PortProxyParser.Parse(result.StandardOutput);
    }

    private Task<ProcessResult> RunNetshAsync(string[] arguments, CancellationToken cancellationToken) =>
        _processes.RunAsync(
            _options.NetshPath,
            arguments,
            standardInput: null,
            NetshTimeout,
            standardOutputLines: null,
            cancellationToken);

    private async Task ReleasePublicPortAsync(PortForward forward, CancellationToken cancellationToken)
    {
        if (forward.PublicPort is not int port)
        {
            return;
        }

        if (forward.Target == ForwardTarget.Host)
        {
            await DeleteRuleAsync(port, cancellationToken).ConfigureAwait(false);
        }

        Release(port);
    }

    private void WarnOutOfRange(string what, string vmName, int port, PortAllocator allocator) =>
        _logger.LogWarning(
            "Port {Port} of {What} {Vm} is outside its configured range ({Start}-{End}); leaving its " +
            "host rule alone. Widen the range or release the forward.",
            port,
            what,
            vmName,
            allocator.Start,
            allocator.End);

    private bool IsSshPort(int port) => port >= _sshPorts.Start && port <= _sshPorts.End;

    private bool IsAppPort(int port) => port >= _appPorts.Start && port <= _appPorts.End;

    /// <summary>
    /// Marks a port that is already spoken for on this host as taken in whichever allocator's range
    /// covers it — which is not always the allocator that handed it out. An admin who moves the SSH
    /// range over a port an existing VM still holds leaves that port sitting in the APP range: the rule
    /// is grandfathered and left alone (see <see cref="ReconcileAsync"/>), so unless the app allocator
    /// is told about it, the next host forward is handed the very port that live rule occupies and the
    /// second netsh rule silently replaces the first.
    ///
    /// The two ranges are disjoint (the constructor refuses an overlap), so at most one of these takes.
    /// </summary>
    private void Reserve(int port)
    {
        _sshPorts.TryReserve(port);
        _appPorts.TryReserve(port);
    }

    /// <summary>The mirror of <see cref="Reserve"/>: whichever allocator holds the port gives it back.</summary>
    private void Release(int port)
    {
        _sshPorts.Release(port);
        _appPorts.Release(port);
    }

    /// <summary>
    /// Is this a port the service manages at all? The union, and only for the sweep that deletes rules
    /// nothing accounts for — deciding to <em>create</em> a rule is per allocator, because that is the
    /// allocator that has to have the port reserved.
    /// </summary>
    private bool IsOurs(int port) => IsSshPort(port) || IsAppPort(port);

    private SemaphoreSlim GateFor(string vmName) =>
        _vmGates.GetOrAdd(vmName, _ => new SemaphoreSlim(1, 1));

    /// <summary>
    /// Builds the exception and logs the failure. <paramref name="reason"/> is composed here and never
    /// from netsh's output: this service does not repeat dependency text anywhere, the log included
    /// (see <see cref="SafeError"/>), and netsh echoes the arguments it was given.
    /// </summary>
    private PortForwardException Fail(string vmName, int publicPort, string reason)
    {
        _logger.LogError(
            "Port forward for {Vm} on {Address}:{Port} failed: {Reason}",
            vmName.Length == 0 ? "-" : vmName,
            _listenAddress,
            publicPort,
            reason);

        return new PortForwardException(vmName, publicPort, reason);
    }

    /// <summary>How a netsh call failed, in our words: what was run and what came back, never its text.</summary>
    private static string Describe(string what, ProcessResult result) =>
        result.TimedOut
            ? $"{what} timed out after {NetshTimeout.TotalSeconds:0} seconds"
            : $"{what} exited with {result.ExitCode}";
}
