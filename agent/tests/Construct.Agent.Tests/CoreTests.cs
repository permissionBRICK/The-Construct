using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Events;
using Construct.Agent.Protection;

namespace Construct.Agent.Tests;

/// <summary>Pieces without an engine: the event buffer, the engine stream's resume and reset, the protection.</summary>
public sealed class CoreTests
{
    [Theory]
    [InlineData(96, 1120, 920)]
    [InlineData(120, 1400, 1150)] // 125 %: the FatPC's window was 1200 x 820 device pixels, too small for the move's list (R6)
    public void The_window_opens_big_enough_for_the_upgrade_in_the_screens_dpi(int dpi, int width, int height)
    {
        var big = Construct.Companion.Core.Desktop.WindowPlacement.Main(new(0, 0, 3840, 2100), dpi);
        Assert.Equal((width, height), (big.Width, big.Height));
        Assert.Equal(((3840 - width) / 2, (2100 - height) / 2), (big.X, big.Y));
        // Never larger than the working area.
        var small = Construct.Companion.Core.Desktop.WindowPlacement.Main(new(0, 0, 1920, 1040), 120);
        Assert.Equal((260, 0, 1400, 1040), (small.X, small.Y, small.Width, small.Height));
    }

    [Theory]
    [InlineData("opencode v2.3.2", "2.3.2")] // OpenCode 2
    [InlineData("1.19.6", "1.19.6")] // OpenCode 1
    public void The_probe_reads_the_opencode_version_of_both_generations(string output, string version) =>
        Assert.Equal(version, Construct.Companion.Core.Probe.ProbeParser.ExtractVersion(output));

    [Theory]
    [InlineData(300, 96, 1, 300)]
    [InlineData(300.2, 144, 1, 451)] // 150 %: device pixels, rounded up so nothing scrolls
    [InlineData(300, 96, 1.25, 375)] // the page's zoom
    [InlineData(40, 96, 1, 120)] // at least a header's worth
    [InlineData(40, 192, 1, 240)]
    public void The_pop_out_takes_the_height_its_page_measured(double css, int dpi, double zoom, int height) =>
        Assert.Equal(height, Construct.Companion.Core.Desktop.TrayModel.FittedHeight(css, dpi, zoom));

    [Fact]
    public async Task An_engine_stream_resumes_after_a_drop_and_ends_each_job_once_across_a_reset()
    {
        var events = new AgentEvents();
        var requests = new List<string>();
        using var stop = new CancellationTokenSource();
        var handler = new ScriptedHandler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (requests) requests.Add(path);
            if (path == "/api/v1/jobs/j2") return """{"id":"j2","state":"succeeded","vmName":"vm2"}""";
            return requests.Count(r => r.StartsWith("/api/v1/events", StringComparison.Ordinal)) switch
            {
                // The first connection takes its starting point, sees two jobs start and drops.
                1 => Engine((10, "reset", null), (11, "job.started", "j1"), (12, "job.started", "j2")),
                // The second resumes after 12: j1 ends, then the engine restarted and lost the point.
                2 => Engine((13, "job.ended", "j1"), (500, "reset", null), (501, "job.ended", "j2"), (502, "vm.created", "vm3")),
                _ => throw new OperationCanceledException(),
            };
        });
        var streams = new EngineStreams(events, _ => new HttpClient(handler, false) { BaseAddress = new Uri("https://engine.test/") }, () => ["e1"], stop.Token)
        {
            RetryDelay = TimeSpan.FromMilliseconds(10),
        };

        streams.Sync();
        using var all = events.Subscribe(0);
        var seen = new List<AgentEvent>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!seen.Any(e => e.Type == "vm.created"))
        {
            await all.Live.WaitToReadAsync(timeout.Token);
            while (all.Live.TryRead(out var item)) seen.Add(item);
        }

        await stop.CancelAsync();
        Assert.Equal(["engine.reset", "job.started", "job.started", "job.ended", "engine.reset", "job.ended", "vm.created"], seen.Select(e => e.Type));
        Assert.Equal([null, "j1", "j2", "j1", null, "j2", "vm3"], seen.Select(e => e.Data?["id"]?.GetValue<string>()));
        Assert.All(seen, e => Assert.Equal("e1", e.Engine));
        Assert.Equal("vm2", seen[5].Data!["vm"]!.GetValue<string>());
        lock (requests) Assert.Equal(["/api/v1/events", "/api/v1/events?since=12", "/api/v1/jobs/j2"], requests.Take(3));
    }

    private static string Engine(params (long Seq, string Type, string? Id)[] items) =>
        string.Concat(items.Select(i => $"id: {i.Seq}\nevent: {i.Type}\ndata: {{\"seq\":{i.Seq},\"type\":\"{i.Type}\",\"id\":{(i.Id is null ? "null" : $"\"{i.Id}\"")}}}\n\n"));

    [Fact]
    public void The_event_buffer_reports_a_gap_instead_of_a_silent_hole()
    {
        var events = new AgentEvents();
        var first = events.Publish("agent").Seq;
        for (var i = 1; i < AgentEvents.Capacity + 10; i++) events.Publish("agent");
        var last = first + AgentEvents.Capacity + 9;

        using (var behind = events.Subscribe(first + 5)) Assert.True(behind.Gap);
        using (var current = events.Subscribe(last - 5))
        {
            Assert.False(current.Gap);
            Assert.Equal(5, current.Replay.Count);
        }

        // A sequence from an earlier run of the agent is behind this run's first event: a gap too.
        using (var earlier = events.Subscribe(first - 100)) Assert.True(earlier.Gap);

        using var live = events.Subscribe(last);
        events.Publish("pairing");
        Assert.True(live.Live.TryRead(out var next));
        Assert.Equal(last + 1, next!.Seq);
    }

    [Fact]
    public void The_file_protection_keeps_its_key_in_a_private_file_and_opens_what_it_protected()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-test-protection-" + Guid.NewGuid().ToString("n"));
        try
        {
            var protection = LinuxProtection.Open(directory, null, LinuxProtection.File);
            Assert.True(protection.Weak);
            var sealedValue = protection.Protect("secret"u8);
            Assert.DoesNotContain("secret", Encoding.UTF8.GetString(sealedValue));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(directory, LinuxProtection.KeyFileName)));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));

            // The recorded choice is kept on the next start.
            var reopened = LinuxProtection.Open(directory, LinuxProtection.File, null);
            Assert.Equal("secret", Encoding.UTF8.GetString(reopened.Unprotect(sealedValue)));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void An_agent_keeps_its_device_key_across_restarts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-test-identity-" + Guid.NewGuid().ToString("n"));
        try
        {
            var options = new AgentOptions { DataDirectory = directory, Protection = "file" };
            string id;
            using (var first = new DeviceAgent(options)) id = first.DeviceId;
            using var second = new DeviceAgent(options);
            Assert.Equal(id, second.DeviceId);
            Assert.False(second.Device().Registered);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(request), Encoding.UTF8, "text/event-stream") });
    }
}
