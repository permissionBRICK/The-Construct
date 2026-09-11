using System.Collections.Concurrent;
using Constructd.Windows.Iso;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Constructd.Tests.Windows;

public sealed class HttpIsoDownloaderTests
{
    [Theory]
    [InlineData("parallel")]
    [InlineData("single")]
    [InlineData("stall")]
    [InlineData("bad-range")]
    [InlineData("cancel")]
    public async Task HostDownloadReportsProgressRecoversAndPublishesOnlyCompleteMedia(string mode)
    {
        var data = Enumerable.Range(0, 2 * 1024 * 1024).Select(i => (byte)i).ToArray();
        var ranges = new ConcurrentQueue<(long Start, long End)>();
        var messages = new List<string>();
        var faulted = 0;
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/ubuntu.iso", async context =>
        {
            long start = 0, end = data.Length - 1;
            var range = context.Request.Headers.Range.ToString();
            if (mode != "single" && range.StartsWith("bytes="))
            {
                var parts = range[6..].Split('-');
                start = long.Parse(parts[0]);
                end = long.Parse(parts[1]);
                context.Response.StatusCode = 206;
                var reportedStart = mode == "bad-range" && end > 0 ? start + 1 : start;
                context.Response.Headers.ContentRange = $"bytes {reportedStart}-{end}/{data.Length}";
            }
            context.Response.Headers.ETag = "\"fixture-v1\"";
            context.Response.ContentLength = end - start + 1;
            var probe = start == 0 && end == 0;
            if (!probe) ranges.Enqueue((start, end));
            var stall = !probe && (mode == "cancel" || mode == "stall" && Interlocked.Exchange(ref faulted, 1) == 0);
            try
            {
                var count = stall ? 65536 : checked((int)(end - start + 1));
                await context.Response.Body.WriteAsync(data.AsMemory((int)start, count), context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);
                if (stall) await Task.Delay(Timeout.Infinite, context.RequestAborted);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        });
        await app.StartAsync();
        var directory = Path.Combine(Path.GetTempPath(), "construct-host-download-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory, "ubuntu.iso");
        var previous = new byte[] { 1, 2, 3 };
        await File.WriteAllBytesAsync(destination, previous);
        using var cancel = new CancellationTokenSource(mode == "cancel" ? TimeSpan.FromSeconds(3) : TimeSpan.FromSeconds(30));
        try
        {
            var download = new HttpIsoDownloader().DownloadAsync(new Uri(app.Urls.Single() + "/ubuntu.iso"),
                destination, new InlineProgress(messages.Add), cancel.Token);
            if (mode == "bad-range")
            {
                var error = await Assert.ThrowsAnyAsync<Exception>(() => download);
                Assert.Contains("incorrect byte range", error.Message);
            }
            else if (mode == "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => download);
            else
                await download;

            Assert.Equal(mode is "bad-range" or "cancel" ? previous : data, await File.ReadAllBytesAsync(destination));
            Assert.Single(Directory.GetFiles(directory));
            Assert.Contains(messages, line => line.Contains("on the host"));
            if (mode is "stall" or "cancel") Assert.Contains(messages, line => line.Contains("MiB/s") && line.Contains("streams"));
            if (mode == "parallel") Assert.Equal(8, ranges.Count);
            if (mode == "stall")
            {
                Assert.Equal(9, ranges.Count);
                Assert.Contains(ranges, range => range.Start % (data.Length / 8) == 65536);
                Assert.Contains(messages, line => line.Contains("1 retries"));
            }
            if (mode == "single") Assert.Contains(ranges, range => range == (0, data.Length - 1));
        }
        finally
        {
            await app.StopAsync();
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class InlineProgress(Action<string> report) : IProgress<string>
    {
        public void Report(string value) => report(value);
    }
}
