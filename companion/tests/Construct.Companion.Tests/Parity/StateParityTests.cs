using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.State;

namespace Construct.Companion.Tests.Parity;

public sealed class StateParityTests
{
    public static IEnumerable<object[]> Settings => ParityTests.Rows("settings-mapping");
    public static IEnumerable<object[]> Identity => ParityTests.Rows("instance-identity");
    internal static void Equal(JsonNode? expected, JsonNode? actual) => Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected: {expected}\nActual: {actual}");
    [Theory, MemberData(nameof(Settings))]
    public void SettingsMatch(JsonElement element)
    {
        var row = JsonNode.Parse(element.GetRawText())!;
        var output = StateJson.Text(row["kind"]) switch
        {
            "to" => SettingsMapping.MapToForm(row["input"] as JsonObject),
            "from" => SettingsMapping.MapFromForm(row["input"] as JsonObject),
            _ => JsonSerializer.SerializeToNode(SettingsMapping.PatchReprovisionChanges(row["previous"] as JsonObject, row["next"] as JsonObject))
        };
        Equal(row["output"], output);
        if (row["outputJson"] is not null) Assert.Equal(StateJson.Text(row["outputJson"]), StateJson.Stringify(output));
    }
    [Theory, MemberData(nameof(Identity))]
    public void IdentityMatches(JsonElement element)
    {
        var row = JsonNode.Parse(element.GetRawText())!; var name = StateJson.Text(row["name"])!; var raw = (JsonObject)row["raw"]!;
        var output = Instances.DeriveDefaults(name, raw);
        Equal(row["output"], output);
        Assert.Equal(row["valid"]!.GetValue<bool>(), Instances.IsValidName(name));
        Equal(row["problems"], JsonSerializer.SerializeToNode(Instances.IdentityProblems(output, raw)));
        Equal(row["local"], JsonSerializer.SerializeToNode(Instances.LocalIdentityProblems(output)));
        Equal(row["remote"], JsonSerializer.SerializeToNode(Instances.RemoteIdentityProblems(output, raw)));
        Equal(row["backend"], JsonSerializer.SerializeToNode(Instances.BackendProblems(raw["backend"])));
        Assert.Equal(StateJson.Text(row["fingerprint"]), Instances.TargetFingerprint(output));
    }
}
