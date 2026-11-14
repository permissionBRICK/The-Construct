using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using Constructd.Core.Logic;

namespace Constructd.Api.Jobs;

/// <summary>The cached source could not be turned into the guest's archive.</summary>
public sealed class ProvisionSourceException(string message) : Exception(message), IConstructdError;

/// <summary>
/// Turns a cached release source ZIP (one top-level <c>&lt;repo&gt;-main/</c> directory) into the
/// <c>tar.gz</c> the PowerShell provisioner uploads, so the guest unpacks it with the PowerShell's own
/// command. The tree is the one <c>bin/fetch-construct-source.sh</c> extracts from the same ZIP: the
/// top directory stripped, the ZIP's file modes (0644 when unset), directories 0755, and
/// <c>.construct-revision</c> holding the commit. Links and other special entries are refused, as there.
/// </summary>
public static class ProvisionSourceArchive
{
    public static async Task WriteTarGzAsync(Stream zip, string commit, string outputPath, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await zip.CopyToAsync(buffer, ct).ConfigureAwait(false);
        buffer.Position = 0;
        using var archive = new ZipArchive(buffer, ZipArchiveMode.Read);

        await using var file = File.Create(outputPath);
        await using var gzip = new GZipStream(file, CompressionLevel.Optimal);
        await using var tar = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        string? root = null;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName;
            var slash = name.IndexOf('/', StringComparison.Ordinal);
            var top = slash < 0 ? name : name[..slash];
            root ??= top;
            if (top != root || name.Contains('\\', StringComparison.Ordinal) || name.Split('/').Any(p => p is "." or ".."))
                throw new ProvisionSourceException("source-invalid: unexpected entry in the source archive");
            var relative = slash < 0 ? "" : name[(slash + 1)..];
            if (relative.Length == 0 || relative.TrimEnd('/') == ".construct-revision") continue;
            var kind = (entry.ExternalAttributes >> 16) & 0xF000;
            var directory = relative.EndsWith('/');
            if (kind != 0 && kind != (directory ? 0x4000 : 0x8000))
                throw new ProvisionSourceException("source-invalid: the source archive holds a link or special file");
            if (directory)
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.Directory, relative.TrimEnd('/'))
                { Mode = (UnixFileMode)0b111_101_101 }, ct).ConfigureAwait(false);
                continue;
            }

            var bits = (entry.ExternalAttributes >> 16) & 0x1FF;
            // The tar header needs the length up front; a ZIP entry stream cannot seek.
            using var data = new MemoryStream();
            await using (var content = entry.Open()) await content.CopyToAsync(data, ct).ConfigureAwait(false);
            data.Position = 0;
            await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, relative)
            { Mode = (UnixFileMode)(bits == 0 ? 0b110_100_100 : bits), DataStream = data }, ct).ConfigureAwait(false);
        }

        if (root is null) throw new ProvisionSourceException("source-invalid: the source archive is empty");
        using var marker = new MemoryStream(Encoding.ASCII.GetBytes(commit + "\n"));
        await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, ".construct-revision")
        { Mode = (UnixFileMode)0b110_100_100, DataStream = marker }, ct).ConfigureAwait(false);
    }
}
