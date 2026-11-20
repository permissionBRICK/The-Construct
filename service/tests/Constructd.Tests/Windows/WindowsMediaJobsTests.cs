using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Media;

namespace Constructd.Tests.Windows;

public sealed class WindowsMediaJobsTests
{
    /// <summary>Records what it was asked to fetch and writes a stand-in file; checks nothing.</summary>
    private sealed class RecordingTransfer(IMediaTransfer inner) : IMediaTransfer
    {
        public List<(Uri Source, string? ExpectedSha256)> Calls { get; } = [];
        public async Task<TransferResult> AcquireAsync(MediaItem item, Uri source, long maxBytes, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
        {
            Calls.Add((source, item.ExpectedSha256));
            await File.WriteAllBytesAsync(item.Path, [1, 2, 3], ct);
            return new(3, new string('0', 64), source);
        }
        public Task WriteChunkAsync(MediaUpload upload, int index, Stream body, long contentLength, CancellationToken ct) => inner.WriteChunkAsync(upload, index, body, contentLength, ct);
        public Task<string> HashAsync(string path, IProgress<string>? progress, CancellationToken ct) => inner.HashAsync(path, progress, ct);
        public Task<bool> LooksLikeIsoAsync(string path, CancellationToken ct) => inner.LooksLikeIsoAsync(path, ct);
        public Task<bool> TryDeleteAsync(string path, CancellationToken ct) => inner.TryDeleteAsync(path, ct);
        public Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct) => inner.ListFilesAsync(ct);
    }

    [Fact]
    public async Task Guest_agent_media_comes_from_a_versioned_https_link_with_its_checksum()
    {
        await using var app = new TestApp();
        app.Service<InMemoryCapacityLedger>().Mode = CapacityMode.Observe;
        var transfer = new RecordingTransfer(app.Service<IMediaTransfer>());
        // Fake mode refuses guest-agent media before any transfer, so build the jobs for a real host.
        var jobs = new WindowsMediaJobs(app.Service<IMediaStore>(), app.Service<IMediaFiles>(), transfer, app.Service<IMediaGate>(),
            app.Clock, app.Service<WindowsMediaResolver>(), app.Service<ICapacityLedger>(), app.Service<MediaJobs>(), new ConstructdOptions());

        var item = await jobs.GuestAgentAsync(null, default);

        Assert.Equal(MediaState.Ready, item.State);
        var (source, expected) = Assert.Single(transfer.Calls);
        Assert.Equal(Uri.UriSchemeHttps, source.Scheme);
        // The unversioned alias redirects to http://, which the transfer refuses.
        Assert.DoesNotContain("/stable-virtio/", source.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal(WindowsMediaJobs.GuestAgentUrl, source.AbsoluteUri);
        Assert.Equal(WindowsMediaJobs.GuestAgentSha256, expected);
        Assert.Matches("^[0-9a-f]{64}$", expected);
        Assert.Equal(WindowsMediaJobs.GuestAgentUrl, item.SourceUrl);
    }
}
