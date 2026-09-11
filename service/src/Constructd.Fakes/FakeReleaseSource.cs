using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Fakes;

public sealed class FakeReleaseSource : IReleaseSource
{
    public List<ReleaseDescriptor> Releases { get; } = [];
    public Dictionary<Uri, byte[]> Assets { get; } = [];
    public Dictionary<string, SourceAssetDescriptor> SourceAssets { get; } = [];
    public string? FakeReleaseDir { get; set; }
    public long MaxSourceBytes { get; set; } = 268435456;
    public Func<CancellationToken, Task>? BeforeDownload { get; set; }
    private int downloadCount;
    public int DownloadCount => downloadCount;
    public async Task<SourceAssetDescriptor> GetSourceAssetAsync(string repository, string commit, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!SourceZipRules.ValidCommit(commit)) throw new SourceException("release-source-invalid-metadata");
        if (SourceAssets.TryGetValue(commit, out var supplied)) return supplied;
        if (FakeReleaseDir is null) throw new SourceException("source-unavailable");
        var dir = Path.Combine(FakeReleaseDir, "host-" + commit);
        var manifest = Path.Combine(dir, "manifest.json");
        if (!File.Exists(manifest)) throw new SourceException("source-unavailable");
        if (new FileInfo(manifest).Length > 1024 * 1024) throw new SourceException("release-source-invalid-metadata");
        var asset = SourceManifestRules.Parse(await File.ReadAllBytesAsync(manifest, ct), repository, commit, MaxSourceBytes);
        Assets[asset.Url] = await File.ReadAllBytesAsync(Path.Combine(dir, "construct-source-" + commit + ".zip"), ct);
        return asset;
    }
    public bool FailDownloads { get; set; }
    public Task<IReadOnlyList<ReleaseDescriptor>> ListHostReleasesAsync(string repository, CancellationToken ct, string? releaseTag = null)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<ReleaseDescriptor>>(Releases.ToArray()); }
    public async Task DownloadAsync(ReleaseAsset asset, string destinationPath, IProgress<string>? progress, CancellationToken ct)
    {
        Interlocked.Increment(ref downloadCount);
        if (BeforeDownload is not null) await BeforeDownload(ct);
        if (FailDownloads || !Assets.TryGetValue(asset.Url, out var bytes)) throw new IOException("Fake release download failed.");
        await File.WriteAllBytesAsync(destinationPath, bytes, ct);
    }
}
