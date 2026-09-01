using System.Collections.Concurrent;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Core.Services;

/// <summary>
/// The stateful half of plan §4.7: gathers the two idle signals (live connections through the
/// forwards, latest in-guest heartbeat), asks the pure <see cref="IdleEvaluator"/> what to do, and
/// applies the answer through the hypervisor driver, audit-logging every action.
///
/// This is a real implementation, not a fake — it is platform-agnostic and moves to the Windows host
/// unchanged; only the driver and the forward manager under it get swapped.
/// </summary>
public sealed class IdlePolicyEngine(
    IVmRepository vms,
    IPortForwardManager forwards,
    IHypervisorDriver driver,
    IAuditLog audit,
    IdleOptions options) : IIdlePolicyEngine
{
    /// <summary>
    /// Per-VM "last seen active" watermark, so the timeout measures a continuous idle window.
    /// Seeded with the first tick's timestamp: a service restart restarts the window rather than
    /// idling out VMs on the strength of history it cannot see. A durable implementation would
    /// persist this alongside the VM row.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastActiveAt =
        new(Ownership.NameComparer);

    private readonly SemaphoreSlim _tickGate = new(1, 1);

    public async Task<IReadOnlyList<IdleOutcome>> EvaluateAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await _tickGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await vms.ListAsync(owner: null, cancellationToken).ConfigureAwait(false);
            var outcomes = new List<IdleOutcome>(all.Count);

            foreach (var vm in all.OrderBy(v => v.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.Add(await EvaluateVmAsync(vm, now, cancellationToken).ConfigureAwait(false));
            }

            return outcomes;
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private async Task<IdleOutcome> EvaluateVmAsync(Vm vm, DateTimeOffset now, CancellationToken ct)
    {
        var policy = IdlePolicyRules.Clamp(vm.IdlePolicy, options);
        var reportInterval = TimeSpan.FromMinutes(Math.Max(1, options.ReportIntervalMinutes));

        try
        {
            var state = await driver.GetStateAsync(vm.Name, ct).ConfigureAwait(false);
            if (state != vm.State)
            {
                await vms.UpdateAsync(vm with { State = state }, ct).ConfigureAwait(false);
            }

            var connections = await forwards.CountActiveConnectionsAsync(vm.Name, ct).ConfigureAwait(false);
            var report = await vms.GetLatestActivityAsync(vm.Name, ct).ConfigureAwait(false);

            var previous = _lastActiveAt.GetOrAdd(vm.Name, now);
            var lastActiveAt = IdleEvaluator.ComputeLastActiveAt(
                previous,
                now,
                connections,
                report,
                reportInterval,
                options.MissingReportGraceMultiple);
            _lastActiveAt[vm.Name] = lastActiveAt;

            var decision = IdleEvaluator.Evaluate(new IdleEvaluationInput(
                vm.Name,
                state,
                policy,
                now,
                lastActiveAt,
                connections,
                report,
                reportInterval,
                options.MissingReportGraceMultiple));

            if (!decision.Acts)
            {
                return new IdleOutcome(vm.Name, decision, Applied: false, Error: null);
            }

            return await ApplyAsync(vm, decision, now, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // The driver's exception may carry command lines or credentials in any of its fields, so
            // only a safe description leaves this method — for the audit trail and for the log alike.
            var safe = SafeError.Describe(ex);
            var decision = new IdleDecision(IdleDecisionKind.None, "evaluation failed");
            await AuditAsync(vm, "vm.idle-evaluate", AuditOutcome.Failure, safe, now, ct)
                .ConfigureAwait(false);
            return new IdleOutcome(vm.Name, decision, Applied: false, Error: safe);
        }
    }

    private async Task<IdleOutcome> ApplyAsync(Vm vm, IdleDecision decision, DateTimeOffset now, CancellationToken ct)
    {
        var action = decision.Kind == IdleDecisionKind.Shutdown ? "vm.idle-shutdown" : "vm.idle-save";

        try
        {
            if (decision.Kind == IdleDecisionKind.Shutdown)
            {
                await driver.StopAsync(vm.Name, ct).ConfigureAwait(false);
            }
            else
            {
                await driver.SaveAsync(vm.Name, ct).ConfigureAwait(false);
            }

            var state = await driver.GetStateAsync(vm.Name, ct).ConfigureAwait(false);
            await vms.UpdateAsync(vm with { State = state }, ct).ConfigureAwait(false);

            // Restart the window so a VM that is powered back on is not acted on again immediately.
            _lastActiveAt[vm.Name] = now;

            await AuditAsync(vm, action, AuditOutcome.Success, decision.Reason, now, ct).ConfigureAwait(false);
            return new IdleOutcome(vm.Name, decision, Applied: true, Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var safe = SafeError.Describe(ex);
            await AuditAsync(vm, action, AuditOutcome.Failure, safe, now, ct).ConfigureAwait(false);
            return new IdleOutcome(vm.Name, decision, Applied: false, Error: safe);
        }
    }

    private Task AuditAsync(
        Vm vm,
        string action,
        AuditOutcome outcome,
        string? detail,
        DateTimeOffset now,
        CancellationToken ct) =>
        audit.AppendAsync(new AuditEntry(now, "system", action, vm.Name, outcome, detail), ct);
}
