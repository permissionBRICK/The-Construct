using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Api.Contracts;
using Constructd.Api.Hosting;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Provisioning;
using Constructd.Tests.Support;
using Constructd.Windows.Updates;
namespace Constructd.Tests.Updates;

/// <summary>
/// VMs following their host's update (12.1, S28): "my VMs" reprovisions only the caller's, an admin may
/// reprovision every VM, the user setting makes the host do it after it updated itself, and a VM token may
/// ask for its own update, which runs the host's update first only when the VM's owner is an admin.
/// </summary>
public sealed class VmUpdateTests
{
    private const string Newer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>A scripted engine whose installed commit is the cached source, with alice's VM "devbox" provisioned once.</summary>
    private static async Task<ProvisionHarness> StartAsync()
    {
        var h = await ProvisionHarness.StartAsync();
        ((FakeReleaseInfo)h.App.Service<IReleaseInfo>()).Installed = new(ProvisionHarness.Commit, "1.0.0", null, "release");
        Assert.Equal(JobState.Succeeded, (await h.RunAsync("first")).State);
        return h;
    }

    private static async Task<(HttpClient Owner, string Token)> AddVmAsync(ProvisionHarness h, string user, string vm, Role role = Role.User)
    {
        var owner = await h.App.CreateUserClientAsync(user, role);
        var created = await owner.CreateVmAsync(vm);
        var response = await owner.PostJsonAsync($"/api/v1/vms/{vm}/jobs", new { kind = "provision", requestId = "first", commit = ProvisionHarness.Commit, desired = ProvisionHarness.Desired() });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(JobState.Succeeded, (await owner.WaitForJobAsync((await response.ReadAsync<VmJobAcceptedResponse>()).JobId)).State);
        return (owner, created.VmToken());
    }

    private static async Task<Job[]> ProvisionJobsAsync(TestApp app, string vm) =>
        (await app.Service<IJobQueryStore>().ListAsync(default)).Where(j => j.Kind == JobKinds.Provision && j.VmName == vm).ToArray();

    /// <summary>Waits until <paramref name="vm"/> has <paramref name="count"/> provision jobs, all finished.</summary>
    private static async Task WaitForJobsAsync(TestApp app, string vm, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var jobs = await ProvisionJobsAsync(app, vm);
            if (jobs.Length >= count && jobs.All(j => j.State is not (JobState.Queued or JobState.Running))) { Assert.Equal(count, jobs.Length); return; }
            await Task.Delay(20);
        }
        throw new TimeoutException($"{vm} did not get {count} provision jobs.");
    }

    private static async Task OfferNewerHostReleaseAsync(TestApp app)
    {
        await app.Service<IHostConfigStore>().SetAsync("updates", HostAdminDefaults.Updates, "test", default);
        var hash = new string('b', 64);
        var json = new JsonObject
        {
            ["schemaVersion"] = 1, ["commit"] = Newer, ["ref"] = "refs/heads/main", ["packageVersion"] = "1.2.3", ["builtAt"] = DateTimeOffset.UtcNow,
            ["repository"] = "permissionBRICK/The-Construct", ["releaseTag"] = "host-" + Newer, ["payloadAsset"] = "construct-host-aaaaaaa-win-x64.zip",
            ["payloadSha256"] = hash, ["payloadSizeBytes"] = 10, ["sumsSha256"] = hash, ["updaterPath"] = "updater/Update-ConstructHost.ps1", ["updaterSha256"] = hash,
            ["channel"] = "stable", ["version"] = "1.2.3+" + Newer, ["guestPayloadHash"] = hash,
            ["packages"] = new JsonObject { ["payload"] = new JsonObject { ["asset"] = "construct-host-aaaaaaa-win-x64.zip", ["sha256"] = hash } },
        };
        app.Service<FakeReleaseSource>().ChannelManifests[new Uri("https://github.com/permissionBRICK/The-Construct/releases/latest/download/manifest.json")] =
            JsonSerializer.SerializeToUtf8Bytes(json);
    }

    [Fact]
    public async Task My_vms_reprovisions_only_the_callers_vms_and_an_admin_reprovisions_all()
    {
        await using var h = await StartAsync();
        await AddVmAsync(h, "bob", "bobbox");
        var response = await h.Client.PostAsync("/api/v1/me/vms/reprovision", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([ProvisionHarness.Vm], (await response.ReadAsync<VmReprovisionResponse>()).Vms);
        await WaitForJobsAsync(h.App, ProvisionHarness.Vm, 2);
        Assert.Single(await ProvisionJobsAsync(h.App, "bobbox"));
        Assert.All(await ProvisionJobsAsync(h.App, ProvisionHarness.Vm), j => Assert.Equal(JobState.Succeeded, j.State));

        using var admin = await h.App.CreateUserClientAsync("root", Role.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.Client.PostAsync("/api/v1/host/vms/reprovision", null)).StatusCode);
        response = await admin.PostAsync("/api/v1/host/vms/reprovision", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(["bobbox", ProvisionHarness.Vm], (await response.ReadAsync<VmReprovisionResponse>()).Vms);
        await WaitForJobsAsync(h.App, "bobbox", 2);
        await WaitForJobsAsync(h.App, ProvisionHarness.Vm, 3);
    }

    [Fact]
    public async Task Auto_reprovision_follows_a_successful_host_update_once_for_users_who_turned_it_on()
    {
        await using var h = await StartAsync();
        await AddVmAsync(h, "bob", "bobbox");
        Assert.False((await (await h.Client.GetAsync("/api/v1/me/updates")).ReadAsync<UserUpdateSettingsBody>()).AutoReprovision);
        var put = await h.Client.PutAsJsonAsync("/api/v1/me/updates", new { autoReprovision = true });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        Assert.True((await (await h.Client.GetAsync("/api/v1/me/updates")).ReadAsync<UserUpdateSettingsBody>()).AutoReprovision);

        // This host just updated itself to the commit it runs.
        var id = new string('d', 32);
        var row = UpdateTests.Row(id) with { Commit = ProvisionHarness.Commit, State = HostUpdateState.Succeeded, Finished = DateTimeOffset.UtcNow };
        await h.App.Service<IHostUpdateStore>().TryStartAsync(row, default);
        await h.App.Service<HostAutoUpdateService>().TickAsync(default);
        await WaitForJobsAsync(h.App, ProvisionHarness.Vm, 2);
        Assert.Single(await ProvisionJobsAsync(h.App, "bobbox"));

        // Once per update.
        await h.App.Service<HostAutoUpdateService>().TickAsync(default);
        await Task.Delay(200);
        Assert.Equal(2, (await ProvisionJobsAsync(h.App, ProvisionHarness.Vm)).Length);
    }

    [Fact]
    public async Task Vm_token_of_a_users_vm_reprovisions_that_vm_and_never_runs_the_host_update()
    {
        await using var h = await StartAsync();
        var (_, token) = await AddVmAsync(h, "carol", "carolbox");
        await OfferNewerHostReleaseAsync(h.App);
        using var vm = h.App.CreateVmTokenClient(token);

        var status = await (await vm.GetAsync("/api/v1/vms/carolbox/update")).ReadAsync<VmUpdateStatusResponse>();
        Assert.False(status.NewerPayload);
        Assert.Equal(ProvisionHarness.Commit, status.AppliedCommit);
        Assert.Null(status.HostUpdate);

        var response = await vm.PostAsync("/api/v1/vms/carolbox/update", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var answer = await response.ReadAsync<VmUpdateResponse>();
        Assert.NotNull(answer.JobId);
        Assert.Null(answer.HostUpdateId);
        await WaitForJobsAsync(h.App, "carolbox", 2);
        Assert.Empty(await h.App.Service<IHostUpdateStore>().ListAsync(10, default));

        // The token reaches its own VM only, and nothing of the host.
        Assert.Equal(HttpStatusCode.Forbidden, (await vm.PostAsync($"/api/v1/vms/{ProvisionHarness.Vm}/update", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vm.GetAsync($"/api/v1/vms/{ProvisionHarness.Vm}/update")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vm.PostAsync("/api/v1/host/updates/run", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vm.PostAsync("/api/v1/me/vms/reprovision", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await vm.PostAsync("/api/v1/host/vms/reprovision", null)).StatusCode);
        // Nor does an admin update somebody else's VM through it.
        using var admin = await h.App.CreateUserClientAsync("root", Role.Admin);
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.PostAsync("/api/v1/vms/carolbox/update", null)).StatusCode);
    }

    [Fact]
    public async Task Vm_token_of_an_admins_vm_runs_the_host_update_first_and_the_vm_follows_it()
    {
        await using var h = await StartAsync();
        var (admin, token) = await AddVmAsync(h, "root", "rootbox", Role.Admin);
        await OfferNewerHostReleaseAsync(h.App);
        using var vm = h.App.CreateVmTokenClient(token);

        var status = await (await vm.GetAsync("/api/v1/vms/rootbox/update")).ReadAsync<VmUpdateStatusResponse>();
        Assert.Equal("1.2.3+" + Newer, status.HostUpdate?.Version);

        var response = await vm.PostAsync("/api/v1/vms/rootbox/update", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var answer = await response.ReadAsync<VmUpdateResponse>();
        Assert.Null(answer.JobId);
        Assert.NotNull(answer.HostUpdateId);
        await admin.WaitForJobAsync(answer.HostJobId!); // the fake stager has no package: the run stops before any handoff
        Assert.Equal(Newer, (await h.App.Service<IHostUpdateStore>().GetAsync(answer.HostUpdateId!, default))!.Commit);
        Assert.Equal(["rootbox"], (await h.App.Service<IHostConfigStore>().GetAsync<UpdateFollowers>(VmUpdates.FollowersKey(answer.HostUpdateId!), default))!.Vms);
        Assert.Single(await ProvisionJobsAsync(h.App, "rootbox"));

        // Once that update succeeded, the host reprovisions the VM that asked; nobody else's.
        var row = (await h.App.Service<IHostUpdateStore>().GetAsync(answer.HostUpdateId!, default))! with { State = HostUpdateState.Succeeded, Commit = ProvisionHarness.Commit };
        await h.App.Service<IHostUpdateStore>().UpsertAsync(row, default);
        await h.App.Service<HostAutoUpdateService>().TickAsync(default);
        await WaitForJobsAsync(h.App, "rootbox", 2);
        Assert.Single(await ProvisionJobsAsync(h.App, ProvisionHarness.Vm));
    }
}
