namespace Construct.Companion.Core.Abstractions;

// Resolves roots and accesses state files; writes replace a file atomically.
// Watch callbacks signal invalidation only, and disposing a watch unsubscribes it.
public interface IFileSystem
{
    string? GetRoot(FileSystemRoot root);
    bool FileExists(string path);
    byte[]? ReadFile(string path);
    void WriteFileAtomic(string path, ReadOnlySpan<byte> contents);
    void DeleteFile(string path);
    void CreateDirectory(string path);
    IReadOnlyList<string> EnumerateFiles(string directory);
    IReadOnlyList<string> EnumerateDirectories(string directory);
    IDisposable Watch(string directory, Action changed);
}
public enum FileSystemRoot { LocalAppData, Temp, UserProfile, InstallDirectory }
