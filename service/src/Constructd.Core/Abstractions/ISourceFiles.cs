namespace Constructd.Core.Abstractions;

public enum SourceFileKind { Part, Zip }
public interface ISourceFiles
{
    string Root { get; }
    string PathFor(string commit, SourceFileKind kind);
    long FreeBytes();
    bool Exists(string commit, SourceFileKind kind);
    void Delete(string commit, SourceFileKind kind);
    void Publish(string commit);
    Stream OpenRead(string commit, SourceFileKind kind);
    IReadOnlyList<(string Commit, SourceFileKind Kind, DateTimeOffset Modified)> List();
}
