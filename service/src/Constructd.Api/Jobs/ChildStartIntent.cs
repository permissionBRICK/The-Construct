using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Jobs;

/// <summary>Runs under the child VM gate; persists the activation clock before any boot.</summary>
public sealed class ChildStartIntent(IOperationKeyStore keys, IAdmissionStore admission,
    ICapacityLedger capacity, IHypervisorDriver hypervisor, IVmRepository vms, IClock clock)
{
    public static readonly JsonSerializerOptions IntentJsonOptions = new(JsonSerializerDefaults.Web);
    public sealed record Intent(long? Lifetime, long ExpectedLeaseVersion, DateTimeOffset ActivationBase,
        IReadOnlyList<ReservationLine> Lines);

    public async Task<Vm> RunAsync(Job job, Vm vm, IReadOnlyList<string> reservationIds, CancellationToken ct)
    {
        var snapshot = await capacity.SnapshotAsync(false, ct);
        var original = new Intent(vm.Lease!.RequestedSeconds, vm.Lease.Version, clock.UtcNow,
            snapshot.Reservations.Where(r => reservationIds.Contains(r.Id)).Select(r => new ReservationLine(r.Resource, r.Amount, r.Artifact, r.Volume)).ToArray());
        var proposed = new OperationKeyRecord(vm.Owner, "child-start", job.Id + ":start", job.Id, vm.Name, job.Id,
            OperationKeyState.InFlight, JsonSerializer.Serialize(original, IntentJsonOptions), vm.PowerGeneration, null, clock.UtcNow);
        var inserted = await keys.TryInsertAsync(proposed, ct);
        if (inserted.Outcome == OperationKeyOutcome.Conflict) throw Error("operation-key-conflict");
        var key = inserted.Existing ?? proposed;
        var intent = JsonSerializer.Deserialize<Intent>(key.IntentJson!, IntentJsonOptions)!;
        vm = await vms.GetAsync(vm.Name, ct) ?? throw Error("vm-deleting");
        if (key.State == OperationKeyState.Completed)
        {
            var error = JsonSerializer.Deserialize<string>(key.ResponseJson!);
            if (error is not null) throw Error(error);
            return vm;
        }
        if (vm.PowerGeneration != key.PowerGeneration) throw Error("power-state-changed");
        var state = await hypervisor.GetStateAsync(vm.Name, ct);
        if (state is not (VmState.Running or VmState.Off or VmState.Saved)) throw Error("vm-state-unknown");
        var holds = snapshot.Reservations.Where(r => r.VmName == vm.Name && (r.OperationId == job.Id || r.OperationId?.StartsWith(job.Id + ":resume:", StringComparison.Ordinal) == true)).ToArray();
        var ids = holds.Select(r => r.Id).ToArray();
        if (state != VmState.Running)
        {
            if (intent.Lifetime is long duration && intent.ActivationBase.AddSeconds(duration) <= clock.UtcNow)
            {
                await admission.MutateAsync(key, async scope =>
                {
                    await scope.ReleaseReservationsAsync(ids, state, "start intent expired");
                    return await scope.CompleteOperationKeyAsync(key.Owner, key.Kind, key.Key, JsonSerializer.Serialize("intent-expired"));
                }, ct);
                throw Error("intent-expired");
            }
            var missing = intent.Lines.Where(line => !holds.Any(r => r.Resource == line.Resource && r.Artifact == line.Artifact && r.Volume == line.Volume && r.Amount == line.Amount)).ToArray();
            if (missing.Length > 0)
            {
                CapacityDecision? decision = null;
                var readmitted = await admission.MutateAsync(key, async scope =>
                {
                    decision = await scope.ReserveAsync(new(vm.Owner, vm.Name, job.Id + ":resume:" + Guid.NewGuid().ToString("n"), missing, TimeSpan.FromHours(2)));
                    if (!decision.Allowed) return await scope.CompleteOperationKeyAsync(key.Owner, key.Kind, key.Key,
                        JsonSerializer.Serialize(decision.Reason == "inventory-incomplete" ? "capacity-unavailable" : "capacity-exhausted"));
                    return true;
                }, ct);
                if (readmitted.Outcome != AdmissionOutcome.Accepted) throw Error("operation-key-conflict");
                if (!decision!.Allowed) throw Error(decision.Reason == "inventory-incomplete" ? "capacity-unavailable" : "capacity-exhausted");
                ids = ids.Concat(decision.ReservationIds).ToArray();
            }
            await hypervisor.StartAsync(vm.Name, ct);
            state = await hypervisor.GetStateAsync(vm.Name, ct);
            for (var attempt = 0; state == VmState.Unknown && attempt < 60; attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), ct);
                state = await hypervisor.GetStateAsync(vm.Name, ct);
            }
            if (state != VmState.Running) throw Error("start-failed");
        }
        // Running recovery needs real reservation evidence too; it must not confirm swept IDs.
        if (!intent.Lines.All(line => holds.Any(r => r.Resource == line.Resource && r.Artifact == line.Artifact && r.Volume == line.Volume && r.Amount == line.Amount)) && inserted.Outcome == OperationKeyOutcome.Replay && state == VmState.Running && ids.Length == holds.Length)
            throw Error("capacity-unavailable");
        var lease = vm.Lease! with { ActivatedAt = intent.ActivationBase,
            ExpiresAt = intent.Lifetime is long seconds ? intent.ActivationBase.AddSeconds(seconds) : null,
            State = intent.Lifetime is null ? LeaseState.Unlimited : LeaseState.Active, Version = intent.ExpectedLeaseVersion + 1 };
        var completed = await admission.MutateAsync(key, async scope =>
        {
            if (!await scope.BumpPowerGenerationAsync(vm.Name, key.PowerGeneration!.Value) ||
                !await scope.UpdateLeaseAsync(vm.Name, lease, intent.ExpectedLeaseVersion)) return false;
            await scope.ConfirmReservationsAsync(ids, VmState.Running);
            return await scope.CompleteOperationKeyAsync(key.Owner, key.Kind, key.Key, "null");
        }, ct);
        if (completed.Outcome is not (AdmissionOutcome.Accepted or AdmissionOutcome.Replay)) throw Error("power-state-changed");
        return (await vms.GetAsync(vm.Name, ct))! with { State = VmState.Running };
    }
    private static ChildValidationException Error(string code) => new(code, "start");
}
