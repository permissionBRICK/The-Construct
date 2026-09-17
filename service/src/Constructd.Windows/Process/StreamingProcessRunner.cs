using System.Diagnostics;
using Constructd.Core.Abstractions;

namespace Constructd.Windows.Process;

public sealed class StreamingProcessRunner : IStreamingProcessRunner
{
    public IStreamingProcess Start(string fileName, IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (key, value) in environment) info.Environment[key] = value;
        var process = new System.Diagnostics.Process { StartInfo = info };
        try
        {
            process.Start();
            return new RunningProcess(process);
        }
        catch
        {
            process.Dispose();
            throw new InvalidOperationException("Could not start console proxy.");
        }
        finally
        {
            foreach (var key in environment.Keys) info.Environment.Remove(key);
        }
    }

    private sealed class RunningProcess : IStreamingProcess
    {
        private readonly System.Diagnostics.Process process;
        private readonly Task stderr;
        public Stream StandardInput => process.StandardInput.BaseStream;
        public Stream StandardOutput => process.StandardOutput.BaseStream;
        public Task Exited { get; }

        public RunningProcess(System.Diagnostics.Process process)
        {
            this.process = process;
            // Drain without buffering or logging, even if a dependency prints its credentials.
            stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
            Exited = process.WaitForExitAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // Already exited.
                await Exited;
                await stderr;
            }
            finally { process.Dispose(); }
        }
    }
}
