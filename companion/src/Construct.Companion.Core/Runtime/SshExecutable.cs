using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Core.Runtime;

public static class SshExecutable
{
    public static string Resolve(IFileSystem files, IEnumerable<string> pathDirectories, string? systemRoot, bool windows)
    {
        foreach (var directory in pathDirectories)
        {
            var path = directory.TrimEnd('/', '\\') + (windows ? "\\ssh.exe" : "/ssh");
            if (files.FileExists(path)) return path;
        }
        if (windows && !string.IsNullOrEmpty(systemRoot))
        {
            var path = systemRoot.TrimEnd('/', '\\') + "\\System32\\OpenSSH\\ssh.exe";
            if (files.FileExists(path)) return path;
        }
        return "ssh";
    }
}
