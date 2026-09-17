using Constructd.Core.Domain;

namespace Constructd.Core.Logic;

public sealed record MemoryPressureVm(IdleEvaluationInput Idle, VmKind Kind, string Owner,
    bool JobRunning, long ResidentBytes, DateTimeOffset? LastStartedAt = null, DateTimeOffset? LastSavedAt = null);
public sealed record MemoryPressureInput(MemoryPressureConfig Config, DateTimeOffset Now,
    long UsedBytes, long TotalBytes, long? SwapUsedBytes, long? SwapTotalBytes,
    bool WasUnderPressure, IReadOnlyList<MemoryPressureVm> Vms);
public sealed record MemoryPressureCandidate(string Name, TimeSpan Remaining, long ResidentBytes);
public sealed record MemoryPressurePlan(bool UnderPressure, IReadOnlyList<MemoryPressureCandidate> Candidates,
    int SavesNeeded, bool InsufficientCandidates, string Reason);

/// <summary>Measured usage only. Reservations and configured VM allocations are not pressure signals.</summary>
public static class MemoryPressureEvaluator
{
    public static MemoryPressurePlan Plan(MemoryPressureInput input)
    {
        var config = input.Config;
        MemoryPressurePlan None(string reason) => new(false, [], 0, false, reason);
        if (!config.Enabled) return None("off");
        if (input.TotalBytes <= 0 || input.UsedBytes < 0 || input.UsedBytes > input.TotalBytes)
            return None("memory measurement unavailable");
        var usedPercent = 100m * input.UsedBytes / input.TotalBytes;
        var swapPercent = input.SwapTotalBytes is > 0 && input.SwapUsedBytes is >= 0
            ? 100m * input.SwapUsedBytes.Value / input.SwapTotalBytes.Value : 0;
        var swapHigh = config.SwapHighWaterPercent > 0 && swapPercent > config.SwapHighWaterPercent;
        var ramHigh = usedPercent > config.HighWaterPercent;
        if (!ramHigh && !swapHigh && !(input.WasUnderPressure && usedPercent >= config.LowWaterPercent))
            return None("idle");

        var candidates = new List<MemoryPressureCandidate>();
        foreach (var vm in input.Vms)
        {
            var idle = vm.Idle with { Now = input.Now };
            if (idle.State != VmState.Running || idle.Policy.IsDisabled || vm.JobRunning || vm.ResidentBytes <= 0) continue;
            var cooldown = TimeSpan.FromMinutes(config.CooldownMinutesAfterSave);
            if (vm.LastStartedAt is { } started && input.Now - started < cooldown ||
                vm.LastSavedAt is { } saved && input.Now - saved < cooldown) continue;
            if (IdleEvaluator.IsActiveNow(idle.ActiveConnections, idle.LastReport, input.Now,
                idle.ReportInterval, idle.MissingReportGraceMultiple)) continue;
            var lastActive = IdleEvaluator.ComputeLastActiveAt(idle.LastActiveAt, input.Now, idle.ActiveConnections,
                idle.LastReport, idle.ReportInterval, idle.MissingReportGraceMultiple);
            // Silence must pass the same grace window as ordinary idle handling.
            var idleSince = IdleEvaluator.IdleSince(idle.LastReport, lastActive, idle.ReportInterval, idle.MissingReportGraceMultiple);
            if (input.Now < idleSince) continue;
            candidates.Add(new(idle.VmName, idleSince + TimeSpan.FromMinutes(idle.Policy.TimeoutMinutes) - input.Now, vm.ResidentBytes));
        }
        var ordered = candidates.OrderBy(c => c.Remaining).ThenByDescending(c => c.ResidentBytes)
            .ThenBy(c => c.Name, StringComparer.Ordinal).ToArray();
        decimal estimatedUsed = input.UsedBytes;
        var needed = 0;
        var target = input.TotalBytes * (config.LowWaterPercent / 100m);
        // Swap reclamation cannot be estimated from resident RAM. Try one, then remeasure.
        while (needed < ordered.Length && (estimatedUsed >= target || swapHigh && needed == 0))
            estimatedUsed -= ordered[needed++].ResidentBytes;
        var insufficient = estimatedUsed >= target || swapHigh && ordered.Length == 0;
        var trigger = ramHigh ? $"used {usedPercent:F0}% > {config.HighWaterPercent}%"
            : swapHigh ? $"swap {swapPercent:F0}% > {config.SwapHighWaterPercent}%"
            : $"used {usedPercent:F0}% >= {config.LowWaterPercent}% low-water mark";
        return new(true, ordered, needed, insufficient, trigger +
            (insufficient ? "; insufficient candidates" : "") + (swapHigh ? "; swap relief requires a new measurement" : ""));
    }
}
