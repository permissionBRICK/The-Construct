namespace Constructd.Core.Abstractions;

/// <summary>
/// The newest released version of a coding agent a VM runs (<c>claude</c>, <c>codex</c>, <c>opencode</c>, <c>t3</c>),
/// looked up where the old panel looked (npm, GitHub releases). Best effort: null when unknown or unreachable.
/// </summary>
public interface IAgentReleases
{
    /// <param name="channel">For <c>t3</c>: <c>nightly</c> reads the nightly tag; anything else the stable one.</param>
    Task<string?> LatestAsync(string tool, string? channel, CancellationToken ct);
}
