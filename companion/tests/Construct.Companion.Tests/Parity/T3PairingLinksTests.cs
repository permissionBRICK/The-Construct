using Construct.Companion.Core.State;

namespace Construct.Companion.Tests.Parity;

public sealed class T3PairingLinksTests
{
    [Fact]
    public void ReadsEveryRouteAndKeepsLegacyFirstUrl()
    {
        const string json = """{"pairUrl":"https://host:2300/pair#one","links":[{"kind":"forwarded","pairUrl":"https://host:2300/pair#one"},{"kind":"direct","pairUrl":"https://guest:5178/pair#two"},{"kind":"direct","pairUrl":"file:///tmp/x"}]}""";
        var links = T3Code.ExtractPairLinks(json);
        Assert.Equal(2, links.Count);
        Assert.Equal("forwarded", links[0].Kind);
        Assert.Equal(T3Code.ExtractPairUrl(json), links[0].PairUrl);
        Assert.Equal("direct", links[1].Kind);
        Assert.Equal("https://guest:5178/pair#two", links[1].PairUrl);
        Assert.Empty(T3Code.ExtractPairLinks("{}"));
    }

    [Fact]
    public void ReadsTheProxyRouteFirstAndDropsUnknownKinds()
    {
        const string json = """{"pairUrl":"https://host:2300/pair#one","links":[{"kind":"proxy","pairUrl":"https://t3.example.net:8443/pair#three"},{"kind":"forwarded","pairUrl":"https://host:2300/pair#one"},{"kind":"lan","pairUrl":"https://x.example/pair#four"}]}""";
        var links = T3Code.ExtractPairLinks(json);
        Assert.Equal(["proxy", "forwarded"], links.Select(l => l.Kind));
        Assert.Equal("https://t3.example.net:8443/pair#three", links[0].PairUrl);
        Assert.Equal("https://host:2300/pair#one", T3Code.ExtractPairUrl(json));
    }
}
