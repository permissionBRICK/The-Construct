using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.HostAdmin;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Ipc;
using Microsoft.Extensions.DependencyInjection;

namespace Construct.Companion.Tests.Ipc;

public sealed class InstanceConsoleTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DispatcherEnsuresGatewayMintsFreshLinksAndClearsSpinner(bool failure)
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        var entry = h.App.Services.GetRequiredService<CompanionInstances>().Get("agent-vm");
        entry.Definition["backend"] = "hyperv-remote";
        var ssh = (FakeSshTransport)entry.Ssh;
        var minted = 0;
        ssh.ScriptHandler = (script, _) => Task.FromResult(script == GuestConsole.BuildEnsureGatewayScript()
            ? new ProcessResult(0, "CONSOLE_GATEWAY=installed\n")
            : failure ? new ProcessResult(1, "", "Host refused console operation (HTTP 403) PRIVATE-password") : new ProcessResult(0, $"http://localhost:18816/#fresh-{++minted}"));
        using var subscription = h.App.Services.GetRequiredService<IpcEvents>().Subscribe(entry.Name);
        var dispatcher = h.App.Services.GetRequiredService<MessageDispatcher>();
        for (var click = 0; click < 2; click++) await dispatcher.DispatchAsync(entry.Name, new() { ["type"] = "command", ["id"] = "openConsole" }, default);
        var completed = new List<System.Text.Json.JsonElement>();
        while (subscription.Reader.TryRead(out var item)) if (item.Data.TryGetProperty("message", out var message) && message.GetProperty("type").GetString() == "lifecyclePrepared") completed.Add(message);
        Assert.Equal(2, completed.Count);
        foreach (var message in completed) {
            Assert.Equal("openConsole", message.GetProperty("id").GetString());
            Assert.DoesNotContain("PRIVATE", message.ToString());
            if (failure) Assert.Contains("403", message.GetProperty("error").GetString());
        }
        var opened = h.Get<FakeLauncher, ILauncher>().Opened;
        Assert.Equal(failure ? 0 : 2, opened.Count);
        if (!failure) Assert.NotEqual(opened[0], opened[1]);
        Assert.Equal(GuestConsole.BuildEnsureGatewayScript(), ssh.Scripts[0]);
        Assert.Equal(GuestConsole.BuildSelfConsoleScript(false), ssh.Scripts[1]);
        Assert.All(ssh.StandardInputs, Assert.Null);
    }
    [Fact]
    public async Task LocalMintKeepsConnectionOnStdinAndRetriesDeadForwardWithFreshTicket()
    {
        var ssh = new FakeSshTransport();
        ssh.ListeningResults.Enqueue(false); ssh.ListeningResults.Enqueue(true);
        var script = GuestConsole.BuildSelfConsoleScript(true);
        ssh.Spool.ExpectRun(script, new ProcessResult(0, "http://localhost:18816/#first"));
        ssh.Spool.ExpectRun(GuestConsole.BuildCloseForwardScript(18816), new ProcessResult(0));
        ssh.Spool.ExpectRun(script, new ProcessResult(0, "http://localhost:18817/#second"));
        var input = new Secret("{\"password\":\"PRIVATE\"}\n");
        Assert.EndsWith("#second", await GuestConsole.MintLiveLinkAsync(ssh, script, input, default));
        Assert.Same(input, ssh.StandardInputs[0]); Assert.Same(input, ssh.StandardInputs[2]);
        Assert.All(ssh.Scripts, s => Assert.DoesNotContain("PRIVATE", s));
        var launch = LocalConsole.BuildHandoffLaunch("/scripts", "work-vm", "Work-VM");
        Assert.Equal("powershell.exe", launch.FileName);
        Assert.Contains("work-vm", launch.StandardInput!.Reveal());
        Assert.DoesNotContain("work-vm", string.Join(" ", launch.Arguments));
        Assert.Contains("-Verb RunAs", System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(LocalConsole.BuildSetupLaunch("/scripts", "work-vm", "Work-VM", true).Arguments.Last())));
    }
    [Fact]
    public async Task OfflineStateAndUnsupportedPlatformKeepConsoleDisabled()
    {
        await using var h = await HttpTests.Harness.Start(runtimeJobs: false);
        var snapshot = h.App.Services.GetRequiredService<StateAggregation>().State("agent-vm", new() { ["online"] = false });
        Assert.Equal(OperatingSystem.IsWindows(), snapshot["state"]!["console"]!["supported"]!.GetValue<bool>());
    }
}
