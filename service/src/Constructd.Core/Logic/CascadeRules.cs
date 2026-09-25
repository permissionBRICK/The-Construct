using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

public static class CascadeRules
{
    // MediaCount is populated by the media integration once IMediaStore references are available.
    public static IReadOnlyList<CascadeChild> Children(IEnumerable<Vm> vms) => vms
        .OrderBy(v => v.Name, Ownership.NameComparer)
        .Select(v => new CascadeChild(v.Name, v.Incarnation, v.Sharing, v.State, v.DiskGb, 0)).ToArray();

    /// <summary>A confirmation with keep=shared leaves every non-private child in place: still parented by
    /// name, so a primary re-created under the same name is its parent again (a reinstall). Private children go with the primary.</summary>
    public const string KeptOutcome = "kept";
    public static bool IsKept(CascadeChild child, bool keepShared) => keepShared && child.Sharing != SharingScope.Private;
    public static Dictionary<string, string> KeptOutcomes(IEnumerable<CascadeChild> children, bool keepShared)
    {
        var kept = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in children) if (IsKept(child, keepShared)) kept[child.Name] = KeptOutcome;
        return kept;
    }

    public static bool Matches(CascadePreview preview, Vm parent, IReadOnlyList<CascadeChild> children, string token, DateTimeOffset now) =>
        preview.Token == token && preview.ExpiresAt > now && preview.ParentIncarnation == parent.Incarnation &&
        preview.Children.Count == children.Count && preview.Children.All(old => children.Any(current =>
            Ownership.SameName(old.Name, current.Name) && old.Incarnation == current.Incarnation && old.Sharing == current.Sharing));
}
