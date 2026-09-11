using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Windows.Source;

/// <summary>Only generated flat files, with the same ancestor/reparse checks as the media store.</summary>
public class SourceFileStore(string root) : ISourceFiles
{
    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
    public string PathFor(string commit, SourceFileKind kind)
    {
        if (!SourceZipRules.ValidCommit(commit) || !Enum.IsDefined(kind)) throw new SourceException("source-path-refused");
        for (var dir = new DirectoryInfo(Root); dir is not null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new SourceException("source-path-refused");
        var path = Path.Combine(Root, commit + (kind == SourceFileKind.Part ? ".part" : ".zip"));
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new SourceException("source-path-refused");
        return path;
    }
    public virtual long FreeBytes() => new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace;
    public bool Exists(string commit, SourceFileKind kind) => File.Exists(PathFor(commit, kind)) || Directory.Exists(PathFor(commit, kind));
    public virtual void Delete(string commit, SourceFileKind kind) => File.Delete(PathFor(commit, kind));
    public void Publish(string commit) => File.Move(PathFor(commit, SourceFileKind.Part), PathFor(commit, SourceFileKind.Zip), false);
    public Stream OpenRead(string commit, SourceFileKind kind) => new FileStream(PathFor(commit, kind), FileMode.Open, FileAccess.Read,
        FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
    public IReadOnlyList<(string Commit, SourceFileKind Kind, DateTimeOffset Modified)> List()
    {
        // Validate ancestors even when the directory is empty.
        _ = PathFor(new string('0', 40), SourceFileKind.Zip);
        if (!Directory.Exists(Root)) return [];
        var items = new List<(string, SourceFileKind, DateTimeOffset)>();
        foreach (var path in Directory.EnumerateFiles(Root))
        {
            var commit = Path.GetFileNameWithoutExtension(path);
            if (!SourceZipRules.ValidCommit(commit) || Path.GetExtension(path) is not (".zip" or ".part")) continue;
            var kind = Path.GetExtension(path) == ".zip" ? SourceFileKind.Zip : SourceFileKind.Part;
            items.Add((commit, kind, File.GetLastWriteTimeUtc(PathFor(commit, kind))));
        }
        return items;
    }
}
