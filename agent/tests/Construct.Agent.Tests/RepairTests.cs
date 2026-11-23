using System.Net.Http.Json;
using Construct.Agent.Http;
using Construct.Agent.State;
using Construct.Agent.Tests.Support;

namespace Construct.Agent.Tests;

/// <summary><c>construct-agent repair</c> with no engine reachable (plan 5.3, S4): it reports and restarts the agent.</summary>
public sealed class RepairTests
{
    [Fact]
    public async Task Repair_reports_and_restarts_the_agent_with_no_engine_reachable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "agent-test-repair-" + Guid.NewGuid().ToString("n"));
        var options = new AgentOptions { DataDirectory = directory, Protection = "file" };
        // A device that knows one engine, which is not running.
        using (new DeviceAgent(options)) { }
        var state = StateFile.Load(options.StatePath);
        state.Engines.Add(new EngineEntry { Id = "e1", Url = $"https://127.0.0.1:{EngineHost.FreePort()}", Pin = new string('A', 64), Name = "gone" });
        StateFile.Save(options.StatePath, state);

        var launch = new AgentLaunch("dotnet", [Path.Combine(AppContext.BaseDirectory, "construct-agent.dll"), "run", "--data-dir", directory]);
        try
        {
            var output = new StringWriter();
            Assert.Equal(0, await Repair.RunAsync(options, launch, output, CancellationToken.None));
            var report = output.ToString();
            Assert.Contains("Agent: not running", report);
            Assert.Contains("Token file: none", report);
            Assert.Contains("not registered yet (secrets protected by file)", report);
            Assert.Contains("Engine gone", report);
            Assert.Contains("not reachable", report);
            var first = EndpointFile.Read(options.EndpointPath)!;
            Assert.Contains($"Agent: running again, pid {first.Pid}", report);

            // A second repair finds it running, stops it and starts a new one.
            output = new StringWriter();
            Assert.Equal(0, await Repair.RunAsync(options, launch, output, CancellationToken.None));
            Assert.Contains($"Agent: running, pid {first.Pid}", output.ToString());
            var second = EndpointFile.Read(options.EndpointPath)!;
            Assert.NotEqual(first.Pid, second.Pid);
            Assert.NotEqual(first.Token, second.Token);
        }
        finally
        {
            if (EndpointFile.Read(options.EndpointPath) is { } running)
            {
                using var http = new HttpClient();
                http.DefaultRequestHeaders.Authorization = new("Bearer", running.Token);
                try { await http.PostAsync($"{running.BaseUrl}/api/shutdown", null); }
                catch (HttpRequestException) { }
                for (var i = 0; i < 40 && File.Exists(options.EndpointPath); i++) await Task.Delay(100);
            }

            Directory.Delete(directory, true);
        }
    }
}
