namespace Constructd.Core.Domain;

/// <summary>
/// An admin-issued API token. Only the hash is ever stored; the plaintext is shown once at
/// issue time and never persisted or logged.
/// </summary>
/// <param name="TokenHash">Lowercase hex SHA-256 of the token secret.</param>
public sealed record ApiToken(
    string Id,
    string UserName,
    string TokenHash,
    DateTimeOffset Created,
    DateTimeOffset? LastUsed,
    string Label);
