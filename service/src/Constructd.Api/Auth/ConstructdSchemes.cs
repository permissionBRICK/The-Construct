namespace Constructd.Api.Auth;

/// <summary>Authentication scheme names.</summary>
public static class ConstructdSchemes
{
    /// <summary>Policy scheme that picks the real scheme from the request (see AuthenticationSetup).</summary>
    public const string Default = "Constructd";

    /// <summary>Admin-issued user tokens: <c>Authorization: Bearer &lt;secret&gt;</c>.</summary>
    public const string Bearer = "Bearer";

    /// <summary>VM-scoped tokens: <c>Authorization: VmToken &lt;secret&gt;</c>.</summary>
    public const string VmToken = "VmToken";

    /// <summary>
    /// Stand-in for Negotiate off Windows. Enabled ONLY in fake mode; see AuthenticationSetup.
    /// </summary>
    public const string TestIdentity = "TestIdentity";
}

/// <summary>Claim types this service adds on top of the standard ones.</summary>
public static class ConstructdClaims
{
    /// <summary>Present on principals that resolve to a <see cref="Core.Domain.User"/> record.</summary>
    public const string KnownUser = "constructd:known-user";

    /// <summary>VM name a VM-scoped token is bound to.</summary>
    public const string VmName = "constructd:vm";
}

/// <summary>Header the test-identity scheme reads (fake mode only).</summary>
public static class ConstructdHeaders
{
    public const string TestIdentity = "X-Constructd-Test-Identity";
}
