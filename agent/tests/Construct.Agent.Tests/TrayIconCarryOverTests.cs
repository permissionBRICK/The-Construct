using Construct.Agent.Update;

namespace Construct.Agent.Tests;

/// <summary>
/// Windows 11 remembers a tray icon's taskbar place per executable path. The agent always runs from <c>app\</c>, so the
/// place is given once, on the first start there: the one the version before had (an old <c>versions\</c> folder, the old
/// Companion), else the taskbar; after that the user's choice is never touched.
/// </summary>
public sealed class TrayIconCarryOverTests
{
    private static readonly string Agent = Path.Combine(Path.GetTempPath(), "agent");
    private static readonly string Self = Path.Combine(Agent, "app", "construct-agent.exe");
    private static readonly string Old = Path.Combine(Agent, "versions", "1.0.0+aaa", "construct-agent.exe");
    private static readonly string Before = Path.Combine(Agent, "versions", "1.0.0+bbb", "construct-agent.exe");
    private static readonly string Companion = Path.Combine(Path.GetTempPath(), "Programs", "ConstructCompanion", "ConstructCompanion.exe");

    private sealed class Settings(params TrayIconEntry[] entries) : ITrayIconSettings
    {
        public List<TrayIconEntry> Entries { get; } = [.. entries];

        public IReadOnlyList<TrayIconEntry> List() => [.. Entries];

        public void SetPromoted(string id, int value)
        {
            var at = Entries.FindIndex(e => e.Id == id);
            Entries[at] = Entries[at] with { Promoted = value };
        }

        public void Delete(string id) => Entries.RemoveAll(e => e.Id == id);

        public int? PromotedOf(string id) => Entries.Single(e => e.Id == id).Promoted;
    }

    [Fact]
    public void A_fresh_install_goes_on_the_taskbar()
    {
        var settings = new Settings(new("self", Self, null), new("other", @"C:\Program Files\Other\other.exe", 0));

        Assert.Equal(TrayIconCarryOver.Outcome.Changed, TrayIconCarryOver.Apply(settings, Self, Agent));
        Assert.Equal(1, settings.PromotedOf("self"));
        Assert.Equal(0, settings.PromotedOf("other"));
    }

    [Fact]
    public void The_move_from_version_folders_takes_the_place_the_version_before_had_and_drops_their_entries()
    {
        var settings = new Settings(new("old", Old, 1), new("before", Before, 0), new("comp", Companion, 1), new("self", Self, null));

        Assert.Equal(TrayIconCarryOver.Outcome.Changed, TrayIconCarryOver.Apply(settings, Self, Agent, Before));

        // The user kept the version before in the overflow: so does app\.
        Assert.Equal(0, settings.PromotedOf("self"));
        Assert.Equal(["comp", "self"], settings.Entries.Select(e => e.Id));
    }

    [Fact]
    public void Without_the_version_before_an_earlier_taskbar_place_wins()
    {
        var settings = new Settings(new("old", Old, 0), new("comp", Companion, 1), new("self", Self, null));

        TrayIconCarryOver.Apply(settings, Self, Agent);

        Assert.Equal(1, settings.PromotedOf("self"));
    }

    [Fact]
    public void A_place_the_user_already_chose_is_kept()
    {
        var settings = new Settings(new("before", Before, 1), new("self", Self, 0));

        Assert.Equal(TrayIconCarryOver.Outcome.Unchanged, TrayIconCarryOver.Apply(settings, Self, Agent, Before));
        Assert.Equal(0, settings.PromotedOf("self"));
    }

    [Fact]
    public void Before_Windows_has_an_entry_for_the_icon_it_asks_again()
    {
        Assert.Equal(TrayIconCarryOver.Outcome.NotYet, TrayIconCarryOver.Apply(new Settings(new TrayIconEntry("before", Before, 1)), Self, Agent, Before));
    }
}
