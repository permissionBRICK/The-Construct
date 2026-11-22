using Construct.Agent.Update;

namespace Construct.Agent.Tests;

/// <summary>
/// Windows 11 remembers a tray icon's taskbar place per executable path, and each update runs the agent from a new
/// <c>versions/</c> directory (FatPC 2026-11-22): the new version's entry gets the choice made for the previous one.
/// </summary>
public sealed class TrayIconCarryOverTests
{
    private static readonly string Versions = Path.Combine(Path.GetTempPath(), "agent", "versions");
    private static readonly string Old = Exe("1.0.0+aaa");
    private static readonly string Previous = Exe("1.0.0+bbb");
    private static readonly string Current = Exe("1.0.0+ccc");
    private static readonly string Companion = Path.Combine(Path.GetTempPath(), "Programs", "ConstructCompanion", "ConstructCompanion.exe");

    private static string Exe(string version) => Path.Combine(Versions, version, "construct-agent.exe");

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

    private static TrayIconCarryOver.Outcome Apply(Settings settings, string? previous = null, params string[] onDisk) =>
        TrayIconCarryOver.Apply(settings, Current, Versions, previous, path => onDisk.Contains(path) || path == Current);

    [Fact]
    public void The_new_version_gets_the_previous_versions_place_and_entries_of_removed_versions_go()
    {
        var settings = new Settings(new("old", Old, 1), new("prev", Previous, 1), new("comp", Companion, 1), new("self", Current, null),
            new("other", @"C:\Program Files\Other\other.exe", null));

        var outcome = Apply(settings, Previous, Previous);

        Assert.Equal(TrayIconCarryOver.Outcome.Changed, outcome);
        Assert.Equal(1, settings.PromotedOf("self"));
        // The version pruned from disk loses its entry; the previous version, the old Companion and other apps keep theirs.
        Assert.Equal(["prev", "comp", "self", "other"], settings.Entries.Select(e => e.Id));
    }

    [Fact]
    public void A_place_kept_in_the_overflow_is_carried_too()
    {
        var settings = new Settings(new("old", Old, 1), new("prev", Previous, 0), new("self", Current, null));

        Assert.Equal(TrayIconCarryOver.Outcome.Changed, Apply(settings, Previous, Previous, Old));
        Assert.Equal(0, settings.PromotedOf("self"));
    }

    [Fact]
    public void A_choice_made_for_this_version_stays()
    {
        var settings = new Settings(new("prev", Previous, 1), new("self", Current, 0));

        Assert.Equal(TrayIconCarryOver.Outcome.Unchanged, Apply(settings, Previous, Previous));
        Assert.Equal(0, settings.PromotedOf("self"));
    }

    [Fact]
    public void Before_windows_has_an_entry_for_this_version_nothing_changes()
    {
        var settings = new Settings(new("old", Old, 1), new("prev", Previous, 1));

        Assert.Equal(TrayIconCarryOver.Outcome.NotYet, Apply(settings, Previous));
        Assert.Equal(2, settings.Entries.Count);
    }

    [Fact]
    public void The_first_start_after_the_move_from_the_companion_takes_the_companions_place()
    {
        var settings = new Settings(new("comp", Companion, 1), new("self", Current, null));

        Assert.Equal(TrayIconCarryOver.Outcome.Changed, Apply(settings));
        Assert.Equal(1, settings.PromotedOf("self"));
    }

    [Fact]
    public void Without_an_earlier_choice_the_entry_is_left_to_windows()
    {
        var settings = new Settings(new("prev", Previous, null), new("comp", Companion, 0), new("self", Current, null));

        Assert.Equal(TrayIconCarryOver.Outcome.Unchanged, Apply(settings, Previous, Previous));
        Assert.Null(settings.PromotedOf("self"));
    }
}
