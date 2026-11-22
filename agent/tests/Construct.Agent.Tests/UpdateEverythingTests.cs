using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;
using Construct.Agent.Update;

namespace Construct.Agent.Tests;

/// <summary>"Update everything" asks only engines the user administers to update and each engine only for the user's own VMs.</summary>
public sealed class UpdateEverythingTests : IAsyncDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "agent-test-everything-" + Guid.NewGuid().ToString("n"));
    private FakeDevice? _device;

    [Fact]
    public async Task An_administered_engine_updates_first_then_the_users_vms_and_a_merely_used_engine_only_reprovisions_them()
    {
        var polls = 0;
        var engines = new FakeEngines().Add("mine").Add("shared")
            .On("mine", "GET whoami", """{"name":"alice","role":"admin"}""")
            .On("mine", "GET host/updates/status", """{"available":{"version":"1.2.0","commit":"abc"}}""")
            .On("mine", "POST host/updates/run", """{"state":"running","jobId":"job-1","updateId":"u1"}""")
            // The engine restarts while it updates: one poll finds nobody.
            .On("mine", "GET jobs/job-1", _ => ++polls switch
            {
                1 => JsonNode.Parse("""{"state":"running"}"""),
                2 => throw new AgentFailure(502, "engine-unreachable", "restarting"),
                _ => JsonNode.Parse("""{"state":"succeeded"}"""),
            })
            .On("mine", "GET health", """{"status":"ok"}""")
            .On("mine", "POST me/vms/reprovision", """{"vms":["work","play"]}""")
            .On("shared", "GET whoami", """{"name":"alice","role":"user"}""")
            .On("shared", "POST me/vms/reprovision", """{"vms":["lab"]}""");
        _device = new FakeDevice(_directory, engines);

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["GET whoami", "GET host/updates/status", "POST host/updates/run", "GET jobs/job-1", "GET jobs/job-1", "GET jobs/job-1", "GET health", "POST me/vms/reprovision"], engines.CallsTo("mine"));
        Assert.Equal(["GET whoami", "POST me/vms/reprovision"], engines.CallsTo("shared"));
        var parts = _device.Everything.View().Parts.Select(p => (p.Part, p.Engine, p.State)).ToArray();
        Assert.Equal([("engine", "mine", "done"), ("vms", "mine", "done"), ("engine", "shared", "skipped"), ("vms", "shared", "done"), ("device", null, "skipped")], parts);
        Assert.False(_device.Everything.View().Running);
    }

    [Fact]
    public async Task A_host_update_that_rolled_back_after_its_handover_leaves_the_vms_alone()
    {
        // The job reported success at the handover, then the install rolled back: the engine answers ok, but with the old commit.
        var engines = new FakeEngines().Add("mine")
            .On("mine", "GET whoami", """{"name":"alice","role":"admin"}""")
            .On("mine", "GET host/updates/status", """{"available":{"version":"1.2.0","commit":"abc123"}}""")
            .On("mine", "POST host/updates/run", """{"state":"running","jobId":"job-1"}""")
            .On("mine", "GET jobs/job-1", """{"state":"succeeded"}""")
            .On("mine", "GET health", """{"status":"ok","commit":"old999"}""");
        _device = new FakeDevice(_directory, engines);

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("POST me/vms/reprovision", engines.CallsTo("mine"));
        var parts = _device.Everything.View().Parts.Where(p => p.Engine == "mine").ToArray();
        Assert.Equal(["failed", "skipped"], parts.Select(p => p.State));
        Assert.Contains("rolled back", parts[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_host_update_that_installed_a_newer_release_than_its_offer_is_not_taken_for_rolled_back()
    {
        // FatPC 2026-11-22: the offer (read up to 15 minutes before) named the first of two nightlies minutes apart; the run
        // read the channel again and installed the second, and the VMs were skipped as after a rollback.
        var engines = new FakeEngines().Add("mine")
            .On("mine", "GET whoami", """{"name":"alice","role":"admin"}""")
            .On("mine", "GET host/updates/status", """
                {"available":{"version":"1.0.0+older","commit":"older111"},
                 "current":{"updateId":"u1","commit":"newer222","state":"succeeded"},"history":[{"updateId":"u1","commit":"newer222"}]}
                """)
            .On("mine", "POST host/updates/run", """{"state":"started","jobId":"job-1","updateId":"u1"}""")
            .On("mine", "GET jobs/job-1", """{"state":"succeeded"}""")
            .On("mine", "GET health", """{"status":"ok","commit":"newer222"}""")
            .On("mine", "POST me/vms/reprovision", """{"vms":["work"]}""");
        _device = new FakeDevice(_directory, engines);

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Contains("POST me/vms/reprovision", engines.CallsTo("mine"));
        Assert.Equal(["done", "done"], _device.Everything.View().Parts.Where(p => p.Engine == "mine").Select(p => p.State));
    }

    [Theory]
    [InlineData("1.0.0+0d488a3b8a7f33413a6cb6dc8e2501888e78c716", "1.0.0 · 0d488a3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abc", "1.2.3 · abc")]
    [InlineData(null, "")]
    public void A_host_version_is_shown_as_the_update_tile_shows_it(string? version, string shown)
    {
        // FatPC 2026-11-22: "Updating to 1.0.0+<40 characters>" ran over the block's border.
        Assert.Equal(shown, UpdateEverything.ShortVersion(version));
    }

    [Fact]
    public async Task A_host_update_that_fails_leaves_that_hosts_vms_alone()
    {
        var engines = new FakeEngines().Add("mine")
            .On("mine", "GET whoami", """{"name":"alice","role":"admin"}""")
            .On("mine", "GET host/updates/status", """{"available":{"version":"1.2.0","commit":"abc"}}""")
            .On("mine", "POST host/updates/run", """{"state":"running","jobId":"job-1"}""")
            .On("mine", "GET jobs/job-1", """{"state":"failed"}""");
        _device = new FakeDevice(_directory, engines);

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("POST me/vms/reprovision", engines.CallsTo("mine"));
        Assert.Equal(["failed", "skipped"], _device.Everything.View().Parts.Where(p => p.Engine == "mine").Select(p => p.State));
    }

    [Fact]
    public async Task An_administered_engine_with_nothing_on_offer_is_not_asked_to_update()
    {
        var engines = new FakeEngines().Add("mine")
            .On("mine", "GET whoami", """{"name":"alice","role":"admin"}""")
            .On("mine", "GET host/updates/status", """{"available":null}""")
            .On("mine", "POST me/vms/reprovision", """{"vms":[]}""");
        _device = new FakeDevice(_directory, engines);

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["GET whoami", "GET host/updates/status", "POST me/vms/reprovision"], engines.CallsTo("mine"));
    }

    [Fact]
    public async Task After_a_host_update_the_vms_wait_until_the_restarted_engine_answers_healthy_and_take_the_reprovision()
    {
        // Finding 20: the reprovision went out while the restarted engine still answered 503.
        var health = 0;
        var posts = 0;
        var engines = new FakeEngines().Add("mine")
            .On("mine", "GET whoami", """{"name":"alice","role":"admin"}""")
            .On("mine", "GET host/updates/status", """{"available":{"version":"1.2.0","commit":"abc"}}""")
            .On("mine", "POST host/updates/run", """{"state":"running","jobId":"job-1"}""")
            .On("mine", "GET jobs/job-1", """{"state":"succeeded"}""")
            .On("mine", "GET health", _ => ++health switch
            {
                1 => throw new AgentFailure(502, "engine-unreachable", "restarting"),
                2 => JsonNode.Parse("""{"status":"maintenance"}"""),
                _ => JsonNode.Parse("""{"status":"ok"}"""),
            })
            .On("mine", "POST me/vms/reprovision", _ => ++posts == 1
                ? throw new AgentFailure(502, "engine-refused", "The engine answered 503.")
                : JsonNode.Parse("""{"vms":["work"]}"""));
        _device = new FakeDevice(_directory, engines);

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["GET health", "GET health", "GET health", "POST me/vms/reprovision", "POST me/vms/reprovision"], engines.CallsTo("mine").SkipWhile(c => c != "GET health"));
        Assert.Equal(("done", "1 VM reprovisioning one after the other."), _device.Everything.View().Parts.Where(p => p.Part == "vms").Select(p => (p.State, p.Detail)).Single());
    }

    [Fact]
    public async Task A_host_on_the_old_stack_is_left_out_with_a_note_and_its_vms_are_not_named()
    {
        // Finding 19: the old stack has no GET /engine; it is updated from its own host page.
        var engines = new FakeEngines().Add("old", known: false).Add("mine")
            .On("mine", "GET whoami", """{"name":"alice","role":"user"}""")
            .On("mine", "POST me/vms/reprovision", """{"vms":[]}""");
        _device = new FakeDevice(_directory, engines);

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        // Not its admin (the old host refuses its update status): nothing else is asked.
        Assert.Equal(["GET engine", "GET host/updates/status"], engines.CallsTo("old"));
        var parts = _device.Everything.View().Parts.Select(p => (p.Part, p.Engine, p.State, p.Detail)).ToArray();
        Assert.Equal([("engine", "old", "skipped", UpdateEverything.OldStackNote), ("engine", "mine", "skipped", "Its admin updates this host."),
            ("vms", "mine", "done", "No VMs of yours here."), ("device", null, "skipped", "This agent does not run from an installed package.")], parts);
    }

    private const string NewCommit = "b62124e768e638e339c21fa7c93fb88e5d522843";

    /// <summary>An old host of the stack before the rework: no <c>GET /engine</c> until its update moved it to the new stack.</summary>
    private static FakeEngines OldHost(string latestCommit, Action? applied = null)
    {
        var moved = false;
        var engines = new FakeEngines().Add("old", known: false);
        return engines
            .On("old", "GET engine", _ => moved ? JsonNode.Parse("""{"engineId":"e-old","hostPublicKey":"k"}""") : throw new AgentFailure(404, "engine-refused", "The engine answered 404."))
            .On("old", "GET host/updates/status", _ => JsonNode.Parse("""{"supportsAutoApply":true,"installed":{"commit":"aaaa"},"current":{"updateId":"u1","state":"applying"}}"""))
            .On("old", "POST host/updates/check", _ => new JsonObject
            {
                ["installed"] = new JsonObject { ["commit"] = new string('a', 40) },
                ["latest"] = new JsonObject { ["commit"] = latestCommit, ["releaseTag"] = "host-" + latestCommit, ["compatible"] = true, ["reasons"] = new JsonArray() },
            })
            .On("old", "GET host/config", """{"updates":{"repository":"example/construct","channel":"stable"}}""")
            .On("old", "POST host/updates/stage", _ =>
            {
                moved = true;
                applied?.Invoke();
                return JsonNode.Parse("""{"updateId":"u2","state":"staged"}""");
            })
            .On("old", "GET health", $$"""{"status":"ok","commit":"{{latestCommit}}"}""")
            .On("old", "POST me/vms/reprovision", """{"vms":["far"]}""");
    }

    /// <summary>A release channel where <paramref name="commit"/>'s host release ships the device agent (the new Construct).</summary>
    private static FakeDevice.StaticHandler Releases(string commit) => new(request =>
        request.RequestUri!.AbsoluteUri == $"https://github.com/example/construct/releases/download/host-{commit}/manifest.json"
            ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"version":"1.0.0+x","packages":{"agentLinux":{"asset":"a.zip","sha256":"00"}}}""") }
            : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));

    [Fact]
    public async Task An_old_host_the_user_administers_that_offers_the_new_version_moves_through_its_own_update_and_its_vms_follow()
    {
        // R5 (owner, 2026-11-21): Update brings every Construct of this device over, an old shared host included.
        var engines = OldHost(NewCommit)
            .On("old", "GET host/updates/status", """{"supportsAutoApply":true,"current":{"updateId":"u1","state":"succeeded"}}""");
        _device = new FakeDevice(_directory, engines, channel: Releases(NewCommit));
        var connected = new List<string>();
        _device.Everything.Connect = (id, _) => { connected.Add(id); return Task.CompletedTask; };

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        var calls = engines.CallsTo("old");
        Assert.Equal(["GET engine", "GET host/updates/status", "POST host/updates/check", "GET host/config", "POST host/updates/stage", "GET engine", "GET health", "GET health", "POST me/vms/reprovision"], calls);
        var stage = engines.Calls.Single(c => c.Call == "POST host/updates/stage").Body!;
        Assert.Equal("host-" + NewCommit, stage["releaseTag"]!.GetValue<string>());
        Assert.True(stage["autoApply"]!.GetValue<bool>());
        Assert.Equal(["old"], connected);
        var parts = _device.Everything.View().Parts.Where(p => p.Engine == "old").Select(p => (p.Part, p.State)).ToArray();
        Assert.Equal([("engine", "done"), ("vms", "done")], parts);
    }

    [Fact]
    public async Task An_old_host_that_offers_another_release_of_the_old_stack_stays_as_it_is()
    {
        // Before the merge to main an old host following main offers old-stack releases only: nothing is staged.
        var engines = OldHost("c0ffee" + new string('1', 34))
            .On("old", "GET host/updates/status", """{"supportsAutoApply":true,"current":null}""");
        _device = new FakeDevice(_directory, engines, channel: Releases(NewCommit));

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("POST host/updates/stage", engines.CallsTo("old"));
        Assert.DoesNotContain("POST me/vms/reprovision", engines.CallsTo("old"));
        Assert.Equal([("engine", "skipped", UpdateEverything.OldStackNote)],
            _device.Everything.View().Parts.Where(p => p.Engine == "old").Select(p => (p.Part, p.State, p.Detail)));
    }

    [Fact]
    public async Task An_old_host_whose_update_rolls_back_keeps_its_vms_as_they_are()
    {
        var engines = OldHost(NewCommit)
            .On("old", "GET engine", _ => throw new AgentFailure(404, "engine-refused", "The engine answered 404."))
            .On("old", "GET host/updates/status", """{"supportsAutoApply":true,"current":{"updateId":"u2","state":"rolledBack"}}""");
        _device = new FakeDevice(_directory, engines, channel: Releases(NewCommit));

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.DoesNotContain("POST me/vms/reprovision", engines.CallsTo("old"));
        var parts = _device.Everything.View().Parts.Where(p => p.Engine == "old").ToArray();
        Assert.Equal(["failed", "skipped"], parts.Select(p => p.State));
        Assert.Contains("rolledBack", parts[0].Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_old_host_that_answers_as_the_new_stack_in_maintenance_and_then_rolls_back_has_not_moved()
    {
        // Review of R5: the new stack answers GET /engine while it still finishes the update; the update rolled back
        // after that, and Move over had remembered the host as moved (later runs reprovisioned at the old stack).
        var polls = 0;
        var engines = OldHost(NewCommit)
            .On("old", "GET engine", _ => ++polls <= 2
                ? JsonNode.Parse("""{"engineId":"e-old","hostPublicKey":"k"}""")
                : throw new AgentFailure(404, "engine-refused", "The engine answered 404."))
            .On("old", "GET health", _ => JsonNode.Parse(polls <= 2 ? """{"status":"maintenance"}""" : $$"""{"status":"ok","commit":"{{new string('a', 40)}}"}"""))
            .On("old", "GET host/updates/status", """{"supportsAutoApply":true,"current":{"updateId":"u2","state":"rolledBack"}}""");
        var update = new OldHostUpdate(engines, (_, _, _) => Task.FromResult(true), TimeProvider.System) { PollInterval = TimeSpan.FromMilliseconds(1) };

        var problem = await update.RunAsync("old", new OldHostOffer("host-" + NewCommit, NewCommit, null), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal("Update rolledBack; previous version kept", problem);
    }

    [Fact]
    public async Task An_old_host_has_moved_only_once_it_is_healthy_with_the_offered_commit()
    {
        var health = 0;
        var engines = OldHost(NewCommit)
            .On("old", "GET engine", """{"engineId":"e-old","hostPublicKey":"k"}""")
            .On("old", "GET health", _ => JsonNode.Parse(++health switch
            {
                1 => """{"status":"maintenance"}""",
                2 => $$"""{"status":"ok","commit":"{{new string('a', 40)}}"}""",
                _ => $$"""{"status":"ok","commit":"{{NewCommit}}"}""",
            }));
        var update = new OldHostUpdate(engines, (_, _, _) => Task.FromResult(true), TimeProvider.System) { PollInterval = TimeSpan.FromMilliseconds(1) };

        Assert.Null(await update.RunAsync("old", new OldHostOffer("host-" + NewCommit, NewCommit, null), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(3, health);
    }

    public async ValueTask DisposeAsync()
    {
        if (_device is not null) await _device.DisposeAsync();
        try { Directory.Delete(_directory, true); }
        catch (DirectoryNotFoundException) { }
    }
}
