using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Api.Contracts;
using Constructd.Api.Hosting;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Tests.Updates;

namespace Constructd.Tests.Provisioning;

/// <summary>
/// A provision a user asks for starts a VM that is off first (the <c>start</c> step), with the Start button's rules, then
/// waits for the guest to answer over SSH; the engine's own reprovisions (the follow-up of a host update, an admin's
/// every-VM reprovision) never start a VM and skip one that is off before any job exists.
/// </summary>
public sealed class ProvisionStartTests
{
    private const string Newer = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    /// <summary>
    /// The harness engine (its installed commit is the cached source), whose guests answer SSH only while the fake
    /// hypervisor runs them, with alice's VM "devbox" provisioned once.
    /// </summary>
    private static async Task<ProvisionHarness> StartAsync(int? sshWaitSeconds = null)
    {
        var h = await ProvisionHarness.StartAsync(settings: sshWaitSeconds is int s
            ? new Dictionary<string, string?> { ["Constructd:StartedVmSshWaitSeconds"] = s.ToString(System.Globalization.CultureInfo.InvariantCulture) }
            : null);
        ((FakeReleaseInfo)h.App.Service<IReleaseInfo>()).Installed = new(ProvisionHarness.Commit, "1.0.0", null, "release");
        h.Ssh.Reachable = t => h.App.Driver.StateOf(t.Host.Split('.')[0]) == VmState.Running;
        Assert.Equal(JobState.Succeeded, (await h.RunAsync("first")).State);
        return h;
    }

    private static async Task StopAsync(HttpClient owner, string vm)
    {
        (await owner.PostAsJsonAsync($"/api/v1/vms/{vm}/power", new { action = "stop" })).EnsureSuccessStatusCode();
    }

    private static async Task<HttpClient> AddVmAsync(ProvisionHarness h, string user, string vm, Role role = Role.User)
    {
        var owner = await h.App.CreateUserClientAsync(user, role);
        await ProvisionVmAsync(owner, vm);
        return owner;
    }

    private static async Task ProvisionVmAsync(HttpClient owner, string vm)
    {
        await owner.CreateVmAsync(vm);
        var response = await owner.PostJsonAsync($"/api/v1/vms/{vm}/jobs", new { kind = "provision", requestId = "first", commit = ProvisionHarness.Commit, desired = ProvisionHarness.Desired() });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(JobState.Succeeded, (await owner.WaitForJobAsync((await response.ReadAsync<VmJobAcceptedResponse>()).JobId)).State);
    }

    private static int Starts(TestApp app, string vm) => app.Driver.Calls.Count(c => c == "start:" + vm);

    private static async Task<Job[]> ProvisionJobsAsync(TestApp app, string vm) =>
        (await app.Service<IJobQueryStore>().ListAsync(default)).Where(j => j.Kind == JobKinds.Provision && j.VmName == vm).ToArray();

    /// <summary>
    /// Waits until <paramref name="vm"/> has <paramref name="count"/> provision jobs, all finished; answers the one that is not
    /// the VM's first provision (the test clock gives every job the same time, so the order says nothing).
    /// </summary>
    private static async Task<Job> WaitForJobsAsync(TestApp app, string vm, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var jobs = await ProvisionJobsAsync(app, vm);
            if (jobs.Length >= count && jobs.All(j => j.State is not (JobState.Queued or JobState.Running)))
            {
                Assert.Equal(count, jobs.Length);
                return jobs.Length == 1 ? jobs[0] : Assert.Single(jobs, j => j.OperationKey != "first");
            }
            await Task.Delay(20);
        }
        throw new TimeoutException($"{vm} did not get {count} provision jobs.");
    }

    private static void AssertStartedThenProvisioned(JobResponse job)
    {
        Assert.Equal(JobState.Succeeded, job.State);
        var steps = job.Progress.Where(p => p.Step is not null).Select(p => p.Step).Distinct().ToArray();
        Assert.Equal("start", steps[0]);
        Assert.Contains("provision", steps);
        Assert.Contains(job.Progress, p => p is { Step: "start", State: "succeeded" });
        Assert.Contains(job.Progress, p => p.Label == "Starting the VM");
    }

    [Fact]
    public async Task A_manual_provision_starts_a_vm_that_is_off_and_then_provisions_it()
    {
        await using var h = await StartAsync();
        await StopAsync(h.Client, ProvisionHarness.Vm);

        var job = await h.RunAsync("second");

        AssertStartedThenProvisioned(job);
        Assert.Equal(1, Starts(h.App, ProvisionHarness.Vm));
        // Left running: the user asked for the provision and is about to use the VM.
        Assert.Equal(VmState.Running, h.App.Driver.StateOf(ProvisionHarness.Vm));
        Assert.Equal(VmState.Running, (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!.State);
    }

    [Fact]
    public async Task A_running_vm_is_provisioned_without_a_start()
    {
        await using var h = await StartAsync();
        var job = await h.RunAsync("second");

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.DoesNotContain(job.Progress, p => p.Step == "start");
        Assert.Equal(0, Starts(h.App, ProvisionHarness.Vm));
        Assert.DoesNotContain(h.App.Driver.Calls, c => c == "stop:" + ProvisionHarness.Vm);
    }

    [Fact]
    public async Task A_manual_provision_waits_for_a_running_vm_that_does_not_answer_yet()
    {
        // FatPC 2026-11-22: the user started the VM by hand while the host updated; the reprovision found it running but
        // still booting and failed its copy within seconds.
        await using var h = await StartAsync();
        var calls = 0;
        h.Ssh.Reachable = _ => Interlocked.Increment(ref calls) > 1;

        var job = await h.RunAsync("second");

        Assert.Equal(JobState.Succeeded, job.State);
        var steps = job.Progress.Where(p => p.Step is not null).Select(p => p.Step).Distinct().ToArray();
        Assert.Equal("start", steps[0]);
        Assert.Contains(job.Progress, p => p is { Step: "start", State: "succeeded" });
        Assert.Contains(job.Progress, p => p.Label == "Waiting for the VM");
        Assert.Equal(0, Starts(h.App, ProvisionHarness.Vm));
    }

    [Fact]
    public async Task A_running_vm_that_never_answers_fails_the_manual_provision_after_the_wait()
    {
        await using var h = await StartAsync(sshWaitSeconds: 1);
        h.Ssh.Reachable = _ => false;

        var job = await h.RunAsync("second");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("guest-unreachable: The VM runs but did not answer over SSH within 1 second. Retry once it is up.", job.Error);
        Assert.Equal("start", ProvisionHarness.Result(job).FailedStep);
        Assert.Equal(0, Starts(h.App, ProvisionHarness.Vm));
    }

    [Fact]
    public async Task The_engines_own_reprovision_does_not_wait_for_a_running_vm_that_does_not_answer()
    {
        await using var h = await StartAsync(sshWaitSeconds: 60);
        using var admin = await h.App.CreateUserClientAsync("root", Role.Admin);
        h.Ssh.Reachable = _ => false;

        var started = DateTime.UtcNow;
        Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsync("/api/v1/host/vms/reprovision", null)).StatusCode);
        var job = await WaitForJobsAsync(h.App, ProvisionHarness.Vm, 2);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("copy", ProvisionHarness.Result(await h.Client.WaitForJobAsync(job.Id)).FailedStep);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), "The engine's own reprovision waited.");
    }

    [Fact]
    public async Task My_vms_reprovision_starts_each_of_my_vms_that_is_off()
    {
        await using var h = await StartAsync();
        await StopAsync(h.Client, ProvisionHarness.Vm);

        var response = await h.Client.PostAsync("/api/v1/me/vms/reprovision", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var last = await WaitForJobsAsync(h.App, ProvisionHarness.Vm, 2);

        AssertStartedThenProvisioned(await h.Client.WaitForJobAsync(last.Id));
        Assert.Equal(VmState.Running, h.App.Driver.StateOf(ProvisionHarness.Vm));
    }

    [Fact]
    public async Task The_vm_update_route_starts_a_vm_that_is_off()
    {
        await using var h = await StartAsync();
        await StopAsync(h.Client, ProvisionHarness.Vm);

        var response = await h.Client.PostAsync($"/api/v1/vms/{ProvisionHarness.Vm}/update", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        AssertStartedThenProvisioned(await h.Client.WaitForJobAsync((await response.ReadAsync<VmUpdateResponse>()).JobId!));
        Assert.Equal(VmState.Running, h.App.Driver.StateOf(ProvisionHarness.Vm));
    }

    [Fact]
    public async Task A_retry_of_a_failed_provision_starts_a_vm_that_is_off()
    {
        await using var h = await StartAsync();
        h.Ssh.Provision = _ => (1, ["boom"]);
        var failed = await h.RunAsync("second");
        Assert.Equal(JobState.Failed, failed.State);
        h.Ssh.Provision = _ => (0, FakeSshRunner.Clean);
        await StopAsync(h.Client, ProvisionHarness.Vm);

        var response = await h.Client.PostJsonAsync($"/api/v1/jobs/{failed.Id}/retry", new { });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        AssertStartedThenProvisioned(await h.Client.WaitForJobAsync((await response.ReadAsync<VmJobAcceptedResponse>()).JobId));
    }

    [Fact]
    public async Task A_refused_start_fails_the_job_with_its_reason_and_no_ssh()
    {
        await using var h = await StartAsync();
        await StopAsync(h.Client, ProvisionHarness.Vm);
        var ledger = h.App.Service<InMemoryCapacityLedger>();
        ledger.Mode = CapacityMode.Enforce;
        ledger.Inventory = new(1, DateTimeOffset.UtcNow, true, 8L << 30, 0, 0, 0, 8L << 30, 512L << 20, 8, 8, 0, 8,
            [new(@"C:\", 64L << 30, 64L << 30, 0, 0, 64L << 30)], [], []);
        var calls = h.Ssh.Calls.Count;

        var job = await h.RunAsync("second");

        Assert.Equal(JobState.Failed, job.State);
        Assert.StartsWith("start-refused: The host has too little free memory to start the VM", job.Error);
        Assert.DoesNotContain("ssh", job.Error, StringComparison.OrdinalIgnoreCase);
        var result = ProvisionHarness.Result(job);
        Assert.Equal("start", result.FailedStep);
        Assert.Equal("start-refused", result.Code);
        Assert.Empty(result.FixPrompt);
        Assert.Equal(calls, h.Ssh.Calls.Count);
        Assert.Equal(VmState.Off, h.App.Driver.StateOf(ProvisionHarness.Vm));
    }

    [Fact]
    public async Task A_guest_that_never_answers_fails_the_job_after_the_wait()
    {
        await using var h = await StartAsync(sshWaitSeconds: 1);
        await StopAsync(h.Client, ProvisionHarness.Vm);
        h.Ssh.Reachable = _ => false;

        var job = await h.RunAsync("second");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("guest-unreachable: The VM started but did not answer over SSH within 1 second. It keeps running; retry once it is up.", job.Error);
        Assert.Equal("start", ProvisionHarness.Result(job).FailedStep);
        Assert.Equal(VmState.Running, h.App.Driver.StateOf(ProvisionHarness.Vm));
    }

    [Fact]
    public async Task A_probe_that_hangs_does_not_stretch_the_wait()
    {
        await using var h = await StartAsync(sshWaitSeconds: 1);
        await StopAsync(h.Client, ProvisionHarness.Vm);
        h.Ssh.ProbeHangs = true;

        var started = DateTime.UtcNow;
        var job = await h.RunAsync("second");

        Assert.Equal(JobState.Failed, job.State);
        Assert.StartsWith("guest-unreachable: ", job.Error);
        // The probe's own limit is 30 seconds: the wait of one second ends it.
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(15), "The wait outlasted its deadline.");
    }

    [Fact]
    public async Task A_job_cancelled_while_its_vm_starts_finishes_the_start_and_leaves_nothing_in_flight()
    {
        await using var h = await StartAsync();
        await StopAsync(h.Client, ProvisionHarness.Vm);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.App.Driver.AfterStart = async _ =>
        {
            started.TrySetResult();
            await release.Task;
        };

        var id = await h.StartAsync("second");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        (await h.Client.PostAsync($"/api/v1/jobs/{id}/cancel", null)).EnsureSuccessStatusCode();
        release.SetResult();
        var job = await h.Client.WaitForJobAsync(id);

        Assert.NotEqual(JobState.Succeeded, job.State);
        Assert.Equal(VmState.Running, h.App.Driver.StateOf(ProvisionHarness.Vm));
        Assert.Equal(VmState.Running, (await h.App.Vms.GetAsync(ProvisionHarness.Vm, default))!.State);
        Assert.Empty(await h.App.Service<IOperationKeyStore>().ListInFlightAsync(ProvisionHarness.Vm, default));
    }

    [Fact]
    public async Task The_follow_up_of_a_host_update_skips_a_vm_that_is_off_and_submits_no_job_for_it()
    {
        await using var h = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PutAsJsonAsync("/api/v1/me/updates", new { autoReprovision = true })).StatusCode);
        await h.Client.CreateVmAsync("devbox2");
        Assert.Equal(HttpStatusCode.OK, (await h.Client.PutJsonAsync("/api/v1/vms/devbox2/desired", ProvisionHarness.Desired())).StatusCode);
        await StopAsync(h.Client, ProvisionHarness.Vm);

        var row = UpdateTests.Row(new string('d', 32)) with { Commit = ProvisionHarness.Commit, State = HostUpdateState.Succeeded, Finished = DateTimeOffset.UtcNow };
        await h.App.Service<IHostUpdateStore>().TryStartAsync(row, default);
        await h.App.Service<HostAutoUpdateService>().TickAsync(default);

        // The running VM in the same chain is reprovisioned; the one that is off gets no job, no start.
        Assert.Equal(JobState.Succeeded, (await WaitForJobsAsync(h.App, "devbox2", 1)).State);
        await Task.Delay(200);
        Assert.Single(await ProvisionJobsAsync(h.App, ProvisionHarness.Vm));
        Assert.Equal(0, Starts(h.App, ProvisionHarness.Vm));
        Assert.Equal(VmState.Off, h.App.Driver.StateOf(ProvisionHarness.Vm));
    }

    [Fact]
    public async Task An_admins_every_vm_reprovision_skips_a_vm_that_is_off()
    {
        await using var h = await StartAsync();
        var bob = await AddVmAsync(h, "bob", "bobbox");
        await StopAsync(bob, "bobbox");
        using var admin = await h.App.CreateUserClientAsync("root", Role.Admin);

        var response = await admin.PostAsync("/api/v1/host/vms/reprovision", null);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        Assert.Equal(JobState.Succeeded, (await WaitForJobsAsync(h.App, ProvisionHarness.Vm, 2)).State);
        await Task.Delay(200);
        Assert.Single(await ProvisionJobsAsync(h.App, "bobbox"));
        Assert.Equal(0, Starts(h.App, "bobbox"));
        Assert.Equal(VmState.Off, h.App.Driver.StateOf("bobbox"));
    }

    [Fact]
    public async Task An_admins_vm_update_starts_that_vm_after_the_host_update_and_skips_the_admins_other_vms_that_are_off()
    {
        await using var h = await StartAsync();
        var admin = await AddVmAsync(h, "root", "rootbox", Role.Admin);
        await ProvisionVmAsync(admin, "rootb2");
        await StopAsync(admin, "rootbox");
        await StopAsync(admin, "rootb2");
        await OfferNewerHostReleaseAsync(h.App);

        var answer = await (await admin.PostAsync("/api/v1/vms/rootbox/update", null)).ReadAsync<VmUpdateResponse>();
        Assert.NotNull(answer.HostUpdateId);
        await admin.WaitForJobAsync(answer.HostJobId!);
        var row = (await h.App.Service<IHostUpdateStore>().GetAsync(answer.HostUpdateId!, default))! with { State = HostUpdateState.Succeeded, Commit = ProvisionHarness.Commit };
        await h.App.Service<IHostUpdateStore>().UpsertAsync(row, default);
        await h.App.Service<HostAutoUpdateService>().TickAsync(default);

        AssertStartedThenProvisioned(await admin.WaitForJobAsync((await WaitForJobsAsync(h.App, "rootbox", 2)).Id));
        await Task.Delay(200);
        Assert.Single(await ProvisionJobsAsync(h.App, "rootb2"));
        Assert.Equal(VmState.Off, h.App.Driver.StateOf("rootb2"));
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
}
