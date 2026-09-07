using System.Net;
using System.Security.Cryptography;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Windows.Media;

public sealed class HttpMediaTransfer(IMediaFiles files, IMediaDnsResolver dns, IUrlAdmissionPolicy policy,
    IMediaConnectionFactory connections, Func<Uri, IReadOnlyList<IPAddress>, HttpMessageHandler>? handlerFactory = null) : IMediaTransfer
{
    public async Task<TransferResult> AcquireAsync(MediaItem item, Uri source, long maxBytes, TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
    {
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct); overall.CancelAfter(timeout);
        var token = overall.Token; var url = source; var part = files.PathFor(item.Id, true);
        try
        {
            for (var hop = 0; hop <= 5; hop++)
            {
                // Reject credentials/scheme before DNS. HTTP permission is checked by the accepting route;
                // every hop independently requires a checksum and forbids a TLS downgrade.
                if (!url.IsAbsoluteUri || url.UserInfo.Length != 0 || url.Scheme is not "https" and not "http" || url.Scheme == "http" && item.ExpectedSha256 is null)
                    throw new MediaException("url-refused");
                var addresses = await dns.ResolveAsync(url.IdnHost, token);
                if (!policy.Check(url, addresses, true, item.ExpectedSha256 is not null).Allowed) throw new MediaException("url-refused");
                progress?.Report("downloading from " + url.IdnHost);
                using var handler = handlerFactory?.Invoke(url, addresses) ?? PinnedAddressHandler.Create(url, addresses, policy, connections);
                using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
                {
                    if (hop == 5 || response.Headers.Location is not { } location) throw new MediaException("url-refused");
                    var next = new Uri(url, location);
                    if (url.Scheme == "https" && next.Scheme == "http") throw new MediaException("url-refused");
                    url = next; continue;
                }
                if (!response.IsSuccessStatusCode) throw new MediaException("media-transfer-failed");
                var length = response.Content.Headers.ContentLength;
                if (length > maxBytes) throw new MediaException("media-too-large");
                await files.CreateAsync(part, 0, token);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var input = await response.Content.ReadAsStreamAsync(token);
                var buffer = new byte[65536]; long total = 0, reported = 0; var last = DateTimeOffset.UtcNow;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(120));
                    var count = await input.ReadAsync(buffer, idle.Token); if (count == 0) break;
                    if (count > maxBytes - total) throw new MediaException("media-too-large");
                    hash.AppendData(buffer, 0, count); await files.WriteAsync(part, total, buffer.AsMemory(0, count), token); total += count;
                    if (total - reported >= 64L << 20 || DateTimeOffset.UtcNow - last >= TimeSpan.FromSeconds(5))
                    { progress?.Report($"downloaded {total} bytes" + (length is null ? "" : $" of {length} bytes")); reported = total; last = DateTimeOffset.UtcNow; }
                }
                if (length is not null && total != length) throw new MediaException("media-transfer-failed");
                var sha = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (item.ExpectedSha256 is not null && !string.Equals(sha, item.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw new MediaException("checksum-mismatch");
                if (item.ExpectedSha256 is null && !await LooksLikeIsoAsync(part, token)) throw new MediaException("not-an-iso");
                await files.PublishAsync(part, item.Path, token);
                return new(total, sha, new UriBuilder(url) { Query = "", Fragment = "" }.Uri);
            }
            throw new MediaException("url-refused");
        }
        catch (Exception ex)
        {
            // Cleanup/accounting belongs to MediaJobs, which records failed deletion for retry.
            throw new MediaException(ex is MediaException safe ? safe.Code : ct.IsCancellationRequested ? "cancelled" : overall.IsCancellationRequested ? "media-timeout" : "media-transfer-failed");
        }
    }
    public async Task WriteChunkAsync(MediaUpload upload, int index, Stream body, long contentLength, CancellationToken ct)
    {
        var offset = (long)index * upload.ChunkBytes;
        if (index < 0 || offset >= upload.SizeBytes || contentLength != Math.Min(upload.ChunkBytes, upload.SizeBytes - offset)) throw new MediaException("chunk-size");
        var buffer = new byte[checked((int)contentLength)];
        try { await body.ReadExactlyAsync(buffer, ct); if (await body.ReadAsync(new byte[1], ct) != 0) throw new MediaException("chunk-size"); }
        catch (EndOfStreamException) { throw new MediaException("chunk-size"); }
        await files.WriteAsync(files.PathFor(upload.MediaId, true), offset, buffer, ct);
    }
    public async Task<string> HashAsync(string path, IProgress<string>? progress, CancellationToken ct)
    { await using var stream = await files.OpenReadAsync(path, ct); return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)); }
    public async Task<bool> LooksLikeIsoAsync(string path, CancellationToken ct)
    {
        await using var stream = await files.OpenReadAsync(path, ct); if (stream.Length < 32775) return false;
        stream.Position = 32768; var buffer = new byte[7]; await stream.ReadExactlyAsync(buffer, ct); return IsoSignature.IsPrimaryDescriptor(buffer);
    }
    public Task<bool> TryDeleteAsync(string path, CancellationToken ct) => files.DeleteAsync(path, ct);
    public async Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct) => (await files.ListAsync(ct)).Select(f => f.Path).ToArray();
}
