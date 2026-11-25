using System.Text.Json.Nodes;

namespace Constructd.Api.Contracts;

/// <summary>
/// <c>PUT /vms/{name}/profiles/{profile}</c> and <c>PUT /profiles/{profile}</c>: the profile and the
/// revision the writer last saw (<c>.state/&lt;profile&gt;.rev</c> on a VM); none when it has none.
/// </summary>
public sealed record ProfileWriteRequest(JsonObject? Profile, long? BaseRevision);

/// <summary>
/// The answer to a profile write, delete or resolve. <c>status</c>: <c>written</c> (the profile is at
/// <c>revision</c>, <c>file</c> is its content, null when deleted), <c>queued</c> (the store was not
/// reached: the writer keeps <c>revision</c> and writes <c>file</c>, the result follows by write-back),
/// <c>conflict</c> (the edit is held in the conflict <c>conflictId</c>; <c>fields</c> are in conflict),
/// <c>closed</c> (a resolve found the conflict closed by somebody else) or <c>recomputed</c> (the
/// profile changed meanwhile: <c>conflictId</c> is the new conflict).
/// </summary>
public sealed record ProfileWriteResponse(string Status, long? Revision, string? File, string? ConflictId, IReadOnlyList<string>? Fields);

/// <summary>One profile in <c>GET /profiles</c>.</summary>
public sealed record ProfileSummary(string Name, long Revision);

/// <summary><c>GET /profiles/{profile}</c>.</summary>
public sealed record ProfileResponse(string Name, long Revision, JsonObject Profile);

/// <summary>
/// An open conflict (design 9.6): the profile, the fields in conflict, the base both sides started
/// from (null without one), ours (null: a deletion), theirs (the store's revision; null: deleted)
/// and the merge of every other field. Resolving writes the profile on the current revision.
/// </summary>
public sealed record ProfileConflictResponse(string Id, string Profile, long? ProfileRevision, IReadOnlyList<string> Fields,
    JsonNode? Base, JsonNode? Ours, JsonNode? Theirs, JsonNode? Merged);

/// <summary>A conflict in the heartbeat reply: enough for the guest to start its agent on it.</summary>
public sealed record ProfileConflictNotice(string Id, string Profile, IReadOnlyList<string> Fields);

/// <summary><c>POST …/profile-conflicts/{id}/resolve</c>: the resolved profile.</summary>
public sealed record ProfileResolveRequest(JsonObject? Profile);

/// <summary>
/// <c>POST /profiles/{profile}/upstream</c> (design 9.7): a device fetched a new version of the file an upstream config
/// repo tracks for this profile. <c>operationId</c> names the submission (a retry sends the same); <c>url</c> and
/// <c>path</c> are the file's provenance (a URL never carries a password), <c>commit</c> the upstream commit and
/// <c>content</c> the file at it. <c>base</c> only when the engine has stored no base for the profile yet: the base the
/// device knows (the old client's stored base, carried over by the converter).
/// </summary>
public sealed record UpstreamApplyRequest(string? OperationId, string? Url, string? Path, string? Ref, string? Commit, JsonObject? Content,
    UpstreamBaseRequest? Base, bool? Import = null);

/// <summary>The base a device knows for an upstream file: its commit and content.</summary>
public sealed record UpstreamBaseRequest(string? Commit, JsonObject? Content);

/// <summary>
/// What an upstream version came to. <c>status</c>: <c>unchanged</c> (applied already, or the file did not change),
/// <c>merged</c> (the profile is at <c>revision</c> with the upstream changes, the new base is stored), <c>conflict</c>
/// (both changed <c>fields</c>: the conflict <c>conflictId</c> holds the upstream version, the profile stays), <c>waiting</c>
/// (an open conflict <c>conflictId</c> on the profile is resolved first; submit again later) or <c>dropped</c> (the
/// profile was deleted here: the file is no longer tracked).
/// </summary>
public sealed record UpstreamApplyResponse(string Status, long? Revision, string? ConflictId, IReadOnlyList<string>? Fields);
