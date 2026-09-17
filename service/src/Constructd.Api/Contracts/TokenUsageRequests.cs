namespace Constructd.Api.Contracts;

public sealed record TokenUsageRequest(DateTimeOffset? GeneratedAt, IReadOnlyList<TokenUsageDayRequest?>? Days);
public sealed record TokenUsageDayRequest(string? Day, string? Tool, long? InputTokens, long? OutputTokens,
    long? CacheCreateTokens, long? CacheReadTokens, long? TotalTokens, decimal? CostUsd,
    IReadOnlyDictionary<string, TokenUsageModelRequest?>? Models);
public sealed record TokenUsageModelRequest(long? TotalTokens, decimal? CostUsd);
