using Constructd.Core.Services;

namespace Constructd.Windows.Iso;

/// <summary>
/// Fetches the source Ubuntu ISO the autoinstall image is remastered from. One admin-configured URL,
/// downloaded once into the cache — the service never goes looking for "the current LTS" on its own,
/// so a host's guests cannot change release because a mirror did.
/// </summary>
public interface IIsoDownloader
{
    Task DownloadAsync(Uri source, string destinationPath, IProgress<string>? progress, CancellationToken cancellationToken);
}

/// <summary>The same bounded, resumable streaming engine used by the local installer.</summary>
public sealed class HttpIsoDownloader : IIsoDownloader
{
    public async Task DownloadAsync(
        Uri source,
        string destinationPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report($"downloading the source ISO on the host from {source.Host} (up to 8 streams; 5s idle timeout)");
        using var transfer = new Construct.Download.Transfer(source.AbsoluteUri, destinationPath, 8, 5, 6, "");
        using var registration = cancellationToken.Register(transfer.Cancel);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        long previousBytes = 0;
        double previousSeconds = 0;
        while (!transfer.Completion.IsCompleted)
        {
            var tick = timer.WaitForNextTickAsync(cancellationToken).AsTask();
            if (await Task.WhenAny(transfer.Completion, tick).ConfigureAwait(false) == transfer.Completion) break;
            await tick.ConfigureAwait(false);
            var done = transfer.Downloaded;
            var total = transfer.Total;
            var elapsed = clock.Elapsed.TotalSeconds;
            var speed = Math.Max(0, (done - previousBytes) / Math.Max(0.001, elapsed - previousSeconds));
            if (total > 0) JobProgressTracker.Current?.Fraction((double)done / total);
            progress?.Report(ProgressLine(transfer.Phase, done, total, speed, transfer.ActiveStreams, transfer.Retries));
            previousBytes = done;
            previousSeconds = elapsed;
        }
        await transfer.Completion.ConfigureAwait(false);
        progress?.Report(Invariant($"source ISO downloaded on the host: {Size(transfer.Downloaded)} in {clock.Elapsed.TotalSeconds:0.0}s | {transfer.Retries} retries"));
    }

    /// <summary>
    /// One download progress line, the same on every host whatever its language: invariant numbers, sizes above 1024 MiB
    /// in GiB ("host ISO download: 2.1 / 3.8 GiB (55.7%) | 11.2 MiB/s | ...").
    /// </summary>
    public static string ProgressLine(string phase, long done, long total, double bytesPerSecond, int streams, int retries)
    {
        var size = total > 0 ? Invariant($"{Sizes(done, total)} ({100d * done / total:0.0}%)") : Size(done);
        var eta = total > done && bytesPerSecond > 0 ? Invariant($" | ETA {(total - done) / bytesPerSecond:0}s") : "";
        return Invariant($"host ISO {phase}: {size} | {bytesPerSecond / Mib:0.0} MiB/s | {streams} streams | {retries} retries{eta}");
    }

    private const double Mib = 1024 * 1024;
    private const double Gib = Mib * 1024;

    /// <summary>A size: MiB up to 1024 MiB, GiB with one decimal above.</summary>
    public static string Size(long bytes) => bytes > Gib ? Invariant($"{bytes / Gib:0.0} GiB") : Invariant($"{bytes / Mib:0} MiB");

    /// <summary>"done / total" in the unit of the total.</summary>
    private static string Sizes(long done, long total) => total > Gib
        ? Invariant($"{done / Gib:0.0} / {total / Gib:0.0} GiB")
        : Invariant($"{done / Mib:0} / {total / Mib:0} MiB");

    private static string Invariant(FormattableString text) => FormattableString.Invariant(text);
}
