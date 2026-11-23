using Constructd.Core.Abstractions;

namespace Constructd.Core.Services;

/// <summary>One sub-step of a job's run: its key, its short label and how long it took.</summary>
public sealed record ProgressPlanStep(string Key, string Label, double Seconds);

/// <summary>The sub-steps of a job's last successful run on a VM, in the order they ran: the weights of the next run's bar.</summary>
public sealed record ProgressPlan(IReadOnlyList<ProgressPlanStep> Steps);

/// <summary>Where the plans of finished jobs are kept, per job kind and VM.</summary>
public interface IProgressPlans
{
    /// <summary>The plan of the last successful run of <paramref name="kind"/> on <paramref name="vm"/>, else of any VM; null when there is none.</summary>
    Task<ProgressPlan?> GetAsync(string kind, string? vm, CancellationToken ct);

    Task SaveAsync(string kind, string? vm, ProgressPlan plan, CancellationToken ct);
}

/// <summary>The plans in the host config store: one per job kind and VM, and the latest of each kind.</summary>
public sealed class HostConfigProgressPlans(IHostConfigStore config) : IProgressPlans
{
    private static string Key(string kind, string? vm) => "progress-plan:" + kind + (vm is null ? "" : ":" + vm.ToLowerInvariant());

    public async Task<ProgressPlan?> GetAsync(string kind, string? vm, CancellationToken ct) =>
        (vm is null ? null : await config.GetAsync<ProgressPlan>(Key(kind, vm), ct)) ?? await config.GetAsync<ProgressPlan>(Key(kind, null), ct);

    public async Task SaveAsync(string kind, string? vm, ProgressPlan plan, CancellationToken ct)
    {
        if (vm is not null) await config.SetAsync(Key(kind, vm), plan, "job", ct);
        await config.SetAsync(Key(kind, null), plan, "job", ct);
    }
}

/// <summary>
/// One overall percentage for a long job (create, reinstall, reprovision, restore), with the current sub-step in a few
/// words. A job's code names its sub-steps as they start (<see cref="Begin"/>, through <see cref="Current"/> wherever it
/// runs: the job, the ISO builder, the download) and, where a sub-step can measure itself, how far it got
/// (<see cref="Fraction"/>). Each sub-step weighs what it took in the plan of the previous run; without one, every
/// sub-step weighs the same and the job's expected list (<see cref="Expect"/>) stands in for the plan. The percentage
/// never goes back and stays below 100 until the job succeeded.
/// </summary>
public sealed class JobProgressTracker(Func<DateTimeOffset> now)
{
    private static readonly AsyncLocal<JobProgressTracker?> InFlow = new();

    /// <summary>The tracker of the job this code runs for, or null outside a job.</summary>
    public static JobProgressTracker? Current => InFlow.Value;

    /// <summary>Makes <paramref name="tracker"/> the current one for the code that runs from here on in this flow.</summary>
    public static IDisposable Use(JobProgressTracker? tracker)
    {
        var before = InFlow.Value;
        InFlow.Value = tracker;
        return new Restore(() => InFlow.Value = before);
    }

    private readonly Lock _gate = new();
    // The plan's weight of each key (seconds of the previous run, or 1 each without one), and the sub-steps it expects.
    private readonly Dictionary<string, double> _weights = new(StringComparer.Ordinal);
    private bool _planned;
    private string? _openPrefix;
    private int _open;
    private double _unknown = 1;
    // This run.
    private readonly List<string> _order = [];
    private readonly Dictionary<string, (string Label, double Seconds)> _measured = new(StringComparer.Ordinal);
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unplanned = new(StringComparer.Ordinal);
    private string? _key;
    private string? _label;
    private DateTimeOffset _since;
    private double _fraction;
    private int _percent;

    /// <summary>The weights of the previous run. Ignored once a plan or an expectation is set.</summary>
    public void UsePlan(ProgressPlan? plan)
    {
        if (plan is not { Steps.Count: > 0 }) return;
        lock (_gate)
        {
            if (_planned) return;
            _planned = true;
            foreach (var step in plan.Steps) _weights[step.Key] = Math.Max(1, step.Seconds) + (_weights.TryGetValue(step.Key, out var w) ? w : 0);
            // A sub-step the previous run did not have weighs what an average one did.
            _unknown = _weights.Values.Average();
        }
    }

    /// <summary>
    /// The sub-steps a first run expects, each weighing the same, plus <paramref name="openCount"/> more whose keys start
    /// with <paramref name="openPrefix"/> and are not known in advance (the guest's provision sections). Ignored once a plan
    /// or an expectation is set: an outer job's expectation stands for the job it runs inside.
    /// </summary>
    public void Expect(IEnumerable<string> keys, string? openPrefix = null, int openCount = 0)
    {
        lock (_gate)
        {
            if (_planned) return;
            _planned = true;
            foreach (var key in keys) _weights[key] = 1;
            _openPrefix = openPrefix;
            _open = openPrefix is null ? 0 : Math.Max(0, openCount);
        }
    }

    /// <summary>
    /// A sub-step starts; the one before it is done. <paramref name="planned"/> false: a sub-step only some runs have (a
    /// provision that starts its stopped VM first) stays out of the next run's plan, so a run without it does not wait for it.
    /// </summary>
    public void Begin(string key, string label, bool planned = true)
    {
        lock (_gate)
        {
            var at = now();
            Close(at);
            if (!planned) _unplanned.Add(key);
            if (!_weights.ContainsKey(key) && !_measured.ContainsKey(key) && _openPrefix is not null && _open > 0 && key.StartsWith(_openPrefix, StringComparison.Ordinal))
                _open--;
            _done.Remove(key);
            if (!_measured.ContainsKey(key))
            {
                _order.Add(key);
                _measured[key] = (label, 0);
            }

            _key = key;
            _label = label;
            _since = at;
            _fraction = 0;
        }
    }

    /// <summary>How far the current sub-step got by its own measure, 0 to 1. It never goes back within the sub-step.</summary>
    public void Fraction(double value)
    {
        if (double.IsNaN(value)) return;
        lock (_gate) _fraction = Math.Max(_fraction, Math.Clamp(value, 0, 1));
    }

    /// <summary>The overall percentage and the current sub-step's label; null before the first sub-step.</summary>
    public (int Percent, string Label)? Snapshot()
    {
        lock (_gate)
        {
            if (_key is null) return null;
            double Weight(string key) => _weights.TryGetValue(key, out var w) ? w : _unknown;
            var done = _done.Sum(Weight);
            var current = _done.Contains(_key) ? 0 : Weight(_key);
            var remaining = _weights.Keys.Where(k => k != _key && !_done.Contains(k) && !_measured.ContainsKey(k)).Sum(Weight) + _open * _unknown;
            var total = done + current + remaining;
            var percent = total <= 0 ? 0 : (int)Math.Floor(100 * (done + _fraction * current) / total);
            _percent = Math.Max(_percent, Math.Min(99, percent));
            return (_percent, _label!);
        }
    }

    /// <summary>The run's sub-steps and their durations, once the job ended; the plan of the next run.</summary>
    public ProgressPlan? Measured()
    {
        lock (_gate)
        {
            Close(now());
            _key = null;
            var kept = _order.Where(k => !_unplanned.Contains(k)).ToArray();
            return kept.Length == 0 ? null : new ProgressPlan([.. kept.Select(k => new ProgressPlanStep(k, _measured[k].Label, Math.Round(_measured[k].Seconds, 1)))]);
        }
    }

    private void Close(DateTimeOffset at)
    {
        if (_key is null || _done.Contains(_key)) return;
        var (label, seconds) = _measured[_key];
        _measured[_key] = (label, seconds + Math.Max(0, (at - _since).TotalSeconds));
        _done.Add(_key);
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
