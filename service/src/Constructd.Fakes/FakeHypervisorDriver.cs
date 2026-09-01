using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Fakes;

/// <summary>
/// In-memory stand-in for the Hyper-V driver: keeps a state machine per VM and records every call,
/// so the API, the creation job and the idle engine can be tested on Linux. The real driver (B7)
/// implements the same interface by invoking PowerShell.
/// </summary>
public sealed class FakeHypervisorDriver : IHypervisorDriver
{
    private readonly ConcurrentDictionary<string, VmState> _states = new(StringComparer.OrdinalIgnoreCase);

    public DriverCapabilities Capabilities { get; set; } = new(Checkpoints: true, Suspend: true, Console: false);

    /// <summary>Every driver call, as <c>op:name</c>, in order.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Set to make <see cref="CreateVmAsync"/> throw.</summary>
    public Exception? CreateFailure { get; set; }

    /// <summary>Result <see cref="WaitReachableAsync"/> returns.</summary>
    public bool Reachable { get; set; } = true;

    /// <summary>Set to make the power operations throw, standing in for a failing hypervisor.</summary>
    public Exception? PowerFailure { get; set; }

    /// <summary>Host name pattern the fake endpoint uses.</summary>
    public string EndpointHostSuffix { get; set; } = ".fake.local";

    /// <summary>
    /// Optional gate that <see cref="CreateVmAsync"/> waits on, so a test can observe the service
    /// while a creation job is still in flight.
    /// </summary>
    public TaskCompletionSource CreateGate { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether <see cref="CreateVmAsync"/> waits for <see cref="CreateGate"/>.</summary>
    public bool HoldCreate { get; set; }

    /// <summary>Lets a held creation continue.</summary>
    public void ReleaseCreate() => CreateGate.TrySetResult();

    public void SetState(string name, VmState state) => _states[name] = state;

    public VmState StateOf(string name) => _states.TryGetValue(name, out var state) ? state : VmState.Absent;

    public async Task CreateVmAsync(VmDescriptor descriptor, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue($"create:{descriptor.Name}");

        if (HoldCreate)
        {
            await CreateGate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (CreateFailure is not null)
        {
            throw CreateFailure;
        }

        progress?.Report(
            $"creating vm {descriptor.Name} ({descriptor.Cpu} cpu, {descriptor.RamGb} GB RAM, {descriptor.DiskGb} GB disk)");
        progress?.Report($"attaching install media {descriptor.IsoPath}");
        _states[descriptor.Name] = VmState.Running;
        progress?.Report("unattended install started");
    }

    public Task RemoveVmAsync(string name, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue($"remove:{name}");
        progress?.Report($"removing vm {name} and its disk chain");
        _states.TryRemove(name, out _);
        return Task.CompletedTask;
    }

    public Task StartAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue($"start:{name}");

        if (PowerFailure is not null)
        {
            return Task.FromException(PowerFailure);
        }
        _states[name] = VmState.Running;
        return Task.CompletedTask;
    }

    public Task StopAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue($"stop:{name}");

        if (PowerFailure is not null)
        {
            return Task.FromException(PowerFailure);
        }
        _states[name] = VmState.Off;
        return Task.CompletedTask;
    }

    public Task SaveAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue($"save:{name}");

        if (PowerFailure is not null)
        {
            return Task.FromException(PowerFailure);
        }
        _states[name] = VmState.Saved;
        return Task.CompletedTask;
    }

    public Task<VmState> GetStateAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(StateOf(name));
    }

    public Task<Endpoint?> GetEndpointAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Endpoint? endpoint = _states.ContainsKey(name) ? new Endpoint($"{name}{EndpointHostSuffix}", 22) : null;
        return Task.FromResult(endpoint);
    }

    public Task<bool> WaitReachableAsync(
        string name,
        TimeSpan timeout,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue($"wait:{name}");
        progress?.Report($"waiting for ssh on {name} (timeout {timeout.TotalMinutes:0}m)");

        if (!Reachable)
        {
            progress?.Report($"{name} did not answer in time");
            return Task.FromResult(false);
        }

        progress?.Report($"{name} answered ssh");
        return Task.FromResult(true);
    }

    public Task DetachInstallMediaAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue($"detach:{name}");
        return Task.CompletedTask;
    }
}
