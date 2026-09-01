using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Constructd.Tests.Support;

/// <summary>One rendered log entry, with everything a sink could ever write out.</summary>
/// <param name="Rendered">
/// Message, state and exception (message, stack trace, <c>Data</c>, inner exceptions) — i.e. the whole
/// text any real sink could emit for this entry.
/// </param>
public sealed record CapturedLogEntry(string Category, LogLevel Level, string Rendered);

/// <summary>
/// Captures what the host logs, so a test can assert that a secret never reaches a log sink. The
/// service must not hand exception objects to a logger; this is how that is verified rather than
/// assumed.
/// </summary>
public sealed class CapturedLogs : ILoggerProvider
{
    public ConcurrentQueue<CapturedLogEntry> Entries { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);

    public void Dispose()
    {
    }

    /// <summary>Everything that was logged, as one blob — what a text sink would have written.</summary>
    public string AllText() => string.Join("\n", Entries.Select(e => $"{e.Category} {e.Level} {e.Rendered}"));

    private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var rendered = formatter(state, exception);

            if (state is not null)
            {
                rendered += " | state: " + state;
            }

            if (exception is not null)
            {
                // Deliberately the full exception: that is exactly what must never contain a secret.
                rendered += " | exception: " + exception;

                foreach (var key in exception.Data.Keys)
                {
                    rendered += $" | data[{key}]={exception.Data[key]}";
                }
            }

            entries.Enqueue(new CapturedLogEntry(category, logLevel, rendered));
        }
    }
}
