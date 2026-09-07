using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

/// <summary>Reports are patches: absent success fields never erase earlier success facts.</summary>
public static class GuestReportRules
{
    public static GuestReport Merge(GuestReport? previous, GuestReport report)
    {
        var old = previous ?? GuestReport.Unknown;
        return new(report.ConstructCommit ?? old.ConstructCommit,
            report.ProvisionedAt ?? old.ProvisionedAt, report.ReinstalledAt ?? old.ReinstalledAt,
            report.ReportedAt ?? old.ReportedAt,
            report.Provenance == GuestReportProvenance.Unknown ? old.Provenance : report.Provenance,
            report.LastAttemptAt ?? old.LastAttemptAt, report.LastAttemptOutcome ?? old.LastAttemptOutcome);
    }
}
