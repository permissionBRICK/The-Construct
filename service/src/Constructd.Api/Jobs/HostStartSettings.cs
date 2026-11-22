using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Jobs;

/// <summary>
/// Keeps each VM's start-with-host setting on the hypervisor in line with <see cref="HostStartRules"/>.
/// A VM gets it when it is created (<see cref="VmDescriptor.StartWithHost"/>), and a Proxmox start or save
/// applies it again. This applies it when a primary's idle policy changes, after an adoption, and once at
/// every engine start, which covers the host's idle settings (they take effect only then) and the VMs made
/// before the rule. A backend without the setting (external machines) has nothing to apply.
/// </summary>
public sealed class HostStartSettings(IVmRepository vms, IHypervisorDriver hypervisor, ConstructdOptions options,
    ILogger<HostStartSettings> logger)
{
    private IVmHostStartDriver? Driver => hypervisor as IVmHostStartDriver;

    /// <summary>Best effort: a failure is logged, and the next engine start tries again.</summary>
    public async Task TryApplyAsync(Vm vm, CancellationToken ct)
    {
        if (Driver is not { } driver || vm.Deleting) return;
        try { await driver.SetStartWithHostAsync(vm.Name, HostStartRules.StartsWithHost(vm, options.Idle), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Could not set whether {VmName} starts with the host", vm.Name); }
    }

    /// <summary>Every VM the engine manages, children included, one at a time. A VM that cannot be set does
    /// not hold up the others.</summary>
    public async Task ApplyAllAsync(CancellationToken ct)
    {
        if (Driver is null) return;
        foreach (var listed in await vms.ListAsync(null, ct))
        {
            if (await vms.GetAsync(listed.Name, ct) is { } vm) await TryApplyAsync(vm, ct);
        }
    }
}
