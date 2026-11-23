using Microsoft.AspNetCore.Http;

namespace Constructd.Store.Records;

/// <summary>Which side of the write table (9.1) a caller is on.</summary>
public enum ParticipantKind
{
    Device,
    Engine,
}

/// <summary>
/// The authenticated caller of the store: the account whose records it reaches, and the participant
/// it is. <see cref="Actor"/> is what the envelope must carry (8.6: the store sets <c>actor</c> from
/// the authenticated principal and refuses a write that claims another).
/// </summary>
public sealed record StorePrincipal(string Account, ParticipantKind Kind, string Id)
{
    public string Actor => (Kind == ParticipantKind.Device ? "device:" : "engine:") + Id;

    public static StorePrincipal Device(string account, string id) => new(account, ParticipantKind.Device, id);

    public static StorePrincipal Engine(string account, string id) => new(account, ParticipantKind.Engine, id);
}

/// <summary>
/// Who is calling the store's routes. Pairing, device keys and store tokens come later (8.3); until
/// then each host supplies its own resolver: constructd maps a signed-in user to that user's device,
/// the stand-alone host maps configured tokens. Null means "not a store participant".
/// </summary>
public interface IStorePrincipalResolver
{
    StorePrincipal? Resolve(HttpContext context);
}
