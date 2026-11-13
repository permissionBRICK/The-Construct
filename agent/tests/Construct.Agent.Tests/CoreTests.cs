using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Agent.Events;
using Construct.Agent.Protection;

namespace Construct.Agent.Tests;

/// <summary>Pieces without an engine: the event buffer, the job watcher's reattach, the protection.</summary>
public sealed class CoreTests
{
    [Fact]
    public async Task A_job_stream_that_drops_is_reattached_without_repeating_lines_or_the_end()
    {
        var events = new AgentEvents();
        var attempts = 0;
        var handler = new ScriptedHandler(() => ++attempts switch
        {
            // The first attach drops after two lines, the second replays them and ends.
            1 => Sse("progress", """{"text":"one"}""", "progress", """{"text":"two"}"""),
            2 => Sse("progress", """{"text":"one"}""", "progress", """{"text":"two"}""", "progress", """{"text":"three"}""", "state", """{"state":"succeeded"}"""),
            _ => throw new InvalidOperationException("Attached after the end."),
        });
        using var client = new HttpClient(handler) { BaseAddress = new Uri("https://engine.test/") };
        var watcher = new JobWatcher(events, _ => client, CancellationToken.None) { RetryDelay = TimeSpan.FromMilliseconds(10) };

        await watcher.Watch("e1", "job-1").WaitAsync(TimeSpan.FromSeconds(10));

        using var all = events.Subscribe(0);
        Assert.Equal(["job.progress", "job.progress", "job.progress", "job.state"], all.Replay.Select(e => e.Type));
        Assert.Equal(["one", "two", "three"], all.Replay.Where(e => e.Type == "job.progress").Select(e => e.Data!["line"]!["text"]!.GetValue<string>()));
        Assert.Equal(2, attempts);
    }

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

    private static string Sse(params string[] pairs)
    {
        var text = new StringBuilder();
        for (var i = 0; i < pairs.Length; i += 2) text.Append($"event: {pairs[i]}\ndata: {pairs[i + 1]}\n\n");
        return text.ToString();
    }

    private sealed class ScriptedHandler(Func<string> body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(), Encoding.UTF8, "text/event-stream") });
    }
}
