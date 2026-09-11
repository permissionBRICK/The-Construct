using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.State;
using Construct.Companion.Fakes;
using Construct.Companion.Tests.Parity;

namespace Construct.Companion.Tests;

public sealed class UpdateAndDriverTests
{
    public static IEnumerable<object[]> AgentUpdates => ParityTests.Rows("agent-updates");
    [Theory, MemberData(nameof(AgentUpdates))]
    public async Task AgentLatestSourcesAndVersionDecisionsMatchJavaScript(JsonElement element)
    {
        var row = JsonNode.Parse(element.GetRawText())!; var source = new FakeUpdateSource(); source.Responses.Enqueue(row["reply"]);
        var output = await UpdatePlanner.AugmentAgentsAsync(source, row["agents"]!.AsArray());
        StateParityTests.Equal(row["output"], output); Assert.Equal(row["requests"]!.AsArray().Select(n => n!.GetValue<string>()), source.Requests.Select(u => u.AbsoluteUri));
    }
    public static IEnumerable<object[]> Discovery => ParityTests.Rows("t3-discovery");
    [Theory, MemberData(nameof(Discovery))]
    public async Task PrebuiltDiscoveryMatchesJavascriptIncludingPagination(JsonElement element)
    {
        var row = JsonNode.Parse(element.GetRawText())!; var source = new FakeUpdateSource(); foreach (var reply in row["replies"]!.AsArray()) source.Responses.Enqueue(reply);
        var manifest = await UpdatePlanner.DiscoverT3PrebuiltAsync(source, row["channel"]!.GetValue<string>());
        StateParityTests.Equal(row["output"], UpdatePlanner.AugmentPrebuilt(row["agent"]!.AsObject(), manifest));
        Assert.Equal(row["requests"]!.AsArray().Select(n => n!.GetValue<string>()), source.Requests.Select(u => u.AbsoluteUri));
    }
    [Theory]
    [InlineData(HypervisorState.Running,"running")]
    [InlineData(HypervisorState.Saved,"off")]
    [InlineData(HypervisorState.Paused,"off")]
    [InlineData(HypervisorState.Absent,"absent")]
    public async Task NativeStateCollapsesWithoutSpawning(HypervisorState input, string expected)
    {
        var hypervisor = new FakeHypervisorState(); hypervisor.States["dev"] = input; var runner = new FakeProcessRunner();
        Assert.Equal(expected, await VmPower.QueryLocalAsync(hypervisor, runner, "dev")); Assert.Empty(runner.Invocations);
    }
    [Fact]
    public async Task UnknownNativeStateUsesPinnedGetVmInvocation()
    {
        var runner = new FakeProcessRunner(); runner.Results.Enqueue(new ProcessResult(0, "VMSTATE=Saved"));
        Assert.Equal("off", await VmPower.QueryLocalAsync(new FakeHypervisorState(), runner, "dev"));
        var actual = Assert.Single(runner.Invocations); Assert.Equal(VmPower.BuildStateProbeLaunch("dev").SpawnArgs, actual.Arguments); Assert.Equal(TimeSpan.FromSeconds(15), actual.Timeout);
    }
    [Fact]
    public void SavedLabelAndPollingDoNotChangeActionContracts()
    {
        Assert.Equal("saved", VmPower.RefineSavedState("off", "hyperv-remote", "Saved")); Assert.Equal("off", VmPower.RefineSavedState("off", "hyperv-local", "Saved"));
        var plan = ResultPollingPlan.Create("/temp", "setCheckpoints", 123); Assert.Equal("CONSTRUCT_CHECKPOINT_RESULT", plan.EnvironmentKey);
        Assert.Equal("pending", plan.Evaluate(null, TimeSpan.FromMinutes(10))); Assert.Equal("timeout", plan.Evaluate(null, TimeSpan.FromMinutes(10).Add(TimeSpan.FromMilliseconds(1)))); Assert.Equal("ok", plan.Evaluate("\ufeffok\r\n", TimeSpan.FromMinutes(11)));
    }
    [Fact]
    public void ConversionRequiresExplicitFinishAndCapturedIdentity()
    {
        var registry = InstanceRegistry.Parse("").Add("dev", new JsonObject()); var current = registry.Resolve("dev");
        var plan = new JsonObject { ["id"] = "id", ["name"] = "dev", ["adminUser"] = "owner", ["publicHost"] = "host", ["fingerprint"] = Instances.TargetFingerprint(current) };
        var result = new JsonObject { ["ok"] = true, ["id"] = "id", ["name"] = "dev", ["owner"] = "owner", ["url"] = "https://host:7462", ["sshPort"] = 2222, ["publicHost"] = "dev.host" };
        Assert.True(HostConversion.PendingStatus("dev", plan, result)!["ready"]!.GetValue<bool>());
        var converted = HostConversion.ConvertedRegistry(registry, plan, result).Resolve("dev"); Assert.Equal("hyperv-remote", StateJson.Text(converted["backend"])); Assert.Equal(22, converted["sshPort"]!.GetValue<int>());
        plan["fingerprint"] = "changed"; Assert.Throws<InvalidOperationException>(() => HostConversion.ConvertedRegistry(registry, plan, result));
    }
    [Fact]
    public void HostUpdateKeepsOperationKeyAndRequiresRecoveryForInterruptedUpdate()
    {
        var pending = new JsonObject { ["releaseTag"] = "host-abc", ["operationKey"] = "operation" };
        Assert.Equal("stage", StateJson.Text(HostUpdatePlanner.PlanAdvance(pending, null)["action"]));
        pending["updateId"] = "id"; var status = new JsonObject { ["current"] = new JsonObject { ["updateId"] = "id", ["state"] = "staged" } };
        var action = HostUpdatePlanner.PlanAdvance(pending, status); Assert.Equal("operation-apply", StateJson.Text(action["body"]?["operationKey"]));
        status["current"]!["state"] = "interrupted"; Assert.Equal("clear", StateJson.Text(HostUpdatePlanner.PlanAdvance(pending, status)["action"])); Assert.Throws<InvalidOperationException>(() => HostUpdatePlanner.PlanStart(status, null, "next"));
        Assert.False(HostUpdatePlanner.ClearPendingOnError(429)); Assert.True(HostUpdatePlanner.ClearPendingOnError(403));
    }
}
