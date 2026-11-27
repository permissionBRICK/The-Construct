using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Constructd.Core.Logic;

namespace Constructd.Tests.Vault;

/// <summary>
/// The host's copy of the scan patterns and the agent-log rules against the record the Companion's
/// VaultProtocol is pinned to as well (test/fixtures/vault-patterns.json): the two must find and
/// classify exactly the same things.
/// </summary>
public sealed class VaultPatternFixtureTests
{
    private static JsonObject Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vault", "vault-patterns.json")))!.AsObject();

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
    public void Agent_logs_and_sqlite_side_files_are_classified_like_the_companion()
    {
        var fixture = Fixture();
        Assert.All(fixture["agentLogs"]!.AsArray(), c =>
            Assert.True(VaultProtocol.IsAgentLog(c!["path"]!.GetValue<string>()) == c["agentLog"]!.GetValue<bool>(), c["path"]!.GetValue<string>()));
        Assert.All(fixture["databases"]!.AsArray(), c =>
            Assert.Equal(c!["database"]!.GetValue<string>(), VaultProtocol.DatabaseFor(c["path"]!.GetValue<string>())));
    }

    [Fact]
    public void A_logs_only_scan_reaches_every_agent_log()
    {
        // The roots are shell globs: * stays within one directory, and a root covers everything below it.
        var roots = VaultProtocol.AgentLogRoots.Select(r => new Regex("^" + Regex.Escape(r).Replace(@"\*", "[^/]*", StringComparison.Ordinal) + "(/|$)")).ToArray();
        var logs = Fixture()["agentLogs"]!.AsArray().Where(c => c!["agentLog"]!.GetValue<bool>()).Select(c => c!["path"]!.GetValue<string>()).ToArray();
        Assert.NotEmpty(logs);
        Assert.All(logs, path => Assert.True(roots.Any(r => r.IsMatch(path)), path));
    }
}
