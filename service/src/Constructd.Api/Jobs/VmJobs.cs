using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

// Disambiguate from Microsoft.AspNetCore.Http.Endpoint.
using DomainEndpoint = Constructd.Core.Domain.Endpoint;

namespace Constructd.Api.Jobs;

/// <summary>
/// The VM lifecycle workflows the job engine runs. They resolve their services from a fresh scope
/// because they outlive the request that submitted them.
///
/// Creation is the hybrid split of plan §4.4: the service builds the ISO, creates the VM, waits for
/// SSH, allocates the forward and issues the VM-scoped token — the client then runs
/// <c>Provision-AgentVM.ps1</c> against the returned endpoint, so user secrets never transit here.
/// </summary>
public static class VmJobs
{
    public static async Task<JobOutcome> CreateAsync(
        IServiceScopeFactory scopes,
        VmDescriptor descriptor,
        string actor,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<ConstructdOptions>();
        var isoBuilder = services.GetRequiredService<IIsoBuilder>();
        var driver = services.GetRequiredService<IHypervisorDriver>();
        var forwards = services.GetRequiredService<IPortForwardManager>();
        var tokens = services.GetRequiredService<ITokenService>();
        var vms = services.GetRequiredService<IVmRepository>();
        var audit = services.GetRequiredService<IAuditLog>();
        var clock = services.GetRequiredService<IClock>();

        var name = descriptor.Name;

        // Set the moment the hypervisor has (or may have) a VM under this name, so that a failure
        // afterwards removes it instead of leaking a VM and its disk chain.
        var vmMayExist = false;

        try
        {
            // Seed credentials exist only for the unattended install; the client's provisioning run
            // replaces them. Generated per VM, never persisted, never logged.
            var seedPassword = TokenHasher.GenerateSecret();

            var isoPath = await isoBuilder.BuildAsync(
                name,
                options.Iso.SeedUser,
                seedPassword,
                options.Iso.BootstrapPublicKeyPath,
                progress,
                cancellationToken).ConfigureAwait(false);

            vmMayExist = true;
            await driver.CreateVmAsync(descriptor with { IsoPath = isoPath }, progress, cancellationToken)
                .ConfigureAwait(false);

            var timeout = TimeSpan.FromMinutes(Math.Max(1, options.VmReachableTimeoutMinutes));
            var reachable = await driver.WaitReachableAsync(name, timeout, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!reachable)
            {
                throw new VmNotReachableException(name, timeout);
            }

            await driver.DetachInstallMediaAsync(name, cancellationToken).ConfigureAwait(false);
            progress.Report("install media detached");

            var port = await forwards.AllocateSshForwardAsync(name, cancellationToken).ConfigureAwait(false);
            progress.Report($"ssh forward allocated on {options.PublicHost}:{port}");

            // Issued after the forward so a failure earlier never leaves a live token behind. This
            // also writes the hash onto the VM record, hence the re-read below.
            var vmToken = await tokens.IssueVmTokenAsync(name, cancellationToken).ConfigureAwait(false);
            progress.Report("vm-scoped token issued (handed out once, to the first retrieval of this job)");

            var state = await driver.GetStateAsync(name, cancellationToken).ConfigureAwait(false);
            var vm = await vms.GetAsync(name, cancellationToken).ConfigureAwait(false);
            if (vm is not null)
            {
                await vms.UpdateAsync(vm with { SshForwardPort = port, State = state }, cancellationToken)
                    .ConfigureAwait(false);
            }

            await AuditAsync(audit, clock, actor, "vm.create.completed", name, AuditOutcome.Success,
                $"endpoint={options.PublicHost}:{port}", cancellationToken).ConfigureAwait(false);

            progress.Report($"vm {name} ready — provision it with Provision-AgentVM.ps1 " +
                            $"-VmHost {options.PublicHost} -SshPort {port}");

            // The token travels in the one-time channel, never in the (durable) job result.
            return new JobOutcome(new VmCreateResult(name, new DomainEndpoint(options.PublicHost, port)), vmToken);
        }
        catch (Exception ex)
        {
            // Only a safe description reaches the progress log, the job error and the audit trail: a
            // driver or ISO builder can put a whole command line — seed password included — into an
            // exception message. The exception itself goes to the service log (see the job engine's
            // diagnostics hook).
            var safe = SafeError.Describe(ex);
            progress.Report($"creation failed: {safe}");
            await RollBackAsync(services, name, vmMayExist, progress).ConfigureAwait(false);

            await AuditAsync(audit, clock, actor, "vm.create.failed", name, AuditOutcome.Failure, safe,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Undoes a partial creation: the hypervisor VM first (an orphan VM keeps consuming disk and
    /// blocks the name on the host), then the ports, then the registry record — which is what frees
    /// the name and the quota slot. Rollback failures are reported but never replace the original
    /// error, which is what the job is failing with.
    /// </summary>
    private static async Task RollBackAsync(
        IServiceProvider services,
        string name,
        bool vmMayExist,
        IProgress<string> progress)
    {
        var driver = services.GetRequiredService<IHypervisorDriver>();
        var forwards = services.GetRequiredService<IPortForwardManager>();
        var vms = services.GetRequiredService<IVmRepository>();

        if (vmMayExist)
        {
            await TryAsync(
                () => driver.RemoveVmAsync(name, progress, CancellationToken.None),
                $"remove the partially created vm {name}",
                progress).ConfigureAwait(false);
        }

        // Each step stands on its own: a forward manager that is unhappy must not stop the registry
        // record from being removed, or the name and the quota slot would stay reserved forever.
        await TryAsync(
            () => forwards.ReleaseSshForwardAsync(name, CancellationToken.None),
            $"release the ssh forward of {name}",
            progress).ConfigureAwait(false);

        await TryAsync(
            () => forwards.RemoveAllForwardsAsync(name, CancellationToken.None),
            $"remove the forwards of {name}",
            progress).ConfigureAwait(false);

        await TryAsync(
            () => vms.RemoveAsync(name, CancellationToken.None),
            $"remove the registry record of {name}",
            progress).ConfigureAwait(false);
    }

    /// <summary>Runs one cleanup step, reporting rather than propagating its failure.</summary>
    private static async Task TryAsync(Func<Task> step, string what, IProgress<string> progress)
    {
        try
        {
            await step().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            progress.Report($"WARNING: could not {what}: {SafeError.Describe(ex)}");
        }
    }

    public static async Task<JobOutcome> RemoveAsync(
        IServiceScopeFactory scopes,
        string name,
        string actor,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var driver = services.GetRequiredService<IHypervisorDriver>();
        var forwards = services.GetRequiredService<IPortForwardManager>();
        var vms = services.GetRequiredService<IVmRepository>();
        var audit = services.GetRequiredService<IAuditLog>();
        var clock = services.GetRequiredService<IClock>();

        try
        {
            await driver.RemoveVmAsync(name, progress, cancellationToken).ConfigureAwait(false);

            var removed = await forwards.RemoveAllForwardsAsync(name, cancellationToken).ConfigureAwait(false);
            await forwards.ReleaseSshForwardAsync(name, cancellationToken).ConfigureAwait(false);
            progress.Report($"released {removed} forward(s) and the ssh forward");

            await vms.RemoveAsync(name, cancellationToken).ConfigureAwait(false);
            progress.Report($"vm {name} removed");

            await AuditAsync(audit, clock, actor, "vm.delete.completed", name, AuditOutcome.Success,
                $"forwards={removed}", cancellationToken).ConfigureAwait(false);

            return new JobOutcome(new VmRemoveResult(name, removed));
        }
        catch (Exception ex)
        {
            var safe = SafeError.Describe(ex);
            progress.Report($"removal failed: {safe}");
            await AuditAsync(audit, clock, actor, "vm.delete.failed", name, AuditOutcome.Failure, safe,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static Task AuditAsync(
        IAuditLog audit,
        IClock clock,
        string actor,
        string action,
        string target,
        AuditOutcome outcome,
        string? detail,
        CancellationToken cancellationToken) =>
        audit.AppendAsync(new AuditEntry(clock.UtcNow, actor, action, target, outcome, detail), cancellationToken);
}
