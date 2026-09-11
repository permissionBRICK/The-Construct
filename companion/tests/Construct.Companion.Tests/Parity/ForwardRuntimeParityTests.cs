using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Runtime;
using static Construct.Companion.Core.Runtime.RuntimeJson;

namespace Construct.Companion.Tests.Parity;

public sealed class ForwardRuntimeParityTests
{
    public static IEnumerable<object[]> Rows => ParityTests.Rows("forward-runtime");
    [Theory, MemberData(nameof(Rows))]
    public void MatchesJavascript(JsonElement row)
    {
        var input = JsonNode.Parse(row.GetProperty("input").GetRawText());
        var expected = JsonNode.Parse(row.GetProperty("output").GetRawText());
        var kind = row.GetProperty("kind").GetString();
        var obj = input as JsonObject ?? new();
        JsonNode? actual = kind switch
        {
            "wire" => new JsonObject { ["request"] = ForwardProtocol.ParseRequest(obj.Str("id"), obj["doc"] as JsonObject),
                ["ack"] = ForwardProtocol.ParseAck(obj.Str("id"), obj["doc"] as JsonObject), ["close"] = ForwardProtocol.ParseClose(obj.Str("id"), obj["doc"] as JsonObject) },
            "plan" => ForwardPlanner.PlanActions(obj), "snapshot" => ForwardPlanner.ToSnapshot(obj),
            "remote" => RemoteForwardList.Read((JsonArray)input!), "lifecycle" => ForwardPlanner.PlanLifecycle(obj),
            "outcome" => ForwardPlanner.PlanStartOutcome(obj), "ack" => ForwardProtocol.AckDocument(obj.Str("id"), (JsonObject)obj["ack"]!),
            "delay" => JsonValue.Create(ForwardProtocol.ReconnectDelayMs(input!.GetValue<double>())),
            "slice" => Slice(Text(input)), "ports" => Ports(obj), _ => throw new InvalidOperationException(kind)
        };
        Assert.True(JsonNode.DeepEquals(expected, actual), $"{kind}: expected {expected}, actual {actual}");
    }
    private static JsonObject Slice(string name)
    { var (b, c) = ForwardProtocol.InstancePortSlice(name); return new() { ["base"] = b, ["count"] = c }; }
    private static JsonArray Ports(JsonObject input)
    {
        var opts = (JsonObject)input["opts"]!;
        return new JsonArray(ForwardProtocol.PortCandidates(Port(input["vmPort"]), Port(opts["prefer"]), opts.Array("taken").Select(p => Port(p) ?? 0),
            Port(opts["base"]) ?? 18800, Port(opts["count"]) ?? 16).Select(p => (JsonNode?)JsonValue.Create(p)).ToArray());
    }
}
