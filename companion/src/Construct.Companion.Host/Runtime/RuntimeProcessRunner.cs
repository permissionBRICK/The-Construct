using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Host.Runtime;

public sealed class RuntimeProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (invocation.Timeout is { } duration) timeout.CancelAfter(duration);
        await using var process = Start(invocation, timeout.Token);
        var stdout = CollectAsync(process.StandardOutput, 4 * 1024 * 1024); var stderr = CollectAsync(process.StandardError, 32768);
        try
        {
            var code = await process.Completion.ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            return new(code, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        finally
        {
            await process.DisposeAsync().ConfigureAwait(false);
            try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
        }
    }
    private static async Task<string> CollectAsync(IAsyncEnumerable<string> source, int limit)
    {
        var text = new StringBuilder();
        await foreach (var chunk in source.ConfigureAwait(false))
        { if (text.Length < limit) text.Append(chunk.AsSpan(0, Math.Min(chunk.Length, limit - text.Length))); }
        return text.ToString();
    }
    public IRunningProcess Start(ProcessInvocation invocation, CancellationToken cancellationToken = default) => new Child(invocation, cancellationToken);
    private sealed class Child : IRunningProcess
    {
        private readonly Process process;
        private readonly CancellationTokenSource stop = new();
        private readonly CancellationTokenRegistration registration;
        private readonly Channel<string> stdout = Channel.CreateBounded<string>(32), stderr = Channel.CreateBounded<string>(32);
        private readonly Task<int> completion;
        private int disposed;
        public Child(ProcessInvocation invocation, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var info = new ProcessStartInfo(invocation.FileName) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (invocation.WorkingDirectory is not null) info.WorkingDirectory = invocation.WorkingDirectory;
            foreach (var arg in invocation.Arguments) info.ArgumentList.Add(arg);
            process = new Process { StartInfo = info };
            try { process.Start(); }
            catch { process.Dispose(); throw new InvalidOperationException("Could not start runtime child process."); }
            registration = token.Register(Kill);
            completion = RunAsync(invocation.StandardInput);
        }
        public IAsyncEnumerable<string> StandardOutput => stdout.Reader.ReadAllAsync();
        public IAsyncEnumerable<string> StandardError => stderr.Reader.ReadAllAsync();
        public Task<int> Completion => completion;
        private async Task<int> RunAsync(Secret? input)
        {
            var output = PumpAsync(process.StandardOutput, stdout.Writer); var error = PumpAsync(process.StandardError, stderr.Writer);
            try
            {
                if (input is not null) await process.StandardInput.WriteAsync(input.Reveal().AsMemory(), stop.Token).ConfigureAwait(false);
                process.StandardInput.Close();
                await process.WaitForExitAsync().ConfigureAwait(false);
                await Task.WhenAll(output, error).ConfigureAwait(false); return process.ExitCode;
            }
            catch { Kill(); throw new InvalidOperationException("Runtime child process I/O failed."); }
            finally
            {
                try { await process.WaitForExitAsync().ConfigureAwait(false); } catch { }
                try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch { }
            }
        }
        private async Task PumpAsync(StreamReader reader, ChannelWriter<string> writer)
        {
            var buffer = new char[4096];
            try
            {
                int read;
                while ((read = await reader.ReadAsync(buffer, stop.Token).ConfigureAwait(false)) > 0)
                    await writer.WriteAsync(new string(buffer, 0, read), stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally { writer.TryComplete(); }
        }
        private void Kill()
        {
            stop.Cancel();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
        public async Task StopAsync(CancellationToken cancellationToken = default)
        { Kill(); try { await completion.ConfigureAwait(false); } catch { } }
        public async ValueTask DisposeAsync()
        {
            await StopAsync().ConfigureAwait(false);
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            registration.Dispose(); process.Dispose();
        }
    }
}
