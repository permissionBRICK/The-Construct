namespace Constructd.Core.Domain;

/// <summary>
/// A service user. Users are created by an admin only — there is no self-registration; an
/// authenticated Windows identity that has no <see cref="User"/> record is authenticated but
/// unauthorized.
/// </summary>
/// <param name="Name">Identity name as the auth scheme reports it (e.g. <c>DOMAIN\alice</c>).</param>
/// <param name="MaxVms">Quota: how many VMs this user may own. 0 means "may not create VMs".</param>
/// <param name="AllowHostForwards">
/// Admin flag (default <c>true</c>): may this user's VMs materialize LAN-reachable
/// <see cref="ForwardTarget.Host"/> forwards (plan §4.6)?
/// </param>
public sealed record User(
    string Name,
    Role Role,
    int MaxVms,
    DateTimeOffset Created,
    bool AllowHostForwards = true);
