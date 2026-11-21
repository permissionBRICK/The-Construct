using Construct.Agent.Tests.Support;
using Construct.Agent.Upgrade;

namespace Construct.Agent.Tests;

/// <summary>
/// The old config repository read in batches (one <c>git cat-file --batch</c> per question, FatPC test
/// 2026-11-21, where one file per git process opened some 60 console windows per dialog refresh): the
/// answers are the files' texts, whatever their names and bytes.
/// </summary>
public sealed class LegacyConfigRepoTests
{
    private const string Bom = "﻿";

    [Fact]
    public void Profiles_upstream_files_and_unmerged_changes_read_in_batches_keep_every_file()
    {
        using var f = new LegacyFixture();
        var big = "{\"name\":\"big\",\"provisionCommands\":[\"" + new string('x', 200_000) + "\"]}";
        f.ConfigRepo(new Dictionary<string, string>
        {
            ["projects/api.json"] = """{"name":"api"}""",
            ["projects/with space.json"] = """{"name":"with space"}""",
            ["projects/bom.json"] = Bom + """{"name":"bom"}""",
            ["projects/big.json"] = big,
            ["projects/default.json"] = """{"name":"default"}""",
            ["projects/nested/inner.json"] = """{"name":"inner"}""",
            ["projects/gone.json"] = """{"name":"gone"}""",
            ["manifest/remotes.json"] = """{"remotes":[]}""",
            ["bases/api.json"] = """{"name":"api"}""",
        }, "vm", new Dictionary<string, string?>
        {
            ["projects/api.json"] = """{"name":"api","hostPackages":["jq"]}""",
            ["projects/gone.json"] = null,
            ["projects/new one.json"] = """{"name":"new one"}""",
        });
        // Gone from the folder (not committed): these come from main's commit, through git alone.
        foreach (var name in new[] { "big", "bom", "with space" }) File.Delete(Path.Combine(f.Root, "config", "projects", name + ".json"));
        var repo = new LegacyConfigRepo(Path.Combine(f.Root, "config"));

        var profiles = repo.Profiles();
        Assert.Equal(["api", "big", "bom", "gone", "with space"], profiles.Keys.ToArray());
        Assert.Equal(big, profiles["big"]);
        Assert.Equal("""{"name":"bom"}""", profiles["bom"]);
        Assert.Equal("""{"name":"with space"}""", profiles["with space"]);

        Assert.Equal(["bases/api.json", "manifest/remotes.json"], repo.UpstreamFiles().Keys.ToArray());

        var changes = repo.UnmergedChanges().OrderBy(c => c.Profile, StringComparer.Ordinal).ToArray();
        Assert.Equal(["api", "gone", "new one"], changes.Select(c => c.Profile).ToArray());
        Assert.All(changes, c => Assert.Equal("vm", c.Branch));
        Assert.Equal("""{"name":"api","hostPackages":["jq"]}""", changes[0].Ours);
        Assert.Equal("""{"name":"api"}""", changes[0].Base);
        Assert.Equal("""{"name":"api"}""", changes[0].Theirs);
        Assert.Null(changes[1].Ours);
        Assert.Equal("""{"name":"gone"}""", changes[1].Base);
        Assert.Null(changes[2].Base);
        Assert.Null(changes[2].Theirs);
    }

    [Fact]
    public void A_folder_without_git_is_read_from_its_files_and_sees_no_branch()
    {
        using var f = new LegacyFixture();
        f.Write(Path.Combine("The-Construct", "config", "projects", "api.json"), """{"name":"api"}""");
        var repo = new LegacyConfigRepo(Path.Combine(f.Root, "config"));
        Assert.False(repo.IsGit);
        Assert.Equal(["api"], repo.Profiles().Keys.ToArray());
        Assert.Empty(repo.UnmergedChanges());
        Assert.Empty(repo.UpstreamFiles());
    }
}
