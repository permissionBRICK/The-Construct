using Constructd.Core.Configuration;
using Constructd.Core.Logic;

namespace Constructd.Proxmox;

/// <summary>Only generated, flat ISO files in the configured media store are attachable.</summary>
public sealed class ProxmoxMediaVolumes(ConstructdOptions options)
{
    public string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(
        options.HostAdmin.Media.RootDir ?? "/var/lib/constructd/media/template/iso"));

    public string ToVolume(string path)
    {
        var full = Confine(path);
        if (!File.Exists(full)) throw new ChildValidationException("media-not-ready", "media");
        return options.Proxmox.MediaStorage + ":iso/" + Path.GetFileName(full);
    }

    public string FromVolume(string volume)
    {
        var prefix = options.Proxmox.MediaStorage + ":iso/";
        if (!volume.StartsWith(prefix, StringComparison.Ordinal)) throw Refused();
        return Confine(Path.Combine(Root, volume[prefix.Length..]));
    }

    private string Confine(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path)) throw Refused();
            var full = Path.GetFullPath(path);
            var name = Path.GetFileName(full);
            if (Path.GetDirectoryName(full) != Root || name.Length != 36 ||
                !name.EndsWith(".iso", StringComparison.Ordinal) || !name[..32].All(Uri.IsHexDigit)) throw Refused();
            for (FileSystemInfo? entry = new FileInfo(full); entry is not null;
                 entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
                if (entry.LinkTarget is not null || entry.Exists && (entry.Attributes & FileAttributes.ReparsePoint) != 0) throw Refused();
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        { throw Refused(); }
    }

    private static ChildValidationException Refused() => new("validation", "mediaPath");
}
