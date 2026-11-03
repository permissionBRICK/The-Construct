using Constructd.Api.Endpoints;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;

namespace Constructd.Api.Jobs;

/// <summary>
/// The RAM a running primary VM may be shrunk to under host memory pressure (Proxmox auto-ballooning).
/// The host default is <see cref="VirtualizationConfig.MinimumRamBytes"/>; an admin can override it per VM
/// (0 = never shrink this VM). Applying it never shrinks a running VM by itself, so it is applied right
/// away: on a change, after every start and creation, and once when the service starts. A cold start
/// would begin at the floor, so Construct clears it just before one and sets it again once the VM runs.
/// </summary>
public sealed class PrimaryMemoryFloor(IHostConfigStore config, IVmRepository vms, IHypervisorDriver hypervisor,
    IVmOperationGate gates, IHostApplicationLifetime lifetime, ConstructdOptions options, ILogger<PrimaryMemoryFloor> logger,
    IVmMemoryFloorDriver? driver = null)
{
    private readonly object sync = new();
    /// <summary><c>MinimumRamGb</c>: null follows the host default, 0 never shrinks the VM.</summary>
    public sealed record Setting(DateTimeOffset Created, int? MinimumRamGb);
    /// <summary><c>EffectiveMinimumRamMb</c>: what the VM gets; null = never shrunk (off, or not below its RAM).</summary>
    public sealed record View(int? MinimumRamGb, long? HostMinimumRamBytes, int? EffectiveMinimumRamMb, int RamGb,
        string Source, bool MayEdit);

    private const long MiB = 1L << 20;
    public bool Supported => options.IsProxmox && driver is not null;
    private static string Section(Vm vm) => "memory-floor:" + vm.Name.ToLowerInvariant();

    public async Task<Setting?> GetAsync(Vm vm, CancellationToken ct)
    {
        var setting = await config.GetAsync<Setting>(Section(vm), ct);
        return setting?.Created == vm.Created ? setting : null;
    }

    public Task SaveAsync(Vm vm, int? minimumRamGb, string actor, CancellationToken ct) =>
        config.SetAsync(Section(vm), new Setting(vm.Created, minimumRamGb), actor, ct);

    public async Task<long?> HostMinimumAsync(CancellationToken ct) =>
        (await config.GetAsync<VirtualizationConfig>("virtualization", ct) ?? HostAdminDefaults.Virtualization).MinimumRamBytes;

    /// <summary>The configured floor in MiB before comparing it with the VM's RAM; null = off.</summary>
    private async Task<int?> FloorMbAsync(Vm vm, CancellationToken ct)
    {
        var bytes = (await GetAsync(vm, ct))?.MinimumRamGb is int gb ? gb == 0 ? (long?)null : gb * 1024 * MiB : await HostMinimumAsync(ct);
        return bytes is long b ? (int)Math.Clamp(b / MiB, 1, int.MaxValue) : null;
    }

    public async Task<View> ProjectAsync(Vm vm, bool admin, CancellationToken ct)
    {
        var setting = await GetAsync(vm, ct);
        var floor = await FloorMbAsync(vm, ct);
        return new(setting?.MinimumRamGb, await HostMinimumAsync(ct), floor < vm.RamBytes / MiB ? floor : null, vm.RamGb,
            setting?.MinimumRamGb is null ? "host" : "vm", admin);
    }

    /// <summary>Applies the floor to a running, paused or stopped primary. A saved VM is locked on the node;
    /// it gets the floor after its next start. Returns whether it was applied.</summary>
    public async Task<bool> ApplyAsync(Vm vm, CancellationToken ct)
    {
        if (!Supported || vm.Kind != VmKind.Primary || vm.Deleting) return false;
        if (await hypervisor.GetStateAsync(vm.Name, ct) is not (VmState.Running or VmState.Paused or VmState.Off)) return false;
        await driver!.SetMemoryFloorAsync(vm.Name, await FloorMbAsync(vm, ct), ct);
        return true;
    }

    /// <summary>Best effort: a failure is logged and the next start or service start retries.</summary>
    public async Task TryApplyAsync(Vm vm, CancellationToken ct)
    {
        try { await ApplyAsync(vm, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Could not apply the minimum RAM to {VmName}", vm.Name); }
    }

    /// <summary>Removes the floor from a stopped primary right before a cold start, which would otherwise
    /// begin at it and take minutes to grow back. The post-start apply sets it again without shrinking.</summary>
    public async Task ClearForColdStartAsync(Vm vm, CancellationToken ct)
    {
        if (!Supported || vm.Kind != VmKind.Primary) return;
        try { await driver!.SetMemoryFloorAsync(vm.Name, null, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Could not clear the minimum RAM of {VmName} before its start", vm.Name); }
    }

    /// <summary>Every primary, each under its VM gate. A VM busy with an operation is skipped: its start, if
    /// that is what runs, applies the floor itself.</summary>
    public async Task ApplyAllAsync(CancellationToken ct)
    {
        if (!Supported) return;
        foreach (var listed in await vms.ListAsync(null, ct))
        {
            if (listed.Kind != VmKind.Primary) continue;
            await using var gate = await PrimaryOperationGate.AcquireAsync(gates, listed.Name, "memory-floor", ct);
            if (gate is null || await vms.GetAsync(listed.Name, ct) is not { } vm) continue;
            await TryApplyAsync(vm, ct);
        }
    }

    /// <summary>The pass after a host-default change, off the request and one at a time.</summary>
    public Task Background { get; private set; } = Task.CompletedTask;
    public Task ScheduleApplyAll()
    {
        lock (sync)
            return Background = Background.ContinueWith(async _ =>
            {
                try { await ApplyAllAsync(lifetime.ApplicationStopping); }
                catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
                catch (Exception ex) { logger.LogWarning(ex, "Applying the host's minimum RAM to primary VMs failed"); }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
    }
}
