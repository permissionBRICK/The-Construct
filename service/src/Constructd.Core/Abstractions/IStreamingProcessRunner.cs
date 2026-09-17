namespace Constructd.Core.Abstractions;

/// <summary>Starts a binary duplex process without a shell. Environment values must never be logged.</summary>
public interface IStreamingProcessRunner
{
    IStreamingProcess Start(string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment);
}

/// <summary>Owns the process tree and pipes. Disposal terminates the tree and waits for cleanup.</summary>
public interface IStreamingProcess : IAsyncDisposable
{
    Stream StandardInput { get; }
    Stream StandardOutput { get; }
    Task Exited { get; }
}
