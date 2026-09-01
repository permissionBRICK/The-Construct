using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// A creation that does not finish must not leave anything behind: no hypervisor VM, no allocated
/// port, and above all no registry reservation — that one holds the name and a quota slot.
/// </summary>
public class RollbackTests
{
    /// <summary>An in-memory job store that can be broken and repaired between requests.</summary>
    private sealed class SwitchableJobStore : IJobStore
    {
        private readonly InMemoryJobStore _inner = new();

        public bool Broken { get; set; }

        public Task UpsertAsync(Job job, CancellationToken cancellationToken) =>
            Broken
                ? throw new IOException("the job database is unavailable")
                : _inner.UpsertAsync(job, cancellationToken);

        public Task<Job?> GetAsync(string id, CancellationToken cancellationToken) =>
            _inner.GetAsync(id, cancellationToken);

        public Task<int> MarkInterruptedAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            _inner.MarkInterruptedAsync(now, cancellationToken);
    }

    /// <summary>A store that refuses to record anything, so job submission fails.</summary>
    private sealed class BrokenJobStore : IJobStore
    {
        public Task UpsertAsync(Job job, CancellationToken cancellationToken) =>
            throw new IOException("the job database is unavailable");

        public Task<Job?> GetAsync(string id, CancellationToken cancellationToken) =>
            Task.FromResult<Job?>(null);

        public Task<int> MarkInterruptedAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
            Task.FromResult(0);
    }

    [Fact]
    public async Task A_failing_forward_manager_does_not_keep_the_name_and_quota_reserved()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob", maxVms: 1);

        // Creation gets as far as the VM existing, then the SSH wait fails and rollback runs into a
        // forward manager that throws on every call.
        app.Driver.Reachable = false;
        app.Forwards.Failure = new InvalidOperationException("netsh is not answering");

        var job = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("work-vm"));

        Assert.Equal(JobState.Failed, job.State);

        // The hypervisor VM is gone and the reservation was released despite the forward failures.
        Assert.Contains("remove:work-vm", app.Driver.Calls);
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/v1/vms/work-vm")).StatusCode);
        Assert.Equal(0, await app.Vms.CountByOwnerAsync("bob", CancellationToken.None));

        var log = string.Join("\n", job.Progress.Select(p => p.Text));
        Assert.Contains("WARNING: could not release the ssh forward", log);
        Assert.Contains("WARNING: could not remove the forwards", log);

        // ...so the quota slot is free again: a retry works once the fakes behave.
        app.Driver.Reachable = true;
        app.Forwards.Failure = null;
        var retry = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("work-vm"));
        Assert.Equal(JobState.Succeeded, retry.State);
    }

    [Fact]
    public async Task A_job_that_cannot_be_queued_releases_the_reservation()
    {
        using var app = new TestApp(
            configureServices: services => services.AddSingleton<IJobStore>(new BrokenJobStore()));

        using var bob = await app.CreateUserClientAsync("bob", maxVms: 1);

        using var response = await bob.PostJsonAsync("/api/v1/vms",
            new { name = "work-vm", cpu = 2, ramGb = 4, diskGb = 32 });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // No VM record was left behind, so the name and the quota slot are still available.
        Assert.Equal(0, await app.Vms.CountByOwnerAsync("bob", CancellationToken.None));
        Assert.Null(await app.Vms.GetAsync("work-vm", CancellationToken.None));

        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();
        Assert.Contains(entries, e => e is
        {
            Action: "vm.create", Target: "work-vm", Outcome: AuditOutcome.Failure,
        });
    }

    /// <remarks>
    /// The window the fence closes: a VM token adding a durable forward after the delete job has
    /// enumerated the existing ones but before the VM row is gone. Such a forward would survive the VM
    /// and be re-materialized for a VM that no longer exists at the next startup reconciliation.
    /// </remarks>
    [Fact]
    public async Task A_vm_token_cannot_slip_a_forward_past_an_accepted_deletion()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        var created = await bob.CreateVmAsync("work-vm");
        using var guest = app.CreateVmTokenClient(created.VmToken());

        await bob.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "vite", target = "host" });

        // Hold the delete job inside the forward teardown, holding the VM's gate.
        app.Forwards.HoldRemoveAll = true;

        using var accepted = await bob.DeleteAsync("/api/v1/vms/work-vm");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        var jobId = (await accepted.ReadAsync<JobAcceptedResponse>()).JobId;
        await app.Forwards.RemoveAllStarted.Task;

        // The guest's token was revoked when the deletion was accepted...
        using var guestAttempt = await guest.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3001, label = "sneaky", target = "host" });
        Assert.Equal(HttpStatusCode.Unauthorized, guestAttempt.StatusCode);

        // ...and even the owner is fenced off while the removal runs.
        using var ownerAttempt = await bob.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3002, label = "also-sneaky", target = "host" });
        Assert.Equal(HttpStatusCode.Conflict, ownerAttempt.StatusCode);
        Assert.Contains("being deleted", await ownerAttempt.Content.ReadAsStringAsync());

        app.Forwards.ReleaseRemoveAll();
        var job = await bob.WaitForJobAsync(jobId);

        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Null(await app.Vms.GetAsync("work-vm", CancellationToken.None));
        Assert.Empty(await app.Service<IForwardStore>().ListAsync(null, CancellationToken.None));
        Assert.Empty(app.Forwards.Materialized);
    }

    [Fact]
    public async Task An_accepted_deletion_fences_power_policy_and_heartbeats()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        app.Forwards.HoldRemoveAll = true;
        using var accepted = await bob.DeleteAsync("/api/v1/vms/work-vm");
        var jobId = (await accepted.ReadAsync<JobAcceptedResponse>()).JobId;
        await app.Forwards.RemoveAllStarted.Task;

        Assert.Equal(HttpStatusCode.Conflict,
            (await bob.PostJsonAsync("/api/v1/vms/work-vm/power", new { action = "stop" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await bob.PutJsonAsync("/api/v1/vms/work-vm/idle-policy",
                new { timeoutMinutes = 5, action = "save" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await bob.PostJsonAsync("/api/v1/vms/work-vm/activity", new { busy = true })).StatusCode);

        // Reads still work, and report that the VM is on its way out.
        var vm = await (await bob.GetAsync("/api/v1/vms/work-vm")).ReadAsync<VmResponse>();
        Assert.True(vm.Deleting);

        app.Forwards.ReleaseRemoveAll();
        Assert.Equal(JobState.Succeeded, (await bob.WaitForJobAsync(jobId)).State);
    }

    /// <remarks>
    /// Accepting a deletion fences the VM and revokes its token; if the job that is supposed to do the
    /// removal cannot be queued, that fence is an advance on nothing and has to be given back.
    /// </remarks>
    [Fact]
    public async Task A_deletion_whose_job_cannot_be_queued_unfences_the_vm()
    {
        var store = new SwitchableJobStore();
        using var app = new TestApp(configureServices: services =>
            services.AddSingleton<IJobStore>(store));

        using var bob = await app.CreateUserClientAsync("bob");
        var created = await bob.CreateVmAsync("work-vm");
        using var guest = app.CreateVmTokenClient(created.VmToken());

        // The VM is operable and its guest can talk to it.
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/v1/vms/work-vm/forwards")).StatusCode);

        store.Broken = true;
        using var response = await bob.DeleteAsync("/api/v1/vms/work-vm");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        store.Broken = false;

        // The VM is back the way it was: not fenced, token intact, mutations allowed again.
        var vm = await app.Vms.GetAsync("work-vm", CancellationToken.None);
        Assert.NotNull(vm);
        Assert.False(vm!.Deleting);

        var reread = await (await bob.GetAsync("/api/v1/vms/work-vm")).ReadAsync<VmResponse>();
        Assert.False(reread.Deleting);

        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/v1/vms/work-vm/forwards")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await guest.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "vite", target = "client" })).StatusCode);

        // ...and there is no half-created removal job for it.
        Assert.DoesNotContain("remove:work-vm", app.Driver.Calls);

        // A later delete still works.
        using var accepted = await bob.DeleteAsync("/api/v1/vms/work-vm");
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.Equal(JobState.Succeeded,
            (await bob.WaitForJobAsync((await accepted.ReadAsync<JobAcceptedResponse>()).JobId)).State);
    }

    [Fact]
    public async Task A_removal_that_fails_on_the_driver_keeps_the_vm_record()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        // The forward manager is what fails here, after the driver removed the VM: the job fails, and
        // the record stays so the user (or an admin) can retry rather than losing track of the VM.
        app.Forwards.Failure = new InvalidOperationException("netsh is not answering");

        using var accepted = await bob.DeleteAsync("/api/v1/vms/work-vm");
        var job = await bob.WaitForJobAsync((await accepted.ReadAsync<JobAcceptedResponse>()).JobId);

        Assert.Equal(JobState.Failed, job.State);

        // Only the exception type is persisted, never a dependency's message.
        Assert.Equal("InvalidOperationException", job.Error);
        Assert.NotNull(await app.Vms.GetAsync("work-vm", CancellationToken.None));
    }
}
