using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Provisioning;

/// <summary><c>GET</c>/<c>PUT /vms/{name}/desired</c>: a VM's stored settings outside a provision request (design 7.5).</summary>
public sealed class DesiredStateRouteTests
{
    private const string Url = $"/api/v1/vms/{ProvisionHarness.Vm}/desired";

    [Fact]
    public async Task A_put_stores_the_settings_changes_nothing_in_the_vm_and_shows_as_waiting()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var empty = await (await h.Client.GetAsync(Url)).ReadAsync<VmDesiredStateResponse>();
        Assert.Null(empty.Desired);
        Assert.Null(empty.Hash);

        var put = await h.Client.PutJsonAsync(Url, ProvisionHarness.Desired("web,api"));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var stored = await put.ReadAsync<VmDesiredStateResponse>();
        Assert.Equal(["web", "api"], stored.Desired!.Projects);
        Assert.Equal("claude-code", stored.Desired.Settings.AiTools);
        Assert.Equal("alice", stored.UpdatedBy);
        Assert.Empty(h.Ssh.Commands);

        var read = await (await h.Client.GetAsync(Url)).ReadAsync<VmDesiredStateResponse>();
        Assert.Equal(stored.Hash, read.Hash);
        var status = await (await h.Client.GetAsync($"/api/v1/vms/{ProvisionHarness.Vm}/provision-status")).ReadAsync<ProvisionStatusResponse>();
        Assert.True(status.SettingsChanged);
        Assert.Equal(stored.Hash, status.DesiredHash);

        // A provision without settings in its request uses the stored ones; afterwards nothing waits.
        var job = await h.Client.WaitForJobAsync((await (await h.Client.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/jobs",
            new { kind = "provision", requestId = "after-put", commit = ProvisionHarness.Commit })).ReadAsync<VmJobAcceptedResponse>()).JobId);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal("web,api", FakeSshRunner.Variable(h.Ssh.Commands.Single(c => c.Contains("bin/provision.sh", StringComparison.Ordinal)), "PROJECTS"));
        status = await (await h.Client.GetAsync($"/api/v1/vms/{ProvisionHarness.Vm}/provision-status")).ReadAsync<ProvisionStatusResponse>();
        Assert.False(status.SettingsChanged);

        // A second change waits again.
        await h.Client.PutJsonAsync(Url, ProvisionHarness.Desired("web"));
        status = await (await h.Client.GetAsync($"/api/v1/vms/{ProvisionHarness.Vm}/provision-status")).ReadAsync<ProvisionStatusResponse>();
        Assert.True(status.SettingsChanged);
    }

    [Fact]
    public async Task Invalid_settings_are_refused_by_field()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var response = await h.Client.PutJsonAsync(Url, new { projects = new[] { "bad name" }, settings = new { } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\"projects\"", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        response = await h.Client.PutJsonAsync(Url, new { projects = Array.Empty<string>(), settings = new { seedUser = "Root;x" } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null((await (await h.Client.GetAsync(Url)).ReadAsync<VmDesiredStateResponse>()).Desired);
    }

    [Fact]
    public async Task Another_user_can_neither_read_nor_change_them()
    {
        await using var h = await ProvisionHarness.StartAsync();
        await h.Client.PutJsonAsync(Url, ProvisionHarness.Desired());
        var bob = await h.App.CreateUserClientAsync("bob");
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync(Url)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PutJsonAsync(Url, ProvisionHarness.Desired("other"))).StatusCode);
        var vmClient = h.App.CreateVmTokenClient(h.VmToken!);
        Assert.Equal(HttpStatusCode.Forbidden, (await vmClient.GetAsync(Url)).StatusCode);
        Assert.Equal(["default"], (await (await h.Client.GetAsync(Url)).ReadAsync<VmDesiredStateResponse>()).Desired!.Projects);
    }

    [Fact]
    public async Task Settings_stored_for_an_earlier_vm_of_the_name_are_not_this_vms()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var vm = (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!;
        var earlier = new VmDesiredState(["secret-project"], new ProvisionSettings { GitEmail = "someone@example.test" });
        await h.App.Service<IVmDesiredStateSource>().SetDesiredAsync(vm.Name,
            new StoredDesiredState(earlier, new string('a', 64), vm.Created.AddDays(-3), "carol", vm.Created.AddDays(-3)), default);

        Assert.Null((await (await h.Client.GetAsync(Url)).ReadAsync<VmDesiredStateResponse>()).Desired);
        var response = await h.Client.PostJsonAsync($"/api/v1/vms/{ProvisionHarness.Vm}/jobs",
            new { kind = "provision", requestId = "no-settings", commit = ProvisionHarness.Commit });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("desired-state-missing", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_row_without_the_creation_time_counts_only_when_written_after_the_vm_was_created()
    {
        await using var h = await ProvisionHarness.StartAsync();
        var vm = (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!;
        var store = h.App.Service<IVmDesiredStateSource>();
        var settings = new VmDesiredState(["p"], new ProvisionSettings());
        await store.SetDesiredAsync(vm.Name, new StoredDesiredState(settings, new string('a', 64), vm.Created.AddDays(-1), "carol"), default);
        Assert.Null((await (await h.Client.GetAsync(Url)).ReadAsync<VmDesiredStateResponse>()).Desired);
        await store.SetDesiredAsync(vm.Name, new StoredDesiredState(settings, new string('a', 64), vm.Created.AddMinutes(1), "alice"), default);
        Assert.Equal(["p"], (await (await h.Client.GetAsync(Url)).ReadAsync<VmDesiredStateResponse>()).Desired!.Projects);
    }
}
