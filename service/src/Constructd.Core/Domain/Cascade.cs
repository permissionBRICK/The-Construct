namespace Constructd.Core.Domain;

/// <param name="Incarnation">null when the hypervisor id is not known yet (create in flight, migrated primary before inventory); compared by strict equality, null included.</param>
public sealed record CascadeChild(string Name, string? Incarnation, SharingScope Sharing, VmState State, int DiskGb, int MediaCount);

/// <summary>The stored preview a cascade confirmation must match (§8.8).</summary>
public sealed record CascadePreview(
    string Parent,
    string? ParentIncarnation,
    string Token,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<CascadeChild> Children,
    CascadeState State,
    string? JobId,
    IReadOnlyDictionary<string, string> Outcomes);
