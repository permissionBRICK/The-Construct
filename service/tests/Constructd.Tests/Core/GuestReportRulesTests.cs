using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

public sealed class GuestReportRulesTests
{
    private static readonly DateTimeOffset InitialInstall = new(2026, 9, 12, 8, 30, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reinstall = InitialInstall.AddDays(4);

    [Fact]
    public void First_provisioning_is_also_the_last_reinstall()
    {
        var merged = GuestReportRules.Merge(null, Report(provisionedAt: InitialInstall));

        Assert.Equal(InitialInstall, merged.ProvisionedAt);
        Assert.Equal(InitialInstall, merged.ReinstalledAt);
    }

    [Fact]
    public void Reinstall_replaces_the_initial_install_date_but_reprovision_does_not_move_it()
    {
        var first = GuestReportRules.Merge(null, Report(provisionedAt: InitialInstall));
        var reinstalled = GuestReportRules.Merge(first, Report(reinstalledAt: Reinstall));
        var reprovisioned = GuestReportRules.Merge(reinstalled, Report(provisionedAt: Reinstall.AddDays(1)));

        Assert.Equal(Reinstall, reinstalled.ReinstalledAt);
        Assert.Equal(Reinstall, reprovisioned.ReinstalledAt);
        Assert.Equal(Reinstall.AddDays(1), reprovisioned.ProvisionedAt);
    }

    [Fact]
    public void Presentation_backfills_old_records_without_changing_the_stored_report()
    {
        var stored = Report(provisionedAt: InitialInstall);

        var presented = GuestReportRules.ForPresentation(stored);

        Assert.Null(stored.ReinstalledAt);
        Assert.Equal(InitialInstall, presented.ReinstalledAt);
    }

    private static GuestReport Report(DateTimeOffset? provisionedAt = null, DateTimeOffset? reinstalledAt = null) =>
        new("abcdef0", provisionedAt, reinstalledAt, InitialInstall, GuestReportProvenance.Provisioner, null, null);
}
