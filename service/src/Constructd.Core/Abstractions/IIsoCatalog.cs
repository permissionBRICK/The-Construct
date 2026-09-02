namespace Constructd.Core.Abstractions;

/// <summary>
/// The install media on this host: versioned files, one "current" pointer, a sidecar describing each
/// one, and pruning. Shared by every build strategy — a builder produces a file, the
/// catalog is what makes it the media new VMs boot from.
///
/// Two rules come from the hypervisor rather than from taste:
/// <list type="bullet">
/// <item>An ISO attached to a VM is <b>held open</b> by Hyper-V. Media is therefore never overwritten
/// in place: every build writes a new versioned file and the pointer is swapped atomically, so a VM
/// installing right now keeps the file it is reading.</item>
/// <item>For the same reason <see cref="Prune"/> skips what it cannot delete instead of failing.</item>
/// </list>
/// </summary>
public interface IIsoCatalog
{
    /// <summary>
    /// The media new VMs should boot from, or null when nothing is published (or the pointer names a
    /// file that is gone). A sidecar that cannot be read leaves
    /// <see cref="IsoCatalogEntry.Sidecar"/> null — the caller decides whether that is fatal.
    /// </summary>
    IsoCatalogEntry? GetCurrent();

    /// <summary>Everything in the catalog, newest first, current one included.</summary>
    IReadOnlyList<IsoCatalogEntry> List();

    /// <summary>A fresh versioned path to build into. Nothing is created; the builder writes it.</summary>
    string NextMediaPath();

    /// <summary>
    /// Record the sidecar for a freshly built ISO and make it current, in that order, with the pointer
    /// swap atomic.
    /// </summary>
    IsoCatalogEntry Publish(string isoPath, IsoSidecar sidecar);

    /// <summary>Delete every entry that is not the current one, skipping any the hypervisor holds open.</summary>
    IsoPruneResult Prune();
}

/// <summary>One built ISO.</summary>
/// <param name="Path">Full path in the host's namespace — what a VM gets attached.</param>
/// <param name="FileName">Its file name, which is what the pointer stores.</param>
/// <param name="IsCurrent">Whether the pointer names it.</param>
/// <param name="SizeBytes">Size on disk; 0 means the file is gone or empty.</param>
/// <param name="Sidecar">What it was built from, or null when the sidecar is missing or unreadable.</param>
public sealed record IsoCatalogEntry(
    string Path,
    string FileName,
    bool IsCurrent,
    long SizeBytes,
    IsoSidecar? Sidecar);

/// <summary>
/// The JSON written next to each ISO. Deliberately plain data: it is read by the admin CLI, printed
/// for a human, and it is the only record of what a given ISO contains.
/// </summary>
public sealed record IsoSidecar(
    DateTimeOffset BuiltAt,
    string SourceIso,
    string SourceSha256,
    string SeedUser,
    string BootstrapKeyFingerprint,
    string HostnameSource,
    string ScriptSha256);

/// <summary>What a prune did, per file, so the CLI can say why something is still there.</summary>
/// <param name="Removed">File names deleted.</param>
/// <param name="Skipped">File names left behind, with the reason (in use, current).</param>
public sealed record IsoPruneResult(
    IReadOnlyList<string> Removed,
    IReadOnlyList<IsoPruneSkip> Skipped);

/// <summary>One file a prune left alone.</summary>
public sealed record IsoPruneSkip(string FileName, string Reason);
