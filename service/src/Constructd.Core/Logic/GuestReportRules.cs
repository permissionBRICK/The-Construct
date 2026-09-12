using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

/// <summary>
/// Reports are patches: absent success fields never erase earlier success facts. The first
/// provisioning timestamp also records the initial disk creation as the last reinstall time.
/// A later reinstall replaces that value, while a later plain provisioning report does not move it.
/// </summary>
public static class GuestReportRules
{
    public static GuestReport Merge(GuestReport? previous, GuestReport report)
    {
        var old = previous ?? GuestReport.Unknown;
        return new(report.ConstructCommit ?? old.ConstructCommit,
            report.ProvisionedAt ?? old.ProvisionedAt, report.ReinstalledAt ?? old.ReinstalledAt ?? report.ProvisionedAt,
            report.ReportedAt ?? old.ReportedAt,
            report.Provenance == GuestReportProvenance.Unknown ? old.Provenance : report.Provenance,
            report.LastAttemptAt ?? old.LastAttemptAt, report.LastAttemptOutcome ?? old.LastAttemptOutcome);
    }

    /// <summary>Supplies the initial provisioning time for old records that predate the fallback.</summary>
    public static GuestReport ForPresentation(GuestReport? report)
    {
        var guest = report ?? GuestReport.Unknown;
        return guest.ReinstalledAt is null && guest.ProvisionedAt is not null
            ? guest with { ReinstalledAt = guest.ProvisionedAt }
            : guest;
    }
}
