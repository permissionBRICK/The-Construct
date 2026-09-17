using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Core.Configuration;
using Constructd.Core.Abstractions;

namespace Constructd.Core.Services;

public sealed partial class IdlePolicyEngine
{
    private sealed record Observation(long Generation, DateTimeOffset Created, VmState State, DateTimeOffset StartedAt);
    private readonly Dictionary<string, Observation> _pressureObservations = new(Ownership.NameComparer);
    private bool _underPressure;
    private DateTimeOffset? _pressureMeasurementAfter;
    private DateTimeOffset? _lastPressureAttempt;

    private DateTimeOffset ObservePressureVm(Vm vm, VmState state, DateTimeOffset now)
    {
        if (!_pressureObservations.TryGetValue(vm.Name, out var previous) || previous.Created != vm.Created ||
            previous.Generation != vm.PowerGeneration || previous.State != VmState.Running && state == VmState.Running)
        {
            previous = new(vm.PowerGeneration, vm.Created, state, now);
            _lastActiveAt[vm.Name] = now;
        }
        _pressureObservations[vm.Name] = previous with { State = state };
        return previous.StartedAt;
    }

    private static bool HasRunningJob(Vm vm, IReadOnlyList<Vm> all, IReadOnlyList<Job> jobs) =>
        jobs.Any(j => j.State is JobState.Queued or JobState.Running &&
            (Ownership.SameName(j.VmName, vm.Name) || j.Id == vm.CurrentJobId ||
             all.Any(child => Ownership.SameName(child.Parent, vm.Name) &&
                 (Ownership.SameName(child.Name, j.VmName) || child.CurrentJobId == j.Id))));

    private async Task<MemoryPressureVm?> PressureRowAsync(Vm vm, HostCapacitySnapshot snapshot,
        IReadOnlyList<Vm> all, IReadOnlyList<Job> jobs, DateTimeOffset now, CancellationToken ct)
    {
        if (vm.Deleting) return null;
        var actual = snapshot.MeasuredVms?.SingleOrDefault(a => CapacityMath.Matches(vm, a));
        if (actual is null || actual.State != VmState.Running) return null;
        var state = await driver.GetStateAsync(vm.Name, ct);
        var started = ObservePressureVm(vm, state, now);
        // Uptime also catches a direct hypervisor restart between scheduler ticks.
        if (actual.UptimeSeconds is >= 0 && double.IsFinite(actual.UptimeSeconds.Value))
        {
            var boot = snapshot.ObservedAt - TimeSpan.FromSeconds(Math.Min(actual.UptimeSeconds.Value, TimeSpan.MaxValue.TotalSeconds / 2));
            if (boot > started) started = boot;
        }
        if (vm.Observed?.LastBootAt is { } observedBoot && observedBoot > started) started = observedBoot;
        var connections = await forwards.CountActiveConnectionsAsync(vm.Name, ct);
        var report = await vms.GetLatestActivityAsync(vm.Name, ct);
        var interval = TimeSpan.FromMinutes(Math.Max(1, options.ReportIntervalMinutes));
        var active = IdleEvaluator.ComputeLastActiveAt(_lastActiveAt.GetOrAdd(vm.Name, now), now, connections,
            report, interval, options.MissingReportGraceMultiple);
        _lastActiveAt[vm.Name] = active;
        return new(new(vm.Name, state, IdlePolicyRules.Clamp(vm.IdlePolicy, options), now, active, connections,
                report, interval, options.MissingReportGraceMultiple), vm.Kind, vm.Owner,
            HasRunningJob(vm, all, jobs), Math.Max(0, pressure!.UseGuestMemoryDemand
                ? actual.MemoryDemandBytes ?? 0 : actual.MemoryAssignedBytes), started, vm.PressureSavedAt);
    }

    private async Task EvaluatePressureAsync(DateTimeOffset now, CancellationToken ct)
    {
        var services = pressure!;
        void Status(string state, decimal? used = null, string? detail = null) =>
            services.State.Set(new(true, state, services.State.Status.LastAction, used, detail));
        try
        {
            var config = await services.Config.GetAsync<MemoryPressureConfig>("memoryPressure", ct) ?? HostAdminDefaults.MemoryPressure;
            if (!config.Enabled)
            {
                _underPressure = false;
                services.State.Set(new(false, "off", services.State.Status.LastAction));
                return;
            }
            var snapshot = await services.Capacity.SnapshotAsync(false, ct);
            var capacityConfig = await services.Config.GetAsync<CapacityConfig>("capacity", ct) ?? HostAdminDefaults.Capacity;
            if (!CapacityMath.RuntimeInventoryComplete(snapshot) || snapshot.MeasuredVms is null || snapshot.RamUsedBytes is null ||
                snapshot.RamTotalBytes <= 0 || snapshot.ObservedAt > now ||
                now - snapshot.ObservedAt > TimeSpan.FromSeconds(2d * capacityConfig.ReconcileSeconds))
            {
                Status("unavailable", detail: "fresh memory inventory unavailable");
                return;
            }
            var used = 100m * snapshot.RamUsedBytes.Value / snapshot.RamTotalBytes;
            if (_pressureMeasurementAfter is { } after && snapshot.ObservedAt <= after)
            {
                Status("waiting-for-measurement", used);
                return;
            }
            var input = new MemoryPressureInput(config, now, snapshot.RamUsedBytes.Value, snapshot.RamTotalBytes,
                snapshot.SwapUsedBytes, snapshot.SwapTotalBytes, _underPressure, []);
            var trigger = MemoryPressureEvaluator.Plan(input);
            _underPressure = trigger.UnderPressure;
            if (!_underPressure) { Status("idle", used); return; }
            if (!driver.Capabilities.Suspend) { Status("unavailable", used, "backend cannot save VMs"); return; }
            if (_lastPressureAttempt is { } attempt && now - attempt < TimeSpan.FromSeconds(config.MinSecondsBetweenSaves))
            { Status("cooldown", used); return; }

            var all = await vms.ListAsync(null, ct);
            foreach (var name in _pressureObservations.Keys.Where(n => !all.Any(v => Ownership.SameName(n, v.Name))).ToArray())
            { _pressureObservations.Remove(name); _lastActiveAt.TryRemove(name, out _); }
            var jobs = await services.Jobs.ListAsync(ct);
            var rows = new List<MemoryPressureVm>();
            foreach (var vm in all)
            {
                await using var held = await vmGate.TryAcquireAsync(vm.Name, "pressure-evaluate", ct);
                if (held is null) continue;
                var current = await vms.GetAsync(vm.Name, ct);
                if (current is null) continue;
                try
                {
                    if (await PressureRowAsync(current, snapshot, all, jobs, now, ct) is { } row) rows.Add(row);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { /* Missing activity/state evidence never grants permission to save. */ }
            }
            var plan = MemoryPressureEvaluator.Plan(input with { Vms = rows });
            Status(plan.InsufficientCandidates ? "insufficient-candidates" : "pressure", used, plan.Reason);
            foreach (var candidate in plan.Candidates)
            {
                await using var held = await vmGate.TryAcquireAsync(candidate.Name, "pressure-save", ct);
                if (held is null) continue;
                var vm = await vms.GetAsync(candidate.Name, ct);
                if (vm is null) continue;
                now = services.Clock.UtcNow;
                if (snapshot.ObservedAt > now || now - snapshot.ObservedAt > TimeSpan.FromSeconds(2d * capacityConfig.ReconcileSeconds))
                {
                    Status("unavailable", detail: "memory measurement expired during evaluation");
                    return;
                }
                input = input with { Now = now };
                // A start, policy change, job or heartbeat may have arrived since planning.
                all = await vms.ListAsync(null, ct);
                jobs = await services.Jobs.ListAsync(ct);
                var fresh = await PressureRowAsync(vm, snapshot, all, jobs, now, ct);
                if (fresh is null || MemoryPressureEvaluator.Plan(input with { Vms = [fresh] }).Candidates.Count == 0) continue;
                var remaining = IdleEvaluator.IdleSince(fresh.Idle.LastReport, fresh.Idle.LastActiveAt,
                    fresh.Idle.ReportInterval, fresh.Idle.MissingReportGraceMultiple)
                    + TimeSpan.FromMinutes(fresh.Idle.Policy.TimeoutMinutes) - now;
                var reason = plan.Reason + $"; {remaining.TotalMinutes:F0} min to idle timeout";
                _lastPressureAttempt = services.Clock.UtcNow;
                try
                {
                    await driver.SaveAsync(vm.Name, ct);
                    if (await driver.GetStateAsync(vm.Name, ct) != VmState.Saved)
                        throw new InvalidOperationException("Save did not reach Saved state.");
                    var savedAt = services.Clock.UtcNow;
                    if (!await services.Metadata.RecordPressureSaveAsync(vm.Name, vm.PowerGeneration, savedAt, ct))
                        throw new InvalidOperationException("VM changed during pressure save.");
                    _lastActiveAt[vm.Name] = savedAt;
                    await services.Capacity.ReleaseAsync(snapshot.Reservations.Where(r => Ownership.SameName(r.VmName, vm.Name))
                        .Select(r => r.Id).ToArray(), VmState.Saved, "memory-pressure", ct);
                    await AuditAsync(vm, "vm.pressure-save", AuditOutcome.Success, reason, savedAt, ct);
                    services.State.Set(new(true, plan.InsufficientCandidates ? "insufficient-candidates" : "pressure",
                        new(vm.Name, savedAt, reason), used, reason));
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    var safe = SafeError.Describe(ex);
                    await AuditAsync(vm, "vm.pressure-save", AuditOutcome.Failure, safe, services.Clock.UtcNow, ct);
                    Status("save-failed", used, safe);
                }
                finally
                {
                    // Even a failed command may have changed memory. Evidence must postdate its completion.
                    _pressureMeasurementAfter = services.Clock.UtcNow;
                    _lastPressureAttempt = services.Clock.UtcNow;
                }
                return; // At most one save attempt per tick.
            }
            Status("insufficient-candidates", used, plan.Reason + "; no candidate available to save");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { Status("unavailable", detail: "memory pressure evaluation failed"); }
    }
}
