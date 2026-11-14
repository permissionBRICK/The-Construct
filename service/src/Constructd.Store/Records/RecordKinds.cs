namespace Constructd.Store.Records;

/// <summary>
/// The kind-level write table of 9.1, the only rule the store can enforce because it cannot read
/// contents. Rules inside a kind (which engine owns which VM, which vault change needs an approval)
/// belong to the writer's code.
/// </summary>
public static class RecordKinds
{
    public const string Profile = "profile";
    public const string Setting = "setting";
    public const string VmDesired = "vm-desired";
    public const string VmApplied = "vm-applied";
    public const string Engine = "engine";
    public const string Device = "device";
    public const string Secret = "secret";
    public const string Conflict = "conflict";
    public const string Upstream = "upstream";
    public const string UpstreamApplied = "upstream-applied";
    public const string Review = "review";

    private static readonly Dictionary<string, (bool Device, bool Engine)> Writers = new(StringComparer.Ordinal)
    {
        [Profile] = (false, true),
        [Setting] = (true, false),
        [VmDesired] = (true, false),
        [VmApplied] = (false, true),
        [Engine] = (true, false),
        [Device] = (true, false),
        [Secret] = (true, true),
        [Conflict] = (false, true),
        [Upstream] = (true, false),
        [UpstreamApplied] = (false, true),
        [Review] = (true, false),
    };

    public static bool IsKnown(string kind) => Writers.ContainsKey(kind);

    public static bool MayWrite(ParticipantKind participant, string kind) =>
        Writers.TryGetValue(kind, out var writers) && (participant == ParticipantKind.Device ? writers.Device : writers.Engine);
}
