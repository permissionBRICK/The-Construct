using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Jobs;

/// <summary>
/// The <c>start</c> step of a provision a user asked for (design 12.1): a VM that is off, saved or paused is started
/// first with the Start button's rules (<see cref="LifecycleStart"/>: the VM's pending settings, admission and capacity),
/// under the VM gate the provision job already holds, and the job then waits a bounded time for the guest to answer over
/// SSH. The VM stays running afterwards. A provision the engine starts by itself never starts a VM: its chain skips a
/// stopped VM before it submits anything (<see cref="VmUpdates"/>).
/// </summary>
public sealed class ProvisionStart(LifecycleStart starts, IVmRepository vms, IHypervisorDriver driver, IProvisionTargets targets,
    ISshRunner ssh, IPersistedJobRunner phases, ConstructdOptions options)
{
    public const string Step = "start";

    /// <summary>The pause between two SSH probes of a VM that is booting.</summary>
    private static readonly TimeSpan ProbeDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether <paramref name="vm"/> is in a state its start leaves: off, saved or paused on a driver that can start VMs.
    /// A state that cannot be read counts as not stopped, so the provision runs as it would have.
    /// </summary>
    public async Task<bool> StoppedAsync(Vm vm, CancellationToken ct)
    {
        if (!driver.Capabilities.Power) return false;
        try { return await driver.GetStateAsync(vm.Name, ct) is VmState.Off or VmState.Saved or VmState.Paused; }
        catch (Exception ex) when (ex is not OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Starts the VM and waits until it answers over SSH; answers where to dial it. The caller holds the VM's gate. A start
    /// that is refused, and a guest that does not answer in time, fail the job with the <c>start</c> step and a plain reason.
    /// </summary>
    public async Task<SshTarget> RunAsync(string jobId, Vm vm, ProvisionJobResult failed, IProgress<string> progress, CancellationToken ct)
    {
        JobProgressTracker.Current?.Begin(Step, ProvisionJob.Label(Step), planned: false);
        await phases.SetPhaseAsync(jobId, Step, CancellationToken.None);
        Report(progress, "running", "the VM is off; starting it first");
        var current = await vms.GetAsync(vm.Name, ct);
        if (current is null || current.Deleting || current.Created != vm.Created)
            throw Failure(progress, failed, "vm-deleting", "The VM is being deleted.");

        string? code;
        CapacityDecision? decision = null;
        try
        {
            // An admitted start is finished whatever happens to the job: cancelled halfway it would leave the VM running
            // with its stored state, operation key and reservations of a start that never completed. A start is short.
            var reply = await starts.RunAsync(current, null, null, starts.PrimaryKey(current), CancellationToken.None);
            code = reply.Code ?? (ReservationRules.Terminal(reply.State) ? "start-failed" : null);
        }
        catch (LifecycleException ex)
        {
            // Started meanwhile: what the job wanted.
            code = ex.Code == "already-running" ? null : ex.Code;
            decision = ex.Capacity;
        }

        if (code is not null) throw Failure(progress, failed, "start-refused", Refusal(code, decision));
        ct.ThrowIfCancellationRequested();
        Report(progress, "running", "the VM runs; waiting for it to answer over SSH");

        // One deadline bounds the whole wait, the endpoint's resolution and each probe included.
        var wait = TimeSpan.FromSeconds(Math.Max(1, options.StartedVmSshWaitSeconds));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(wait);
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (await targets.ResolveAsync(current, deadline.Token) is { } target)
                {
                    try
                    {
                        if (await ssh.RunAsync(target, "true", null, null, ProbeTimeout, deadline.Token) == 0)
                        {
                            Report(progress, "succeeded", "the VM answers over SSH");
                            return target;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException) { }
                }

                await Task.Delay(ProbeDelay, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Failure(progress, failed, "guest-unreachable",
                $"The VM started but did not answer over SSH within {Span(wait)}. It keeps running; retry once it is up.");
        }
    }

    /// <summary>Why the engine did not start the VM, in plain words.</summary>
    private static string Refusal(string code, CapacityDecision? decision) => code switch
    {
        "capacity-exhausted" when decision is { Resource: { } resource } =>
            $"The host has too little free {Resource(resource)} to start the VM ({Amount(resource, decision.Requested)} needed, {Amount(resource, decision.Available)} free). Stop another VM, then retry.",
        "capacity-exhausted" => "The host has no room to start the VM right now. Stop another VM, then retry.",
        "capacity-unavailable" => "The host could not count its free capacity, so it did not start the VM. Retry in a minute.",
        "configuration-incomplete" => "A change to the VM's hardware settings has not finished, so it was not started. Start it from its page.",
        "vm-state-unknown" => "The VM's power state could not be read, so it was not started.",
        "start-failed" => "The hypervisor could not start the VM. Start it from its page to see why.",
        "operation-key-conflict" or "power-state-changed" or "superseded" => "The VM's power state changed while it was being started. Retry.",
        "vm-deleting" => "The VM is being deleted.",
        _ => $"The VM could not be started ({code}).",
    };

    private static string Resource(string resource) => resource switch { "ram" => "memory", "cpu" => "CPU", "storage" => "disk space", _ => resource };

    private static string Amount(string resource, long amount) => resource is "ram" or "storage"
        ? $"{amount / (1024d * 1024 * 1024):0.#} GB"
        : amount.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Span(TimeSpan wait) => wait.TotalSeconds % 60 == 0
        ? $"{wait.TotalMinutes:0} minute{(wait.TotalMinutes == 1 ? "" : "s")}"
        : $"{wait.TotalSeconds:0} second{(wait.TotalSeconds == 1 ? "" : "s")}";

    private static JobFailureException Failure(IProgress<string> progress, ProvisionJobResult failed, string code, string message)
    {
        Report(progress, "failed", message);
        return new JobFailureException($"{code}: {message}", failed with { FailedStep = Step, Code = code });
    }

    private static void Report(IProgress<string> progress, string state, string line)
    {
        if (progress is IJobStepProgress steps) steps.Report(new JobStepProgress(Step, state, line));
        else progress.Report(line);
    }
}
