using System.Security.Claims;
using Constructd.Api.Auth;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;

namespace Constructd.Tests.Proxmox;

/// <summary>
/// Kerberos names a user <c>user@REALM</c>; a Windows host and the user store say <c>DOMAIN\user</c>.
/// The mapping is pure, opt-in through the NetBIOS domain name, and applied by the claims
/// transformation so <c>whoami</c>, the audit trail and the lookup all see one spelling.
/// </summary>
public sealed class PrincipalNamesTests
{
    [Theory]
    [InlineData("alice@CORP.EXAMPLE.COM", "HOME", "", @"HOME\alice")]
    [InlineData("alice@CORP.EXAMPLE.COM", "HOME", "corp.example.com", @"HOME\alice")]
    [InlineData("alice@OTHER.REALM", "HOME", "CORP.EXAMPLE.COM", "alice@OTHER.REALM")]
    [InlineData(@"HOME\alice", "HOME", "", @"HOME\alice")]
    [InlineData("alice", "HOME", "", "alice")]
    [InlineData("alice@CORP.EXAMPLE.COM", "", "", "alice@CORP.EXAMPLE.COM")]
    [InlineData("@CORP.EXAMPLE.COM", "HOME", "", "@CORP.EXAMPLE.COM")]
    [InlineData("alice@", "HOME", "", "alice@")]
    public void Normalize_maps_a_realm_principal_onto_the_domain_form(string name, string domain, string realm, string expected)
    {
        Assert.Equal(expected, PrincipalNames.Normalize(name, domain, realm));
    }

    [Fact]
    public async Task The_claims_transformation_renames_a_kerberos_principal_and_finds_the_domain_user()
    {
        var users = new InMemoryUserStore();
        await users.CreateAsync(new User(@"HOME\alice", Role.Admin, 3, DateTimeOffset.UtcNow), CancellationToken.None);
        var options = new ConstructdOptions { Negotiate = new NegotiateOptions { DomainName = "HOME", Realm = "CORP.EXAMPLE.COM" } };
        var transformation = new UserClaimsTransformation(users, options);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice@CORP.EXAMPLE.COM")], "Negotiate", ClaimTypes.Name, ClaimTypes.Role);

        var result = await transformation.TransformAsync(new ClaimsPrincipal(identity));

        Assert.Equal(@"HOME\alice", result.Identity!.Name);
        Assert.True(result.IsKnownUser());
        Assert.True(result.IsAdmin());
    }

    [Fact]
    public async Task An_unknown_kerberos_principal_is_still_renamed_but_gains_no_role()
    {
        var options = new ConstructdOptions { Negotiate = new NegotiateOptions { DomainName = "HOME" } };
        var transformation = new UserClaimsTransformation(new InMemoryUserStore(), options);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "stranger@CORP.EXAMPLE.COM")], "Negotiate", ClaimTypes.Name, ClaimTypes.Role);

        var result = await transformation.TransformAsync(new ClaimsPrincipal(identity));

        Assert.Equal(@"HOME\stranger", result.Identity!.Name);
        Assert.False(result.IsKnownUser());
    }
}
