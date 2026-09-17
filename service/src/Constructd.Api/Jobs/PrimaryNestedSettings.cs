using Constructd.Core.Abstractions;
using Constructd.Core.Domain;

namespace Constructd.Api.Jobs;

/// <summary>Desired nested setting belongs to this VM incarnation. Callers hold the VM gate.</summary>
public sealed class PrimaryNestedSettings(IHostConfigStore config, IHypervisorDriver hypervisor, IVmNestedDriver driver)
{
    public sealed record Setting(DateTimeOffset Created, bool Enabled);
    private static string Section(Vm vm) => "nested:" + vm.Name.ToLowerInvariant();
    public async Task<Setting?> GetAsync(Vm vm, CancellationToken ct)
    {
        var setting = await config.GetAsync<Setting>(Section(vm), ct);
        return setting?.Created == vm.Created ? setting : null;
    }
    public Task SaveAsync(Vm vm, bool enabled, string actor, CancellationToken ct) =>
        config.SetAsync(Section(vm), new Setting(vm.Created, enabled), actor, ct);
    public async Task ApplyAsync(Vm vm, VmState state, CancellationToken ct)
    {
        if (vm.Kind != VmKind.Primary || state != VmState.Off) return;
        var setting = await GetAsync(vm, ct);
        if (setting is null) return;
        if (setting.Enabled && !hypervisor.NestedAvailable) throw new LifecycleException("unsupported-on-host");
        await driver.SetNestedAsync(vm.Name, setting.Enabled, ct);
    }
}
