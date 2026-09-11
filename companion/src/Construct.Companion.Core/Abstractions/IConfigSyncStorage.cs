namespace Construct.Companion.Core.Abstractions;

// Atomic create and metadata are required for the lock shared with JS/PowerShell.
public interface IConfigSyncStorage
{
    bool TryCreateFile(string path, string content);
    DateTimeOffset? LastWriteTime(string path);
    bool DirectoryExists(string path);
    void DeleteDirectory(string path);
    int ProcessId { get; }
    bool ProcessIsDefinitelyDead(int pid);
}
