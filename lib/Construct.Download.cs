// Compatible with Windows PowerShell 5.1 / .NET Framework and PowerShell 7.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Cryptography;

namespace Construct.Download
{
    public sealed class Transfer : IDisposable
    {
        private readonly string url, destination, expectedHash;
        private readonly int streams, idleMs, retries;
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();
        private long downloaded, total = -1;
        private int retryCount, active;
        private volatile string phase = "Connecting";
        private string validator;
        private bool ranged;
        public Task Completion { get; private set; }
        public long Downloaded { get { return Interlocked.Read(ref downloaded); } }
        public long Total { get { return Interlocked.Read(ref total); } }
        public int Retries { get { return Volatile.Read(ref retryCount); } }
        public int ActiveStreams { get { return Volatile.Read(ref active); } }
        public string Phase { get { return phase; } }

        public Transfer(string url, string destination, int streams, int idleSeconds, int retries, string expectedHash)
        {
            this.url = url; this.destination = Path.GetFullPath(destination);
            this.streams = streams; this.idleMs = checked(idleSeconds * 1000);
            this.retries = retries; this.expectedHash = expectedHash;
            if (streams < 1 || streams > 32 || idleSeconds < 1 || retries < 0) throw new ArgumentOutOfRangeException();
            var uri = new Uri(url);
            if (uri.Scheme != "http" && uri.Scheme != "https") throw new ArgumentException("Expected an HTTP(S) URL.");
            Completion = Task.Run(async delegate { await RunAsync().ConfigureAwait(false); });
        }

        private sealed class InvalidDownload : Exception
        { public InvalidDownload(string message) : base(message) { } }

        private HttpRequestMessage Request(long? start, long? end)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd("Construct-Downloader/1.0");
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (start.HasValue) request.Headers.Range = new RangeHeaderValue(start, end);
            if (start.HasValue && validator != null) request.Headers.TryAddWithoutValidation("If-Range", validator);
            return request;
        }

        private async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpRequestMessage request)
        {
            // Headers have a separate, bounded deadline; slow connection setup is not a stalled body.
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token))
            {
                timeout.CancelAfter(Math.Max(15000, idleMs));
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    int code = (int)response.StatusCode;
                    response.Dispose();
                    if (code == 408 || code == 429 || code >= 500) throw new IOException("HTTP " + code);
                    throw new InvalidDownload("Download refused: HTTP " + code);
                }
                if (response.Content.Headers.ContentEncoding.Count != 0)
                { response.Dispose(); throw new InvalidDownload("Unexpected encoded response; byte offsets would be unsafe."); }
                return response;
            }
        }

        private static string Validator(HttpResponseMessage response)
        {
            if (response.Headers.ETag != null && !response.Headers.ETag.IsWeak) return response.Headers.ETag.ToString();
            if (response.Content.Headers.LastModified.HasValue) return response.Content.Headers.LastModified.Value.ToString("R");
            return null;
        }

        private async Task ProbeAsync(HttpClient client)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    using (var request = Request(0, 0))
                    using (var response = await SendAsync(client, request).ConfigureAwait(false))
                    {
                        if (response.StatusCode == HttpStatusCode.PartialContent)
                        {
                            var range = response.Content.Headers.ContentRange;
                            if (range == null || range.Unit != "bytes" || range.From != 0 || range.To != 0 || !range.Length.HasValue || range.Length <= 0)
                                throw new InvalidDownload("Invalid range probe response.");
                            total = range.Length.Value;
                            validator = Validator(response);
                            // A validator prevents combining segments from different versions of the file.
                            ranged = validator != null;
                        }
                        else if (response.StatusCode == HttpStatusCode.OK) total = response.Content.Headers.ContentLength ?? -1;
                        else throw new InvalidDownload("Unexpected download probe status.");
                        return;
                    }
                }
                catch (InvalidDownload) { throw; }
                catch (Exception e)
                {
                    cancel.Token.ThrowIfCancellationRequested();
                    if (!(e is IOException || e is HttpRequestException || e is OperationCanceledException) || attempt >= retries) throw;
                    Interlocked.Increment(ref retryCount);
                }
                await Task.Delay(1000, cancel.Token).ConfigureAwait(false);
            }
        }

        private async Task<int> ReadAsync(Stream input, byte[] buffer, int count, HttpResponseMessage response)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel.Token))
            {
                var read = input.ReadAsync(buffer, 0, count, timeout.Token);
                var delay = Task.Delay(idleMs, timeout.Token);
                if (await Task.WhenAny(read, delay).ConfigureAwait(false) != read)
                {
                    timeout.Cancel();
                    response.Dispose(); // .NET Framework streams do not always honor ReadAsync cancellation.
                    try { await read.ConfigureAwait(false); } catch { }
                    cancel.Token.ThrowIfCancellationRequested();
                    throw new IOException("Stream stalled for " + idleMs / 1000 + " seconds.");
                }
                timeout.Cancel();
                return await read.ConfigureAwait(false);
            }
        }

        private async Task SegmentAsync(HttpClient client, string part, long start, long end)
        {
            long offset = start;
            Interlocked.Increment(ref active);
            try
            {
                using (var output = new FileStream(part, FileMode.Open, FileAccess.Write, FileShare.ReadWrite, 131072, true))
                {
                    var buffer = new byte[131072];
                    for (int attempt = 0; ; attempt++)
                    {
                        cancel.Token.ThrowIfCancellationRequested();
                        try
                        {
                            if (!ranged)
                            {
                                // An origin without ranges cannot resume: restart its single stream honestly.
                                Interlocked.Add(ref downloaded, -(offset - start));
                                offset = start; output.SetLength(0);
                            }
                            output.Position = offset;
                            using (var request = Request(ranged ? (long?)offset : null, ranged ? (long?)end : null))
                            using (var response = await SendAsync(client, request).ConfigureAwait(false))
                            {
                                if (ranged)
                                {
                                    var range = response.Content.Headers.ContentRange;
                                    if (response.StatusCode != HttpStatusCode.PartialContent || range == null || range.Unit != "bytes" ||
                                        range.From != offset || range.To != end || range.Length != Total || Validator(response) != validator ||
                                        (response.Content.Headers.ContentLength.HasValue && response.Content.Headers.ContentLength != end - offset + 1))
                                        throw new InvalidDownload("Server changed the file or returned an incorrect byte range.");
                                }
                                else
                                {
                                    if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDownload("Expected a complete download response.");
                                    total = response.Content.Headers.ContentLength ?? -1;
                                    end = Total > 0 ? Total - 1 : long.MaxValue;
                                }
                                using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                {
                                    while (offset <= end)
                                    {
                                        int count = end == long.MaxValue ? buffer.Length : (int)Math.Min(buffer.Length, end - offset + 1);
                                        int n = await ReadAsync(input, buffer, count, response).ConfigureAwait(false);
                                        if (n == 0)
                                        {
                                            if (Total >= 0) throw new IOException("Stream ended before all bytes arrived.");
                                            break;
                                        }
                                        await output.WriteAsync(buffer, 0, n, cancel.Token).ConfigureAwait(false);
                                        offset += n; Interlocked.Add(ref downloaded, n);
                                    }
                                }
                            }
                            await output.FlushAsync(cancel.Token).ConfigureAwait(false);
                            return;
                        }
                        catch (InvalidDownload) { throw; }
                        catch (Exception e)
                        {
                            cancel.Token.ThrowIfCancellationRequested();
                            if (!(e is IOException || e is HttpRequestException || e is OperationCanceledException) || attempt >= retries) throw;
                            Interlocked.Increment(ref retryCount);
                        }
                        await Task.Delay(1000, cancel.Token).ConfigureAwait(false);
                    }
                }
            }
            catch { cancel.Cancel(); throw; }
            finally { Interlocked.Decrement(ref active); }
        }

        private async Task RunAsync()
        {
            string part = destination + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                using (var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = streams })
                using (var client = new HttpClient(handler))
                {
                    client.Timeout = Timeout.InfiniteTimeSpan;
                    // .NET Framework defaults to two connections per origin.
                    ServicePointManager.FindServicePoint(new Uri(url)).ConnectionLimit = Math.Max(8, streams);
                    await ProbeAsync(client).ConfigureAwait(false);
                    int count = ranged ? (int)Math.Min(streams, Total) : 1;
                    phase = ranged ? "Downloading" : "Downloading (single stream: server lacks safe ranges)";
                    using (var output = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite))
                        if (ranged) output.SetLength(Total);
                    var workers = new Task[count];
                    for (int i = 0; i < count; i++)
                    {
                        long start = ranged ? Total * i / count : 0;
                        long end = ranged ? Total * (i + 1) / count - 1 : long.MaxValue;
                        workers[i] = SegmentAsync(client, part, start, end);
                    }
                    await Task.WhenAll(workers).ConfigureAwait(false);
                }
                cancel.Token.ThrowIfCancellationRequested();
                if (Downloaded == 0 || (Total >= 0 && Downloaded != Total)) throw new InvalidDownload("Download size mismatch.");
                if (!String.IsNullOrEmpty(expectedHash))
                {
                    phase = "Verifying SHA256";
                    using (var sha = SHA256.Create())
                    using (var input = File.OpenRead(part))
                    {
                        string actual = BitConverter.ToString(sha.ComputeHash(input)).Replace("-", "");
                        if (!String.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDownload("SHA256 mismatch.");
                    }
                }
                cancel.Token.ThrowIfCancellationRequested();
                if (File.Exists(destination)) File.Replace(part, destination, null);
                else File.Move(part, destination);
                phase = "Complete";
            }
            finally { if (File.Exists(part)) File.Delete(part); }
        }

        public void Cancel() { cancel.Cancel(); }
        public void Dispose()
        {
            cancel.Cancel();
            try { Completion.GetAwaiter().GetResult(); } catch { }
            cancel.Dispose();
        }
    }
}
