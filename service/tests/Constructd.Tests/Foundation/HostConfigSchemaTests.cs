using System.Text.Json;
using Constructd.Api.Infrastructure;

namespace Constructd.Tests.Foundation;

public sealed class HostConfigSchemaTests
{
    /// <summary>Settings of the new stack the old clients' forms (frozen, design 15) do not show; a PUT may omit them.</summary>
    private static readonly string[] NewStackOnly = ["updates.auto"];

    [Fact]
    public void FormFixtureCoversRecordsAndDefaults()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "hostadmin-config-schema.json")));
        var sections = fixture.RootElement.EnumerateArray().ToArray();
        Assert.Equal(HostConfigValidation.Defaults.Keys.Order(), sections.Select(s => s.GetProperty("key").GetString()).Order());
        foreach (var section in sections)
        {
            var defaults = HostConfigValidation.Defaults[section.GetProperty("key").GetString()!];
            var fields = section.GetProperty("fields").EnumerateArray().ToArray();
            var names = fields.Select(f => f.GetProperty("key").GetString()).Concat(section.GetProperty("rawOnly").EnumerateArray().Select(f => f.GetString()))
                .Concat(NewStackOnly.Where(n => n.StartsWith(section.GetProperty("key").GetString() + ".", StringComparison.Ordinal)).Select(n => n[(n.IndexOf('.') + 1)..]));
            Assert.Equal(defaults.GetType().GetProperties().Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name)).Order(), names.Order());
            var json = JsonSerializer.SerializeToElement(defaults, ApiJson.Options);
            foreach (var field in fields)
                Assert.True(JsonElement.DeepEquals(json.GetProperty(field.GetProperty("key").GetString()!), field.GetProperty("default")), field.GetProperty("key").GetString());
        }
    }
}
