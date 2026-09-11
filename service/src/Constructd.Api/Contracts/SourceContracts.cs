namespace Constructd.Api.Contracts;
public sealed record SourceEnsureRequest(string? Commit);
public sealed record SourceReadyResponse(string Commit, string State, long SizeBytes, string Sha256, string ReleaseTag);
public sealed record SourceQueuedResponse(string JobId, string Commit, string State);
