using System.IO.Compression;
using System.Text.RegularExpressions;
using Constructd.Core.Domain;
namespace Constructd.Core.Logic;

public static class SourceZipRules
{
    public static bool ValidCommit(string? commit) => commit is not null && Regex.IsMatch(commit, "\\A[0-9a-f]{40}\\z");
    public static bool Pinned(string commit, string pin) => commit.Equals(pin, StringComparison.OrdinalIgnoreCase) ||
        pin.Length >= 7 && pin.Length < commit.Length && pin.All(Uri.IsHexDigit) && commit.StartsWith(pin, StringComparison.OrdinalIgnoreCase);
    public static void Validate(Stream stream)
    {
        try
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read, true);
            if (zip.Entries.Count is 0 or > 20000) throw new SourceException("extraction-refused");
            long total = 0; string? root = null;
            var names = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                var directory = entry.FullName.EndsWith('/');
                var name = directory ? entry.FullName[..^1] : entry.FullName;
                var kind = (entry.ExternalAttributes >> 16) & 0xF000;
                if (!ZipEntryRules.IsSafe(name) || (kind != 0 && kind != (directory ? 0x4000 : 0x8000)) ||
                    (entry.ExternalAttributes & 0x400) != 0 || !names.TryAdd(name, directory) ||
                    (total = checked(total + entry.Length)) > Math.Min(1L << 30, checked(stream.Length * 16)))
                    throw new SourceException("extraction-refused");
                var top = name.Split('/')[0]; root ??= top;
                if (top != root || !Regex.IsMatch(root, "\\A[A-Za-z0-9_.-]+-main\\z") || name == root && !directory)
                    throw new SourceException("extraction-refused");
            }
            foreach (var name in names.Keys)
            {
                var parent = name;
                while (parent.LastIndexOf('/') is var slash && slash >= 0)
                { parent = parent[..slash]; if (names.TryGetValue(parent, out var directory) && !directory) throw new SourceException("extraction-refused"); }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or OverflowException or ArgumentException)
        { throw new SourceException("extraction-refused"); }
    }
}
