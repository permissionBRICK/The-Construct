namespace Constructd.Core.Domain;

/// <param name="RequestedSeconds">null = never.</param>
/// <param name="Version">Bumped on every lease write; expiry jobs re-check it (§5.4).</param>
public sealed record Lease(
    string RequestedText,
    long? RequestedSeconds,
    DateTimeOffset? ActivatedAt,
    DateTimeOffset? ExpiresAt,
    LeaseState State,
    long Version,
    DateTimeOffset? LastExpiryAttemptAt,
    string? LastExpiryOutcome);
