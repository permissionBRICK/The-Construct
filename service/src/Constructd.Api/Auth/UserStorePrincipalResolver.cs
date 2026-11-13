using Constructd.Store.Records;

namespace Constructd.Api.Auth;

/// <summary>
/// Who calls the store's routes in constructd until device keys and store tokens exist (8.3): a
/// signed-in user counts as that user's device, in that user's account. VM tokens and vault
/// devices are not store participants. The engine itself does not come through here: it calls its
/// store in process as <c>engine:&lt;id&gt;</c> (<see cref="Composition.EngineStoreIdentity"/>).
/// </summary>
public sealed class UserStorePrincipalResolver : IStorePrincipalResolver
{
    public StorePrincipal? Resolve(HttpContext context)
    {
        var user = context.User;
        if (!user.IsKnownUser() || user.IsVmToken() || user.IsVaultDevice()) return null;
        var name = user.NameOrEmpty();
        return name.Length == 0 ? null : StorePrincipal.Device(name, name);
    }
}
