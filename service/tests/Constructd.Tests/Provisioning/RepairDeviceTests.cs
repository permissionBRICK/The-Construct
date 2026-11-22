using System.Net;
using System.Net.Http.Json;
using Constructd.Api.Contracts;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Tests.Updates;

namespace Constructd.Tests.Provisioning;

/// <summary>
/// The device a repair opens on (design 11.5): the one whose device token asked for the failed job, or for the
/// repair; the session says <c>startedHere</c> to that device only and never names a device.
/// </summary>
public sealed class RepairDeviceTests
{
    private const string Vm = ProvisionHarness.Vm;

    public RepairDeviceTests() => Constructd.Api.Repair.RepairSessions.RebootProbeDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>Every guest step fails: each provision fails and starts a repair.</summary>
    private static FakeSshRunner Failing() => new() { Provision = _ => (1, FakeSshRunner.WithErrors(("Installing SDKs", 2, "/var/log/construct/provision/step-0.log"))) };

    private static async Task<(ProvisionHarness H, UserDevices Devices, HttpClient Laptop, HttpClient Desktop)> StartAsync()
    {
        var h = await ProvisionHarness.StartAsync(Failing());
        JobRetryTests.Installed(h);
        var (devices, laptop) = await UserDevices.StartAsync(h.App, "alice");
        var desktop = await devices.PairAsync("desktop");
        return (h, devices, laptop, desktop);
    }

    private static async Task<string> ProvisionAsync(HttpClient client, string requestId)
    {
        var response = await client.PostJsonAsync($"/api/v1/vms/{Vm}/jobs",
            new { kind = "provision", requestId, commit = ProvisionHarness.Commit, desired = ProvisionHarness.Desired() });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var id = (await response.ReadAsync<VmJobAcceptedResponse>()).JobId;
        Assert.Equal(JobState.Failed, (await client.WaitForJobAsync(id)).State);
        return id;
    }

    private static async Task<RepairSessionResponse?> ReadAsync(HttpClient client, string? id = null)
    {
        var response = await client.GetAsync($"/api/v1/vms/{Vm}/repair" + (id is null ? "" : "/" + id));
        return response.StatusCode == HttpStatusCode.OK ? await response.ReadAsync<RepairSessionResponse>() : null;
    }

    /// <summary>The VM's session of failed job <paramref name="jobId"/> once it waits for an answer.</summary>
    private static async Task<RepairSessionResponse> WaitingAsync(HttpClient client, string? jobId = null)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            if (await ReadAsync(client) is { State: "waiting" } session && (jobId is null || session.JobId == jobId)) return session;
            await Task.Delay(25);
        }

        throw new TimeoutException("No waiting repair of job " + jobId);
    }

    private static async Task<bool> StartedHereAsync(HttpClient client, string id) => (await ReadAsync(client, id))!.StartedHere;

    [Fact]
    public async Task The_device_that_started_a_failed_provision_is_told_its_repair_started_there_and_no_other_caller_is()
    {
        var (h, devices, laptop, desktop) = await StartAsync();
        await using var _ = h;
        using var __ = devices;
        var failed = await ProvisionAsync(laptop, "click-1");

        var session = await WaitingAsync(laptop, failed);
        Assert.True(session.Automatic);
        Assert.True(session.StartedHere);
        Assert.True(await StartedHereAsync(laptop, session.Id));
        Assert.NotNull(session.T3Thread);
        // Another device of the same user, and the user's own sign-in without a device token, are not that PC.
        Assert.False((await ReadAsync(desktop))!.StartedHere);
        Assert.False(await StartedHereAsync(desktop, session.Id));
        Assert.False((await ReadAsync(h.Client))!.StartedHere);
        var admin = await h.App.CreateUserClientAsync("root", Role.Admin);
        Assert.False((await ReadAsync(admin))!.StartedHere);

        // The answer never names a device.
        var raw = await (await laptop.GetAsync($"/api/v1/vms/{Vm}/repair")).Content.ReadAsStringAsync();
        Assert.All(devices.Ids, id => Assert.DoesNotContain(id, raw, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_repair_asked_for_by_hand_started_on_the_device_that_asked_and_a_job_without_a_device_token_has_none()
    {
        var (h, devices, laptop, desktop) = await StartAsync();
        await using var _ = h;
        using var __ = devices;
        // The user's sign-in, no device token: the automatic repair opens on no device.
        var failed = await ProvisionAsync(h.Client, "click-1");
        var automatic = await WaitingAsync(h.Client, failed);
        Assert.False(automatic.StartedHere);
        Assert.False(await StartedHereAsync(laptop, automatic.Id));
        Assert.False(await StartedHereAsync(desktop, automatic.Id));
        Assert.Equal(HttpStatusCode.NoContent, (await h.Client.DeleteAsync($"/api/v1/vms/{Vm}/repair/{automatic.Id}")).StatusCode);

        // By hand from the desktop: that device, whatever started the job.
        var started = await desktop.PostAsync($"/api/v1/vms/{Vm}/repair", null);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var manual = await started.ReadAsync<RepairSessionResponse>();
        Assert.False(manual.Automatic);
        Assert.True(manual.StartedHere);
        Assert.True(await StartedHereAsync(desktop, manual.Id));
        Assert.False(await StartedHereAsync(laptop, manual.Id));
        Assert.False(await StartedHereAsync(h.Client, manual.Id));
    }

    [Fact]
    public async Task A_retry_and_a_vm_update_carry_the_device_that_asked_for_them()
    {
        var (h, devices, laptop, desktop) = await StartAsync();
        await using var _ = h;
        using var __ = devices;
        var failed = await ProvisionAsync(laptop, "click-1");
        Assert.True((await WaitingAsync(laptop, failed)).StartedHere);

        // The retry from the desktop fails again: its repair opens there, not on the laptop.
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        var retry = await desktop.PostAsJsonAsync($"/api/v1/jobs/{failed}/retry", new { });
        Assert.Equal(HttpStatusCode.Accepted, retry.StatusCode);
        var retried = (await retry.ReadAsync<VmJobAcceptedResponse>()).JobId;
        Assert.Equal(JobState.Failed, (await desktop.WaitForJobAsync(retried)).State);
        var second = await WaitingAsync(desktop, retried);
        Assert.True(second.StartedHere);
        Assert.False(await StartedHereAsync(laptop, second.Id));

        // The VM's own update route, asked for by the laptop.
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        var update = await laptop.PostAsync($"/api/v1/vms/{Vm}/update", null);
        Assert.Equal(HttpStatusCode.Accepted, update.StatusCode);
        var updated = (await update.ReadAsync<VmUpdateResponse>()).JobId!;
        Assert.Equal(JobState.Failed, (await laptop.WaitForJobAsync(updated)).State);
        var third = await WaitingAsync(laptop, updated);
        Assert.True(third.StartedHere);
        Assert.False(await StartedHereAsync(desktop, third.Id));
    }

    [Fact]
    public async Task Reprovisioning_my_vms_carries_the_device_to_each_job_and_a_reprovision_the_engine_starts_has_none()
    {
        var (h, devices, laptop, desktop) = await StartAsync();
        await using var _ = h;
        using var __ = devices;
        (await h.Client.PutAsJsonAsync($"/api/v1/vms/{Vm}/desired", ProvisionHarness.Desired())).EnsureSuccessStatusCode();

        // "Update everything" on the laptop reprovisions the user's VMs one after the other.
        var response = await laptop.PostAsync("/api/v1/me/vms/reprovision", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([Vm], (await response.ReadAsync<VmReprovisionResponse>()).Vms);
        var mine = await WaitingAsync(laptop);
        Assert.True(mine.StartedHere);
        Assert.False(await StartedHereAsync(desktop, mine.Id));
        Assert.Equal(devices.Ids[0], h.App.Service<JobDevices>().Of(mine.JobId));

        // The host's own follow-up after it updated itself: actor system, no device.
        h.App.Clock.Advance(TimeSpan.FromMinutes(1));
        (await h.Client.PutAsJsonAsync("/api/v1/me/updates", new { autoReprovision = true })).EnsureSuccessStatusCode();
        var row = UpdateTests.Row(new string('d', 32)) with { Commit = ProvisionHarness.Commit, State = HostUpdateState.Succeeded, Finished = DateTimeOffset.UtcNow };
        Assert.Equal([Vm], await h.App.Service<VmUpdates>().FollowAsync(row, default));
        RepairSessionResponse? followed = null;
        var until = DateTime.UtcNow.AddSeconds(30);
        while (followed is null && DateTime.UtcNow < until)
        {
            if (await ReadAsync(laptop) is { State: "waiting" } s && s.Id != mine.Id) followed = s;
            else await Task.Delay(25);
        }

        Assert.NotNull(followed);
        var job = (await h.App.Service<IJobQueryStore>().ListAsync(default)).Single(j => j.Id == followed.JobId);
        Assert.Equal("system", job.Initiator);
        Assert.False(followed.StartedHere);
        Assert.False(await StartedHereAsync(desktop, followed.Id));
        Assert.Null(h.App.Service<JobDevices>().Of(followed.JobId));
    }

    [Fact]
    public void The_jobs_devices_are_kept_for_the_latest_jobs_only()
    {
        var devices = new JobDevices();
        devices.Remember("none", null);
        for (var i = 0; i <= JobDevices.Kept; i++) devices.Remember("job-" + i, "device-" + i);
        devices.Remember("job-1", "other");

        Assert.Null(devices.Of("none"));
        Assert.Null(devices.Of("job-0"));
        Assert.Equal("device-1", devices.Of("job-1"));
        Assert.Equal("device-" + JobDevices.Kept, devices.Of("job-" + JobDevices.Kept));
    }
}
