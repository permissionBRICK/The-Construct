using Constructd.Core.Logic;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Evaluates every VM's idle policy and applies the decisions through
/// <see cref="IHypervisorDriver"/> (plan §4.7). The decision logic itself is the pure
/// <see cref="IdleEvaluator"/>; this interface is the stateful, side-effecting wrapper the
/// scheduler ticks once a minute.
/// </summary>
public interface IIdlePolicyEngine
{
    /// <returns>The decision taken for every VM considered, in name order.</returns>
    Task<IReadOnlyList<IdleOutcome>> EvaluateAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>What the engine decided (and, for acting decisions, did) for one VM.</summary>
/// <param name="Error">
/// A safe description of the failure, if any (<see cref="Logic.SafeError"/>). The exception itself is
/// deliberately not carried anywhere: it would end up in an audit entry or a log line.
/// </param>
public sealed record IdleOutcome(string VmName, IdleDecision Decision, bool Applied, string? Error);
