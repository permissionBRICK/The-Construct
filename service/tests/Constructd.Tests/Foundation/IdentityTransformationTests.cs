using System.Security.Claims;
using Constructd.Api.Auth;
using Constructd.Core.Domain;
using Constructd.Fakes;
namespace Constructd.Tests.Foundation;

public sealed class IdentityTransformationTests
{
    [Fact]
    public async Task NegotiateLikeIdentityWithNonInstanceRoleClaimsCanBeTransformed()
    {
        var users=new InMemoryUserStore();await users.CreateAsync(new("alice",Role.Admin,1,DateTimeOffset.UtcNow),default);
        var principal=new ClaimsPrincipal(new TokenGroupIdentity());
        var result=await new UserClaimsTransformation(users).TransformAsync(principal);
        Assert.True(result.IsKnownUser());Assert.True(result.IsInRole("Admin"));Assert.False(principal.IsKnownUser());
    }
    private sealed class TokenGroupIdentity : ClaimsIdentity
    {
        public TokenGroupIdentity():base([new Claim(ClaimTypes.Name,"alice")],"Negotiate",ClaimTypes.Name,ClaimTypes.GroupSid) { }
        private TokenGroupIdentity(TokenGroupIdentity old):base(old) { }
        public override IEnumerable<Claim> Claims=>base.Claims.Append(new Claim(ClaimTypes.GroupSid,"S-1-5-32-544"));
        public override ClaimsIdentity Clone()=>new TokenGroupIdentity(this);
    }
}
