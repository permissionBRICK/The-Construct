using System.Globalization;
using System.Text;
using Constructd.Core.Configuration;

namespace Constructd.Api.Infrastructure;

/// <summary>
/// The host service's own log file: one file per UTC day under the data directory, kept for a
/// bounded number of days (<see cref="FileLogOptions"/>). Writing is best-effort -- a full or
/// read-only disk never affects the service -- and every line is flushed, so the file is current
/// while a job is running.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly int _retentionDays;
    private readonly Func<DateTimeOffset> _clock;
    private DateOnly? _day;
    private StreamWriter? _writer;

    public FileLoggerProvider(string directory, int retentionDays, Func<DateTimeOffset>? clock = null)
    {
        _directory = directory;
        _retentionDays = Math.Max(1, retentionDays);
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The directory to log to, or null when the file log is off.</summary>
    public static string? Resolve(ConstructdOptions options)
    {
        var cfg = options.FileLog;
        if (!cfg.Enabled)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(cfg.Directory))
        {
            return Path.GetFullPath(cfg.Directory);
        }

        // Fake mode (development, tests) writes nothing unless asked to.
        if (options.Fake)
        {
            return null;
        }

        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!, "logs");
    }

    public static string FileName(DateOnly day) => "constructd-" + day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".log";

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = _clock();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)).Append(' ')
            .Append(Level(level)).Append(' ')
            .Append(category).Append("  ")
            .Append(message);
        if (exception is not null)
        {
            line.Append(Environment.NewLine).Append("    ").Append(exception.ToString().Replace("\n", "\n    ", StringComparison.Ordinal));
        }

        lock (_gate)
        {
            try
            {
                var day = DateOnly.FromDateTime(now.UtcDateTime);
                if (_writer is null || _day != day)
                {
                    Roll(day);
                }

                _writer?.WriteLine(line.ToString());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _writer = null; // retried on the next entry
            }
        }
    }

    private void Roll(DateOnly day)
    {
        _writer?.Dispose();
        _writer = null;
        Directory.CreateDirectory(_directory);
        var stream = new FileStream(Path.Combine(_directory, FileName(day)), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _day = day;
        Prune(day);
    }

    private void Prune(DateOnly today)
    {
        var oldest = today.AddDays(-_retentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, "constructd-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.Length != "constructd-yyyyMMdd".Length ||
                !DateOnly.TryParseExact(name["constructd-".Length..], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ||
                day >= oldest)
            {
                continue;
            }

            try { File.Delete(file); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string Level(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "CRIT ",
        _ => "?????",
    };

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            provider.Write(category, logLevel, formatter(state, exception), exception);
        }
    }
}
