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
    [InlineData("christoph@DC.HTL-SKY.NET", "HOME", "", @"HOME\christoph")]
    [InlineData("christoph@DC.HTL-SKY.NET", "HOME", "dc.htl-sky.net", @"HOME\christoph")]
    [InlineData("christoph@OTHER.REALM", "HOME", "DC.HTL-SKY.NET", "christoph@OTHER.REALM")]
    [InlineData(@"HOME\christoph", "HOME", "", @"HOME\christoph")]
    [InlineData("alice", "HOME", "", "alice")]
    [InlineData("christoph@DC.HTL-SKY.NET", "", "", "christoph@DC.HTL-SKY.NET")]
    [InlineData("@DC.HTL-SKY.NET", "HOME", "", "@DC.HTL-SKY.NET")]
    [InlineData("christoph@", "HOME", "", "christoph@")]
    public void Normalize_maps_a_realm_principal_onto_the_domain_form(string name, string domain, string realm, string expected)
    {
        Assert.Equal(expected, PrincipalNames.Normalize(name, domain, realm));
    }

    [Fact]
    public async Task The_claims_transformation_renames_a_kerberos_principal_and_finds_the_domain_user()
    {
        var users = new InMemoryUserStore();
        await users.CreateAsync(new User(@"HOME\christoph", Role.Admin, 3, DateTimeOffset.UtcNow), CancellationToken.None);
        var options = new ConstructdOptions { Negotiate = new NegotiateOptions { DomainName = "HOME", Realm = "DC.HTL-SKY.NET" } };
        var transformation = new UserClaimsTransformation(users, options);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "christoph@DC.HTL-SKY.NET")], "Negotiate", ClaimTypes.Name, ClaimTypes.Role);

        var result = await transformation.TransformAsync(new ClaimsPrincipal(identity));

        Assert.Equal(@"HOME\christoph", result.Identity!.Name);
        Assert.True(result.IsKnownUser());
        Assert.True(result.IsAdmin());
    }

    [Fact]
    public async Task An_unknown_kerberos_principal_is_still_renamed_but_gains_no_role()
    {
        var options = new ConstructdOptions { Negotiate = new NegotiateOptions { DomainName = "HOME" } };
        var transformation = new UserClaimsTransformation(new InMemoryUserStore(), options);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "stranger@DC.HTL-SKY.NET")], "Negotiate", ClaimTypes.Name, ClaimTypes.Role);

        var result = await transformation.TransformAsync(new ClaimsPrincipal(identity));

        Assert.Equal(@"HOME\stranger", result.Identity!.Name);
        Assert.False(result.IsKnownUser());
    }
}
