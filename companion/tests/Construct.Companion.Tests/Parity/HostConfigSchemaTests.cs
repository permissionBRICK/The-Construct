using System.Text.Json.Nodes;
using Construct.Companion.Core.HostAdmin;

namespace Construct.Companion.Tests.Parity;

public sealed class HostConfigSchemaTests
{
    [Fact]
    public void EmbeddedSchemaMatchesFixtureAndSections()
    {
        var fixture = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Parity", "Fixtures", "hostadmin-config-schema.json")));
        Assert.True(JsonNode.DeepEquals(fixture, HostConfigSchema.Sections));
        Assert.Equal(HostAdminProtocol.ConfigSections, HostConfigSchema.Sections.Select(s => HostAdminProtocol.Text(s!["key"])));
    }
}
