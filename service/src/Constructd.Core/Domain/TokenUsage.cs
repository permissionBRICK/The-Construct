namespace Constructd.Core.Domain;

public sealed record TokenUsageDay(string Day, string Tool, long InputTokens, long OutputTokens,
    long CacheCreateTokens, long CacheReadTokens, long TotalTokens, long CostUsdMicros, string ModelsJson = "{}");
public sealed record TokenUsageRow(string Vm, string Owner, TokenUsageDay Usage,
    DateTimeOffset ReportedAt, DateTimeOffset? VmDeletedAt = null, string Incarnation = "");
public sealed record TokenUsageTotals(decimal Tokens, decimal CostUsd);
public sealed record TokenUsageTool(string Tool, decimal Tokens, decimal CostUsd);
public sealed record TokenUsageVm(string Vm, string User, bool Deleted, decimal Tokens, decimal CostUsd,
    DateTimeOffset? LastReportedAt, IReadOnlyList<TokenUsageTool> Tools);
public sealed record TokenUsageUser(string User, decimal Tokens, decimal CostUsd, int Vms,
    DateTimeOffset? LastReportedAt, IReadOnlyList<TokenUsageTool> Tools);
public sealed record TokenUsageSummary(string Window, DateTimeOffset GeneratedAt, TokenUsageTotals Totals,
    IReadOnlyList<TokenUsageUser> ByUser, IReadOnlyList<TokenUsageVm> ByVm);
