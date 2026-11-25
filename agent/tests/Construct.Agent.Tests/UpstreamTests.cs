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
    public async Task A_link_whose_ref_is_no_branch_name_is_refused_before_git_runs()
    {
        string repo = "";
        await using var rig = await MovedAsync(Shared((r, _) => repo = r));
        await rig.Agent.Host.Upstream.Links.AddAsync(new Upstream.UpstreamLink("https://git.example.test/other.git", "--upload-pack=touch /tmp/x",
            [new Upstream.UpstreamFile("projects/other.json", "other")]), CancellationToken.None);

        var view = await rig.Agent.PostAsync("api/upstream/refresh", null);
        var other = view["links"]!.AsArray().Single(l => l!["url"]!.GetValue<string>() == "https://git.example.test/other.git")!;
        Assert.Equal("The ref is no branch name.", other["error"]!.GetValue<string>());
        // Git never ran for it: no cache was made.
        Assert.False(Directory.Exists(Path.Combine(rig.Agent.Directory, "upstream", Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("https://git.example.test/other.git")))[..16])));
        // The other link is refreshed all the same.
        Assert.Equal("up-to-date", view["links"]!.AsArray().Single(l => l!["url"]!.GetValue<string>() == repo)!["files"]![0]!["state"]!.GetValue<string>());
    }

    [Fact]
    public async Task Before_the_device_is_set_up_a_refresh_does_nothing()
    {
        await using var agent = await TestAgent.StartAsync();
        var view = await agent.PostAsync("api/upstream/refresh", null);
        Assert.Empty(view["links"]!.AsArray());
        Assert.Null(view["refreshedAt"]);
    }

    [Fact]
    public async Task A_user_can_link_a_repo_browse_its_profiles_import_with_rename_refresh_and_remove_it()
    {
        await using var rig = await MovedAsync(f =>
        {
            f.Settings(new JsonObject
            {
                ["constructRepo"] = "permissionBRICK/The-Construct", ["constructRef"] = "main", ["installedCommit"] = new string('a', 40),
                ["gitUserName"] = "Ada", ["gitEmail"] = "ada@example.test", ["projects"] = new JsonArray("local"), ["t3code"] = true,
            });
            f.SshKey("agent_vm_ed25519");
            f.ConfigRepo(new Dictionary<string, string>
            {
                ["projects/local.json"] = """{"name":"local","hostPackages":["ripgrep"]}""",
            });
        });

        var (repo, commit1) = rig.Fixture.UpstreamRepo(new Dictionary<string, string>
        {
            ["projects/webshop.json"] = """{"name":"webshop","repos":[{"url":"https://example.test/webshop.git"}],"sdks":{"node":"20"}}""",
            ["projects/local.json"] = """{"name":"local","repos":[{"url":"https://example.test/local.git"}],"sdks":{"python":"3.12"}}""",
            ["projects/broken.json"] = """{"name":"broken","repos":"not-an-array"}""",
            ["projects/default.json"] = """{"name":"default"}""",
            ["projects/project.schema.json"] = """{"title":"schema"}""",
            ["projects/demo.sample.json"] = """{"name":"demo"}""",
        });

        // Link the repo: fetches first and returns discovered profiles (reserved/sample filtered, broken marked invalid, collision flagged).
        var added = await rig.Agent.PostAsync("api/upstream/repos", new JsonObject { ["url"] = repo });
        var id = added["id"]!.GetValue<string>();
        Assert.NotEmpty(id);
        Assert.Equal(repo, added["url"]!.GetValue<string>());
        Assert.Equal(commit1, added["commit"]!.GetValue<string>());
        var files = added["files"]!.AsArray();
        Assert.Equal(["broken", "local", "webshop"], files.Select(f => f!["name"]!.GetValue<string>()).ToArray());
        Assert.False(files[0]!["valid"]!.GetValue<bool>());
        Assert.True(files[1]!["existsLocally"]!.GetValue<bool>());
        Assert.True(files[1]!["valid"]!.GetValue<bool>());
        Assert.False(files[2]!["existsLocally"]!.GetValue<bool>());
        Assert.False(files[2]!["tracked"]!.GetValue<bool>());

        // Trying to import colliding "local" without renaming is refused and leaves the local profile untouched.
        var refused = await rig.Agent.PostAsync($"api/upstream/repos/{id}/import", new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["path"] = "projects/local.json" }),
        });
        Assert.Equal("refused", refused["results"]![0]!["state"]!.GetValue<string>());
        var engine = rig.Agent.Host.Upgrade.Bridge.Read().LocalEngine!;
        var localProfile = (await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/local"))["profile"]!.AsObject();
        Assert.Equal("""["ripgrep"]""", localProfile["hostPackages"]!.ToJsonString());

        // Import "webshop" as itself and "local" renamed as "team-local".
        var imported = await rig.Agent.PostAsync($"api/upstream/repos/{id}/import", new JsonObject
        {
            ["files"] = new JsonArray(
                new JsonObject { ["path"] = "projects/webshop.json" },
                new JsonObject { ["path"] = "projects/local.json", ["as"] = "team-local" }),
        });
        Assert.All(imported["results"]!.AsArray(), r => Assert.Equal("merged", r!["state"]!.GetValue<string>()));
        var webshop = (await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/webshop"))["profile"]!.AsObject();
        Assert.Equal("20", webshop["sdks"]!["node"]!.GetValue<string>());
        var teamLocal = (await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/team-local"))["profile"]!.AsObject();
        Assert.Equal("team-local", teamLocal["name"]!.GetValue<string>());
        Assert.Equal("3.12", teamLocal["sdks"]!["python"]!.GetValue<string>());

        // Listing files again shows both as tracked, and sees newly added upstream files.
        rig.Fixture.UpstreamCommit(repo, new Dictionary<string, string>
        {
            ["projects/webshop.json"] = """{"name":"webshop","repos":[{"url":"https://example.test/webshop.git"}],"sdks":{"node":"22"}}""",
            ["projects/billing.json"] = """{"name":"billing","sdks":{"go":"1.23"}}""",
        });
        var listed = await rig.Agent.GetAsync($"api/upstream/repos/{id}/files");
        var byPath = listed["files"]!.AsArray().ToDictionary(f => f!["path"]!.GetValue<string>());
        Assert.True(byPath["projects/webshop.json"]!["tracked"]!.GetValue<bool>());
        Assert.Equal("webshop", byPath["projects/webshop.json"]!["trackedAs"]!.GetValue<string>());
        Assert.True(byPath["projects/local.json"]!["tracked"]!.GetValue<bool>());
        Assert.Equal("team-local", byPath["projects/local.json"]!["trackedAs"]!.GetValue<string>());
        Assert.False(byPath["projects/billing.json"]!["tracked"]!.GetValue<bool>());

        // Refresh picks up the upstream change to webshop.
        var refreshed = await rig.Agent.PostAsync("api/upstream/refresh", null);
        var link = refreshed["links"]!.AsArray().Single()!;
        Assert.Equal("merged", link["files"]!.AsArray().Single(f => f!["profile"]!.GetValue<string>() == "webshop")!["state"]!.GetValue<string>());
        Assert.Equal("22", (await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/webshop"))["profile"]!["sdks"]!["node"]!.GetValue<string>());

        // Deleting webshop locally and explicitly re-importing it succeeds.
        var beforeDelete = await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/webshop");
        var del = await rig.Agent.Api.DeleteAsync($"api/engines/{engine}/api/v1/profiles/webshop?baseRevision={beforeDelete["revision"]!.GetValue<long>()}");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);
        Assert.Equal("written", (await del.Content.ReadFromJsonAsync<JsonObject>())!["status"]!.GetValue<string>());
        var reimported = await rig.Agent.PostAsync($"api/upstream/repos/{id}/import", new JsonObject
        {
            ["files"] = new JsonArray(new JsonObject { ["path"] = "projects/webshop.json" }),
        });
        Assert.Equal("merged", reimported["results"]![0]!["state"]!.GetValue<string>());
        Assert.Equal("22", (await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/webshop"))["profile"]!["sdks"]!["node"]!.GetValue<string>());

        // Removing the link clears it from GET /api/upstream while keeping imported profiles in the account.
        var removed = await rig.Agent.Api.DeleteAsync($"api/upstream/repos/{id}");
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty((await rig.Agent.GetAsync("api/upstream"))["links"]!.AsArray());
        Assert.Equal("22", (await rig.Agent.GetAsync($"api/engines/{engine}/api/v1/profiles/webshop"))["profile"]!["sdks"]!["node"]!.GetValue<string>());
    }

    [Fact]
    public async Task Linking_a_repo_discovers_root_profiles_when_no_projects_dir_exists_and_refuses_bad_urls()
    {
        await using var rig = await MovedAsync(f =>
        {
            f.Settings(new JsonObject
            {
                ["constructRepo"] = "permissionBRICK/The-Construct", ["constructRef"] = "main", ["installedCommit"] = new string('a', 40),
                ["gitUserName"] = "Ada", ["gitEmail"] = "ada@example.test", ["projects"] = new JsonArray(), ["t3code"] = true,
            });
            f.SshKey("agent_vm_ed25519");
            f.ConfigRepo(new Dictionary<string, string> { ["projects/seed.json"] = """{"name":"seed"}""" });
        });

        // Password in URL is refused before git runs and nothing is stored.
        var withPw = await rig.Agent.Api.PostAsJsonAsync("api/upstream/repos", new JsonObject { ["url"] = "https://alice:secret@git.example.test/configs.git" });
        Assert.Equal(HttpStatusCode.BadRequest, withPw.StatusCode);
        Assert.Equal("url-has-password", (await withPw.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());

        // Bad ref is refused before git runs.
        var badRef = await rig.Agent.Api.PostAsJsonAsync("api/upstream/repos", new JsonObject { ["url"] = "https://git.example.test/configs.git", ["ref"] = "--upload-pack=x" });
        Assert.Equal(HttpStatusCode.BadRequest, badRef.StatusCode);
        Assert.Equal("invalid-ref", (await badRef.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());

        // Missing repo fails fetch with 422 and is not saved.
        var missing = await rig.Agent.Api.PostAsJsonAsync("api/upstream/repos", new JsonObject { ["url"] = Path.Combine(rig.Agent.Directory, "does-not-exist") });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, missing.StatusCode);
        Assert.Empty((await rig.Agent.GetAsync("api/upstream"))["links"]!.AsArray());

        // Top-level *.json repo (no projects/ folder) is supported.
        var (rootRepo, _) = rig.Fixture.UpstreamRepo(new Dictionary<string, string>
        {
            ["svc.json"] = """{"name":"svc","sdks":{"dotnet":"10"}}""",
            ["README.md"] = "configs",
        });
        var added = await rig.Agent.PostAsync("api/upstream/repos", new JsonObject { ["url"] = rootRepo });
        var file = Assert.Single(added["files"]!.AsArray())!;
        Assert.Equal("svc.json", file["path"]!.GetValue<string>());
        Assert.Equal("svc", file["name"]!.GetValue<string>());
        Assert.True(file["valid"]!.GetValue<bool>());
    }
}
