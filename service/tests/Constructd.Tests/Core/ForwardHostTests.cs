using Constructd.Core.Domain;

namespace Constructd.Tests.Core;

/// <summary>
/// The one host rule behind every forward link (docs/expose.md). The matrix mirrors the
/// extension's <c>sanitizeHostLabel</c>/<c>urlHostFor</c> tests (extension/test/forwarder.test.js)
/// and the guest CLI's (test/construct-expose.test.sh), because three implementations of one rule
/// are only one rule while they agree address for address.
/// </summary>
public class ForwardHostTests
{
    [Theory]
    [InlineData("christoph-pc", "christoph-pc")]
    [InlineData("pc.home.example", "pc.home.example")]
    [InlineData("10.0.0.7", "10.0.0.7")]
    [InlineData("  pc  ", "pc")]
    // A bracketed literal is accepted and UNWRAPPED: one wire representation, and the brackets
    // belong to the URL, not to the value.
    [InlineData("fe80::1", "fe80::1")]
    [InlineData("[fe80::1]", "fe80::1")]
    [InlineData("[2001:db8::8a2e:370:7334]", "2001:db8::8a2e:370:7334")]
    [InlineData("::ffff:10.0.0.1", "::ffff:10.0.0.1")]
    public void Normalizes_to_the_bare_wire_form(string label, string expected) =>
        Assert.Equal(expected, ForwardHost.Normalize(label));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("pc/evil")]
    [InlineData("evil.test@pc")]
    [InlineData("pc?a=b")]
    // Parsed, not character-classed: every one of these passes a plausible class.
    [InlineData("::::")]
    [InlineData("1::2::3")]
    [InlineData("1:2:3:4:5:6:7:8:9")]
    [InlineData("12345::1")]
    [InlineData("[fe80::1")]
    [InlineData("fe80::1]")]
    [InlineData("[[fe80::1]]")]
    // A zone id would need %25 in a URL and is meaningful only on the PC that owns the
    // interface — the opposite of a label advertising that PC to other machines.
    [InlineData("fe80::1%eth0")]
    [InlineData("[fe80::1%25eth0]")]
    public void Refuses_everything_that_is_not_a_host(string? label) =>
        Assert.Null(ForwardHost.Normalize(label));

    [Theory]
    [InlineData("christoph-pc", "christoph-pc")]
    [InlineData("10.0.0.7", "10.0.0.7")]
    // EXACTLY ONE bracket pair, from either spelling — the double-bracket bug in one line.
    [InlineData("fe80::1", "[fe80::1]")]
    [InlineData("[fe80::1]", "[fe80::1]")]
    public void Brackets_a_literal_exactly_once(string label, string expected) =>
        Assert.Equal(expected, ForwardHost.ForUrl(label));

    [Fact]
    public void An_unusable_label_has_no_url_host_so_the_caller_falls_back_to_loopback() =>
        Assert.Null(ForwardHost.ForUrl("1::2::3"));

    // ── The shared fixture matrix ───────────────────────────────────────────────────────
    // One rule, three implementations — this one, `net.isIP` in extension/src/forwarder.js and
    // `is_ipv6_literal` in bin/construct-expose.sh — and they are only one rule while they agree
    // address for address. The SAME list runs in extension/test/forwarder.test.js and in
    // test/construct-expose.test.sh.

    public static TheoryData<string> ValidLiterals =>
    [
        "::", "::1", "fe80::1", "2001:db8::8a2e:370:7334", "1:2:3:4:5:6:7:8",
        "0:0:0:0:0:0:0:0", "1::", "::2", "0::0", "::ffff:10.0.0.1",
        "1:2:3:4:5:6:1.2.3.4", "::1.2.3.4", "1::1.2.3.4", "1:2:3:4:5:6:7::",
        "fe80::0204:61ff:fe9d:f156", "ABCD::1",
    ];

    /// <summary>
    /// The four marked (*) are the pre-fix controls: a "plausible IPv6" shape filter accepts every
    /// one of them, and each then reached a URL authority (<c>http://[1:::]:5173/</c>). Only a real
    /// parse refuses them.
    /// </summary>
    public static TheoryData<string> InvalidLiterals =>
    [
        "::::", "1::2::3", "1:2:3:4:5:6:7:8:9", "1.2.3:4", "....:", ":::",
        ":1", "1:", "12345::1" /* (*) */, "::ffff:999.1.1.1", "::ffff:1.2.3.004",
        "1:2" /* (*) */, "1:2:3:4:5:6:7" /* (*) */, "1:::" /* (*) */,
        "1::2:3:4:5:6:7:8", "::1.2.3.4.5", "1:2:3:4:5:6:7:1.2.3.4",
        // An embedded IPv4 address is the literal's FINAL 32 bits, so it can never appear
        // before the "::" — the grammar boundary a per-run "quad must be last" check misses.
        "192.0.2.1::", "192.0.2.1::1", "1:192.0.2.1::", "1.2.3.4::1:2",
    ];

    [Theory]
    [MemberData(nameof(ValidLiterals))]
    public void Matrix_accepts_every_real_literal_bare_or_bracketed(string literal)
    {
        Assert.Equal(literal, ForwardHost.Normalize(literal));
        Assert.Equal(literal, ForwardHost.Normalize($"[{literal}]"));
        Assert.Equal($"[{literal}]", ForwardHost.ForUrl(literal));
        Assert.Equal($"[{literal}]", ForwardHost.ForUrl($"[{literal}]"));
    }

    [Theory]
    [MemberData(nameof(InvalidLiterals))]
    public void Matrix_refuses_everything_that_only_looks_like_one(string literal)
    {
        Assert.Null(ForwardHost.Normalize(literal));
        Assert.Null(ForwardHost.ForUrl(literal));
    }
}
