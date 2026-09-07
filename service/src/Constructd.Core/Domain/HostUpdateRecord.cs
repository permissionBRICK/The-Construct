namespace Constructd.Core.Domain;

public sealed record HostUpdatePhase(string Name, DateTimeOffset At, string? Outcome, string? Error);

public sealed record HostUpdateRecord(
    string Id,
    string Commit,
    string? ReleaseTag,
    string? PackageVersion,
    HostUpdateState State,
    string? Phase,
    IReadOnlyList<HostUpdatePhase> Phases,
    DateTimeOffset Started,
    DateTimeOffset? Finished,
    string? Error,
    string? PreviousCommit,
    string Actor,
    IReadOnlyList<string> BlockingJobs);

public sealed record InstalledRelease(string Commit, string PackageVersion, DateTimeOffset? InstalledAt, string Source);
