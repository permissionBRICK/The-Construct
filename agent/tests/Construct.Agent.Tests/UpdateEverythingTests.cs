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

        Assert.Equal(["GET engine"], engines.CallsTo("old"));
        var parts = _device.Everything.View().Parts.Select(p => (p.Part, p.Engine, p.State, p.Detail)).ToArray();
        Assert.Equal([("engine", "old", "skipped", UpdateEverything.OldStackNote), ("engine", "mine", "skipped", "Its admin updates this host."),
            ("vms", "mine", "done", "No VMs of yours here."), ("device", null, "skipped", "This agent does not run from an installed package.")], parts);
    }

    public async ValueTask DisposeAsync()
    {
        if (_device is not null) await _device.DisposeAsync();
        try { Directory.Delete(_directory, true); }
        catch (DirectoryNotFoundException) { }
    }
}
