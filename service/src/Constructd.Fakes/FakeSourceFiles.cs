using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Fakes;

public sealed class FakeSourceFiles : ISourceFiles, IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "construct-source-" + Guid.NewGuid().ToString("n"));
    public bool FailDelete { get; set; }
    public long AvailableBytes { get; set; } = 100L << 30;
    public FakeSourceFiles() { Directory.CreateDirectory(Root); }
    public string PathFor(string commit, SourceFileKind kind)
    { if (!SourceZipRules.ValidCommit(commit) || !Enum.IsDefined(kind)) throw new SourceException("source-path-refused"); return Path.Combine(Root, commit + (kind == SourceFileKind.Part ? ".part" : ".zip")); }
    public long FreeBytes() => AvailableBytes;
    public bool Exists(string commit, SourceFileKind kind) => File.Exists(PathFor(commit, kind));
    public void Delete(string commit, SourceFileKind kind)
    { if (FailDelete) throw new IOException("delete fault"); File.Delete(PathFor(commit, kind)); }
    public void Publish(string commit) => File.Move(PathFor(commit, SourceFileKind.Part), PathFor(commit, SourceFileKind.Zip));
    public Stream OpenRead(string commit, SourceFileKind kind) => File.OpenRead(PathFor(commit, kind));
    public IReadOnlyList<(string Commit, SourceFileKind Kind, DateTimeOffset Modified)> List() => Directory.EnumerateFiles(Root)
        .Where(p => SourceZipRules.ValidCommit(Path.GetFileNameWithoutExtension(p)) && Path.GetExtension(p) is ".zip" or ".part")
        .Select(p => (Path.GetFileNameWithoutExtension(p), Path.GetExtension(p) == ".zip" ? SourceFileKind.Zip : SourceFileKind.Part, new DateTimeOffset(File.GetLastWriteTimeUtc(p)))).ToArray();
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
}
