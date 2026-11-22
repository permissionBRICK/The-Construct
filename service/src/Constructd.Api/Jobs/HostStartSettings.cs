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
/// <param name="lifetime">Absent in a command-line run (an adoption), which applies the setting directly.</param>
public sealed class HostStartSettings(IVmRepository vms, IHypervisorDriver hypervisor, IVmOperationGate gates,
    ConstructdOptions options, ILogger<HostStartSettings> logger, IHostApplicationLifetime? lifetime = null)
{
    /// <summary>The VM gate's owner while a setting is applied: a background pass, which a user's operation waits for
    /// and which leaves a running repair alone.</summary>
    public const string GateOwner = "host-start";

    private IVmHostStartDriver? Driver => hypervisor as IVmHostStartDriver;

    /// <summary>
    /// Applies the rule to <paramref name="name"/> under its gate, from its record as it is then. A create, reinstall,
    /// start or save holding the gate sets the hypervisor's setting from its own, older reading; this one runs after
    /// it, waiting in the background when the gate is held, so the latest idle policy wins.
    /// </summary>
    public async Task RefreshAsync(string name, CancellationToken ct)
    {
        if (Driver is null) return;
        var held = await gates.TryAcquireAsync(name, GateOwner, ct);
        if (held is null)
        {
            _ = Task.Run(() => WaitAndApplyAsync(name, lifetime?.ApplicationStopping ?? CancellationToken.None), CancellationToken.None);
            return;
        }

        await using (held) await ApplyCurrentAsync(name, ct);
    }

    private async Task WaitAndApplyAsync(string name, CancellationToken stopping)
    {
        try
        {
            await using var held = await gates.AcquireAsync(name, GateOwner, stopping);
            await ApplyCurrentAsync(name, stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception ex) { logger.LogWarning(ex, "Could not set whether {VmName} starts with the host", name); }
    }

    private async Task ApplyCurrentAsync(string name, CancellationToken ct)
    {
        if (await vms.GetAsync(name, ct) is { } vm) await TryApplyAsync(vm, ct);
    }

    /// <summary>Best effort, for a caller that holds the VM's gate: a failure is logged, and the next engine start tries again.</summary>
    public async Task TryApplyAsync(Vm vm, CancellationToken ct)
    {
        if (Driver is not { } driver || vm.Deleting) return;
        try { await driver.SetStartWithHostAsync(vm.Name, HostStartRules.StartsWithHost(vm, options.Idle), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { logger.LogWarning(ex, "Could not set whether {VmName} starts with the host", vm.Name); }
    }

    /// <summary>Every VM the engine manages, children included, one at a time, each under its gate. A VM that cannot be
    /// set does not hold up the others; one an operation holds is set once that operation ends.</summary>
    public async Task ApplyAllAsync(CancellationToken ct)
    {
        if (Driver is null) return;
        foreach (var listed in await vms.ListAsync(null, ct)) await RefreshAsync(listed.Name, ct);
    }
}
