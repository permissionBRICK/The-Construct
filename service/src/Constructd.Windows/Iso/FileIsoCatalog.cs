using System.Globalization;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging;

namespace Constructd.Windows.Iso;

/// <summary>
/// The ISO catalog as plain files in the cache directory (plan §4.10):
///
/// <code>
///   construct-autoinstall-20260902T141530Z.iso        the media
///   construct-autoinstall-20260902T141530Z.iso.json   its sidecar
///   current.pointer                                    the file name of the current one
/// </code>
///
/// Files, not a database, because this has to be readable — and repairable — by an administrator
/// standing at the host with the service stopped, which is exactly when it matters.
///
/// Two hypervisor facts shape it. An ISO attached to a VM is held open by Hyper-V, so media is never
/// overwritten in place: a build writes a new versioned file and the pointer is swapped by writing a
/// temporary file and renaming it over the old one, which is atomic on NTFS. And a delete that fails
/// because of that handle is an expected outcome (<see cref="Prune"/> reports it), not an error.
///
/// It knows nothing about how the ISO was built. Any strategy can publish into it.
/// </summary>
public sealed class FileIsoCatalog : IIsoCatalog
{
    /// <summary>Prefix + wildcard of the versioned media files, in one place.</summary>
    public const string FilePrefix = "construct-autoinstall-";

    public const string FileSuffix = ".iso";

    public const string SidecarSuffix = ".json";

    public const string PointerFileName = "current.pointer";

    /// <summary>How many names one second's worth of concurrent builds may claim before giving up.</summary>
    private const int MaxNameAttempts = 64;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly IIsoFileSystem _files;
    private readonly IClock _clock;
    private readonly ILogger<FileIsoCatalog> _logger;
    private readonly string _cacheDir;

    public FileIsoCatalog(IIsoFileSystem files, IClock clock, string cacheDir, ILogger<FileIsoCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);

        _files = files;
        _clock = clock;
        _logger = logger;
        _cacheDir = ArgumentGuard.WindowsPath(cacheDir, "Constructd:Iso:CacheDir").TrimEnd('\\', '/');
    }

    /// <summary>Where the pointer lives — the admin CLI names it when something is wrong.</summary>
    public string PointerPath => $@"{_cacheDir}\{PointerFileName}";

    public IsoCatalogEntry? GetCurrent()
    {
        var fileName = ReadPointer();
        if (fileName is null)
        {
            return null;
        }

        var path = $@"{_cacheDir}\{fileName}";
        var size = _files.FileLength(path);
        if (!_files.FileExists(path) || size <= 0)
        {
            // The pointer outlived its ISO: somebody deleted the file, or a disk filled up mid-build.
            _logger.LogWarning("The ISO pointer names {File}, which is missing or empty.", fileName);
            return null;
        }

        return new IsoCatalogEntry(path, fileName, IsCurrent: true, size, ReadSidecar(path));
    }

    public IReadOnlyList<IsoCatalogEntry> List()
    {
        var current = ReadPointer();

        return _files.ListFiles(_cacheDir, $"{FilePrefix}*{FileSuffix}")
            .Select(path =>
            {
                var fileName = FileNameOf(path);
                return new IsoCatalogEntry(
                    path,
                    fileName,
                    string.Equals(fileName, current, StringComparison.OrdinalIgnoreCase),
                    _files.FileLength(path),
                    ReadSidecar(path));
            })
            // Newest first, and the name sorts by time because the stamp is fixed-width UTC.
            .OrderByDescending(entry => entry.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// A fresh versioned path, RESERVED before it is handed out: the empty file is created with
    /// CREATE_NEW semantics, so the name belongs to this caller and nothing else can take it.
    ///
    /// The reservation is the point. A timestamp alone is not a unique name — two administrators (two
    /// processes; the builder's in-process lock does not reach across them) starting a build in the
    /// same second, or a clock that went backwards, would otherwise both be handed the same path and
    /// both xorriso runs would write the same file. That is exactly the "never overwrite media in
    /// place" rule this catalog exists to keep, and a VM installing from that file would be reading
    /// the bytes of another build.
    ///
    /// A build that then fails leaves an empty file behind; it is never current (nothing points at it)
    /// and <see cref="Prune"/> removes it.
    /// </summary>
    public string NextMediaPath()
    {
        _files.CreateDirectory(_cacheDir);

        var stamp = _clock.UtcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        for (var attempt = 1; attempt <= MaxNameAttempts; attempt++)
        {
            // The plain stamp first, so the usual case reads as the documented name; a collision only
            // then gets a discriminator, which keeps both the prefix and the sort order intact.
            var suffix = attempt == 1 ? string.Empty : $"-{attempt}";
            var candidate = $@"{_cacheDir}\{FilePrefix}{stamp}{suffix}{FileSuffix}";

            if (_files.TryCreateNewFile(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            $"Could not reserve a name for the new autoinstall ISO in {_cacheDir}: " +
            $"{MaxNameAttempts} candidates for {stamp} are already taken.");
    }

    public IsoCatalogEntry Publish(string isoPath, IsoSidecar sidecar)
    {
        ArgumentNullException.ThrowIfNull(sidecar);

        var path = ArgumentGuard.WindowsPath(isoPath, "iso path");
        var fileName = FileNameOf(path);

        var size = _files.FileLength(path);
        if (!_files.FileExists(path) || size <= 0)
        {
            throw new InvalidOperationException(
                $"The build reported success but {path} is missing or empty; the catalog was not changed.");
        }

        // Sidecar first: a pointer that names an ISO with no sidecar would be published state nobody
        // can describe, and the consuming builder refuses it.
        _files.WriteAllText(SidecarPathFor(path), JsonSerializer.Serialize(sidecar, Json));

        // The swap itself. Write beside the pointer, then rename over it: a reader either sees the old
        // name or the new one, never a half-written file — which matters because a VM may be
        // installing from the old ISO while this runs.
        var temporary = $"{PointerPath}.tmp";
        _files.WriteAllText(temporary, fileName + Environment.NewLine);
        _files.MoveFile(temporary, PointerPath, overwrite: true);

        _logger.LogInformation("Published autoinstall media {File} ({Bytes} bytes).", fileName, size);

        return new IsoCatalogEntry(path, fileName, IsCurrent: true, size, sidecar);
    }

    public IsoPruneResult Prune()
    {
        var removed = new List<string>();
        var skipped = new List<IsoPruneSkip>();

        foreach (var entry in List())
        {
            if (entry.IsCurrent)
            {
                skipped.Add(new IsoPruneSkip(entry.FileName, "it is the current media"));
                continue;
            }

            if (!_files.TryDeleteFile(entry.Path))
            {
                // Hyper-V holds the handle: a VM still has this ISO attached (an install in flight, or
                // a VM whose media was never detached). Leaving it is the only correct answer.
                skipped.Add(new IsoPruneSkip(entry.FileName, "it is in use — a VM still has it attached"));
                continue;
            }

            _files.TryDeleteFile(SidecarPathFor(entry.Path));
            removed.Add(entry.FileName);
        }

        return new IsoPruneResult(removed, skipped);
    }

    private static string SidecarPathFor(string isoPath) => isoPath + SidecarSuffix;

    private static string FileNameOf(string path)
    {
        var cut = path.LastIndexOfAny(['\\', '/']);
        return cut < 0 ? path : path[(cut + 1)..];
    }

    /// <summary>
    /// The file name in the pointer, or null when there is none. Anything that is not a plain
    /// catalog file name is refused rather than followed: the pointer decides which file the service
    /// hands to Hyper-V, so a path with a directory in it would be a way out of the cache directory.
    /// </summary>
    private string? ReadPointer()
    {
        if (!_files.FileExists(PointerPath))
        {
            return null;
        }

        string content;
        try
        {
            content = _files.ReadAllText(PointerPath);
        }
        catch (IOException)
        {
            _logger.LogWarning("The ISO pointer could not be read.");
            return null;
        }

        var fileName = content.Trim();
        if (fileName.Length == 0)
        {
            return null;
        }

        var wellFormed =
            fileName.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) &&
            fileName.EndsWith(FileSuffix, StringComparison.OrdinalIgnoreCase) &&
            fileName.IndexOfAny(['\\', '/', ':']) < 0 &&
            !fileName.Contains("..", StringComparison.Ordinal);

        if (!wellFormed)
        {
            _logger.LogWarning("The ISO pointer does not name a catalog file; ignoring it.");
            return null;
        }

        return fileName;
    }

    private IsoSidecar? ReadSidecar(string isoPath)
    {
        var path = SidecarPathFor(isoPath);
        if (!_files.FileExists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IsoSidecar>(_files.ReadAllText(path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            _logger.LogWarning("The sidecar of {File} could not be read.", FileNameOf(isoPath));
            return null;
        }
    }
}
