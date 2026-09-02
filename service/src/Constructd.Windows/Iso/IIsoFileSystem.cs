using System.Security.Cryptography;
using System.Text;

namespace Constructd.Windows.Iso;

/// <summary>
/// The file-system operations the ISO build needs, as a seam.
///
/// It exists for one reason: the tests run on Linux, where <c>C:\ProgramData\…</c> is not a path.
/// Faking these few calls is what lets the exact <c>wsl.exe</c> argument vector — WSL path mapping
/// included — be asserted off Windows, which is the only place that argument vector is ever checked.
/// </summary>
public interface IIsoFileSystem
{
    void CreateDirectory(string path);

    bool FileExists(string path);

    /// <summary>Size in bytes; 0 for a file that is not there.</summary>
    long FileLength(string path);

    string ReadAllText(string path);

    /// <summary>Writes UTF-8 without a BOM. The ISO build script must stay a valid shell script.</summary>
    void WriteAllText(string path, string content);

    void DeleteFile(string path);

    /// <summary>Lowercase hex SHA-256 of a file.</summary>
    string ComputeSha256(string path);

    /// <summary>
    /// Full paths of the files in a directory matching a wildcard pattern; empty when the directory is
    /// not there. Unordered — the catalog sorts what it gets.
    /// </summary>
    IReadOnlyList<string> ListFiles(string directoryPath, string searchPattern);

    /// <summary>Rename, replacing the destination. This is the ISO catalog's atomic pointer swap.</summary>
    void MoveFile(string sourcePath, string destinationPath, bool overwrite);

    /// <summary>
    /// Create an empty file, failing if it already exists — one atomic operation, not a check
    /// followed by a create. It is how the ISO catalog RESERVES a versioned name: two administrators
    /// running a build in the same second are two processes, and nothing in this service can serialize
    /// them, so the file system has to be the arbiter.
    /// </summary>
    bool TryCreateNewFile(string path);

    /// <summary>
    /// Delete, reporting rather than throwing when the file is locked. Hyper-V holds an open handle on
    /// an ISO that is attached to a VM, so "cannot delete it" is a normal answer here, not an error.
    /// </summary>
    bool TryDeleteFile(string path);
}

/// <summary>The real thing: <see cref="System.IO"/>.</summary>
public sealed class IsoFileSystem : IIsoFileSystem
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public bool FileExists(string path) => File.Exists(path);

    public long FileLength(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length : 0;
    }

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string content) => File.WriteAllText(path, content, Utf8NoBom);

    public void DeleteFile(string path) => File.Delete(path);

    public string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public IReadOnlyList<string> ListFiles(string directoryPath, string searchPattern) =>
        Directory.Exists(directoryPath)
            ? Directory.GetFiles(directoryPath, searchPattern, SearchOption.TopDirectoryOnly)
            : [];

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite) =>
        File.Move(sourcePath, destinationPath, overwrite);

    public bool TryCreateNewFile(string path)
    {
        try
        {
            // CreateNew is CREATE_NEW: the kernel refuses if the name exists, so the winner of a race
            // between two processes is decided once, by the file system.
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            // Attached to a running VM: Hyper-V has it open.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
