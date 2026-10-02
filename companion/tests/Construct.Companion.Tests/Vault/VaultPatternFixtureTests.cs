using System.Text.Json.Nodes;
using Construct.Companion.Core.Vault;

namespace Construct.Companion.Tests.Vault;

// The Companion's VaultProtocol against the record the host service's copy is pinned to as well
// (test/fixtures/vault-patterns.json): a hosted VM's scrub must find and classify exactly what a
// local VM's scrub does.
public sealed class VaultPatternFixtureTests
{
    private static JsonObject Fixture()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "test", "fixtures", "vault-patterns.json");
            if (File.Exists(path)) return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        }
        throw new FileNotFoundException("test/fixtures/vault-patterns.json was not found above the test output directory.");
    }

    public static IEnumerable<object[]> PatternCases() => Fixture()["patterns"]!.AsArray().Select(c => new object[] { c!["name"]!.GetValue<string>() });

    [Theory]
    [MemberData(nameof(PatternCases))]
    public void Patterns_match_the_shared_fixture(string name)
    {
        var item = Fixture()["patterns"]!.AsArray().Single(c => c!["name"]!.GetValue<string>() == name)!;
        var expected = item["patterns"]!.AsArray().Select(p => p!.GetValue<string>()).ToArray();

        Assert.Equal(expected, VaultProtocol.Patterns(item["value"]!.GetValue<string>(), item["username"]!.GetValue<string>()));
    }

    [Fact]
    public void Agent_logs_and_sqlite_side_files_are_classified_like_the_host()
    {
        var fixture = Fixture();
        Assert.All(fixture["agentLogs"]!.AsArray(), c =>
            Assert.True(VaultProtocol.IsAgentLog(c!["path"]!.GetValue<string>()) == c["agentLog"]!.GetValue<bool>(), c["path"]!.GetValue<string>()));
        Assert.All(fixture["databases"]!.AsArray(), c =>
            Assert.Equal(c!["database"]!.GetValue<string>(), VaultProtocol.DatabaseFor(c["path"]!.GetValue<string>())));
    }
}
