using Constructd.Core.Services;

namespace Constructd.Windows.Iso;

/// <summary>
/// Fetches the source Ubuntu ISO the autoinstall image is remastered from: the admin-configured URL, or, on an engine
/// with none configured, the current image of the 24.04 line named by its release directory's SHA256SUMS
/// (<see cref="UbuntuIsoIndex"/>). Downloaded once into the cache.
/// </summary>
public interface IIsoDownloader
{
    Task DownloadAsync(Uri source, string destinationPath, IProgress<string>? progress, CancellationToken cancellationToken);

    /// <summary>A small text document (a release directory's SHA256SUMS).</summary>
    Task<string> ReadTextAsync(Uri source, CancellationToken cancellationToken);
}

/// <summary>
/// The Ubuntu Server line the engine installs when no source ISO is configured, and how its current image is found:
/// the release directory's SHA256SUMS names the live-server image of the newest point release and its SHA-256, as
/// the old client's Auto-Install.ps1 looked it up. The line stays 24.04; only its point release moves.
/// </summary>
public static partial class UbuntuIsoIndex
{
    public const string Line = "24.04";

    /// <summary>The release directory.</summary>
    public static readonly Uri Directory = new($"https://releases.ubuntu.com/{Line}/");

    public static readonly Uri Sums = new(Directory, "SHA256SUMS");

    /// <summary>The file name pattern of the line's live-server images in the cache.</summary>
    public const string CachePattern = "ubuntu-24.04*-live-server-amd64.iso";

    /// <summary>The newest live-server amd64 image of the line in a SHA256SUMS text, with its SHA-256; null when it names none.</summary>
    public static (string FileName, string Sha256)? Newest(string sums) =>
        sums.Split('\n').Select(l => SumLine().Match(l.Trim())).Where(m => m.Success)
            .Select(m => (FileName: m.Groups["name"].Value, Sha256: m.Groups["sha"].Value.ToLowerInvariant()))
            .OrderBy(e => PointRelease(e.FileName)).Select(e => ((string, string)?)e).LastOrDefault();

    /// <summary>The point release of an image of the line (0 for the first release), or -1 for another file.</summary>
    public static int PointRelease(string fileName) =>
        ImageName().Match(fileName) is { Success: true } m ? (m.Groups["point"].Success ? int.Parse(m.Groups["point"].Value, System.Globalization.CultureInfo.InvariantCulture) : 0) : -1;

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<sha>[0-9a-fA-F]{64})\s+\*?(?<name>ubuntu-24\.04(\.[0-9]{1,3})?-live-server-amd64\.iso)$")]
    private static partial System.Text.RegularExpressions.Regex SumLine();

    [System.Text.RegularExpressions.GeneratedRegex(@"^ubuntu-24\.04(\.(?<point>[0-9]{1,3}))?-live-server-amd64\.iso$")]
    private static partial System.Text.RegularExpressions.Regex ImageName();
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

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public async Task<string> ReadTextAsync(Uri source, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(source, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > 1 << 20) throw new HttpRequestException("The document is too large.");
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
}
