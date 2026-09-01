using System.Security.Claims;
using Constructd.Core.Domain;

namespace Constructd.Api.Auth;

/// <summary>Reading the service's identity model off a <see cref="ClaimsPrincipal"/>.</summary>
public static class PrincipalExtensions
{
    /// <summary>Identity name, or empty when unauthenticated.</summary>
    public static string NameOrEmpty(this ClaimsPrincipal principal) =>
        principal.Identity?.Name ?? string.Empty;

    /// <summary>True when this principal resolved to a <see cref="User"/> record.</summary>
    public static bool IsKnownUser(this ClaimsPrincipal principal) =>
        principal.HasClaim(c => c.Type == ConstructdClaims.KnownUser);

    /// <summary>The VM a VM-scoped token is bound to, or null for user principals.</summary>
    public static string? VmTokenName(this ClaimsPrincipal principal) =>
        principal.FindFirst(ConstructdClaims.VmName)?.Value;

    public static bool IsVmToken(this ClaimsPrincipal principal) => principal.VmTokenName() is not null;

    public static Role RoleOrDefault(this ClaimsPrincipal principal) =>
        principal.IsInRole(nameof(Role.Admin)) ? Role.Admin : Role.User;

    public static bool IsAdmin(this ClaimsPrincipal principal) =>
        principal.IsKnownUser() && principal.IsInRole(nameof(Role.Admin));

    /// <summary>Actor name for audit entries.</summary>
    public static string Actor(this ClaimsPrincipal principal)
    {
        var name = principal.NameOrEmpty();
        return string.IsNullOrEmpty(name) ? "anonymous" : name;
    }
}
