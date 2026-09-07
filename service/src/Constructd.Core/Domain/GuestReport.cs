namespace Constructd.Core.Domain;

public sealed record GuestReport(
    string? ConstructCommit,
    DateTimeOffset? ProvisionedAt,
    DateTimeOffset? ReinstalledAt,
    DateTimeOffset? ReportedAt,
    GuestReportProvenance Provenance,
    DateTimeOffset? LastAttemptAt,
    string? LastAttemptOutcome)
{
    public static GuestReport Unknown { get; } = new(null, null, null, null, GuestReportProvenance.Unknown, null, null);
}
