using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Configuration;
namespace Constructd.Fakes;

/// <summary>Deterministic capacity seam. Tests supply one inventory epoch; every write is serialized.</summary>
public sealed partial class InMemoryCapacityLedger(IClock clock) : ICapacityLedger
{
    private readonly Dictionary<string, Reservation> _reservations = new();
    public HostCapacitySnapshot Inventory { get; set; } = new(0, DateTimeOffset.MinValue, false, 0, 0, 0, 0, 0, 0, 0, null, 0, null, [], [], []);
    public CapacityMode Mode { get; set; } = CapacityMode.Enforce;
    // Opt-in full evidence mode for feature tests; the precomputed Inventory seam remains compatible.
    public Func<InventorySnapshot>? ReadInventory { get; set; }
    public Func<IReadOnlyList<Vm>>? ReadManagedVms { get; set; }
    public Func<CapacityConfig>? ReadConfig { get; set; }
    public Func<string, EffectiveAllowance?>? ReadAllowance { get; set; }
    public InMemoryAuditLog? Audit { get; set; }
    public IOperationRegistry? Operations { get; set; }
    public Task<CapacityDecision> TryReserveAsync(ReservationRequest request, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            ct.ThrowIfCancellationRequested();
            {
                ReservationRules.Validate(request);
                if (request.Lines.Any(l => l.Resource == ReservationResource.Storage && _reservations.Values.Any(r => r.Resource == ReservationResource.Storage &&
                    StringComparer.OrdinalIgnoreCase.Equals(r.Artifact, l.Artifact))))
                    return Task.FromResult(new CapacityDecision(false, [], null, null, 0, 0, 0, "reservation-conflict", Inventory.Epoch));
                var evidence = ReadInventory?.Invoke();
                var snapshot = Snapshot(evidence);
                CapacityDecision? decision = null;
                if (ReadInventory is not null)
                {
                    decision = CapacityMath.Decide(request, snapshot, ReadConfig?.Invoke() ?? HostAdminDefaults.Capacity with { Mode = Mode }, ReadAllowance?.Invoke(request.Owner), CapacityMath.AccountedReservations(evidence!, _reservations.Values.ToArray(), ReadManagedVms?.Invoke() ?? []));
                    if (!decision.Allowed) return Task.FromResult(decision);
                }
                else foreach (var group in request.Lines.GroupBy(l => (l.Resource, l.Volume)))
                {
                    var amount = group.Sum(l => l.Amount);
                    var available = group.Key.Resource switch
                    {
                        ReservationResource.Ram => snapshot.RamAvailableBytes,
                        ReservationResource.Cpu => snapshot.CpuAvailable is int cpu ? cpu : long.MaxValue,
                        _ => snapshot.Volumes.FirstOrDefault(v => StringComparer.OrdinalIgnoreCase.Equals(v.Root, group.Key.Volume))?.AvailableBytes ?? 0,
                    };
                    if (Mode == CapacityMode.Enforce && (!snapshot.Complete || amount > available))
                        return Task.FromResult(new CapacityDecision(false, [], group.Key.Resource.ToString().ToLowerInvariant(), "host", amount, available, available, snapshot.Complete ? "capacity-exhausted" : "inventory-incomplete", snapshot.Epoch));
                }
                var ids = new List<string>(); foreach (var line in request.Lines)
                {
                    var id = Guid.NewGuid().ToString("n"); ids.Add(id);
                    _reservations[id] = new(id, line.Resource, request.Owner, request.VmName, line.Artifact, line.Volume, line.Amount, ReservationPhase.Pending,
                        ReservationOrigin.Api, request.OperationId, clock.UtcNow, clock.UtcNow + request.PendingTimeout, null);
                }
                Audit?.AppendAsync(new(clock.UtcNow, request.Owner, decision?.Reason is null ? "capacity.reserve" : "capacity.observe", request.VmName ?? request.OperationId, AuditOutcome.Success, decision?.Reason), ct).GetAwaiter().GetResult();
                return Task.FromResult(decision is null ? new CapacityDecision(true, ids, null, null, 0, 0, 0, null, snapshot.Epoch) : decision with { ReservationIds = ids });
            }

        }
    }
    internal ReservationRequest AdoptAbandonedPrimaryStorage(ReservationRequest request, InMemoryJobStore jobs)
    {
        lock (InMemoryTransaction.Gate)
        {
            foreach (var row in _reservations.Values.Where(r => r.Resource == ReservationResource.Storage && r.OperationId is not null &&
                Ownership.SameName(r.ScopeOwner, request.Owner) && Ownership.SameName(r.VmName, request.VmName)).ToArray())
            {
                if (!request.Lines.Any(l => l.Resource == row.Resource && (l.Amount >= row.Amount || Mode == CapacityMode.Observe) &&
                    StringComparer.OrdinalIgnoreCase.Equals(l.Artifact, row.Artifact) && StringComparer.OrdinalIgnoreCase.Equals(l.Volume, row.Volume))) continue;
                if (Operations?.IsAlive(row.OperationId!) == true || jobs.GetAsync(row.OperationId!, default).GetAwaiter().GetResult() is not
                    { Kind: "create-vm", State: JobState.Failed or JobState.Cancelled }) continue;
                request = request with { Lines = request.Lines.Select(l => l.Resource == row.Resource &&
                    StringComparer.OrdinalIgnoreCase.Equals(l.Artifact, row.Artifact) ? l with { Amount = Math.Max(l.Amount, row.Amount) } : l).ToArray() };
                _reservations.Remove(row.Id);
                Audit?.AppendAsync(new(clock.UtcNow, request.Owner, "capacity.adopt", request.VmName!, AuditOutcome.Success, "failed-primary-create"), default).GetAwaiter().GetResult();
            }
            return request;
        }
    }
    public Task ConfirmAsync(IReadOnlyList<string> ids, VmState observed, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            foreach (var id in ids)
                if (_reservations.TryGetValue(id, out var r) && ReservationRules.CanConfirm(r, observed))
                    _reservations[id] = r with { Phase = ReservationPhase.Held, ConfirmedAt = clock.UtcNow };
            return Task.CompletedTask;

        }
    }
    public Task ReleaseAsync(IReadOnlyList<string> ids, VmState observed, string reason, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            foreach (var id in ids)
                if (_reservations.TryGetValue(id, out var r) && ReservationRules.CanRelease(r, observed))
                    _reservations.Remove(id);
            return Task.CompletedTask;

        }
    }
    public Task TrimAsync(string id, long amount, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            {
                if (!_reservations.TryGetValue(id, out var r)) throw new KeyNotFoundException("Unknown reservation.");
                if (r.Resource != ReservationResource.Storage || amount < 0 || amount > r.Amount) throw new ArgumentException("Only a storage reservation may shrink.");
                _reservations[id] = r with { Amount = amount }; return Task.CompletedTask;
            }

        }
    }
    public Task ExtendAsync(IReadOnlyList<string> ids, TimeSpan by, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            if (by <= TimeSpan.Zero) throw new ArgumentException("Extension must be positive.");
            foreach (var id in ids) if (_reservations.TryGetValue(id, out var r) && r.Phase == ReservationPhase.Pending)
                _reservations[id] = r with { PendingUntil = (r.PendingUntil ?? clock.UtcNow) + by };
            return Task.CompletedTask;

        }
    }
    // The fake has no external evidence to justify sweeping. It reports every hold as retained.
    public Task<IReadOnlyList<OrphanOutcome>> ReconcileAsync(CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult<IReadOnlyList<OrphanOutcome>>(_reservations.Values.Select(r => new OrphanOutcome(r.Id, OrphanResolution.Kept, "No absence evidence supplied by fake inventory.")).ToArray());
        }
    }
    public Task<HostCapacitySnapshot> SnapshotAsync(bool refresh, CancellationToken ct)
    {
        lock (InMemoryTransaction.Gate)
        {
            return Task.FromResult(Snapshot());
        }
    }
    private HostCapacitySnapshot Snapshot(InventorySnapshot? evidence = null)
    {
        if (ReadInventory is not null) return CapacityMath.Calculate(evidence ?? ReadInventory(), ReadConfig?.Invoke() ?? HostAdminDefaults.Capacity with { Mode = Mode },
            _reservations.Values.ToArray(), ReadManagedVms?.Invoke() ?? []);
        var ram = _reservations.Values.Where(r => r.Resource == ReservationResource.Ram).Sum(r => r.Amount);
        var cpu = _reservations.Values.Where(r => r.Resource == ReservationResource.Cpu).Sum(r => r.Amount);
        return Inventory with
        {
            RamReservedBytes = Inventory.RamReservedBytes + ram,
            RamAvailableBytes = Math.Max(0, Inventory.RamAvailableBytes - ram),
            CpuActive = checked(Inventory.CpuActive + (int)cpu),
            CpuAvailable = Inventory.CpuAvailable is int avail ? Math.Max(0, avail - (int)cpu) : null,
            Volumes = Inventory.Volumes.Select(v =>
            {
                var storage = _reservations.Values.Where(r => r.Resource == ReservationResource.Storage && StringComparer.OrdinalIgnoreCase.Equals(r.Volume, v.Root)).Sum(r => r.Amount);
                return v with { GrowthReservedBytes = v.GrowthReservedBytes + storage, AvailableBytes = Math.Max(0, v.AvailableBytes - storage) };
            }).ToArray(),
            Reservations = Inventory.Reservations.Concat(_reservations.Values).ToArray()
        };
    }
}
