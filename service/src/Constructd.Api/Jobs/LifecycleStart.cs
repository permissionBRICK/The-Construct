using System.Text.Json;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Jobs;

/// <summary>Caller holds the VM gate. An accepted start keeps its original clock across retries.</summary>
public sealed class LifecycleStart(IVmRepository vms, IHypervisorDriver driver, IChildVmStorage storage,
    ICapacityLedger capacity, IAdmissionStore admission, IOperationKeyStore keys, IClock clock, ChildStartIntent childStarts, IJobStore jobs, IOperationRegistry operations, PrimaryCpuSettings cpuSettings)
{
    public sealed record Intent(string? Lifetime, long? Seconds, long LeaseVersion, DateTimeOffset ActivationBase,
        IReadOnlyList<ReservationLine> Lines, string OperationId, bool Restart = false);
    public sealed record Reply(VmState State, Lease? Lease, string? Code = null);
    public async Task<Reply> RunAsync(Vm vm, string? lifetime, long? seconds, OperationKeyRecord proposed, CancellationToken ct)
    {
        if ((await keys.ListInFlightAsync(vm.Name, ct)).Any(k => ConfigurationIntent.Applies(k, vm))) throw new LifecycleException("configuration-incomplete");
        var key = await keys.GetAsync(proposed.Owner, proposed.Kind, proposed.Key, ct);
        if (key is not null && (key.Fingerprint != proposed.Fingerprint || !Ownership.SameName(key.Target, vm.Name)))
            throw new LifecycleException("operation-key-conflict");
        if (key?.State == OperationKeyState.Completed)
            return JsonSerializer.Deserialize<Reply>(key.ResponseJson!, ApiJson.Options)!;
        var state = await driver.GetStateAsync(vm.Name, ct);
        // Only before a new start intent: a recovered intent must keep the hardware it reserved.
        if (key is null) vm = await cpuSettings.ApplyAsync(vm, state, ct);
        if (key is null)
        {
            foreach (var pending in (await keys.ListInFlightAsync(vm.Name, ct)).Where(k => k.PowerGeneration == vm.PowerGeneration && k.Kind is "lifecycle-start" or "restart-start" or "child-start"))
            {
                if (state == VmState.Unknown) throw new LifecycleException("vm-state-unknown");
                var held = (await capacity.SnapshotAsync(false, ct)).Reservations.Where(r => Ownership.SameName(r.VmName, vm.Name)).ToArray();
                if (state == VmState.Running)
                {
                    if (pending.Kind == "child-start" && pending.JobId is not null && await jobs.GetAsync(pending.JobId, ct) is { } original)
                        await childStarts.RunAsync(original, vm, held.Select(r => r.Id).ToArray(), ct);
                    else await CompleteAsync(vm, pending, state, ct);
                    throw new LifecycleException("already-running");
                }
                if (state is not (VmState.Off or VmState.Saved or VmState.Paused)) throw new LifecycleException("vm-state-unknown");
                var operation = pending.Kind == "child-start" ? pending.JobId : JsonSerializer.Deserialize<Intent>(pending.IntentJson!, ApiJson.Options)!.OperationId;
                var superseded = await admission.MutateAsync(pending, async scope =>
                {
                    await scope.ReleaseReservationsAsync(held.Where(r => r.OperationId == operation || r.OperationId?.StartsWith(operation + ":resume:", StringComparison.Ordinal) == true).Select(r => r.Id).ToArray(), state, "superseded-start");
                    return await scope.CompleteOperationKeyAsync(pending.Owner, pending.Kind, pending.Key, pending.Kind == "child-start" ?
                        JsonSerializer.Serialize("superseded") : JsonSerializer.Serialize(new Reply(state, vm.Lease, "superseded"), ApiJson.Options));
                }, ct);
                if (superseded.Outcome != AdmissionOutcome.Accepted) throw new LifecycleException("operation-key-conflict");
            }
            if (state == VmState.Running) throw new LifecycleException("already-running");
            if (state is not (VmState.Off or VmState.Saved or VmState.Paused)) throw new LifecycleException("vm-state-unknown");
            var rows = (await capacity.SnapshotAsync(false, ct)).Reservations.Where(r => Ownership.SameName(r.VmName, vm.Name)).ToArray();
            var lines = new List<ReservationLine>();
            var restart = proposed.Kind == "restart-start";
            if (state != VmState.Paused)
            {
                lines.Add(new(ReservationResource.Ram, vm.RamBytes, null, null));
                lines.Add(new(ReservationResource.Cpu, vm.Cpu, null, null));
            }
            // The stopped VM's old save liability is released only on confirmed Off; reserve its next run.
            if (driver.Capabilities.Suspend && (state == VmState.Off || !rows.Any(ReservationRules.SavedState)))
            {
                ChildStoragePlacement? placement = null;
                try { placement = vm.Kind == VmKind.Primary ? await storage.ResolvePrimaryStorageAsync(vm.Name, ct) : await storage.ResolveStorageAsync(vm.Name, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch when (vm.Kind == VmKind.Primary) { }
                lines.Add(new(ReservationResource.Storage, vm.RamBytes + CapacityMath.SavedStateOverhead,
                    "saved-state:" + (vm.Incarnation ?? vm.Name), placement?.ConfigVolume ?? "unknown"));
            }
            var intent = new Intent(lifetime, seconds, vm.Lease?.Version ?? 0, clock.UtcNow, lines, Guid.NewGuid().ToString("n"), proposed.Kind == "restart-start");
            key = proposed with { IntentJson = JsonSerializer.Serialize(intent, ApiJson.Options), PowerGeneration = vm.PowerGeneration };
            CapacityDecision? decision = null;
            var accepted = await admission.MutateAsync(key, async scope =>
            {
                var current = await scope.ReadVmAsync(vm.Name);
                if (current is null || current.Deleting || current.PowerGeneration != vm.PowerGeneration) return false;
                if (state == VmState.Off && !restart)
                    await scope.ReleaseReservationsAsync(rows.Where(r => (r.Resource != ReservationResource.Storage || ReservationRules.SavedState(r))).Select(r => r.Id).ToArray(), state, "observed-off");
                var replaceCpu = state == VmState.Off && restart && rows.Where(r => r.Resource == ReservationResource.Cpu).Sum(r => r.Amount) != vm.Cpu;
                if (replaceCpu)
                    await scope.ReleaseReservationsAsync(rows.Where(r => r.Resource == ReservationResource.Cpu).Select(r => r.Id).ToArray(), state, "cpu-change");
                if (replaceCpu) rows = rows.Where(r => r.Resource != ReservationResource.Cpu).ToArray();
                var requested = restart ? lines.Where(line => rows.Where(r => Matches(r, line)).Sum(r => r.Amount) < line.Amount).ToArray() : lines.ToArray();
                decision = await scope.ReserveAsync(new(vm.Owner, vm.Name, intent.OperationId, requested, TimeSpan.FromMinutes(10)));
                return decision.Allowed;
            }, ct);
            if (decision is { Allowed: false }) throw new LifecycleException(decision.Reason == "inventory-incomplete" ? "capacity-unavailable" : "capacity-exhausted", decision);
            if (accepted.Outcome != AdmissionOutcome.Accepted) throw new LifecycleException("operation-key-conflict");
        }
        vm = await vms.GetAsync(vm.Name, ct) ?? throw new LifecycleException("vm-deleting");
        return await CompleteAsync(vm, key, state, ct);
    }

    private static bool Matches(Reservation row, ReservationLine line) => row.Resource == line.Resource &&
        (line.Artifact is null || string.Equals(row.Artifact, line.Artifact, StringComparison.OrdinalIgnoreCase) ||
         line.Artifact.StartsWith("saved-state:", StringComparison.OrdinalIgnoreCase) && ReservationRules.SavedState(row));

    public async Task<Reply> CompleteAsync(Vm vm, OperationKeyRecord key, VmState state, CancellationToken ct)
    {
        if ((await keys.ListInFlightAsync(vm.Name, ct)).Any(k => ConfigurationIntent.Applies(k, vm))) throw new LifecycleException("configuration-incomplete");
        var intent = JsonSerializer.Deserialize<Intent>(key.IntentJson!, ApiJson.Options)!;
        using var active = operations.Register(intent.OperationId, "lifecycle-start", vm.Name);
        if (vm.PowerGeneration != key.PowerGeneration) throw new LifecycleException("power-state-changed");
        if (state is not (VmState.Running or VmState.Off or VmState.Saved or VmState.Paused)) throw new LifecycleException("vm-state-unknown");
        var rows = (await capacity.SnapshotAsync(false, ct)).Reservations.Where(r => Ownership.SameName(r.VmName, vm.Name)).ToArray();
        if (state != VmState.Running)
        {
            if (vm.Kind == VmKind.Child && (intent.Restart ? LeaseRules.Due(vm.Lease, clock.UtcNow) || vm.Lease?.State == LeaseState.Overdue : intent.Seconds is long duration && intent.ActivationBase.AddSeconds(duration) <= clock.UtcNow))
            {
                var expired = new Reply(state, vm.Lease, intent.Restart ? "lease-due" : "intent-expired");
                await admission.MutateAsync(key, async scope =>
                {
                    await scope.ReleaseReservationsAsync(rows.Where(r => r.OperationId == intent.OperationId || r.OperationId?.StartsWith(intent.OperationId + ":resume:", StringComparison.Ordinal) == true).Select(r => r.Id).ToArray(), state, "intent-expired");
                    return await scope.CompleteOperationKeyAsync(key.Owner, key.Kind, key.Key, JsonSerializer.Serialize(expired, ApiJson.Options));
                }, ct);
                return expired;
            }
            var missing = intent.Lines.Where(line => rows.Where(r => Matches(r, line)).Sum(r => r.Amount) < line.Amount).ToArray();
            if (missing.Length > 0)
            {
                CapacityDecision? decision = null;
                var readmitted = await admission.MutateAsync(key, async scope =>
                {
                    decision = await scope.ReserveAsync(new(vm.Owner, vm.Name, intent.OperationId + ":resume:" + Guid.NewGuid().ToString("n"), missing, TimeSpan.FromMinutes(10)));
                    if (!decision.Allowed)
                    {
                        await scope.ReleaseReservationsAsync(rows.Where(r => r.OperationId == intent.OperationId ||
                            r.OperationId?.StartsWith(intent.OperationId + ":resume:", StringComparison.Ordinal) == true).Select(r => r.Id).ToArray(), state, "readmission-refused");
                        return await scope.CompleteOperationKeyAsync(key.Owner, key.Kind, key.Key,
                            JsonSerializer.Serialize(new Reply(state, vm.Lease, decision.Reason == "inventory-incomplete" ? "capacity-unavailable" : "capacity-exhausted"), ApiJson.Options));
                    }
                    return true;
                }, ct);
                if (readmitted.Outcome != AdmissionOutcome.Accepted) throw new LifecycleException("operation-key-conflict");
                if (!decision!.Allowed) return new(state, vm.Lease, decision.Reason == "inventory-incomplete" ? "capacity-unavailable" : "capacity-exhausted");
            }
            var failed = false;
            try { await driver.StartAsync(vm.Name, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { failed = true; }
            try { state = await driver.GetStateAsync(vm.Name, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { state = VmState.Unknown; }
            // The primary API returns the driver's observed state, as before host administration.
            var waiting = System.Diagnostics.Stopwatch.StartNew();
            while (!failed && vm.Kind == VmKind.Child && state == VmState.Unknown && waiting.Elapsed < TimeSpan.FromSeconds(30))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
                state = await driver.GetStateAsync(vm.Name, ct);
            }
            if (state != VmState.Running && (failed || vm.Kind == VmKind.Child || ReservationRules.Terminal(state)))
            {
                var failedReply = new Reply(state, vm.Lease, failed || vm.Kind == VmKind.Child ? "start-failed" : null);
                var held = (await capacity.SnapshotAsync(false, ct)).Reservations.Where(r => Ownership.SameName(r.VmName, vm.Name)).ToArray();
                await admission.MutateAsync(key, async scope =>
                {
                    // Off/Saved is direct evidence that this start consumes no RAM/CPU.
                    await scope.ReleaseReservationsAsync(held.Where(r => r.Resource != ReservationResource.Storage || ReservationRules.SavedState(r)).Select(r => r.Id).ToArray(), state, "start-finished-without-running");
                    await scope.UpdatePowerStateAsync(vm.Name, state, key.PowerGeneration!.Value);
                    return await scope.CompleteOperationKeyAsync(key.Owner, key.Kind, key.Key, JsonSerializer.Serialize(failedReply, ApiJson.Options));
                }, ct);
                return failedReply;
            }
        }
        rows = (await capacity.SnapshotAsync(false, ct)).Reservations.Where(r => Ownership.SameName(r.VmName, vm.Name)).ToArray();
        var lease = vm.Lease is null || intent.Restart ? null : LeaseRules.Activate(vm.Lease, intent.Lifetime!, intent.Seconds, intent.ActivationBase);
        var reply = new Reply(state, lease ?? vm.Lease);
        var completed = await admission.MutateAsync(key, async scope =>
        {
            if (!await scope.UpdatePowerStateAsync(vm.Name, state, key.PowerGeneration!.Value)) return false;
            if (lease is not null && !await scope.UpdateLeaseAsync(vm.Name, lease, intent.LeaseVersion)) return false;
            await scope.ConfirmReservationsAsync(rows.Select(r => r.Id).ToArray(), state);
            return await scope.CompleteOperationKeyAsync(key.Owner, key.Kind, key.Key, JsonSerializer.Serialize(reply, ApiJson.Options));
        }, ct);
        if (completed.Outcome is not (AdmissionOutcome.Accepted or AdmissionOutcome.Replay)) throw new LifecycleException("power-state-changed");
        return reply;
    }
}

public sealed class LifecycleException(string code, CapacityDecision? capacity = null) : Exception(code), IConstructdError
{
    public string Code { get; } = code;
    public CapacityDecision? Capacity { get; } = capacity;
}
