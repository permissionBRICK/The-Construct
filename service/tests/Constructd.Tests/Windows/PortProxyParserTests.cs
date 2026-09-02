using Constructd.Windows.Forwards;

namespace Constructd.Tests.Windows;

/// <summary>
/// netsh output fixtures. The parser has to survive localization — the host this service runs on is
/// whatever the admin installed — so these are the real English and German layouts, plus the shapes
/// that must not be mistaken for rules.
/// </summary>
public sealed class PortProxyParserTests
{
    private const string English = """
        Listen on ipv4:             Connect to ipv4:

        Address         Port        Address         Port
        --------------- ----------  --------------- ----------
        0.0.0.0         2201        172.20.144.5    22
        0.0.0.0         2301        172.20.144.5    3000
        """;

    private const string German = """
        Lauschen auf ipv4:          Verbindung mit ipv4:

        Adresse         Port        Adresse         Port
        --------------- ----------  --------------- ----------
        0.0.0.0         2201        172.20.144.5    22
        """;

    [Fact]
    public void The_english_layout_parses()
    {
        var rules = PortProxyParser.Parse(English);

        Assert.Equal(2, rules.Count);
        Assert.Equal(new PortProxyRule("0.0.0.0", 2201, "172.20.144.5", 22), rules[0]);
        Assert.Equal(new PortProxyRule("0.0.0.0", 2301, "172.20.144.5", 3000), rules[1]);
    }

    [Fact]
    public void The_german_layout_parses_identically()
    {
        // Rows are read by shape, not by column header, so a localized host reconciles the same way.
        var rules = PortProxyParser.Parse(German);

        Assert.Equal([new PortProxyRule("0.0.0.0", 2201, "172.20.144.5", 22)], rules);
    }

    [Fact]
    public void Headers_separators_and_blank_lines_are_not_rules()
    {
        Assert.Empty(PortProxyParser.Parse("""
            Listen on ipv4:             Connect to ipv4:

            Address         Port        Address         Port
            --------------- ----------  --------------- ----------
            """));
    }

    [Fact]
    public void No_output_at_all_is_no_rules_rather_than_an_error()
    {
        // A host with no portproxy rules prints nothing at all.
        Assert.Empty(PortProxyParser.Parse(string.Empty));
        Assert.Empty(PortProxyParser.Parse(null));
    }

    [Fact]
    public void Windows_line_endings_and_ragged_spacing_parse()
    {
        var rules = PortProxyParser.Parse("0.0.0.0   2201  172.20.144.5   22\r\n");

        Assert.Equal([new PortProxyRule("0.0.0.0", 2201, "172.20.144.5", 22)], rules);
    }

    [Fact]
    public void The_wildcard_listen_address_reads_as_any()
    {
        var rules = PortProxyParser.Parse("*   2201  172.20.144.5   22");

        Assert.Equal("0.0.0.0", rules[0].ListenAddress);
    }

    [Fact]
    public void Ipv6_rows_and_nonsense_are_skipped_rather_than_guessed_at()
    {
        var rules = PortProxyParser.Parse("""
            ::              2201        fe80::1         22
            hello           world       and             more
            0.0.0.0         70000       172.20.144.5    22
            0.0.0.0         2201        172.20.144.5    22
            """);

        Assert.Equal([new PortProxyRule("0.0.0.0", 2201, "172.20.144.5", 22)], rules);
    }
}
