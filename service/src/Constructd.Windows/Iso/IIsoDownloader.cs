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
            var size = total > 0
                ? $"{done / 1048576d:N0} / {total / 1048576d:N0} MiB ({100d * done / total:N1}%)"
                : $"{done / 1048576d:N0} MiB";
            var eta = total > done && speed > 0 ? $" | ETA {(total - done) / speed:N0}s" : "";
            progress?.Report($"host ISO {transfer.Phase}: {size} | {speed / 1048576d:N1} MiB/s | " +
                $"{transfer.ActiveStreams} streams | {transfer.Retries} retries{eta}");
            previousBytes = done;
            previousSeconds = elapsed;
        }
        await transfer.Completion.ConfigureAwait(false);
        progress?.Report($"source ISO downloaded on the host: {transfer.Downloaded / 1048576d:N0} MiB in {clock.Elapsed.TotalSeconds:N1}s | {transfer.Retries} retries");
    }
}
