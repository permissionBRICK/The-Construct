namespace Constructd.Core.Logic;
public static class MediaNameSanitizer
{
    public static string Clean(string? value) => new((value ?? "media.iso").Where(c => !char.IsControl(c) && c is not '/' and not '\\').Take(120).ToArray());
}
