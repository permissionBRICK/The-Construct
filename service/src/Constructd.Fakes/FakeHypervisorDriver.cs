using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Fakes;

/// <summary>
/// In-memory stand-in for the Hyper-V driver: keeps a state machine per VM and records every call,
/// so the API, the creation job and the idle engine can be tested on Linux. The real driver (B7)
/// implements the same interface by invoking PowerShell.
/// </summary>
public sealed class FakeHypervisorDriver : IHypervisorDriver, IVmCpuDriver, IVmMemoryDriver, IGuestNetworkConfigurator, IVmNestedDriver, IVmMemoryFloorDriver, IVmFactsDriver, IVmHostStartDriver, IVmStorageDriver, IStateReadSettling
{
    /// <summary>Current VHDX path per VM; populated on create or move.</summary>
    public ConcurrentDictionary<string, string> DiskPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Set to make <see cref="MoveStorageAsync"/> throw.</summary>
    public Exception? MoveStorageFailure { get; set; }

    public Task<VmStorageMoveResult> MoveStorageAsync(string name, string destinationDirectory, IProgress<string>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Enqueue($"move-storage:{name}:{destinationDirectory}");
        if (MoveStorageFailure is not null) return Task.FromException<VmStorageMoveResult>(MoveStorageFailure);
        if (StateOf(name) == VmState.Saved) throw new InvalidOperationException("Turn off or start the VM before moving its disk.");
        var dest = Constructd.Core.Logic.StoragePathRules.NormalizeDirectory(destinationDirectory);
        var current = DiskPaths.GetValueOrDefault(name) ?? $@"{Constructd.Core.Logic.StoragePathRules.DefaultPrimaryRoot}\{name}.vhdx";
        var currentDir = Constructd.Core.Logic.StoragePathRules.DirectoryOf(current);
        var fileName = Path.GetFileName(current.Replace('\\', '/'));
        var next = dest.EndsWith('\\') ? dest + fileName : dest + "\\" + fileName;
        var volume = Constructd.Core.Logic.StoragePathRules.VolumeRoot(dest);
        if (Constructd.Core.Logic.StoragePathRules.SameDirectory(currentDir, dest))
        {
            progress?.Report($"Disk for {name} is already in {dest}");
            return Task.FromResult(new VmStorageMoveResult(current, current, volume, false));
        }
        progress?.Report($"Moving virtual disk for {name} to {dest}");
        DiskPaths[name] = next;
        progress?.Report($"Moved virtual disk to {next}");
        return Task.FromResult(new VmStorageMoveResult(current, next, volume, true));
    }
    /// <summary>The fake answers at once: a read that stays Unknown is given up on soon, a few Unknown answers are read past.</summary>
    public TimeSpan SettleWindow { get; set; } = TimeSpan.FromMilliseconds(500);

    public TimeSpan RereadInterval { get; set; } = TimeSpan.FromMilliseconds(10);

    /// <summary>What <see cref="GetFactsAsync"/> reports for a VM the fake did not create (an existing VM to adopt).</summary>
    public ConcurrentDictionary<string, HypervisorVmFacts> Facts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Task<HypervisorVmFacts?> GetFactsAsync(string name, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Enqueue($"facts:{name}");
        return Task.FromResult(Facts.TryGetValue(name, out var facts) ? facts with { State = StateOf(name) } : null);
    }

    public Exception? NetworkFailure { get; set; }
    public Task ConfigureNetworkAsync(string name, string? address, string? gateway, IReadOnlyList<string>? dns, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (StateOf(name) != VmState.Off) throw new InvalidOperationException("VM must be off.");
        if (NetworkFailure is not null) throw NetworkFailure;
        Calls.Enqueue($"network:{name}:{address ?? "dhcp"}:{gateway}:{string.Join(" ", dns ?? [])}");
        return Task.CompletedTask;
    }
    public bool NestedAvailable { get; set; } = true;
    public ConcurrentDictionary<string, bool> NestedValues { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Task<bool> GetNestedAsync(string name, CancellationToken ct) => Task.FromResult(NestedValues.GetValueOrDefault(name));
    public Task SetNestedAsync(string name, bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (StateOf(name) != VmState.Off) throw new InvalidOperationException("VM must be off.");
        if (PowerFailure is not null) throw PowerFailure;
        Calls.Enqueue($"nested:{name}:{enabled}");
        NestedValues[name] = enabled;
        return Task.CompletedTask;
    }
    /// <summary>Whether a host boot brings each VM back: from its creation, then from the last <see cref="SetStartWithHostAsync"/>.</summary>
    public ConcurrentDictionary<string, bool> StartWithHost { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>VMs whose start-with-host setting cannot be written, standing in for a hypervisor that refuses it.</summary>
    public ConcurrentDictionary<string, Exception> StartWithHostFailures { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Task SetStartWithHostAsync(string name, bool enabled, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Calls.Enqueue($"start-with-host:{name}:{enabled}");
        if (StartWithHostFailures.TryGetValue(name, out var failure)) return Task.FromException(failure);
        StartWithHost[name] = enabled;
        return Task.CompletedTask;
    }
    public ConcurrentDictionary<string, VmDescriptor> Descriptors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public ConcurrentDictionary<string, int> MemorySizes { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? MemoryFailure { get; set; }
    public Task SetMemoryAsync(string name, int ramGb, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (StateOf(name) != VmState.Off) throw new InvalidOperationException("VM must be off.");
        if ((MemoryFailure ?? PowerFailure) is { } failure) throw failure;
        Calls.Enqueue($"memory:{name}:{ramGb}");
        MemorySizes[name] = ramGb;
        return Task.CompletedTask;
    }
    /// <summary>Applied balloon minimums in MiB; null = removed. A VM never configured has no entry.</summary>
    public ConcurrentDictionary<string, int?> MemoryFloors { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Exception? MemoryFloorFailure { get; set; }
    public Task SetMemoryFloorAsync(string name, int? minimumMb, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (MemoryFloorFailure is { } failure) throw failure;
        var ramMb = (MemorySizes.TryGetValue(name, out var gb) ? gb : Descriptors.TryGetValue(name, out var d) ? d.RamGb : 0) * 1024;
        int? applied = minimumMb is int floor && floor < ramMb ? floor : null;
        Calls.Enqueue($"memory-floor:{name}:{applied?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}");
        MemoryFloors[name] = applied;
        return Task.CompletedTask;
    }
    public ConcurrentDictionary<string, int> CpuCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Task SetCpuCountAsync(string name, int cpus, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (StateOf(name) != VmState.Off) throw new InvalidOperationException("VM must be off.");
        if (PowerFailure is not null) throw PowerFailure;
        Calls.Enqueue($"cpu:{name}:{cpus}");
        CpuCounts[name] = cpus;
        return Task.CompletedTask;
    }
    private readonly ConcurrentDictionary<string, VmState> _states = new(StringComparer.OrdinalIgnoreCase);

    public DriverCapabilities Capabilities { get; set; } =
        new(Checkpoints: true, Suspend: true, Console: DriverConsole.None);

    /// <summary>Every driver call, as <c>op:name</c>, in order.</summary>
    public ConcurrentQueue<string> Calls { get; } = new();

    /// <summary>Set to make <see cref="CreateVmAsync"/> throw.</summary>
    public Exception? CreateFailure { get; set; }

    /// <summary>Result <see cref="WaitReachableAsync"/> returns.</summary>
    public bool Reachable { get; set; } = true;

    /// <summary>Set to make the power operations throw, standing in for a failing hypervisor.</summary>
    public Exception? PowerFailure { get; set; }
    public VmState StateAfterStart { get; set; } = VmState.Running;

    /// <summary>Host name pattern the fake endpoint uses.</summary>
    public string EndpointHostSuffix { get; set; } = ".fake.local";
    public bool ReportEndpoint { get; set; } = true;

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

    private readonly Dictionary<string, int> _unknownReads = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The next <paramref name="reads"/> state reads of <paramref name="name"/> answer <see cref="VmState.Unknown"/>,
    /// as a Proxmox read that fails for a moment right after a create does. Zero clears it.
    /// </summary>
    public void AnswerUnknown(string name, int reads)
    {
        lock (_unknownReads) _unknownReads[name] = reads;
    }

    public VmState StateOf(string name) => _states.TryGetValue(name, out var state) ? state : VmState.Absent;

    public async Task CreateVmAsync(VmDescriptor descriptor, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue($"create:{descriptor.Name}");
        Descriptors[descriptor.Name] = descriptor;
        StartWithHost[descriptor.Name] = descriptor.StartWithHost;
        NestedValues[descriptor.Name] = descriptor.Nested;

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

    /// <summary>Awaited right after a start took effect: a test holds a start there.</summary>
    public Func<string, Task>? AfterStart { get; set; }

    public async Task StartAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Enqueue($"start:{name}");

        if (PowerFailure is not null)
        {
            throw PowerFailure;
        }
        _states[name] = StateAfterStart;
        if (AfterStart is { } hold) await hold(name);
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
        lock (_unknownReads)
        {
            if (_unknownReads.TryGetValue(name, out var left) && left > 0) { _unknownReads[name] = left - 1; return Task.FromResult(VmState.Unknown); }
        }
        return Task.FromResult(StateOf(name));
    }

    public Task<Endpoint?> GetEndpointAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Endpoint? endpoint = ReportEndpoint && _states.ContainsKey(name) ? new Endpoint($"{name}{EndpointHostSuffix}", 22) : null;
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
