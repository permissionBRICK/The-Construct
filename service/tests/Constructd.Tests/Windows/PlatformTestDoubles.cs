using System.Net;
using Constructd.Core.Abstractions;
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

    /// <summary>Every file whose hash was asked for — hashing gigabytes is not free.</summary>
    public List<string> Hashed { get; } = [];

    public string ComputeSha256(string path)
    {
        Hashed.Add(path);
        return Hashes.TryGetValue(path, out var hash) ? hash : "0";
    }

    /// <summary>Paths the fake refuses to delete — an ISO Hyper-V has attached to a running VM.</summary>
    public HashSet<string> Locked { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> ListFiles(string directoryPath, string searchPattern)
    {
        var prefix = directoryPath.TrimEnd('\\', '/') + "\\";
        var star = searchPattern.IndexOf('*');
        var head = star < 0 ? searchPattern : searchPattern[..star];
        var tail = star < 0 ? string.Empty : searchPattern[(star + 1)..];

        return Files.Keys.Concat(Sizes.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Where(path =>
            {
                var name = path[prefix.Length..];
                return !name.Contains('\\', StringComparison.Ordinal) &&
                       name.StartsWith(head, StringComparison.OrdinalIgnoreCase) &&
                       name.EndsWith(tail, StringComparison.OrdinalIgnoreCase) &&
                       name.Length >= head.Length + tail.Length;
            })
            .ToList();
    }

    public void MoveFile(string sourcePath, string destinationPath, bool overwrite)
    {
        if (!overwrite && FileExists(destinationPath))
        {
            throw new IOException($"{destinationPath} already exists");
        }

        if (Files.TryGetValue(sourcePath, out var content))
        {
            Files[destinationPath] = content;
            Written[destinationPath] = content;
        }

        if (Sizes.TryGetValue(sourcePath, out var size))
        {
            Sizes[destinationPath] = size;
        }

        Moves.Add((sourcePath, destinationPath));
        Files.Remove(sourcePath);
        Sizes.Remove(sourcePath);
    }

    /// <summary>Every rename, in order — the ISO catalog's pointer swap is one of these.</summary>
    public List<(string Source, string Destination)> Moves { get; } = [];

    public bool TryCreateNewFile(string path)
    {
        if (FileExists(path))
        {
            return false;
        }

        Sizes[path] = 0;
        return true;
    }

    public bool TryDeleteFile(string path)
    {
        if (Locked.Contains(path))
        {
            return false;
        }

        DeleteFile(path);
        return true;
    }
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

/// <summary>
/// A build strategy that produces a file without WSL, xorriso or three gigabytes of I/O: it records
/// what it was asked for and "writes" the ISO into the fake file system. Everything the catalog and
/// the admin CLI do around a build is exercised against it.
/// </summary>
public sealed class FakeIsoMediaBuilder(FakeIsoFileSystem files) : IIsoMediaBuilder
{
    public List<IsoMediaRequest> Requests { get; } = [];

    /// <summary>Set to make the build fail the way a real one does.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Size the "built" ISO gets; 0 stands in for a build that produced nothing.</summary>
    public long BuiltSize { get; set; } = 3_000_000_000;

    public string SourceIso { get; set; } = @"C:\isos\ubuntu-24.04.3-live-server-amd64.iso";

    public string SourceSha256 { get; set; } = "abc123";

    public string BootstrapKeyFingerprint { get; set; } = "SHA256:AAAA";

    public string ScriptSha256 { get; set; } = "script-hash";

    public Task<IsoMediaResult> BuildMediaAsync(
        IsoMediaRequest request,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        Requests.Add(request);

        if (Failure is not null)
        {
            return Task.FromException<IsoMediaResult>(Failure);
        }

        progress?.Report($"building generic autoinstall media (hostname source: {request.HostnameSource})");
        files.WithBinary(request.OutputPath, BuiltSize);

        return Task.FromResult(new IsoMediaResult(
            request.OutputPath, SourceIso, SourceSha256, BootstrapKeyFingerprint, ScriptSha256));
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
