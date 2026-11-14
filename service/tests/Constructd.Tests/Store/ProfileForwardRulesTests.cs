using System.Text.Json.Nodes;
using Constructd.Sync.Profiles;

namespace Constructd.Tests.Store;

/// <summary>
/// A profile's HTTPS forwards (docs/https-forwards.md): the engine accepts, refuses and normalises them as
/// the guest's validator does (extension/src/projects.js), so a profile written on either side is the same.
/// </summary>
public sealed class ProfileForwardRulesTests
{
    private static JsonObject P(string forwards) => JsonNode.Parse("{\"name\":\"p\",\"forwards\":" + forwards + "}")!.AsObject();

    private static string[] Errors(string forwards) => ProfileRules.Validate(P(forwards)).Select(e => e.Message).ToArray();

    private static string Stored(string forwards) => ProfileRules.Finish(ProfileRules.Normalize(P(forwards)))["forwards"]?.ToJsonString() ?? "null";

    [Theory]
    [InlineData("""[{"port":3000}]""")]
    [InlineData("""[{"port":3000,"to":"host","label":"web"}]""")]
    [InlineData("""[{"port":5173,"https":true,"upstreamTls":false}]""")]
    [InlineData("""[{"port":5173,"https":true,"upstreamTls":true,"name":"vite-dev","label":"vite dev","to":"host"}]""")]
    [InlineData("""[{"port":5173,"https":false}]""")]
    public void A_valid_forward_is_accepted(string forwards) => Assert.Empty(Errors(forwards));

    [Theory]
    [InlineData("""[{"port":5173,"https":true}]""", "upstreamTls is required")]
    [InlineData("""[{"port":5173,"https":true,"upstreamTls":false,"to":"client"}]""", "must be \"host\" or left out")]
    [InlineData("""[{"port":5173,"https":"yes","upstreamTls":false}]""", "https must be true or false")]
    [InlineData("""[{"port":5173,"https":true,"upstreamTls":1}]""", "upstreamTls must be true or false")]
    [InlineData("""[{"port":5173,"https":true,"upstreamTls":false,"name":"Not_A_Label"}]""", "name must be one DNS label")]
    [InlineData("""[{"port":5173,"https":true,"upstreamTls":false,"name":"a\n"}]""", "name must be one DNS label")]
    [InlineData("""[{"port":5173,"name":"web"}]""", "name needs \"https\": true")]
    [InlineData("""[{"port":5173,"upstreamTls":false}]""", "upstreamTls needs \"https\": true")]
    [InlineData("""[{"port":5173,"scheme":"https"}]""", "unknown key \"scheme\"")]
    public void An_invalid_forward_is_refused_with_the_guests_message(string forwards, string expected) =>
        Assert.Contains(Errors(forwards), e => e.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void An_https_forward_is_stored_in_the_guests_form()
    {
        // The host serves it, whatever "to" was left as; the keys come in the guest's order.
        Assert.Equal("""[{"port":5173,"to":"host","https":true,"upstreamTls":false,"name":"vite","label":"vite dev"}]""",
            Stored("""[{"label":"vite dev","name":"vite","upstreamTls":false,"https":true,"port":5173}]"""));
        // "https": false is not an HTTPS forward and is not kept.
        Assert.Equal("""[{"port":3000,"to":"client"}]""", Stored("""[{"port":3000,"https":false}]"""));
        Assert.Equal("""[{"port":3000,"to":"host","label":"web"}]""", Stored("""[{"port":3000,"to":"host","label":"web"}]"""));
    }

    [Fact]
    public void An_https_forward_and_a_client_forward_of_one_port_merge_as_two_entries()
    {
        var b = P("""[{"port":5173}]""");
        var ours = P("""[{"port":5173},{"port":5173,"https":true,"upstreamTls":false}]""");
        var theirs = P("""[{"port":5173,"label":"vite"}]""");
        var result = ProfileMerge.Merge(b, ours, theirs);
        Assert.True(result.Clean, string.Join(", ", result.Conflicts));
        Assert.Empty(ProfileRules.Validate(result.Merged));
        var merged = result.Merged["forwards"]!.AsArray();
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, f => f!["https"]?.GetValue<bool>() == true && f["to"]!.GetValue<string>() == "host");
        Assert.Contains(merged, f => f!["label"]?.GetValue<string>() == "vite" && f["to"]!.GetValue<string>() == "client");
    }
}
