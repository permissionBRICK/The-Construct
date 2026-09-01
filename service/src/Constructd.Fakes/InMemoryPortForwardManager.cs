using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Fakes;

/// <summary>
/// Port-forward manager without netsh: it allocates out of the same two ranges the real one uses,
/// keeps its state in the injected <see cref="IForwardStore"/> (so it survives a restart exactly like
/// the real one) and records which host forwards it "materialized", standing in for the portproxy
/// rules. The Windows implementation (B7/B8) replaces <see cref="Materialize"/> with netsh rules and
/// <see cref="CountActiveConnectionsAsync"/> with a host TCP-table lookup.
/// </summary>
public sealed class InMemoryPortForwardManager : IPortForwardManager
{
    private readonly IClock _clock;
    private readonly IVmRepository _vms;
    private readonly IForwardStore _store;
    private readonly PortAllocator _sshPorts;
    private readonly PortAllocator _appPorts;
    private readonly ConcurrentDictionary<string, int> _sshForwards = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _connections = new(StringComparer.OrdinalIgnoreCase);

    // One gate per VM: allocation, adding a forward and tearing all of them down are state transitions
    // of the same VM and must not interleave.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _vmGates = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryPortForwardManager(
        IClock clock,
        IVmRepository vms,
        IForwardStore store,
        PortRangeOptions sshRange,
        PortRangeOptions appRange)
    {
        ArgumentNullException.ThrowIfNull(sshRange);
        ArgumentNullException.ThrowIfNull(appRange);

        _clock = clock;
        _vms = vms;
        _store = store;
        _sshPorts = new PortAllocator(sshRange.Start, sshRange.End);
        _appPorts = new PortAllocator(appRange.Start, appRange.End);
    }

    /// <summary>Ids of the forwards that are materialized on the host (i.e. the netsh rules).</summary>
    public ConcurrentDictionary<string, string> Materialized { get; } = new(StringComparer.Ordinal);

    /// <summary>Set to make the mutating operations fail, for rollback tests.</summary>
    public Exception? Failure { get; set; }

    /// <summary>
    /// When set, <see cref="RemoveAllForwardsAsync"/> waits for <see cref="RemoveAllGate"/> while
    /// holding the VM's gate — so a test can race another request against a teardown in progress.
    /// </summary>
    public bool HoldRemoveAll { get; set; }

    public TaskCompletionSource RemoveAllStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource RemoveAllGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void ReleaseRemoveAll() => RemoveAllGate.TrySetResult();

    /// <summary>Test hook for the "no client connections" idle signal.</summary>
    public void SetActiveConnections(string vmName, int count) => _connections[vmName] = count;

    /// <remarks>
    /// The port is written onto the VM record — its canonical, durable home — <em>before</em> the host
    /// rule is created. A crash between the two therefore leaves a stored allocation with no rule,
    /// which startup reconciliation repairs; the reverse (a live rule nobody knows about, which a later
    /// VM could be handed as well) cannot happen.
    /// </remarks>
    public async Task<int> AllocateSshForwardAsync(string vmName, CancellationToken cancellationToken)
    {
        ThrowIfFailing();

        var gate = GateFor(vmName);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_sshForwards.TryGetValue(vmName, out var known))
            {
                return known;
            }

            var vm = await _vms.GetAsync(vmName, cancellationToken).ConfigureAwait(false)
                     ?? throw new InvalidOperationException($"Unknown VM '{vmName}'.");

            // Already allocated durably (e.g. by a previous attempt): adopt it.
            if (vm.SshForwardPort is int stored)
            {
                _sshPorts.TryReserve(stored);
                _sshForwards[vmName] = stored;
                Materialized[SshKey(vmName)] = Rule(stored, vmName, 22);
                return stored;
            }

            var port = _sshPorts.Allocate();
            try
            {
                await _vms.UpdateAsync(vm with { SshForwardPort = port }, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                _sshPorts.Release(port);
                throw;
            }

            _sshForwards[vmName] = port;
            Materialized[SshKey(vmName)] = Rule(port, vmName, 22);
            return port;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<bool> ReleaseSshForwardAsync(string vmName, CancellationToken cancellationToken)
    {
        ThrowIfFailing();

        var gate = GateFor(vmName);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vm = await _vms.GetAsync(vmName, cancellationToken).ConfigureAwait(false);
            var storedPort = vm?.SshForwardPort;

            var released = _sshForwards.TryRemove(vmName, out var port);
            if (released)
            {
                _sshPorts.Release(port);
            }

            if (storedPort is int durable)
            {
                _sshPorts.Release(durable);
                await _vms.UpdateAsync(vm! with { SshForwardPort = null }, cancellationToken)
                    .ConfigureAwait(false);
                released = true;
            }

            Materialized.TryRemove(SshKey(vmName), out _);
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
        ThrowIfFailing();

        // Counting, checking that the VM is still there, allocating and storing behind the VM's gate,
        // so concurrent requests cannot exceed the cap, share a public port, or slip a forward past a
        // teardown that is already running.
        var gate = GateFor(vmName);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var vm = await _vms.GetAsync(vmName, cancellationToken).ConfigureAwait(false);
            if (vm is null || vm.Deleting)
            {
                return AddForwardResult.VmUnavailable;
            }

            var existing = await _store.CountByVmAsync(vmName, cancellationToken).ConfigureAwait(false);
            if (existing >= maxForwards)
            {
                return AddForwardResult.LimitReached;
            }

            int? publicPort = target == ForwardTarget.Host ? _appPorts.Allocate() : null;
            var forward = new PortForward(
                Id: Guid.NewGuid().ToString("n"),
                VmName: vmName,
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
                Materialize(forward, port);
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
        ThrowIfFailing();

        var forward = await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (forward is null || !string.Equals(forward.VmName, vmName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!await _store.RemoveAsync(id, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        ReleasePublicPort(forward);
        return true;
    }

    public async Task<int> RemoveAllForwardsAsync(string vmName, CancellationToken cancellationToken)
    {
        ThrowIfFailing();

        // Under the VM's gate, so an add cannot land between the enumeration and the removals.
        var gate = GateFor(vmName);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (HoldRemoveAll)
            {
                RemoveAllStarted.TrySetResult();
                await RemoveAllGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            var removed = 0;
            foreach (var forward in await _store.ListAsync(vmName, cancellationToken).ConfigureAwait(false))
            {
                if (await _store.RemoveAsync(forward.Id, cancellationToken).ConfigureAwait(false))
                {
                    ReleasePublicPort(forward);
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
    /// Rebuilds this process's view from the durable state: reserves every allocated port in its
    /// range (an SSH port from the VM registry, an app port from the forward store) and re-materializes
    /// the host rules. Called at startup, which is what makes forwards survive a service restart.
    /// </summary>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var repaired = 0;

        foreach (var vm in await _vms.ListAsync(owner: null, cancellationToken).ConfigureAwait(false))
        {
            if (vm.SshForwardPort is not int port)
            {
                continue;
            }

            _sshForwards[vm.Name] = port;
            _sshPorts.TryReserve(port);

            if (Materialized.TryAdd(SshKey(vm.Name), Rule(port, vm.Name, 22)))
            {
                repaired++;
            }
        }

        foreach (var forward in await _store.ListAsync(vmName: null, cancellationToken).ConfigureAwait(false))
        {
            if (forward.PublicPort is not int port)
            {
                continue;
            }

            _appPorts.TryReserve(port);

            if (!Materialized.ContainsKey(forward.Id))
            {
                Materialize(forward, port);
                repaired++;
            }
        }

        return repaired;
    }

    public Task<int> CountActiveConnectionsAsync(string vmName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_connections.TryGetValue(vmName, out var count) ? count : 0);
    }

    private static string SshKey(string vmName) => $"ssh:{vmName}";

    private static string Rule(int publicPort, string vmName, int vmPort) =>
        $"host:{publicPort} -> {vmName}:{vmPort}";

    private SemaphoreSlim GateFor(string vmName) =>
        _vmGates.GetOrAdd(vmName, _ => new SemaphoreSlim(1, 1));

    private void ThrowIfFailing()
    {
        if (Failure is not null)
        {
            throw Failure;
        }
    }

    private void Materialize(PortForward forward, int publicPort) =>
        Materialized[forward.Id] = Rule(publicPort, forward.VmName, forward.VmPort);

    private void ReleasePublicPort(PortForward forward)
    {
        if (forward.PublicPort is int port)
        {
            _appPorts.Release(port);
            Materialized.TryRemove(forward.Id, out _);
        }
    }
}
