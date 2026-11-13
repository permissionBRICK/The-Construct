using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;

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
            .On("mine", "POST me/vms/reprovision", """{"vms":["work","play"]}""")
            .On("shared", "GET whoami", """{"name":"alice","role":"user"}""")
            .On("shared", "POST me/vms/reprovision", """{"vms":["lab"]}""");
        _device = new FakeDevice(_directory, engines);

        _device.Everything.Start();
        await _device.Everything.Work.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["GET whoami", "GET host/updates/status", "POST host/updates/run", "GET jobs/job-1", "GET jobs/job-1", "GET jobs/job-1", "POST me/vms/reprovision"], engines.CallsTo("mine"));
        Assert.Equal(["GET whoami", "POST me/vms/reprovision"], engines.CallsTo("shared"));
        var parts = _device.Everything.View().Parts.Select(p => (p.Part, p.Engine, p.State)).ToArray();
        Assert.Equal([("engine", "mine", "done"), ("vms", "mine", "done"), ("engine", "shared", "skipped"), ("vms", "shared", "done"), ("device", null, "skipped")], parts);
        Assert.False(_device.Everything.View().Running);
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

    public async ValueTask DisposeAsync()
    {
        if (_device is not null) await _device.DisposeAsync();
        try { Directory.Delete(_directory, true); }
        catch (DirectoryNotFoundException) { }
    }
}
