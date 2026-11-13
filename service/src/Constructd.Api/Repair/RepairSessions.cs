using System.Collections.Concurrent;
using System.Text.Json;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Services;

namespace Constructd.Api.Repair;

/// <summary>Where a repair session is.</summary>
public enum RepairState
{
    /// <summary>The agent works.</summary>
    Working,
    /// <summary>The agent finished a turn without fixing the cause and waits for a reply.</summary>
    Waiting,
    /// <summary>Over; <see cref="RepairSession.Outcome"/> says how.</summary>
    Ended,
}

/// <summary>
/// One repair agent run on a VM (design 11.5). Its output lives only here, in memory: never in the
/// database or a log, since it can hold whatever the VM holds. An engine restart ends it.
/// </summary>
public sealed class RepairSession
{
    internal RepairSession(string id, Vm vm, Job failed, bool automatic, DateTimeOffset started)
    {
        Id = id; Vm = vm.Name; Owner = vm.Owner; VmCreated = vm.Created; JobId = failed.Id; JobKind = failed.Kind;
        Automatic = automatic; Started = started;
    }

    internal readonly Lock Gate = new();
    internal readonly LinkedList<RepairLine> Lines = new();
    internal long LastSeq;
    internal IGuestSession? Guest;
    internal readonly CancellationTokenSource Ending = new();
    internal bool Retrying;
    internal SshTarget? Target;
    internal string? EndRequested;
    internal DateTimeOffset LastAnnounced;
    internal bool AnnouncePending;

    public string Id { get; }
    public string Vm { get; }
    public string Owner { get; }
    public DateTimeOffset VmCreated { get; }
    public string JobId { get; }
    public string JobKind { get; }
    public bool Automatic { get; }
    public DateTimeOffset Started { get; }
    public RepairState State { get; internal set; } = RepairState.Working;
    public DateTimeOffset? Ended { get; internal set; }

    /// <summary>
    /// <c>fixed</c> (the agent fixed the cause), <c>stopped</c> (the user ended it), <c>superseded</c> (a job
    /// on the VM ended it), <c>error</c> (the agent or the connection failed); null while it runs.
    /// </summary>
    public string? Outcome { get; internal set; }

    public RepairAgentInfo? Agent { get; internal set; }

    /// <summary>The retry the engine started after a fix, if any.</summary>
    public string? RetryJobId { get; internal set; }

    /// <summary>Why there was no retry after a fix (a restore needs its key again, the job was superseded).</summary>
    public string? RetryRefused { get; internal set; }

    /// <summary>The lines after <paramref name="after"/>, oldest first.</summary>
    public IReadOnlyList<RepairLine> LinesAfter(long after)
    {
        lock (Gate) return Lines.Where(l => l.Seq > after).ToArray();
    }
}

/// <summary>Why a repair could not start.</summary>
public sealed class RepairRefusedException(int status, string code, string message, string? sessionId = null) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public string? SessionId { get; } = sessionId;
}

/// <summary>
/// The repair sessions, one at a time per VM (design 11.5, S26). A session starts when a
/// <c>provision</c>, <c>reinstall</c> or <c>restore</c> job fails with failed guest steps (unless the host's
/// <c>repair.auto</c> is off) or by request; it runs <c>bin/construct-repair-agent.sh</c> on the VM over SSH,
/// keeps its last <see cref="RepairScript.MaxLines"/> lines, and announces itself on the change stream
/// (<c>repair.started</c>, <c>repair.output</c> at most once a second, <c>repair.ended</c>). A job that takes
/// the VM's operation gate ends the session first (<see cref="RepairAwareVmOperationGate"/>), so a repair and
/// a lifecycle job never run on one VM together. When the agent ends saying it fixed the cause, the failed job
/// is retried once; a retry that fails again starts no repair.
/// </summary>
public sealed class RepairSessions(IServiceProvider services, InMemoryVmOperationGate gates, IGuestSessionRunner guests,
    ISshRunner ssh, IProvisionTargets targets, IJobStore jobs, IJobQueryStore history, IVmRepository vms, IHostConfigStore config,
    IClock clock, ChangeFeed changes, ILogger<RepairSessions> logger)
{
    /// <summary>How long an automatic start waits for the failed job to let go of the VM's gate.</summary>
    public static TimeSpan GateWait { get; set; } = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(1);

    // VM name -> its latest session (ended ones stay readable until the next starts).
    private readonly ConcurrentDictionary<string, RepairSession> _byVm = new(StringComparer.OrdinalIgnoreCase);

    // Failed jobs the engine retried after a fix: the retry's failure starts no repair.
    private readonly ConcurrentDictionary<string, byte> _retried = new(StringComparer.Ordinal);

    /// <summary>How long a repair asked for by hand waits for a short operation (a reconcile pass) on the VM.</summary>
    private static readonly TimeSpan ManualGateWait = TimeSpan.FromSeconds(5);

    public RepairSession? Latest(string vm) => _byVm.TryGetValue(vm, out var session) ? session : null;

    /// <summary>The session <paramref name="id"/> of VM <paramref name="vm"/>, and only of that VM.</summary>
    public RepairSession? Find(string vm, string id) =>
        Latest(vm) is { } session && string.Equals(session.Id, id, StringComparison.Ordinal) ? session : null;

    /// <summary>Whether a job's failure may start a repair by itself.</summary>
    public async Task<bool> AutomaticAsync(CancellationToken ct) =>
        (await config.GetAsync<RepairConfig>("repair", ct) ?? HostAdminDefaults.Repair).Auto;

    /// <summary>Called for each job that ended: starts the repair of a failed lifecycle job when the host allows it.</summary>
    public async Task OnJobEndedAsync(string jobId, CancellationToken ct)
    {
        var job = await jobs.GetAsync(jobId, ct);
        if (job is not { State: JobState.Failed, VmName: { } name } || !JobRetryRules.IsLifecycleKind(job.Kind)) return;
        // The retry after a fix carries the failed job's id in its request id (one retry per failed job).
        if (job.OperationKey is { } key && key.StartsWith("retry:", StringComparison.Ordinal) && _retried.ContainsKey(key["retry:".Length..])) return;
        if (Prompt(job) is null || !await AutomaticAsync(ct)) return;
        if (await vms.GetAsync(name, ct) is not { Kind: VmKind.Primary } vm) return;
        try { await StartAsync(vm, job, automatic: true, ct); }
        catch (RepairRefusedException ex) { logger.LogInformation("No repair for job {Job}: {Code}.", job.Id, ex.Code); }
    }

    /// <summary>The VM's latest failed lifecycle job, for a repair asked for by hand.</summary>
    public async Task<Job> LatestFailedAsync(Vm vm, CancellationToken ct)
    {
        var latest = (await history.ListAsync(ct))
            .Where(j => JobRetryRules.IsLifecycleKind(j.Kind) && Ownership.SameName(j.VmName, vm.Name) && Ownership.SameName(j.Owner, vm.Owner) && j.Created >= vm.Created)
            .OrderByDescending(j => j.Created).FirstOrDefault();
        return latest is { State: JobState.Failed } ? await jobs.GetAsync(latest.Id, ct) ?? latest
            : throw new RepairRefusedException(409, "no-failed-job", "The VM's latest provision, reinstall or restore did not fail.");
    }

    /// <summary>
    /// Starts a repair of <paramref name="failed"/> on <paramref name="vm"/>. The failed job must be the VM's
    /// latest lifecycle job and no job may run on the VM; the VM's gate is held while the session is set up.
    /// </summary>
    public async Task<RepairSession> StartAsync(Vm vm, Job failed, bool automatic, CancellationToken ct)
    {
        if (Latest(vm.Name) is { State: not RepairState.Ended } running)
            throw new RepairRefusedException(409, "repair-running", "A repair already runs on this VM.", running.Id);
        var prompt = Prompt(failed) ?? throw new RepairRefusedException(409, "not-repairable",
            "The job did not fail in a step an agent on the VM can repair.");
        var target = await targets.ResolveAsync(vm, ct)
            ?? throw new RepairRefusedException(409, "vm-endpoint-unknown", "The VM has no SSH endpoint the engine can dial.");

        var id = Guid.NewGuid().ToString("n");
        IAsyncDisposable? gate;
        // An automatic start waits for the failed job to let go of the gate, right after it ended.
        using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            wait.CancelAfter(automatic ? GateWait : ManualGateWait);
            try { gate = await gates.AcquireAsync(vm.Name, "repair:" + id, wait.Token); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { gate = null; }
        }
        if (gate is null) throw new RepairRefusedException(409, "operation-in-progress", "Another operation runs on this VM.");

        await using (gate)
        {
            // Under the gate no job starts on the VM: check that none runs and none came after the failed one.
            var current = await vms.GetAsync(vm.Name, ct);
            if (current is null || current.Created != vm.Created || current.Deleting)
                throw new RepairRefusedException(409, "vm-changed", "The VM this job ran on no longer exists.");
            var all = await history.ListAsync(ct);
            if (all.Any(j => Ownership.SameName(j.VmName, vm.Name) && j.State is JobState.Queued or JobState.Running))
                throw new RepairRefusedException(409, "operation-in-progress", "A job runs on this VM.");
            if (JobRetryRules.Refusal(failed, current, all) is { } refused)
                throw new RepairRefusedException(refused.Status, refused.Code == "job-superseded" ? "job-superseded" : refused.Code, refused.Message);
            if (Latest(vm.Name) is { State: not RepairState.Ended } other)
                throw new RepairRefusedException(409, "repair-running", "A repair already runs on this VM.", other.Id);

            var session = new RepairSession(id, current, failed, automatic, clock.UtcNow) { Target = target };
            _byVm[vm.Name] = session;
            changes.Publish("repair.started", session.Id, session.Vm, session.Owner);
            var rebooted = RepairScript.Rebooted(failed.Result is null ? null : JsonSerializer.SerializeToElement(failed.Result, ApiJson.Options));
            _ = Task.Run(() => RunAsync(session, RepairScript.StartCommand(id, prompt, target.IsRoot), rebooted), CancellationToken.None);
            return session;
        }
    }

    /// <summary>How long a repair waits for a VM that the failed job rebooted.</summary>
    public static TimeSpan RebootWait { get; set; } = TimeSpan.FromMinutes(5);

    private async Task RunAsync(RepairSession session, string command, bool rebooted)
    {
        var target = session.Target!;
        if (rebooted && !await WaitForGuestAsync(session, target))
        {
            Finish(session, session.EndRequested ?? "error");
            return;
        }

        IGuestSession guest;
        lock (session.Gate)
        {
            // A job or the user may have ended the session while it waited.
            if (session.EndRequested is not null) guest = null!;
            else
            {
                try { guest = session.Guest = guests.Start(target, command, line => OnLine(session, line)); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    session.EndRequested = "error";
                    guest = null!;
                    Append(session, new RepairLine(0, clock.UtcNow, "log", "The repair agent could not be started: " + SafeError.Describe(ex)));
                }
            }
        }

        if (guest is null)
        {
            Finish(session, session.EndRequested ?? "error");
            return;
        }

        await FollowAsync(session, guest);
    }

    /// <summary>Waits until the VM answers over SSH again; false when it does not in time or the session was ended.</summary>
    private async Task<bool> WaitForGuestAsync(RepairSession session, SshTarget target)
    {
        Append(session, new RepairLine(0, clock.UtcNow, "note", "The VM restarts after the failed run; the repair starts when it is back."));
        var until = DateTimeOffset.UtcNow + RebootWait;
        // The job's own reboot comes a few seconds after it ended.
        await Task.Delay(RebootProbeDelay, session.Ending.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        while (!session.Ending.IsCancellationRequested && DateTimeOffset.UtcNow < until)
        {
            try
            {
                if (await ssh.RunAsync(target, "true", null, null, TimeSpan.FromSeconds(30), session.Ending.Token) == 0) return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
            catch (OperationCanceledException) { return false; }
            await Task.Delay(RebootProbeDelay, session.Ending.Token).ContinueWith(_ => { }, TaskScheduler.Default);
        }

        if (!session.Ending.IsCancellationRequested)
            Append(session, new RepairLine(0, clock.UtcNow, "log", "The VM did not answer over SSH after its restart."));
        return false;
    }

    /// <summary>The pause before and between the probes for a VM coming back from its reboot.</summary>
    public static TimeSpan RebootProbeDelay { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Sends a reply to the agent. False when the session is over.</summary>
    public async Task<bool> ReplyAsync(RepairSession session, string text, CancellationToken ct)
    {
        IGuestSession? guest;
        lock (session.Gate)
        {
            if (session.State == RepairState.Ended || session.EndRequested is not null) return false;
            guest = session.Guest;
        }

        if (guest is null) return false;
        try { await guest.WriteLineAsync(RepairScript.ReplyLine(text), ct); }
        catch (IOException) { return false; }
        catch (ObjectDisposedException) { return false; }
        lock (session.Gate)
        {
            if (session.State == RepairState.Waiting) session.State = RepairState.Working;
        }

        Append(session, new RepairLine(0, clock.UtcNow, "reply", text));
        return true;
    }

    /// <summary>Ends a session (the user's <c>DELETE</c>, or a job on the VM) and waits until its processes on the VM are gone.</summary>
    public async Task EndAsync(RepairSession session, string outcome)
    {
        IGuestSession? guest;
        lock (session.Gate)
        {
            // Over, or over and retrying its job (whose start comes through here): nothing left to end.
            if (session.State == RepairState.Ended || session.Retrying) return;
            session.EndRequested ??= outcome;
            guest = session.Guest;
        }

        session.Ending.Cancel();

        if (guest is not null)
        {
            await guest.DisposeAsync();
            await guest.Exited.ContinueWith(_ => { }, TaskScheduler.Default);
            await StopOnGuestAsync(session);
        }

        // FollowAsync finishes the session when the connection ends; this covers a session that never had one.
        Finish(session, session.EndRequested ?? outcome);
    }

    /// <summary>Ends the VM's running session, if any, because a job takes the VM.</summary>
    public async Task EndForJobAsync(string vm)
    {
        if (Latest(vm) is { State: not RepairState.Ended } session)
        {
            try { await EndAsync(session, "superseded"); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Ending the repair on {Vm} failed: {Error}", vm, SafeError.Describe(ex));
            }
        }
    }

    /// <summary>Ends every running session (the engine stops).</summary>
    public Task EndAllAsync() =>
        Task.WhenAll(_byVm.Values.Where(s => s.State != RepairState.Ended).Select(s => EndForJobAsync(s.Vm)));

    private void OnLine(RepairSession session, string raw)
    {
        var line = Append(session, RepairScript.Parse(raw, 0, clock.UtcNow));
        lock (session.Gate)
        {
            if (line.Kind == "start" && RepairScript.AgentInfo(raw) is { } agent) session.Agent = agent;
            if (line.Kind == "turn" && session.State == RepairState.Working && line.Outcome != "fixed") session.State = RepairState.Waiting;
        }
    }

    private async Task FollowAsync(RepairSession session, IGuestSession guest)
    {
        try { await guest.Exited; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogInformation("The repair connection to {Vm} ended: {Error}", session.Vm, SafeError.Describe(ex));
        }

        string outcome;
        bool requested;
        lock (session.Gate)
        {
            var end = session.Lines.LastOrDefault(l => l.Kind == "end");
            requested = session.EndRequested is not null;
            outcome = session.EndRequested ?? (end?.Outcome is "fixed" ? "fixed" : end?.Outcome is "stopped" ? "stopped" : "error");
        }

        if (!requested)
        {
            await guest.DisposeAsync();
            // The connection may have gone while the agent still runs: make sure it is gone too.
            await StopOnGuestAsync(session);
        }

        if (outcome == "fixed")
        {
            lock (session.Gate)
            {
                // Ended by a job or the user meanwhile: no retry.
                if (session.EndRequested is null) session.Retrying = true;
            }

            if (session.Retrying) await RetryAsync(session);
            else outcome = session.EndRequested!;
        }

        Finish(session, outcome);
    }

    /// <summary>Retries the failed job once, by the retry route's rules, as the job's own initiator.</summary>
    private async Task RetryAsync(RepairSession session)
    {
        try
        {
            var failed = (await history.ListAsync(CancellationToken.None)).FirstOrDefault(j => j.Id == session.JobId);
            var vm = await vms.GetAsync(session.Vm, CancellationToken.None);
            if (failed is null || vm is null || vm.Created != session.VmCreated)
            {
                Refuse(session, "The VM changed; the job is not retried.");
                return;
            }

            if (failed.Kind == JobKinds.Restore)
            {
                Refuse(session, "A restore needs its backup's key again: retry it from a device.");
                return;
            }

            if (JobRetryRules.Refusal(failed, vm, await history.ListAsync(CancellationToken.None)) is { } refused)
            {
                Refuse(session, refused.Message);
                return;
            }

            var provisioning = services.GetRequiredService<ProvisionJobs>();
            var requestId = JobRetryRules.RequestId(failed);
            // Marked before it starts: its failure must not start another repair.
            _retried[failed.Id] = 0;
            var (job, _) = await provisioning.SubmitAsync(vm, failed.Initiator ?? failed.Owner,
                new ProvisionSubmission(requestId, null, null, false, new JobSecretBag(), failed.Kind), CancellationToken.None);
            lock (session.Gate) session.RetryJobId = job.Id;
            Append(session, new RepairLine(0, clock.UtcNow, "retry", $"The {failed.Kind} runs again (job {job.Id})."));
        }
        catch (ProvisionRequestException ex)
        {
            Refuse(session, ex.Message);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Refuse(session, "The job could not be retried: " + SafeError.Describe(ex));
        }
    }

    private void Refuse(RepairSession session, string why)
    {
        lock (session.Gate) session.RetryRefused = why;
        Append(session, new RepairLine(0, clock.UtcNow, "retry", why));
    }

    private async Task StopOnGuestAsync(RepairSession session)
    {
        if (session.Target is not { } target) return;
        try
        {
            await ssh.RunAsync(target, RepairScript.StopCommand(session.Id, target.IsRoot), null, null, StopTimeout, CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Stopping the repair agent on {Vm} failed: {Error}", session.Vm, SafeError.Describe(ex));
        }
    }

    private void Finish(RepairSession session, string outcome)
    {
        lock (session.Gate)
        {
            if (session.State == RepairState.Ended) return;
            session.State = RepairState.Ended;
            session.Outcome = outcome;
            session.Ended = clock.UtcNow;
            session.Guest = null;
        }

        changes.Publish("repair.ended", session.Id, session.Vm, session.Owner);
    }

    private RepairLine Append(RepairSession session, RepairLine line)
    {
        bool announce;
        lock (session.Gate)
        {
            line = line with { Seq = ++session.LastSeq };
            session.Lines.AddLast(line);
            while (session.Lines.Count > RepairScript.MaxLines) session.Lines.RemoveFirst();
            var now = clock.UtcNow;
            announce = now - session.LastAnnounced >= AnnounceInterval;
            if (announce) session.LastAnnounced = now;
            else if (!session.AnnouncePending)
            {
                session.AnnouncePending = true;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(AnnounceInterval);
                    lock (session.Gate) { session.AnnouncePending = false; session.LastAnnounced = clock.UtcNow; }
                    changes.Publish("repair.output", session.Id, session.Vm, session.Owner);
                });
            }
        }

        if (announce) changes.Publish("repair.output", session.Id, session.Vm, session.Owner);
        return line;
    }

    /// <summary>The job's part of the prompt, or null when an agent on the VM cannot repair it.</summary>
    public static string? Prompt(Job job)
    {
        JsonElement? result = job.Result is null ? null : JsonSerializer.SerializeToElement(job.Result, ApiJson.Options);
        return RepairScript.JobPrompt(job.Kind, result, job.Progress);
    }
}
