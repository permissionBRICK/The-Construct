using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

public static class LeaseRules
{
    public static bool Due(Lease? lease, DateTimeOffset now) => lease is { State: LeaseState.Active or LeaseState.Overdue, ExpiresAt: not null } && lease.ExpiresAt <= now;
    public static bool RetryDue(Lease? lease, DateTimeOffset now, TimeSpan retry) => Due(lease, now) &&
        (lease!.State == LeaseState.Active || lease.LastExpiryAttemptAt is null || lease.LastExpiryAttemptAt <= now - retry);
    public static Lease Activate(Lease lease, string text, long? seconds, DateTimeOffset at) =>
        new(text, seconds, at, seconds is long finite ? at.AddSeconds(finite) : null,
            seconds is null ? LeaseState.Unlimited : LeaseState.Active, lease.Version + 1, null, null);
    public static Lease ExpiryOutcome(Lease lease, DateTimeOffset at, bool completed, string outcome) =>
        lease with { State = completed ? LeaseState.Expired : LeaseState.Overdue, Version = lease.Version + 1,
            LastExpiryAttemptAt = at, LastExpiryOutcome = outcome };
}
