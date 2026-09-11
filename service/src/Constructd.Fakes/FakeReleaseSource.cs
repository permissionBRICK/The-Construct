using Constructd.Core.Abstractions;
namespace Constructd.Fakes;

public sealed class FakeReleaseSource : IReleaseSource
{
    public List<ReleaseDescriptor> Releases { get; } = [];
    public Dictionary<Uri, byte[]> Assets { get; } = [];
    public bool FailDownloads { get; set; }
    public Task<IReadOnlyList<ReleaseDescriptor>> ListHostReleasesAsync(string repository, CancellationToken ct, string? releaseTag = null)
    { ct.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<ReleaseDescriptor>>(Releases.ToArray()); }
    public Task DownloadAsync(ReleaseAsset asset, string destinationPath, IProgress<string>? progress, CancellationToken ct)
    {
        if (FailDownloads || !Assets.TryGetValue(asset.Url, out var bytes)) throw new IOException("Fake release download failed.");
        return File.WriteAllBytesAsync(destinationPath, bytes, ct);
    }
}
