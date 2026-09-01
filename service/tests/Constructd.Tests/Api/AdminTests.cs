using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Domain;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public class AdminTests
{
    [Fact]
    public async Task An_admin_creates_a_user_and_issues_it_a_token()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        using var created = await admin.PostJsonAsync("/api/v1/users",
            new { name = "bob", role = "user", maxVms = 2 });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var user = await created.ReadAsync<UserResponse>();
        Assert.Equal("bob", user.Name);
        Assert.Equal(Role.User, user.Role);
        Assert.Equal(2, user.MaxVms);
        Assert.True(user.AllowHostForwards);

        using var tokenResponse = await admin.PostJsonAsync("/api/v1/users/bob/tokens", new { label = "laptop" });
        Assert.Equal(HttpStatusCode.Created, tokenResponse.StatusCode);
        var issued = await tokenResponse.ReadAsync<TokenIssuedResponse>();
        Assert.False(string.IsNullOrWhiteSpace(issued.Token));

        // The issued token really works, and resolves to the new user.
        using var bob = app.CreateAnonymousClient();
        bob.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer", issued.Token);
        var whoami = await (await bob.GetAsync("/api/v1/whoami")).ReadAsync<WhoAmIResponse>();
        Assert.Equal("bob", whoami.Name);
    }

    [Fact]
    public async Task The_token_plaintext_is_never_stored()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.PostJsonAsync("/api/v1/users", new { name = "bob", role = "user", maxVms = 1 });

        var issued = await (await admin.PostJsonAsync("/api/v1/users/bob/tokens", new { label = "laptop" }))
            .ReadAsync<TokenIssuedResponse>();

        var stored = await app.Service<Constructd.Core.Abstractions.ITokenService>()
            .ListAsync("bob", CancellationToken.None);

        var token = Assert.Single(stored);
        Assert.Equal(Constructd.Core.Logic.TokenHasher.Hash(issued.Token), token.TokenHash);
        Assert.DoesNotContain(issued.Token, token.TokenHash);
    }

    [Fact]
    public async Task A_plain_user_may_not_touch_the_admin_surface()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");

        Assert.Equal(HttpStatusCode.Forbidden,
            (await bob.PostJsonAsync("/api/v1/users", new { name = "eve", role = "admin", maxVms = 9 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.DeleteAsync("/api/v1/users/bob")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await bob.PostJsonAsync("/api/v1/users/bob/tokens", new { label = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Theory]
    [InlineData(null, "user", 1, "name")]
    [InlineData("bob", "wizard", 1, "role")]
    [InlineData("bob", null, 1, "role")]
    [InlineData("bob", "user", null, "maxVms")]
    [InlineData("bob", "user", -1, "maxVms")]
    public async Task Invalid_user_payloads_are_rejected(string? name, string? role, int? maxVms, string expected)
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        using var response = await admin.PostJsonAsync("/api/v1/users", new { name, role, maxVms });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Duplicate_users_are_rejected()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        await admin.PostJsonAsync("/api/v1/users", new { name = "bob", role = "user", maxVms = 1 });
        using var again = await admin.PostJsonAsync("/api/v1/users", new { name = "bob", role = "user", maxVms = 1 });

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }

    [Fact]
    public async Task A_token_label_is_required_so_tokens_stay_distinguishable()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.PostJsonAsync("/api/v1/users", new { name = "bob", role = "user", maxVms = 1 });

        using var response = await admin.PostJsonAsync("/api/v1/users/bob/tokens", new { label = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Tokens_cannot_be_issued_for_an_unknown_user()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        using var response = await admin.PostJsonAsync("/api/v1/users/nobody/tokens", new { label = "x" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_user_who_still_owns_vms_cannot_be_deleted()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        using var bob = await app.CreateUserClientAsync("bob");
        await bob.CreateVmAsync("work-vm");

        using var response = await admin.DeleteAsync("/api/v1/users/bob");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task An_admin_cannot_delete_themselves()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        using var response = await admin.DeleteAsync("/api/v1/users/admin");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task Mutating_calls_land_in_the_audit_log()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.PostJsonAsync("/api/v1/users", new { name = "bob", role = "user", maxVms = 1 });
        await admin.PostJsonAsync("/api/v1/users/bob/tokens", new { label = "laptop" });

        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();

        Assert.Contains(entries, e => e is { Action: "user.create", Target: "bob", Actor: "admin" });
        Assert.Contains(entries, e => e is { Action: "token.issue", Target: "bob" });
        // Newest first.
        Assert.Equal("token.issue", entries[0].Action);
        // The secret itself is never audited.
        Assert.All(entries, e => Assert.DoesNotContain("token=", e.Detail ?? string.Empty));
    }

    [Fact]
    public async Task The_audit_limit_is_honoured()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

        for (var i = 0; i < 5; i++)
        {
            await admin.PostJsonAsync("/api/v1/users", new { name = $"user{i}", role = "user", maxVms = 1 });
        }

        var entries = await (await admin.GetAsync("/api/v1/audit?limit=2")).ReadAsync<List<AuditResponse>>();

        Assert.Equal(2, entries.Count);
    }
}
