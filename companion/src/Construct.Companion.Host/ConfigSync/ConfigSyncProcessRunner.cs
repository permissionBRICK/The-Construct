using System.Diagnostics;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Host.ConfigSync;

// Optional portable process adapter. Runtime composition can supply its own runner.
public sealed class ConfigSyncProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default)
    {
        await using var running = new Running(invocation, cancellationToken);
        var stdout = Collect(running.StandardOutput); var stderr = Collect(running.StandardError);
        var code = await running.Completion; return new(code, await stdout, await stderr);
    }
    private static async Task<string> Collect(IAsyncEnumerable<string> chunks) { var text = new System.Text.StringBuilder(); await foreach (var chunk in chunks) text.Append(chunk); return text.ToString(); }
    public IRunningProcess Start(ProcessInvocation invocation, CancellationToken cancellationToken = default) => new Running(invocation, cancellationToken);
    private sealed class Running : IRunningProcess
    {
        private readonly Process process = new(); private readonly CancellationTokenSource stop; private int disposed;
        private readonly Channel<string> stdout = Channel.CreateUnbounded<string>(); private readonly Channel<string> stderr = Channel.CreateUnbounded<string>();
        public IAsyncEnumerable<string> StandardOutput => stdout.Reader.ReadAllAsync(); public IAsyncEnumerable<string> StandardError => stderr.Reader.ReadAllAsync();
        public Task<int> Completion { get; }
        public Running(ProcessInvocation invocation, CancellationToken ct)
        {
            stop = CancellationTokenSource.CreateLinkedTokenSource(ct); if (invocation.Timeout is { } timeout) stop.CancelAfter(timeout);
            process.StartInfo = new(invocation.FileName) { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = invocation.WorkingDirectory ?? "" };
            foreach (var arg in invocation.Arguments) process.StartInfo.ArgumentList.Add(arg);
            foreach (var pair in invocation.EnvironmentOverrides ?? new Dictionary<string, string?>()) { if (pair.Value == null) process.StartInfo.Environment.Remove(pair.Key); else process.StartInfo.Environment[pair.Key] = pair.Value; }
            Completion = Run(invocation);
        }
        private async Task<int> Run(ProcessInvocation invocation)
        {
            try
            {
                process.Start(); var outTask = Pump(process.StandardOutput, stdout.Writer); var errTask = Pump(process.StandardError, stderr.Writer);
                using var registration = stop.Token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
                try { if (invocation.StandardInput != null) await process.StandardInput.WriteAsync(invocation.StandardInput.Reveal()); } catch (IOException) { }
                process.StandardInput.Close(); await process.WaitForExitAsync(); await Task.WhenAll(outTask, errTask); return stop.IsCancellationRequested ? -1 : process.ExitCode;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return -1; }
            finally { stdout.Writer.TryComplete(); stderr.Writer.TryComplete(); }
        }
        private static async Task Pump(StreamReader reader, ChannelWriter<string> channel)
        {
            var buffer = new char[4096]; int count; while ((count = await reader.ReadAsync(buffer)) > 0) await channel.WriteAsync(new string(buffer, 0, count));
        }
        public async Task StopAsync(CancellationToken cancellationToken = default) { if (!Completion.IsCompleted) await stop.CancelAsync(); await Completion.WaitAsync(cancellationToken); }
        public async ValueTask DisposeAsync() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; await StopAsync(); process.Dispose(); stop.Dispose(); }
    }
}
