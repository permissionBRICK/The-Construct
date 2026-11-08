using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;

namespace Constructd.Api.Backups;

/// <summary>
/// The encrypted payloads under <c>Constructd:Backups:RootDir</c>:
/// <c>&lt;root&gt;/&lt;first 16 hex of sha256(lowercase owner)&gt;/&lt;lowercase vm&gt;/&lt;id&gt;.age</c>.
/// Every segment is generated or validated (hex digest, VM name rule, 32-hex id), and no directory on
/// the way may be a link, so a path can never leave the root.
/// </summary>
public sealed partial class BackupFileStore(string root, IFreeSpaceProbe probe) : IBackupFiles
{
    private const string Final = ".age";
    private const string Part = ".age.partial";

    public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    public long FreeBytes() => probe.FreeBytes(Root);

    public Stream CreatePartial(string owner, string vm, string id)
    {
        var directory = Directory(owner, vm);
        CreateDirectory(Root);
        CreateDirectory(Path.GetDirectoryName(directory)!);
        CreateDirectory(directory);
        var path = Checked(Path.Combine(directory, Name(id, Part)));
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None, BufferSize = 0,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    public void Publish(string owner, string vm, string id)
    {
        var directory = Directory(owner, vm);
        File.Move(Checked(Path.Combine(directory, Name(id, Part))), Checked(Path.Combine(directory, Name(id, Final))), overwrite: false);
    }

    public Stream? OpenRead(string owner, string vm, string id)
    {
        var path = Checked(Path.Combine(Directory(owner, vm), Name(id, Final)));
        return File.Exists(path)
            // FileShare.Delete: deleting or rotating a backup does not wait for a download of it to end.
            ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan)
            : null;
    }

    public void Delete(string owner, string vm, string id)
    {
        var directory = Directory(owner, vm);
        foreach (var suffix in new[] { Part, Final })
        {
            var path = Checked(Path.Combine(directory, Name(id, suffix)));
            if (File.Exists(path)) File.Delete(path);
        }
    }

    public void PruneDirectory(string owner, string vm)
    {
        var directory = Directory(owner, vm);
        foreach (var path in new[] { directory, Path.GetDirectoryName(directory)! })
        {
            Checked(path);
            if (System.IO.Directory.Exists(path) && !System.IO.Directory.EnumerateFileSystemEntries(path).Any())
                System.IO.Directory.Delete(path);
        }
    }

    public IReadOnlyList<BackupFileEntry> List()
    {
        Checked(Root);
        if (!System.IO.Directory.Exists(Root)) return [];
        var entries = new List<BackupFileEntry>();
        foreach (var ownerDir in System.IO.Directory.EnumerateDirectories(Root))
        {
            if (!OwnerPattern().IsMatch(Path.GetFileName(ownerDir)) || IsLink(ownerDir)) continue;
            foreach (var vmDir in System.IO.Directory.EnumerateDirectories(ownerDir))
            {
                if (!VmNameValidator.IsValid(Path.GetFileName(vmDir)) || IsLink(vmDir)) continue;
                foreach (var file in System.IO.Directory.EnumerateFiles(vmDir))
                {
                    var name = Path.GetFileName(file);
                    var partial = name.EndsWith(Part, StringComparison.Ordinal);
                    var id = partial ? name[..^Part.Length] : name.EndsWith(Final, StringComparison.Ordinal) ? name[..^Final.Length] : null;
                    if (id is null || !BackupKeysRules.IdPattern().IsMatch(id) || IsLink(file)) continue;
                    entries.Add(new(id, partial, file, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero)));
                }
            }
        }
        return entries;
    }

    public void DeleteFile(BackupFileEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var full = Checked(Path.GetFullPath(entry.Path));
        if (!full.StartsWith(Root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("backup-path-refused");
        if (File.Exists(full)) File.Delete(full);
    }

    /// <summary>The first 16 hex digits of the SHA-256 of the lowercase owner name.</summary>
    public static string OwnerSegment(string owner) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(owner.ToLowerInvariant())))[..16];

    private static string Name(string id, string suffix) =>
        BackupKeysRules.IdPattern().IsMatch(id) ? id + suffix : throw new IOException("backup-path-refused");

    private string Directory(string owner, string vm)
    {
        var name = vm.ToLowerInvariant();
        if (string.IsNullOrEmpty(owner) || !VmNameValidator.IsValid(name)) throw new IOException("backup-path-refused");
        return Path.Combine(Root, OwnerSegment(owner), name);
    }

    /// <summary>Refuses a path when it, or any directory from the root down to it, is a link.</summary>
    private string Checked(string path)
    {
        for (var current = path; current is not null && current.Length >= Root.Length; current = Path.GetDirectoryName(current))
        {
            if (IsLink(current)) throw new IOException("backup-path-refused");
            if (current.Length == Root.Length) break;
        }
        for (var ancestor = Path.GetDirectoryName(Root); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (IsLink(ancestor)) throw new IOException("backup-path-refused");
        return path;
    }

    private void CreateDirectory(string path)
    {
        Checked(path);
        if (System.IO.Directory.Exists(path)) return;
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(path);
        else System.IO.Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static bool IsLink(string path) =>
        (File.Exists(path) || System.IO.Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    [GeneratedRegex("^[0-9a-f]{16}$", RegexOptions.CultureInvariant)]
    private static partial Regex OwnerPattern();
}

/// <summary>Free bytes of the file system that holds a path (its nearest existing ancestor).</summary>
public sealed class DriveFreeSpaceProbe : IFreeSpaceProbe
{
    public long FreeBytes(string path)
    {
        var existing = Path.GetFullPath(path);
        while (!System.IO.Directory.Exists(existing) && Path.GetDirectoryName(existing) is { } parent) existing = parent;
        return new DriveInfo(existing).AvailableFreeSpace;
    }
}
