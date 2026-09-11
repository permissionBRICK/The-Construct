using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Audio;
using Construct.Companion.Core.Notifications;
using Construct.Companion.Core.Repatch;
using Construct.Companion.Core.Runtime;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Fakes;
namespace Construct.Companion.Tests.Parity;

public sealed class RuntimeParityTests
{
    public static IEnumerable<object[]> Rows => new[] { "notify-runtime", "audio-runtime", "repatch-runtime" }
        .SelectMany(area => ParityTests.Rows(area).Select(row => new object[] { area, row[0] }));
    [Theory, MemberData(nameof(Rows))]
    public async Task MatchesJavascript(string area, JsonElement row)
    {
        var input = JsonNode.Parse(row.GetProperty("input").GetRawText()); var kind = row.GetProperty("kind").GetString();
        var obj = input as JsonObject ?? new(); JsonNode? actual;
        if (area == "notify-runtime") actual = kind switch
        {
            "claim" => JsonValue.Create(NotificationProtocol.ClaimScript(input!.GetValue<string>())),
            "watch" => JsonValue.Create(NotificationProtocol.WatchScript(input!.GetValue<string>())),
            "parse" => NotificationProtocol.ParseEntries(input!.GetValue<string>()),
            "toast" => JsonValue.Create(NotificationProtocol.Toast((JsonObject)obj["entry"]!, obj.Str("launchUri")).Xml),
            "select" => NotificationProtocol.SelectDeliverable(obj.Array("entries"), obj["opts"]!["now"]!.GetValue<double>(), obj["opts"]!["ttlMs"]!.GetValue<double>(), obj["opts"]!["max"]!.GetValue<int>()),
            _ => throw new InvalidOperationException(kind)
        };
        else if (area == "repatch-runtime") actual = kind switch
        {
            "parse" => RepatchProtocol.ParseStatus(input!.GetValue<string>()),
            "repairs" => RepatchProtocol.DecideRepairs((JsonObject)obj["status"]!, obj.True("streamingOn"), obj.True("micOn")),
            "startup" => RepatchProtocol.PlanStartupActions(obj.True("streamingOn"), obj.True("micOn"), obj.True("micLive"), obj.True("hasHostAudio")),
            _ => throw new InvalidOperationException(kind)
        };
        else if (kind is "tunnel" or "watchArgs")
        {
            var cfg = obj["cfg"]!.Deserialize<SshConfiguration>(IpcJson.Options)!; var key = obj["keyPath"]?.GetValue<string>();
            var runner = new FakeProcessRunner();
            var args = kind == "tunnel" ? RuntimeSshArgs.Reverse(cfg, 8767, 30000, key) : RuntimeSshArgs.Watch(cfg, "echo test", key);
            await using var process = runner.Start(new ProcessInvocation("ssh", args));
            actual = JsonSerializer.SerializeToNode(runner.Invocations.Single().Arguments);
        }
        else actual = kind switch
        {
            "message" => new AudioStatus(obj["status"].True("enabled"), obj["status"].True("capturing"), obj["status"]!["tunnel"]?.GetValue<string>(),
                obj["status"]!["gatePatched"]?.GetValue<bool>()).ToMessage(obj.Str("instance")),
            "enable" => JsonValue.Create(AudioProtocol.EnableScript(obj.Str("text"), obj.Str("text"), obj["port"]!.GetValue<int>(), obj["count"]!.GetValue<int>())),
            "disable" => JsonValue.Create(AudioProtocol.DisableScript(obj["self"]!.GetValue<int>(), obj.Str("text"), obj["port"]!.GetValue<int>(), obj["count"]!.GetValue<int>())),
            "parse" => new JsonObject { ["busy"] = JsonSerializer.SerializeToNode(AudioProtocol.ParseBusyPorts(input!.GetValue<string>())), ["patched"] = AudioProtocol.ConfirmPatched("CONSTRUCT_GATE_PATCHED", input.GetValue<string>()) },
            "ports" => JsonSerializer.SerializeToNode(AudioProtocol.PortCandidates(((JsonArray)input!).Select(n => n!.GetValue<int>()))),
            _ => throw new InvalidOperationException(kind)
        };
        var expected = JsonNode.Parse(row.GetProperty("output").GetRawText());
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{area}/{kind}: expected {expected}, actual {actual}");
    }
}
