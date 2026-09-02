using System.Collections.Concurrent;
using Constructd.Core.Abstractions;

namespace Constructd.Fakes;

/// <summary>One recorded child-process launch — exactly what was asked for, nothing normalized.</summary>
public sealed record RecordedProcess(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? StandardInput,
    TimeSpan Timeout);

/// <summary>
/// A process runner that starts nothing: it records the file name and the <em>argument vector</em> and
/// answers from a queue of scripted results. That is what lets the Windows implementations be tested
/// on Linux — every command line this service would issue is asserted argument by argument, so a
/// change to one is a failing test rather than a field surprise.
/// </summary>
public sealed class RecordingProcessRunner : IProcessRunner
{
    private readonly ConcurrentQueue<Func<RecordedProcess, ProcessResult>> _responses = new();

    /// <summary>Every launch, in order.</summary>
    public List<RecordedProcess> Calls { get; } = [];

    /// <summary>Answer used once the scripted <see cref="Respond(ProcessResult)"/> queue is empty.</summary>
    public ProcessResult Default { get; set; } = new(0, string.Empty, string.Empty, TimedOut: false);

    /// <summary>Set to make <see cref="RunAsync"/> throw, standing in for "could not start the process".</summary>
    public Exception? Failure { get; set; }

    public RecordedProcess this[int index] => Calls[index];

    public RecordingProcessRunner Respond(ProcessResult result)
    {
        _responses.Enqueue(_ => result);
        return this;
    }

    /// <summary>Queues a successful run whose stdout is the given lines (newline-separated).</summary>
    public RecordingProcessRunner RespondStdout(params string[] lines)
    {
        return Respond(new ProcessResult(0, string.Join(Environment.NewLine, lines), string.Empty, TimedOut: false));
    }

    /// <summary>Queues an answer computed from the call, for tests that vary by VM name.</summary>
    public RecordingProcessRunner Respond(Func<RecordedProcess, ProcessResult> responder)
    {
        _responses.Enqueue(responder);
        return this;
    }

    public Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInput,
        TimeSpan timeout,
        IProgress<string>? standardOutputLines,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var call = new RecordedProcess(fileName, [.. arguments], standardInput, timeout);
        lock (Calls)
        {
            Calls.Add(call);
        }

        if (Failure is not null)
        {
            return Task.FromException<ProcessResult>(Failure);
        }

        var result = _responses.TryDequeue(out var responder) ? responder(call) : Default;

        // The real runner reports lines as they arrive; replaying them here keeps the progress path
        // under test.
        if (standardOutputLines is not null && result.StandardOutput.Length > 0)
        {
            foreach (var line in result.StandardOutput.Split('\n'))
            {
                standardOutputLines.Report(line.TrimEnd('\r'));
            }
        }

        return Task.FromResult(result);
    }
}
