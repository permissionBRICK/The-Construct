using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;
using Constructd.Store.Records;
using Microsoft.Extensions.DependencyInjection;

namespace Construct.Agent.Tests;

/// <summary>
/// Upstream config repos after the move (design 9.7, docs/config-sync.md 7): a converted install keeps receiving the
/// changes to its shared profiles. The device fetches the company repository with plain git, the engine merges each
/// tracked file with the stored base, local edits survive, and the same field changed on both sides is a conflict.
/// </summary>
public sealed class UpstreamTests
{
    private const string V1 = """{"name":"api","repos":[{"url":"https://example.test/api.git"}],"sdks":{"node":"20"}}""";

    /// <summary>A company repo with <c>projects/api.json</c>, and an old install that imported it and edited it locally.</summary>
    private static Action<LegacyFixture> Shared(Action<string, string> made, bool manifest = true) => f =>
    {
        var (repo, commit) = f.UpstreamRepo(new Dictionary<string, string> { ["projects/api.json"] = V1 });
        made(repo, commit);
        f.Settings(new JsonObject
        {
            ["constructRepo"] = "permissionBRICK/The-Construct", ["constructRef"] = "main", ["installedCommit"] = new string('a', 40),
            ["gitUserName"] = "Ada", ["gitEmail"] = "ada@example.test", ["projects"] = new JsonArray("api"), ["t3code"] = true,
        });
        f.SshKey("agent_vm_ed25519");
        var files = new Dictionary<string, string>
        {
            // Local edit: a package added here.
            ["projects/api.json"] = """{"name":"api","repos":[{"url":"https://example.test/api.git"}],"sdks":{"node":"20"},"hostPackages":["jq"]}""",
        };
        if (manifest)
        {
            foreach (var (path, text) in Legacy(repo, commit)) files[path] = text;
        }

        f.ConfigRepo(files);
    };

    /// <summary>The old client's manifest of the shared file and its stored base.</summary>
    private static Dictionary<string, string> Legacy(string repo, string commit) => new()
    {
        ["manifest/remotes.json"] = new JsonArray(new JsonObject { ["url"] = repo }).ToJsonString(),
        ["manifest/api.json"] = new JsonObject
        {
            ["remoteUrl"] = repo, ["ref"] = "main", ["pathInRemote"] = "projects/api.json", ["importedAs"] = "api", ["baseCommit"] = commit,
        }.ToJsonString(),
        ["bases/api.json"] = V1,
    };

    private static async Task<UpgradeRig> MovedAsync(Action<LegacyFixture> build)
    {
        var rig = await UpgradeRig.CreateAsync(build);
        await rig.EngineStepAsync();
        await rig.SetupAsync();
        var view = await rig.ConvertAsync();
        Assert.Equal("converted", view["stage"]!.GetValue<string>());
        return rig;
    }

    private static async Task<JsonObject> ProfileAsync(UpgradeRig rig)
    {
        var engine = rig.Agent.Host.Upgrade.Bridge.Read().LocalEngine!;
        return (await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/api"))["profile"]!.AsObject();
    }

    private static async Task<string> RecordsAsync(UpgradeRig rig)
    {
        var account = (await rig.Agent.GetAsync("api/state"))["device"]!["account"]!.GetValue<string>();
        var records = (await rig.Local.Services.GetRequiredService<RecordStore>().FeedAsync(account, 0, TimeSpan.Zero, 10_000, CancellationToken.None)).Records;
        return string.Join('\n', records.OrderBy(r => r.Id, StringComparer.Ordinal).Select(r => $"{r.Id} {r.Revision}"));
    }

    private static JsonNode File(JsonNode view) => view["links"]!.AsArray().Single()!["files"]!.AsArray().Single()!;

    [Fact]
    public async Task A_moved_install_keeps_receiving_upstream_changes_merged_with_its_local_edits()
    {
        string repo = "", first = "";
        await using var rig = await MovedAsync(Shared((r, c) => (repo, first) = (r, c)));
        var fixture = rig.Fixture;

        // Nothing new upstream yet: the stored base is the upstream file, the local edit stays.
        var unchanged = await rig.Agent.PostAsync("api/upstream/refresh", null);
        Assert.Equal("up-to-date", File(unchanged)["state"]!.GetValue<string>());
        Assert.Equal(first, unchanged["links"]![0]!["commit"]!.GetValue<string>());

        // A colleague changes the shared profile upstream.
        var second = fixture.UpstreamCommit(repo, new Dictionary<string, string>
        {
            ["projects/api.json"] = """{"name":"api","repos":[{"url":"https://example.test/api.git"}],"sdks":{"node":"22"},"provisionCommands":["npm ci"]}""",
        });
        var merged = await rig.Agent.PostAsync("api/upstream/refresh", null);
        Assert.True(File(merged)["state"]!.GetValue<string>() == "merged", merged.ToJsonString());
        Assert.Equal(second, merged["links"]![0]!["commit"]!.GetValue<string>());
        var profile = await ProfileAsync(rig);
        Assert.Equal("22", profile["sdks"]!["node"]!.GetValue<string>());
        Assert.Equal("""["npm ci"]""", profile["provisionCommands"]!.ToJsonString());
        Assert.Equal("""["jq"]""", profile["hostPackages"]!.ToJsonString());

        // Refreshed again: nothing changes in the store.
        var records = await RecordsAsync(rig);
        Assert.Equal("up-to-date", File(await rig.Agent.PostAsync("api/upstream/refresh", null))["state"]!.GetValue<string>());
        Assert.Equal(records, await RecordsAsync(rig));
        // The last refresh is what the Projects page reads.
        var view = await rig.Agent.GetAsync("api/upstream");
        Assert.False(view["running"]!.GetValue<bool>());
        Assert.NotNull(view["refreshedAt"]);
        Assert.Equal(repo, view["links"]![0]!["url"]!.GetValue<string>());

        // The same field changed here and upstream: a conflict; the local value stays until it is resolved.
        var engine = rig.Agent.Host.Upgrade.Bridge.Read().LocalEngine!;
        var current = await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/api");
        var edited = current["profile"]!.DeepClone().AsObject();
        edited["sdks"]!["node"] = "24";
        var put = await rig.Agent.Api.PutAsJsonAsync($"api/engines/{engine}/api/v1/profiles/api", new JsonObject { ["profile"] = edited, ["baseRevision"] = current["revision"]!.GetValue<long>() });
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        fixture.UpstreamCommit(repo, new Dictionary<string, string>
        {
            ["projects/api.json"] = """{"name":"api","repos":[{"url":"https://example.test/api.git"}],"sdks":{"node":"18"},"provisionCommands":["npm ci"]}""",
        });
        var conflict = File(await rig.Agent.PostAsync("api/upstream/refresh", null));
        Assert.Equal("conflict", conflict["state"]!.GetValue<string>());
        Assert.Contains("sdks.node", conflict["message"]!.GetValue<string>());
        Assert.Equal("24", (await ProfileAsync(rig))["sdks"]!["node"]!.GetValue<string>());
        Assert.Single((await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profile-conflicts")).AsArray());
    }

    [Fact]
    public async Task Links_an_earlier_converter_kept_as_data_are_carried_over_and_a_file_removed_upstream_leaves_the_profile()
    {
        string repo = "", first = "";
        // An install moved before upstream links came to the new stack: the old converter kept them in legacy-upstream.json.
        await using var rig = await MovedAsync(Shared((r, c) => (repo, first) = (r, c), manifest: false));
        var files = new JsonObject();
        foreach (var (path, text) in Legacy(repo, first)) files[path] = text;
        System.IO.File.WriteAllText(Path.Combine(rig.Agent.Directory, "legacy-upstream.json"), new JsonObject { ["v"] = 1, ["files"] = files }.ToJsonString());

        rig.Fixture.UpstreamCommit(repo, new Dictionary<string, string>
        {
            ["projects/api.json"] = """{"name":"api","repos":[{"url":"https://example.test/api.git"}],"sdks":{"node":"22"}}""",
        });
        var merged = await rig.Agent.PostAsync("api/upstream/refresh", null);
        Assert.True(File(merged)["state"]!.GetValue<string>() == "merged", merged.ToJsonString());
        Assert.False(System.IO.File.Exists(Path.Combine(rig.Agent.Directory, "legacy-upstream.json")));
        var profile = await ProfileAsync(rig);
        Assert.Equal("22", profile["sdks"]!["node"]!.GetValue<string>());
        Assert.Equal("""["jq"]""", profile["hostPackages"]!.ToJsonString());

        // Removed upstream: the local profile stays, the link says so (config-sync 7).
        System.IO.File.Delete(Path.Combine(repo, "projects", "api.json"));
        rig.Fixture.UpstreamCommit(repo, new Dictionary<string, string> { ["README.md"] = "moved" });
        Assert.Equal("removed-upstream", File(await rig.Agent.PostAsync("api/upstream/refresh", null))["state"]!.GetValue<string>());
        Assert.Equal("22", (await ProfileAsync(rig))["sdks"]!["node"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_repository_that_cannot_be_fetched_is_named_and_changes_nothing()
    {
        string repo = "";
        await using var rig = await MovedAsync(Shared((r, _) => repo = r));
        var records = await RecordsAsync(rig);
        Directory.Delete(repo, true);

        var view = await rig.Agent.PostAsync("api/upstream/refresh", null);
        var link = view["links"]![0]!;
        Assert.NotNull(link["error"]);
        Assert.Equal("failed", File(view)["state"]!.GetValue<string>());
        Assert.Equal(records, await RecordsAsync(rig));
        Assert.Equal("""["jq"]""", (await ProfileAsync(rig))["hostPackages"]!.ToJsonString());
    }

    [Fact]
    public async Task Before_the_device_is_set_up_a_refresh_does_nothing()
    {
        await using var agent = await TestAgent.StartAsync();
        var view = await agent.PostAsync("api/upstream/refresh", null);
        Assert.Empty(view["links"]!.AsArray());
        Assert.Null(view["refreshedAt"]);
    }
}
