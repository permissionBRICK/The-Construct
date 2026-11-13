using Construct.Keys;
using Constructd.Store.Records;
using Microsoft.AspNetCore.Http;

namespace Constructd.Store.Identity;

/// <summary>
/// Who calls the store, from the tokens it issued (<see cref="RequestProof.StoreTokenHeader"/>): a
/// device token counts only with a valid proof by the device's registered key (8.1), an engine
/// participant token as it is. The same in constructd and in the stand-alone host; whatever else the
/// caller signed in with does not make it a store participant.
/// </summary>
public sealed class DirectoryPrincipalResolver(StoreDirectory directory, TimeProvider? time = null, IStorePrincipalResolver? fallback = null) : IStorePrincipalResolver
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public StorePrincipal? Resolve(HttpContext context)
    {
        var token = context.Request.Headers[RequestProof.StoreTokenHeader].ToString();
        if (token.Length == 0 || token.Length > 200) return fallback?.Resolve(context);
        if (directory.Resolve(token) is not { } owner) return null;
        if (owner.Kind == ParticipantKind.Device &&
            !RequestProof.Verify(owner.PublicKey!, token, context.Request.Headers[RequestProof.Header].ToString(), _time.GetUtcNow()))
        {
            return null;
        }

        return new StorePrincipal(owner.Account, owner.Kind, owner.Id);
    }
}

/// <summary>
/// Who may register the first device of an account without a setup code: in constructd, the
/// signed-in user, in that user's own account (5.4). A stand-alone store has none and uses its setup
/// code instead.
/// </summary>
public interface IFirstDeviceAuthority
{
    /// <summary>The account the caller may create its first device in, or null.</summary>
    string? AccountOf(HttpContext context);
}
