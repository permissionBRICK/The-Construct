using System.Net;
using Constructd.Core.Configuration;
using Constructd.Tests.Support;
using Constructd.Windows.Forwards;
using Constructd.Windows.Iso;
using Microsoft.Extensions.Logging;

namespace Constructd.Tests.Windows;

/// <summary>
/// An in-memory stand-in for the ISO build's file-system access. The tests run on Linux, where
/// <c>C:\ProgramData\…</c> is not a path — faking these few calls is what lets the exact
/// <c>wsl.exe</c> argument vector, WSL path mapping included, be asserted here.
/// </summary>
public sealed class FakeIsoFileSystem : IIsoFileSystem
{
    public Dictionary<string, string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> Deleted { get; } = [];

    /// <summary>Everything ever written, kept even after the file is deleted again.</summary>
    public Dictionary<string, string> Written { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Files whose content is not text (a real ISO); only their size matters.</summary>
    public Dictionary<string, long> Sizes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> Hashes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeIsoFileSystem WithFile(string path, string content = "")
    {
        Files[path] = content;
        return this;
    }

    /// <summary>A binary file of the given size — an ISO, as far as the builder is concerned.</summary>
    public FakeIsoFileSystem WithBinary(string path, long size = 4096, string? sha256 = null)
    {
        Sizes[path] = size;
        if (sha256 is not null)
        {
            Hashes[path] = sha256;
        }

        return this;
    }

    public void CreateDirectory(string path) => Directories.Add(path);

    public bool FileExists(string path) => Files.ContainsKey(path) || Sizes.ContainsKey(path);

    public long FileLength(string path) =>
        Sizes.TryGetValue(path, out var size) ? size :
        Files.TryGetValue(path, out var content) ? content.Length : 0;

    public string ReadAllText(string path) =>
        Files.TryGetValue(path, out var content) ? content : throw new FileNotFoundException(path);

    public void WriteAllText(string path, string content)
    {
        Files[path] = content;
        Written[path] = content;
    }

    public void DeleteFile(string path)
    {
        Deleted.Add(path);
        Files.Remove(path);
        Sizes.Remove(path);
    }

    public string ComputeSha256(string path) => Hashes.TryGetValue(path, out var hash) ? hash : "0";
}

/// <summary>Records what would have been downloaded and "writes" it into the fake file system.</summary>
public sealed class FakeIsoDownloader(FakeIsoFileSystem files) : IIsoDownloader
{
    public List<(Uri Source, string Destination)> Downloads { get; } = [];

    /// <summary>Set to make the download fail.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Size the downloaded file gets; 0 stands in for a truncated download.</summary>
    public long DownloadedSize { get; set; } = 3_000_000_000;

    public string? DownloadedSha256 { get; set; }

    public Task DownloadAsync(
        Uri source,
        string destinationPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Downloads.Add((source, destinationPath));

        if (Failure is not null)
        {
            return Task.FromException(Failure);
        }

        files.WithBinary(destinationPath, DownloadedSize, DownloadedSha256);
        progress?.Report("downloaded");
        return Task.CompletedTask;
    }
}

/// <summary>A DNS the test writes: host name → IPv4, with everything else unresolvable.</summary>
public sealed class StubHostAddressResolver : IHostAddressResolver
{
    public Dictionary<string, string> Addresses { get; } = new(StringComparer.OrdinalIgnoreCase);

    public StubHostAddressResolver With(string host, string address)
    {
        Addresses[host] = address;
        return this;
    }

    public Task<IPAddress?> ResolveIPv4Async(string host, CancellationToken cancellationToken) =>
        Task.FromResult(Addresses.TryGetValue(host, out var address) ? IPAddress.Parse(address) : null);
}

/// <summary>Collects progress reports so a test can assert what a job would have seen.</summary>
public sealed class ProgressSink : IProgress<string>
{
    public List<string> Lines { get; } = [];

    public void Report(string value)
    {
        lock (Lines)
        {
            Lines.Add(value);
        }
    }
}

/// <summary>
/// A real logger whose every entry is captured, rendered exactly as a text sink would write it. This
/// is how "the child's output never reaches a log" is verified rather than assumed: the platform
/// implementations are the one place in the service that holds raw stderr from another process.
/// </summary>
public sealed class LogSink : IDisposable
{
    private readonly ILoggerFactory _factory;

    public LogSink()
    {
        _factory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddProvider(Captured);
        });
    }

    public CapturedLogs Captured { get; } = new();

    /// <summary>Everything logged so far, as one blob — what a text sink would have written.</summary>
    public string Text => Captured.AllText();

    public ILogger<T> Logger<T>() => _factory.CreateLogger<T>();

    public void Dispose() => _factory.Dispose();
}

/// <summary>The options a Windows-platform test starts from; every path is a real Windows path.</summary>
public static class PlatformOptions
{
    public const string ScriptsDir = @"C:\Construct";

    public const string CacheDir = @"C:\ProgramData\Construct\service\iso";

    public static ConstructdOptions Create(Action<ConstructdOptions>? configure = null)
    {
        var options = new ConstructdOptions
        {
            ScriptsDir = ScriptsDir,
            PublicHost = "buildbox.test",
            SshForwardPorts = new PortRangeOptions(2201, 2299),
            AppForwardPorts = new PortRangeOptions(2300, 2999),
        };

        options.Iso.CacheDir = CacheDir;
        options.Iso.SourcePath = @"C:\isos\ubuntu-24.04.3-live-server-amd64.iso";

        configure?.Invoke(options);
        return options;
    }
}
