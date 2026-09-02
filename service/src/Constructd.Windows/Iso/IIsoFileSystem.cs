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
}
