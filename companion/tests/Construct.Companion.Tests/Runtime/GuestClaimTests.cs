using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Notifications;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Tests.Runtime;

public sealed class GuestClaimTests
{
    [Fact]
    public async Task RealSharedClaimScriptIsExclusiveAndRecoversStaleClaimsOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        var directory = Path.Combine(Path.GetTempPath(), "cc-notify-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            for (var i = 0; i < 30; i++) await File.WriteAllTextAsync(Path.Combine(directory, $"{i}.json"), $"{{\"body\":\"message-{i}\"}}\n");
            var stale = Path.Combine(directory, "stale.json.claimed.123"); await File.WriteAllTextAsync(stale, "{\"body\":\"recovered\"}\n"); File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-3));
            var runner = new RuntimeProcessRunner(); var invocation = new ProcessInvocation("bash", ["-s"], StandardInput: new Secret(NotificationProtocol.ClaimScript(directory)), Timeout: TimeSpan.FromSeconds(10));
            var results = await Task.WhenAll(runner.RunAsync(invocation), runner.RunAsync(invocation));
            Assert.All(results, r => Assert.Equal(0, r.Code));
            var entries = NotificationProtocol.ParseEntries(string.Join('\n', results.Select(r => r.Stdout)));
            Assert.Equal(31, entries.Count); Assert.Equal(31, entries.Select(e => e!["body"]!.GetValue<string>()).Distinct().Count()); Assert.Empty(Directory.EnumerateFiles(directory));
        }
        finally { Directory.Delete(directory, true); }
    }
    [Fact]
    public async Task RealRunnerCancellationKillsAndReapsChildOnLinux()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var cancel = new CancellationTokenSource(); var runner = new RuntimeProcessRunner();
        await using var process = runner.Start(new ProcessInvocation("bash", ["-s"], StandardInput: new Secret("printf 'ready\\n'\nexec sleep 60\n")), cancel.Token);
        await using var output = process.StandardOutput.GetAsyncEnumerator(); Assert.True(await output.MoveNextAsync()); Assert.Contains("ready", output.Current);
        cancel.Cancel(); await process.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); Assert.True(process.Completion.IsCompleted);
    }
}
