namespace Construct.Companion.Core.Abstractions;

// State discovery needs marker timestamps; profile imports must never replace a file
// that appeared after their pre-check. Implementations publish complete bytes atomically.
public interface IStateFileSystem : IFileSystem
{
    bool DirectoryExists(string path);
    DateTimeOffset? LastWriteTime(string path);
    bool WriteFileIfAbsent(string path, ReadOnlySpan<byte> contents);
}
