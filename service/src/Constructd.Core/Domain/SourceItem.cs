using Constructd.Core.Logic;
namespace Constructd.Core.Domain;

public enum SourceState { Downloading, Ready, Deleting, Failed }
public sealed record SourceItem(string Commit, SourceState State, long SizeBytes, string Sha256,
    string ReleaseTag, string? Error, string? JobId, DateTimeOffset Created,
    DateTimeOffset? ReadyAt, DateTimeOffset LastUsedAt);
public sealed class SourceException(string code) : Exception(code), IConstructdError
{
    public string Code { get; } = code;
}
