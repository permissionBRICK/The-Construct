using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.State;
using Construct.Companion.Core.Probe;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Fakes;

namespace Construct.Companion.Tests.Parity;

public sealed class PlanningParityTests
{
    public static IEnumerable<object[]> Rows => new[] { "registry-state", "lifecycle-invocations", "lifecycle-launches", "vm-power", "probe-parsing", "usage-parsing", "updates-planning", "remote-identity", "t3-pure", "state-json-bytes", "agent-update-scripts", "usage-exports", "instance-fingerprints" }.SelectMany(area => ParityTests.Rows(area).Select(row => new object[] { area, row[0] }));
    [Theory, MemberData(nameof(Rows))]
    public async Task MatchesJavaScript(string area, JsonElement element)
    {
        var row = JsonNode.Parse(element.GetRawText())!;
        string S(string key) => StateJson.Text(row[key]) ?? "";
        JsonObject? O(string key) => row[key] as JsonObject;
        void Equal(string key, JsonNode? actual) => StateParityTests.Equal(row[key], actual);
        void Value<T>(string key, T actual) => Equal(key, JsonSerializer.SerializeToNode(actual));
        switch (area)
        {
            case "agent-update-scripts": Value("output", AgentUpdateScript.Build(row["input"] is JsonArray ids ? ids.Select(StateJson.String) : null)); break;
            case "usage-exports": Value("output", UsageParser.BuildExportPayload(StateJson.Text(row["input"]), S("savedAt"))); break;
            case "instance-fingerprints": Value("output", Instances.TargetFingerprint(O("input"))); break;
            case "state-json-bytes": Value("output", System.Text.Encoding.UTF8.GetString(StateJson.Bytes(row["input"]!))); break;
            case "registry-state":
                var registry = InstanceRegistry.Parse(S("text")); Value("problems", registry.Problems); Equal("document", registry.ToFileDocument()); Equal("active", registry.ResolveActive("missing", "dev")); break;
            case "lifecycle-invocations":
                var opts = O("opts")!; var instance = opts["instance"] as JsonObject; var declared = opts["instanceParams"] is JsonArray d ? d.Select(StateJson.String).ToArray() : null;
                Equal("output", LifecycleBuilder.BuildInvocation(S("action"), opts)); Value("args", LifecycleBuilder.InstanceArgs(S("action"), instance, declared)); Value("params", LifecycleBuilder.ParamsForAction(S("action"), instance, declared)); break;
            case "lifecycle-launches":
                var launchOpts = O("opts")!; var args = row["args"]!.AsArray().Select(StateJson.String).ToArray();
                var launch = PowerShellLaunch.BuildHostLaunch(S("script"), args, StateJson.Boolean(launchOpts["elevate"]) == true, StateJson.Boolean(launchOpts["keepOpen"]) == true, launchOpts["argSpec"] as JsonArray);
                Equal("output", new JsonObject { ["file"] = launch.File, ["spawnArgs"] = JsonSerializer.SerializeToNode(launch.SpawnArgs), ["command"] = launch.Command });
                Value("child", PowerShellLaunch.BuildChildCommandLine(S("script"), args, StateJson.Boolean(launchOpts["keepOpen"]) == true));
                var runner = new FakeProcessRunner(); await runner.RunAsync(launch.Invocation()); Assert.Equal(launch.SpawnArgs, runner.Invocations.Single().Arguments); break;
            case "vm-power":
                if (S("kind") == "parse") { Value("state", VmPower.ParseVmState(S("input"))); Value("checkpoints", VmPower.ParseAutoCheckpoints(S("input"))); }
                else
                {
                    var probeLaunch = S("kind") == "state" ? VmPower.BuildStateProbeLaunch(StateJson.Text(row["name"])) : S("kind") == "checkpoints" ? VmPower.BuildAutoCheckpointProbeLaunch(StateJson.Text(row["name"])) : VmPower.BuildElevatedCommandLaunch(VmPower.BuildStartCommand(StateJson.Text(row["name"])));
                    Equal("output", new JsonObject { ["file"] = probeLaunch.File, ["spawnArgs"] = JsonSerializer.SerializeToNode(probeLaunch.SpawnArgs), ["command"] = probeLaunch.Command });
                    var recorder = new FakeProcessRunner(); await recorder.RunAsync(probeLaunch.Invocation()); Assert.Equal(probeLaunch.SpawnArgs, recorder.Invocations.Single().Arguments);
                }
                break;
            case "probe-parsing": var map = ProbeParser.ParseProbe(S("input")); Value("map", map); Equal("output", ProbeParser.ToState(map, StateJson.Text(row["host"]))); break;
            case "usage-parsing":
                if (S("kind") == "parse") Equal("output", UsageParser.ParseUsage(O("input")));
                else { var n = row["input"]!.GetValue<double>(); Value("tokens", UsageParser.FormatTokens(n)); Value("cost", UsageParser.FormatCost(n)); } break;
            case "updates-planning":
                if (S("kind") == "markers") { var markers = UpdatePlanner.ReadMarkers(O("raw"), O("state")); Equal("markers", markers); Value("stale", UpdatePlanner.IsProvisionStale(markers, StateJson.Text(row["guest"]))); Value("effective", UpdatePlanner.EffectiveProvisionedCommit(markers, StateJson.Text(row["guest"]))); Value("args", UpdatePlanner.ConstructRefreshArgs(markers)); }
                else if (S("kind") == "compare") Equal("output", UpdatePlanner.ConstructUpdateFromCompare(O("input")));
                else { Value("newer", UpdatePlanner.IsNewer(S("latest"), S("installed"))); Value("nightly", UpdatePlanner.IsNewerNightly(S("latest"), S("installed"))); } break;
            case "remote-identity":
                if (S("kind") == "endpoint") Equal("output", RemoteHost.ReadEndpoint(row["input"]));
                else if (S("kind") == "fingerprint") Value("output", RemoteHost.FormatFingerprint(StateJson.Text(row["input"])));
                else { Value("normalized", RemoteHost.NormalizeServiceUrl(S("input"))); Value("slug", RemoteHost.HostSlug(S("input"))); var fs = new FakeFileSystem(); fs.Roots[FileSystemRoot.LocalAppData] = "/local"; Value("pin", RemoteHost.PinPath(fs, S("input"))); } break;
            case "t3-pure": Value("output", S("kind") switch { "install" => T3Code.BuildInstallScript(S("channel")), "disable" => T3Code.BuildDisableScript(), _ => T3Code.ExtractPairUrl(StateJson.Text(row["input"])) }); break;
        }
    }
}
