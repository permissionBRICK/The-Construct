using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Constructd.Tests.Support;

namespace Constructd.Tests.Vault;

/// <summary>The phone pages: anonymous, embedded, strict headers, no inline code and no third-party resources.</summary>
public sealed partial class VaultWebTests
{
    [Theory]
    [InlineData("/vault/", "text/html", "data-page=\"index\"")]
    [InlineData("/vault/pair", "text/html", "data-page=\"pair\"")]
    [InlineData("/vault/vault.js", "text/javascript", "constructVaultDevice")]
    [InlineData("/vault/vault.css", "text/css", "prefers-color-scheme")]
    public async Task Pages_are_served_anonymously_with_strict_headers(string path, string type, string marker)
    {
        using var app = new TestApp();
        using var anonymous = app.CreateAnonymousClient();
        using var response = await anonymous.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
            response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Contains(marker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_index_without_a_trailing_slash_redirects_so_relative_links_resolve()
    {
        using var app = new TestApp();
        using var client = app.CreateClient(new() { AllowAutoRedirect = false });
        using var response = await client.GetAsync("/vault");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("vault/", response.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Pages_carry_no_inline_code_styles_or_foreign_resources()
    {
        using var app = new TestApp();
        using var anonymous = app.CreateAnonymousClient();
        foreach (var path in new[] { "/vault/", "/vault/pair" })
        {
            var html = await anonymous.GetStringAsync(path);
            Assert.All(ScriptTag().Matches(html), m => Assert.Matches("src=\"vault\\.js\"", m.Value));
            Assert.Equal(1, ScriptTag().Count(html));
            Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(" style=", html, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotMatch(" on[a-z]+=", html);
            Assert.DoesNotContain("http://", html, StringComparison.Ordinal);
            Assert.DoesNotContain("https://", html, StringComparison.Ordinal);
        }

        var script = await anonymous.GetStringAsync("/vault/vault.js");
        // Data reaches the DOM through textContent only.
        Assert.DoesNotContain("innerHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("insertAdjacentHTML", script, StringComparison.Ordinal);
        Assert.DoesNotContain("eval(", script, StringComparison.Ordinal);
        Assert.Contains("history.replaceState", script, StringComparison.Ordinal);
        Assert.Contains("'VaultDevice '", script, StringComparison.Ordinal);
        Assert.Contains("location.replace", script, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_advertises_the_key_vault()
    {
        using var app = new TestApp();
        using var anonymous = app.CreateAnonymousClient();
        var health = await anonymous.GetFromJsonAsync<JsonObject>("/api/v1/health");
        Assert.Contains("key-vault", health!["apiFeatures"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [GeneratedRegex("<script[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptTag();
}
