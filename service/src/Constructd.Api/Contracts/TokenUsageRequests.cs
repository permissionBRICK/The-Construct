namespace Constructd.Api.Contracts;

/// <param name="Agents">The coding agents installed in the VM and their versions (optional; stored per VM, read with <c>GET /vms/{name}/agents</c>).</param>
public sealed record TokenUsageRequest(DateTimeOffset? GeneratedAt, IReadOnlyList<TokenUsageDayRequest?>? Days,
    IReadOnlyList<GuestAgentRequest?>? Agents = null);
/// <param name="Tool"><c>claude</c>, <c>codex</c>, <c>opencode</c>, <c>antigravity</c> or <c>t3</c>.</param>
/// <param name="Version">What its <c>--version</c> printed, as major.minor.patch with an optional suffix.</param>
/// <param name="SignedIn">Whether the agent is signed in, by its CLI's own check (optional).</param>
public sealed record GuestAgentRequest(string? Tool, string? Version, bool? SignedIn = null);
public sealed record TokenUsageDayRequest(string? Day, string? Tool, long? InputTokens, long? OutputTokens,
    long? CacheCreateTokens, long? CacheReadTokens, long? TotalTokens, decimal? CostUsd,
    IReadOnlyDictionary<string, TokenUsageModelRequest?>? Models);
public sealed record TokenUsageModelRequest(long? TotalTokens, decimal? CostUsd);
