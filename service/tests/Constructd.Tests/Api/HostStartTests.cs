using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Constructd.Tests.Api;

/// <summary>
/// Which VMs a host brings back when it boots (<c>HostStartRules</c>): never a child, a primary only while its
/// effective idle policy is off. Asserted on the fake driver's start-with-host setting; what the Proxmox and Hyper-V
/// drivers make of it has its own tests.
/// </summary>
public sealed class HostStartTests
{
    private static readonly IdlePolicy TwoHours = new(120, IdleAction.Save);

    private static Vm Record(string name, IdlePolicy idle, VmKind kind = VmKind.Primary, bool deleting = false) =>
        new(name, "alice", 1, 1, 1, DateTimeOffset.UnixEpoch, VmState.Running, null, null, idle, Vm.NoForwards,
            Deleting: deleting, Kind: kind, Parent: kind == VmKind.Child ? "parent" : null);

    private static async Task<JobResponse> CreateAsync(HttpClient client, string name, int timeoutMinutes, string action) =>
        await client.WaitForJobAsync(await client.StartCreateVmAsync(name, opts: new { idlePolicy = new { timeoutMinutes, action } }));

    private static async Task PutPolicyAsync(HttpClient client, string name, int timeoutMinutes, string action) =>
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/v1/vms/{name}/idle-policy", new { timeoutMinutes, action })).StatusCode);

    [Fact]
    public async Task A_primary_starts_with_the_host_only_while_its_idle_policy_is_off()
    {
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("alice");

        // The default policy idles out after two hours: such a VM is created to stay down at host boot.
        await owner.CreateVmAsync("idler");
        Assert.False(app.Driver.StartWithHost["idler"]);
        Assert.Equal(JobState.Succeeded, (await CreateAsync(owner, "keeper", 0, "off")).State);
        Assert.True(app.Driver.StartWithHost["keeper"]);

        // An idle timeout clears it; removing the timeout sets it back.
        await PutPolicyAsync(owner, "keeper", 60, "save");
        Assert.False(app.Driver.StartWithHost["keeper"]);
        await PutPolicyAsync(owner, "keeper", 0, "off");
        Assert.True(app.Driver.StartWithHost["keeper"]);
        await PutPolicyAsync(owner, "idler", 30, "off");
        Assert.True(app.Driver.StartWithHost["idler"]);
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.True(done(), "The setting did not follow.");
    }

    [Fact]
    public async Task A_policy_change_while_an_operation_holds_the_vm_is_set_after_it_from_the_latest_policy()
    {
        // Review of #133: a create or reinstall writes the setting from the record it read before the hypervisor VM
        // existed; a policy change beside it failed (no VM yet) or was overwritten. It now follows the operation.
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("alice");
        Assert.Equal(JobState.Succeeded, (await CreateAsync(owner, "keeper", 0, "off")).State);
        Assert.True(app.Driver.StartWithHost["keeper"]);

        var held = await app.Service<IVmOperationGate>().AcquireAsync("keeper", "create-job", default);
        await PutPolicyAsync(owner, "keeper", 60, "save");
        // The operation still holds the VM: nothing is set beside it, and it ends writing its own older reading.
        Assert.DoesNotContain("start-with-host:keeper:False", app.Driver.Calls);
        app.Driver.StartWithHost["keeper"] = true;
        await held.DisposeAsync();

        await UntilAsync(() => !app.Driver.StartWithHost["keeper"]);
    }

    [Fact]
    public async Task The_startup_pass_waits_for_an_operation_that_holds_the_vm_and_reads_the_vm_after_it()
    {
        // Review of #133: the pass read a VM, a start beside it changed what the VM should get, and the pass wrote its
        // stale result. Under the VM's gate it runs after the start, from the record as the start left it.
        using var app = new TestApp();
        Assert.Equal(VmAddOutcome.Added, await app.Vms.AddAsync(Record("keeper", TwoHours), 10, default));
        app.Driver.StartWithHost["keeper"] = false;

        var held = await app.Service<IVmOperationGate>().AcquireAsync("keeper", "start-job", default);
        await app.Service<HostStartSettings>().ApplyAllAsync(default);
        Assert.DoesNotContain(app.Driver.Calls, c => c.StartsWith("start-with-host:keeper:", StringComparison.Ordinal));
        // Meanwhile the VM's policy changed: it never idles out now.
        var vm = (await app.Vms.GetAsync("keeper", default))!;
        await app.Vms.UpdateAsync(vm with { IdlePolicy = IdlePolicy.Disabled }, default);
        await held.DisposeAsync();

        await UntilAsync(() => app.Driver.StartWithHost["keeper"]);
    }

    [Fact]
    public async Task A_user_operation_waits_for_the_setting_instead_of_being_refused()
    {
        // The setting holds a VM's gate for one hypervisor call, as a background pass: a Stop clicked meanwhile waits
        // for it (PrimaryOperationGate) instead of answering "another operation runs".
        using var app = new TestApp();
        using var owner = await app.CreateUserClientAsync("alice");
        Assert.Equal(JobState.Succeeded, (await CreateAsync(owner, "keeper", 0, "off")).State);

        var held = await app.Service<IVmOperationGate>().AcquireAsync("keeper", HostStartSettings.GateOwner, default);
        var stop = owner.PostAsJsonAsync("/api/v1/vms/keeper/power", new { action = "stop" });
        await Task.Delay(200);
        Assert.False(stop.IsCompleted);
        await held.DisposeAsync();
        Assert.True((await stop).IsSuccessStatusCode);
    }

    [Fact]
    public async Task A_host_that_forces_idling_keeps_every_primary_down_at_its_boot()
    {
        using var app = new TestApp(new Dictionary<string, string?>
        {
            ["Constructd:Idle:MaxTimeoutMinutes"] = "60",
            ["Constructd:Idle:ForceEnabled"] = "true",
        });
        using var owner = await app.CreateUserClientAsync("alice");

        Assert.Equal(JobState.Succeeded, (await CreateAsync(owner, "vm", 0, "off")).State);
        Assert.False(app.Driver.StartWithHost["vm"]);
        await PutPolicyAsync(owner, "vm", 0, "off");
        Assert.False(app.Driver.StartWithHost["vm"]);

        // Stored with idling off before the host forced it: the policy is clamped where it is evaluated.
        Assert.Equal(VmAddOutcome.Added, await app.Vms.AddAsync(Record("older", IdlePolicy.Disabled), 10, default));
        app.Driver.StartWithHost["older"] = true;
        await app.Service<HostStartSettings>().ApplyAllAsync(default);
        Assert.False(app.Driver.StartWithHost["older"]);
    }

    [Fact]
    public async Task A_child_never_starts_with_the_host()
    {
        using var app = new TestApp();
        using var client = await app.CreateUserClientAsync("alice");
        await app.Vms.AddAsync(Record("parent", IdlePolicy.Disabled), 5, default);
        await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice",
            UserAllowance.Unset with { AllowChildCreation = true, MaxRetainedChildren = 5, AllowNeverLifetime = true }, default);
        app.Service<InMemoryCapacityLedger>().Mode = CapacityMode.Observe;
        await app.Service<IMediaStore>().AddAsync(new("install", "alice", "install.iso", MediaRole.Install, MediaSource.Upload, null,
            @"C:\media\install.iso", MediaState.Ready, 40960, 40960, null, null, null, null, null, app.Clock.UtcNow, app.Clock.UtcNow, null), default);

        var response = await client.PostAsJsonAsync("/api/v1/vms/parent/children", new
        {
            name = "child", cpus = 1, ramMb = 512, diskGb = 1, lifetime = "10m", media = new { installMediaId = "install" },
            preset = "windows", start = true, operationKey = "create-key-1",
        });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
        Assert.Equal(JobState.Succeeded, (await client.WaitForJobAsync(jobId)).State);
        Assert.Contains("start:child", app.Driver.Calls);
        Assert.DoesNotContain("start-with-host:child:True", app.Driver.Calls);

        // A child made before the rule, when children were created to come back with the host:
        app.Driver.StartWithHost["child"] = true;
        await app.Service<HostStartSettings>().ApplyAllAsync(default);
        Assert.False(app.Driver.StartWithHost["child"]);
    }

    [Fact]
    public async Task The_startup_pass_fixes_earlier_vms_skips_what_it_does_not_manage_and_goes_on_past_a_failure()
    {
        using var app = new TestApp();
        foreach (var vm in new[]
                 {
                     Record("broken", TwoHours), Record("child", IdlePolicy.Disabled, VmKind.Child), Record("idler", TwoHours),
                     Record("keeper", IdlePolicy.Disabled), Record("leaving", TwoHours, deleting: true),
                 })
            Assert.Equal(VmAddOutcome.Added, await app.Vms.AddAsync(vm, 10, default));
        // As VMs made before the rule have it: every one starts with the host, except a primary that lost it.
        foreach (var name in new[] { "broken", "child", "idler", "leaving", "unmanaged" }) app.Driver.StartWithHost[name] = true;
        app.Driver.StartWithHost["keeper"] = false;
        app.Driver.StartWithHostFailures["broken"] = new InvalidOperationException("refused");

        await app.Service<HostStartSettings>().ApplyAllAsync(default);

        Assert.False(app.Driver.StartWithHost["child"]);
        Assert.False(app.Driver.StartWithHost["idler"]);
        Assert.True(app.Driver.StartWithHost["keeper"]);
        // The VM that cannot be set is logged; the others were set all the same.
        Assert.True(app.Driver.StartWithHost["broken"]);
        Assert.Contains(app.Logs.Entries, e => e.Level == LogLevel.Warning && e.Rendered.Contains("broken", StringComparison.Ordinal));
        // A VM being deleted and one the engine does not manage stay as they are.
        Assert.True(app.Driver.StartWithHost["leaving"]);
        Assert.True(app.Driver.StartWithHost["unmanaged"]);
        Assert.DoesNotContain(app.Driver.Calls, c => c.StartsWith("start-with-host:leaving:", StringComparison.Ordinal) ||
                                                    c.StartsWith("start-with-host:unmanaged:", StringComparison.Ordinal));

        // A second pass leaves the same settings.
        await app.Service<HostStartSettings>().ApplyAllAsync(default);
        Assert.Equal((false, false, true), (app.Driver.StartWithHost["child"], app.Driver.StartWithHost["idler"], app.Driver.StartWithHost["keeper"]));
    }
}
