using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

public static class CascadeRules
{
    // MediaCount is populated by the media integration once IMediaStore references are available.
    public static IReadOnlyList<CascadeChild> Children(IEnumerable<Vm> vms) => vms
        .OrderBy(v => v.Name, Ownership.NameComparer)
        .Select(v => new CascadeChild(v.Name, v.Incarnation, v.Sharing, v.State, v.DiskGb, 0)).ToArray();

    public static bool Matches(CascadePreview preview, Vm parent, IReadOnlyList<CascadeChild> children, string token, DateTimeOffset now) =>
        preview.Token == token && preview.ExpiresAt > now && preview.ParentIncarnation == parent.Incarnation &&
        preview.Children.Count == children.Count && preview.Children.All(old => children.Any(current =>
            Ownership.SameName(old.Name, current.Name) && old.Incarnation == current.Incarnation && old.Sharing == current.Sharing));
}
