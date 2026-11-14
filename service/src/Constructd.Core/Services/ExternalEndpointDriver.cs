using System.Collections.Concurrent;
using System.Net.Sockets;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Core.Services;

/// <summary>Why an external VM cannot do what was asked. The message is safe to show and to audit.</summary>
public sealed class ExternalEndpointException(string message) : Exception(message), IConstructdError;

/// <summary>
/// The external-endpoint driver (<c>Constructd:Backend = external</c>, design 7.2): a VM that already
/// exists somewhere and is reachable over SSH at <c>{sshHost, sshPort, user}</c> from
/// <see cref="ConstructdOptions.External"/>. Creating it only registers a listed name, deleting it only
/// unregisters it, and the service never powers it: <see cref="DriverCapabilities.Power"/> is false and
/// the power calls refuse with <c>unsupported-on-host</c>. A listed VM is <see cref="VmState.Absent"/>
/// until the engine has registered it; then it is <see cref="VmState.Running"/> while SSH accepts
/// connections and <see cref="VmState.Unknown"/> otherwise.
/// </summary>
public sealed class ExternalEndpointDriver(ConstructdOptions options, IVmRepository vms) : IHypervisorDriver, IVmNestedDriver, IVmCpuDriver, IVmMemoryDriver
{
    // Unregistered while the delete job still holds the VM record.
    private readonly ConcurrentDictionary<string, bool> _removed = new(StringComparer.OrdinalIgnoreCase);

    public const string Unsupported = "unsupported-on-host";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public DriverCapabilities Capabilities { get; } = new(Checkpoints: false, Suspend: false, DriverConsole.None, Power: false);

    public bool NestedAvailable => false;

    /// <summary>The configured endpoint for <paramref name="name"/>, or null when it is not listed.</summary>
    public ExternalVmOptions? Find(string name) =>
        options.External.Vms.FirstOrDefault(vm => string.Equals(vm.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    /// <summary>A configuration problem that should stop the service from starting, or null.</summary>
    public static string? Validate(ExternalOptions external)
    {
        foreach (var (name, vm) in external.Vms)
        {
            if (!VmNameValidator.IsValid(name)) return $"Constructd:External:Vms:{name} is not a valid VM name: {VmNameValidator.Rule}";
            if (string.IsNullOrWhiteSpace(vm.SshHost)) return $"Constructd:External:Vms:{name}:SshHost is not set.";
            if (vm.SshPort is < 1 or > 65535) return $"Constructd:External:Vms:{name}:SshPort must be between 1 and 65535.";
            if (string.IsNullOrWhiteSpace(vm.User)) return $"Constructd:External:Vms:{name}:User is not set.";
        }

        return null;
    }

    public Task CreateVmAsync(VmDescriptor descriptor, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var vm = Find(descriptor.Name) ?? throw new ExternalEndpointException(
            $"external-vm-not-configured: '{descriptor.Name}' is not listed under Constructd:External:Vms.");
        _removed.TryRemove(descriptor.Name, out _);
        progress?.Report($"registered external VM {descriptor.Name} at {vm.User}@{vm.SshHost}:{vm.SshPort}");
        return Task.CompletedTask;
    }

    public Task RemoveVmAsync(string name, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        _removed[name] = true;
        progress?.Report($"unregistered external VM {name}; the machine itself is left as it is");
        return Task.CompletedTask;
    }

    public Task StartAsync(string name, CancellationToken cancellationToken) => throw new ExternalEndpointException(Unsupported);

    public Task StopAsync(string name, CancellationToken cancellationToken) => throw new ExternalEndpointException(Unsupported);

    public Task SaveAsync(string name, CancellationToken cancellationToken) => throw new ExternalEndpointException(Unsupported);

    public async Task<VmState> GetStateAsync(string name, CancellationToken cancellationToken)
    {
        if (Find(name) is not { } vm || _removed.ContainsKey(name) ||
            await vms.GetAsync(name, cancellationToken).ConfigureAwait(false) is null)
        {
            return VmState.Absent;
        }

        return await ReachableAsync(vm, cancellationToken).ConfigureAwait(false) ? VmState.Running : VmState.Unknown;
    }

    public Task<Endpoint?> GetEndpointAsync(string name, CancellationToken cancellationToken) =>
        Task.FromResult(Find(name) is { } vm ? new Endpoint(vm.SshHost, vm.SshPort, vm.SshHost) : null);

    public async Task<bool> WaitReachableAsync(string name, TimeSpan timeout, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (Find(name) is not { } vm) return false;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            if (await ReachableAsync(vm, cancellationToken).ConfigureAwait(false)) return true;
            if (DateTimeOffset.UtcNow >= deadline) return false;
            progress?.Report($"waiting for SSH on {vm.SshHost}:{vm.SshPort}");
            await Task.Delay(PollInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task DetachInstallMediaAsync(string name, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<bool> GetNestedAsync(string name, CancellationToken ct) => Task.FromResult(false);

    public Task SetNestedAsync(string name, bool enabled, CancellationToken ct) => throw new ExternalEndpointException(Unsupported);

    public Task SetCpuCountAsync(string name, int cpus, CancellationToken ct) => throw new ExternalEndpointException(Unsupported);

    public Task SetMemoryAsync(string name, int ramGb, CancellationToken ct) => throw new ExternalEndpointException(Unsupported);

    private static async Task<bool> ReachableAsync(ExternalVmOptions vm, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(vm.SshHost, vm.SshPort, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}

/// <summary>No install media: an external VM is installed already.</summary>
public sealed class NoInstallMedia : IIsoBuilder
{
    public Task<string> BuildAsync(string vmName, string seedUser, string seedPassword, string bootstrapPubKeyPath,
        IProgress<string>? progress, CancellationToken cancellationToken, bool redownload = false) => Task.FromResult(string.Empty);
}
