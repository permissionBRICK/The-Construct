using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Jobs;

/// <summary>
/// Runs an <see cref="JobKinds.UpdateAgents"/> job over SSH: invokes ONLY the narrowed installer
/// (<c>AI_TOOLS_OVERRIDE</c> with <c>AI_CONSOLE_INTEGRATION=false</c>) for each requested CLI agent and/or
/// <c>bin/construct-t3-update.sh</c> for T3 Code, then refreshes the VM's agent report so the UI updates at once.
/// Never runs <c>bin/provision.sh</c>.
/// </summary>
public sealed class AgentUpdateJob(ISshRunner ssh, IPersistedJobRunner phases, VmAgents agents,
    IVmDesiredStateSource desiredStates, IClock clock)
{
    private static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ReportTimeout = TimeSpan.FromMinutes(5);

    public async Task<JobOutcome> RunAsync(string jobId, Vm vm, SshTarget target, IReadOnlyList<string> tools,
        IProgress<string> progress, CancellationToken cancel)
    {
        JobProgressTracker.Current?.Expect([.. tools, AgentUpdateScript.ReportStep]);
        var desired = await desiredStates.GetDesiredForAsync(vm, CancellationToken.None);
        var claudeUser = desired?.Desired.Settings.ClaudeUser;
        var updated = new List<string>(tools.Count);

        foreach (var tool in tools)
        {
            cancel.ThrowIfCancellationRequested();
            JobProgressTracker.Current?.Begin(tool, AgentUpdateScript.Label(tool));
            await phases.SetPhaseAsync(jobId, tool, CancellationToken.None);
            var display = VmAgents.Names.GetValueOrDefault(tool, tool);
            Report(progress, tool, "running", $"updating {display}");

            var lines = new List<string>();
            var filter = new GuestOutputFilter();
            var sink = new LineProgress(line =>
            {
                var clean = ProvisionScript.CleanLine(line);
                if (filter.Pass(clean) is not { } safe) return;
                lines.Add(safe);
                Report(progress, tool, "running", safe);
            });

            var command = ProvisionScript.AsRoot(AgentUpdateScript.Command(tool, claudeUser), target.IsRoot, sudoPasswordOnStdin: false);
            int exit;
            try
            {
                // Run the guest installer to completion (bounded by UpdateTimeout, matching ProvisionJob) so user
                // cancellation stops cleanly between installers rather than leaving orphaned work behind the gate.
                exit = await ssh.RunAsync(target, command, null, sink, UpdateTimeout, CancellationToken.None);
            }
            catch (SshRunnerException ex)
            {
                var message = SafeError.Describe(ex);
                Report(progress, tool, "failed", message);
                if (updated.Count > 0) await RefreshAgentsAsync(vm, target, desired, progress);
                throw new JobFailureException($"ssh-failed: {message}",
                    new AgentUpdateJobResult(tools, updated, false, tool, "ssh-failed", message));
            }

            if (exit != 0)
            {
                var (code, message) = Failure(target, display, exit, lines);
                Report(progress, tool, "failed", message);
                if (updated.Count > 0) await RefreshAgentsAsync(vm, target, desired, progress);
                throw new JobFailureException($"{code}: {message}",
                    new AgentUpdateJobResult(tools, updated, false, tool, code, message));
            }

            updated.Add(tool);
            Report(progress, tool, "succeeded", $"{display} updated");
        }

        cancel.ThrowIfCancellationRequested();
        JobProgressTracker.Current?.Begin(AgentUpdateScript.ReportStep, AgentUpdateScript.Label(AgentUpdateScript.ReportStep));
        await phases.SetPhaseAsync(jobId, AgentUpdateScript.ReportStep, CancellationToken.None);
        Report(progress, AgentUpdateScript.ReportStep, "running", "checking coding agent versions");
        var refreshed = await RefreshAgentsAsync(vm, target, desired, progress);
        cancel.ThrowIfCancellationRequested();
        Report(progress, AgentUpdateScript.ReportStep, "succeeded",
            refreshed ? "coding agents reported" : "the coding agents are reported by the VM's next usage report");

        return new JobOutcome(new AgentUpdateJobResult(tools, updated, true));
    }

    /// <summary>
    /// Reads the guest's agent report (<c>construct-usage-report.sh --agents</c>) and saves it so the engine emits
    /// <c>vm.agents.changed</c> right away (R10). Best-effort: the guest's timer reports later if this call cannot reach it.
    /// </summary>
    private async Task<bool> RefreshAgentsAsync(Vm vm, SshTarget target, StoredDesiredState? desired, IProgress<string> progress)
    {
        IReadOnlyList<VmAgentVersion>? reported = null;
        try
        {
            var lines = new List<string>();
            var sink = new LineProgress(line => lines.Add(ProvisionScript.CleanLine(line)));
            var command = ProvisionScript.AsRoot(ProvisionScript.AgentReportCommand, target.IsRoot, sudoPasswordOnStdin: false);
            var exit = await ssh.RunAsync(target, command, null, sink, ReportTimeout, CancellationToken.None);
            if (exit == 0) reported = VmAgents.Parse(string.Join('\n', lines));
        }
        catch (Exception ex) when (ex is SshRunnerException or OperationCanceledException)
        {
            // Unreachable or timed out: the guest's timer reports later.
        }

        if (reported is null) return false;
        await agents.SaveAsync(vm, reported, clock.UtcNow, CancellationToken.None, VmAgents.Chosen(desired?.Desired), busy: false);
        return true;
    }

    private static (string Code, string Message) Failure(SshTarget target, string display, int exit, List<string> lines)
    {
        if (!target.IsRoot && lines.Any(l => l.Contains("sudo: a password is required", StringComparison.Ordinal)))
            return ("secret-missing", $"{ProvisionJob.SudoPasswordSecret}: {target.User} needs a password for sudo on this guest.");
        if (exit == AgentUpdateScript.ReprovisionRequiredExit)
            return ("reprovision-required", "This VM needs one reprovision before single-agent updates can run.");
        if (exit == 255)
            return ("ssh-failed", $"the guest could not be reached over SSH as {target.User}@{target.Host}:{target.Port}.");
        return ("agent-update-failed", $"{display} update failed (exit {exit}).");
    }

    private static void Report(IProgress<string> progress, string step, string state, string line)
    {
        if (progress is IJobStepProgress steps) steps.Report(new JobStepProgress(step, state, line));
        else progress.Report($"{step}: {state}: {line}");
    }

    private sealed class LineProgress(Action<string> onLine) : IProgress<string>
    {
        public void Report(string value) => onLine(value);
    }
}

/// <summary>
/// Accepts <see cref="JobKinds.UpdateAgents"/> jobs: resolves the agents to update, takes the VM's operation gate and
/// maintenance gate, re-verifies the VM under the gate (incarnation, owner, kind, deleting), starts a stopped VM first
/// (user-asked rule, design 12.1), and runs <see cref="AgentUpdateJob"/>.
/// </summary>
public sealed class AgentUpdateJobs(IVmRepository vms, IJobStore jobs, IPersistedJobRunner runner, IVmOperationGate gates,
    IMaintenanceGate maintenance, IProvisionTargets targets, ProvisionStart starter, AgentUpdateJob work,
    VmAgents agents, IAgentReleases releases, IVmDesiredStateSource desiredStates, IClock clock)
{
    /// <summary>The reported agents on <paramref name="vm"/> whose latest known release is newer than installed.</summary>
    public async Task<(IReadOnlyList<string> Behind, bool Known)> BehindAsync(Vm vm, CancellationToken ct)
    {
        var report = await agents.GetAsync(vm, ct);
        if (report is null || report.Agents.Count == 0) return ([], false);
        var channel = (await desiredStates.GetDesiredForAsync(vm, ct))?.Desired.Settings.T3CodeChannel;
        var behind = new List<string>();
        var anyKnown = false;
        foreach (var agent in report.Agents)
        {
            if (!AgentUpdateScript.IsKnownTool(agent.Tool)) continue;
            var latest = await releases.LatestAsync(agent.Tool, agent.Tool == "t3" ? channel : null, ct);
            if (!string.IsNullOrWhiteSpace(latest)) anyKnown = true;
            if (VmAgents.IsNewer(latest, agent.Version)) behind.Add(agent.Tool);
        }

        return (AgentUpdateScript.Order(behind), anyKnown);
    }

    /// <summary>
    /// Starts a dedicated <c>update-agents</c> job for the validated <paramref name="tools"/> (or all agents currently
    /// behind when <paramref name="tools"/> is null). Acquires the VM gate before reloading and re-verifying the VM
    /// snapshot so a concurrent deletion or rename cannot redirect the update to another VM.
    /// </summary>
    public async Task<(Job Job, IReadOnlyList<string> Agents)> StartAsync(Vm authorized, string actor, IReadOnlyList<string>? tools, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("n");
        var operation = await gates.TryAcquireAsync(authorized.Name, id, ct);
        if (operation is null)
        {
            gates.IsHeld(authorized.Name, out var holder);
            throw new ProvisionRequestException(409, "operation-in-progress", "Another operation runs on this VM.", holder);
        }

        IDisposable? handle = new GateHandle(operation, null);
        try
        {
            var vm = await vms.GetAsync(authorized.Name, ct)
                ?? throw new ProvisionRequestException(404, "not-found", "Unknown VM.");
            if (vm.Incarnation != authorized.Incarnation)
                throw new ProvisionRequestException(409, "vm-changed", "This VM was replaced; reload and try again.");
            if (!Ownership.SameName(vm.Owner, authorized.Owner))
                throw new ProvisionRequestException(403, "not-owner", "Only the VM's owner can update its coding agents.");
            if (vm.Kind != VmKind.Primary)
                throw new ProvisionRequestException(409, "not-a-primary", "Only primary VMs can update coding agents.");
            if (vm.Deleting)
                throw new ProvisionRequestException(409, "vm-deleting", "This VM is being deleted.");
            if (vm.CurrentJobId is { } current && await jobs.GetAsync(current, ct) is { State: JobState.Queued or JobState.Running })
                throw new ProvisionRequestException(409, "operation-in-progress", "Another job runs on this VM.", current);

            IReadOnlyList<string> ordered;
            if (tools is not null)
            {
                ordered = AgentUpdateScript.Order(tools);
                if (ordered.Count == 0)
                    throw new ProvisionRequestException(400, "invalid-agents", "Expected one or more coding agents.");
            }
            else
            {
                var (behind, known) = await BehindAsync(vm, ct);
                if (behind.Count == 0)
                {
                    if (!known)
                        throw new ProvisionRequestException(409, "agents-unknown", "Coding agent versions or latest releases are not known yet.");
                    throw new ProvisionRequestException(409, "agents-up-to-date", "All reported coding agents are already up to date.");
                }

                ordered = behind;
            }

            var startFirst = await starter.StoppedAsync(vm, ct);
            var target = startFirst ? null : await targets.ResolveAsync(vm, ct)
                ?? throw new ProvisionRequestException(409, "vm-endpoint-unknown", "The VM has no SSH endpoint the engine can dial.");

            var entered = maintenance.TryEnter("mutation:" + JobKinds.UpdateAgents, id, vm.Name)
                ?? throw new ProvisionRequestException(503, "maintenance", "The host is in maintenance.");
            handle = new GateHandle(operation, entered);

            // Persist the requested agent list on the initial row so a retry after an engine restart keeps the exact scope.
            var initial = new AgentUpdateJobResult(ordered, [], false);
            var queued = new Job(id, JobKinds.UpdateAgents, vm.Name, vm.Owner, JobState.Queued, [], initial, null, clock.UtcNow, null,
                actor, null, startFirst ? ProvisionStart.Step : ordered[0]);
            await jobs.UpsertAsync(queued, ct);

            await runner.StartPersistedAsync(queued, handle, async (progress, token) =>
            {
                var dial = target;
                AgentUpdateJobResult FailedStart(string code, string message) =>
                    new(ordered, [], false, ProvisionStart.Step, code, message);
                if (startFirst) dial = await starter.RunAsync(id, vm, FailedStart, progress, token);
                else dial = await starter.AnswerAsync(id, vm, dial!, FailedStart, progress, token);
                return await work.RunAsync(id, vm, dial!, ordered, progress, token);
            }, CancellationToken.None);
            handle = null;
            return (queued, ordered);
        }
        finally { handle?.Dispose(); }
    }

    private sealed class GateHandle(IAsyncDisposable operation, IDisposable? maintenance) : IDisposable
    {
        public void Dispose()
        {
            try { maintenance?.Dispose(); }
            finally { operation.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
    }
}
