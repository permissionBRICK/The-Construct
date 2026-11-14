using System.Text;
using Constructd.Core.Abstractions;

namespace Constructd.Core.Services;

/// <summary>
/// <see cref="IGuestSessionRunner"/> over the system <c>ssh</c> with <see cref="SystemSshRunner"/>'s
/// options, started through <see cref="IStreamingProcessRunner"/> (an argv array, no local shell).
/// The output is handed on and never logged: it can hold what the guest holds.
/// </summary>
public sealed class SshGuestSessionRunner(IStreamingProcessRunner processes) : IGuestSessionRunner
{
    /// <summary>How long a session may take to end by itself once its stdin is closed.</summary>
    public static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    public IGuestSession Start(SshTarget target, string command, Action<string> onLine)
    {
        var process = processes.Start("ssh", SystemSshRunner.SshArguments(target, command), new Dictionary<string, string>());
        return new Session(process, onLine);
    }

    private sealed class Session : IGuestSession
    {
        private readonly IStreamingProcess _process;
        private readonly SemaphoreSlim _write = new(1, 1);
        private int _closed;

        public Session(IStreamingProcess process, Action<string> onLine)
        {
            _process = process;
            Exited = ReadAsync(onLine);
        }

        public Task Exited { get; }

        private async Task ReadAsync(Action<string> onLine)
        {
            using var reader = new StreamReader(_process.StandardOutput, Encoding.UTF8);
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) onLine(line);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            try { await _process.Exited.ConfigureAwait(false); }
            catch (InvalidOperationException) { }
        }

        public async Task WriteLineAsync(string line, CancellationToken ct)
        {
            await _write.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _closed) == 1) throw new IOException("The session has ended.");
                var bytes = Encoding.UTF8.GetBytes(line + "\n");
                await _process.StandardInput.WriteAsync(bytes, ct).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
            }
            finally { _write.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            await _write.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Interlocked.Exchange(ref _closed, 1) == 1) return;
                // End of stdin ends the guest's session; the connection goes after a grace period either way.
                try { _process.StandardInput.Close(); } catch (IOException) { } catch (ObjectDisposedException) { }
                await Task.WhenAny(Exited, Task.Delay(CloseGrace)).ConfigureAwait(false);
                await _process.DisposeAsync().ConfigureAwait(false);
                await Exited.ConfigureAwait(false);
            }
            finally { _write.Release(); }
        }
    }
}
