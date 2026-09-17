using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Hosting;

public sealed record TokenUsageVmFigures(TokenUsageTotals Today, TokenUsageTotals Month, DateTimeOffset? LastReportedAt);
public sealed class TokenUsageReader(ITokenUsageStore store, IClock clock)
{
    public async Task<TokenUsageVmFigures> VmAsync(string vm, string? owner, CancellationToken ct)
    {
        var rows = await store.ListAsync(owner, vm, ct);
        return new(TokenUsageMath.Aggregate(rows, "today", clock.UtcNow).Totals,
            TokenUsageMath.Aggregate(rows, "month", clock.UtcNow).Totals, rows.Count == 0 ? null : rows.Max(r => r.ReportedAt));
    }
    public async Task<decimal> UserMonthAsync(string owner, CancellationToken ct) =>
        TokenUsageMath.Aggregate(await store.ListAsync(owner, null, ct), "month", clock.UtcNow).Totals.Tokens;
}
