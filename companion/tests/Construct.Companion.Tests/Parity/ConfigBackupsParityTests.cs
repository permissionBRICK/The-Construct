using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Backups;
namespace Construct.Companion.Tests.Parity;

// extension/src/backups.js against BackupProtocol (docs/plans/host-config-backups.md §3.7, §3.8).
public sealed class ConfigBackupsParityTests
{
    public static IEnumerable<object[]> Rows => ParityTests.Rows("config-backups");
    [Theory, MemberData(nameof(Rows))]
    public void MatchesJavaScript(JsonElement element)
    {
        var row = JsonNode.Parse(element.GetRawText())!; var input = row["input"]; var now = DateTimeOffset.FromUnixTimeMilliseconds(row["now"]!.GetValue<long>());
        JsonNode? actual = row["kind"]!.GetValue<string>() switch
        {
            "panel" => BackupProtocol.PanelModel(input!.AsObject(), now),
            "key" => BackupProtocol.KeyView(input),
            "override" => BackupProtocol.ParseOverride(input!["form"], input["maxKeep"]),
            "warning" => BackupProtocol.Warning(input!["instance"]!.GetValue<string>(), input["status"], input["warned"]),
            "fileName" => JsonValue.Create(BackupProtocol.FileName(input!.GetValue<string>(), now)),
            var kind => throw new InvalidOperationException(kind)
        };
        StateParityTests.Equal(row["output"], actual);
    }
    [Fact]
    public void FixtureCoversEveryKindWithRealInputs()
    {
        var rows = Rows.Select(r => JsonNode.Parse(((JsonElement)r[0]).GetRawText())!).ToArray();
        Assert.Equal(["fileName", "key", "override", "panel", "warning"], rows.Select(r => r["kind"]!.GetValue<string>()).Distinct().Order());
        // A failing status, a pinned backup and a per-VM override reach the C# model, not only empty inputs.
        Assert.Contains(rows, r => r["kind"]!.GetValue<string>() == "panel" && r["output"]?["warning"]?.GetValue<string>().Length > 0
            && r["output"]!["items"]!.AsArray().Any(i => i!["pinned"]!.GetValue<bool>()) && r["output"]!["override"]?["keep"]?.GetValue<string>() == "6");
        Assert.Contains(rows, r => r["kind"]!.GetValue<string>() == "warning" && r["output"]!["notify"]!.GetValue<bool>());
    }
}
