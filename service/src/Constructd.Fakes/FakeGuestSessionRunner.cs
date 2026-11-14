using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Constructd.Core.Abstractions;

namespace Constructd.Fakes;

/// <summary>
/// A guest that answers like <c>bin/construct-repair-agent.sh</c>, for fake mode and the tests. By default
/// the agent looks at the log, asks a question, and on the first reply says it fixed the cause; the end of
/// stdin ends it as "stopped". <see cref="Script"/> replaces that conversation.
/// </summary>
public sealed class FakeGuestSessionRunner : IGuestSessionRunner
{
    public ConcurrentQueue<FakeGuestSession> Sessions { get; } = new();

    /// <summary>What the fake agent does; the default asks once and then fixes.</summary>
    public Func<FakeGuestSession, Task> Script { get; set; } = AskThenFix;

    public IGuestSession Start(SshTarget target, string command, Action<string> onLine)
    {
        var session = new FakeGuestSession(target, command, onLine);
        Sessions.Enqueue(session);
        _ = Task.Run(async () =>
        {
            try { await Script(session); }
            finally { session.Exit(); }
        });
        return session;
    }

    public static async Task AskThenFix(FakeGuestSession s)
    {
        s.Emit(new { type = "start", agent = "claude", via = "direct" });
        s.Emit(new { type = "tool", text = "Bash: tail -n 50 /var/log/construct/provision.log" });
        s.Emit(new { type = "text", text = "The step failed because a package mirror timed out. I can retry it, or switch the mirror. Which do you prefer?" });
        s.Emit(new { type = "turn", outcome = "question" });
        if (await s.ReadLineAsync() is null)
        {
            s.Emit(new { type = "end", outcome = "stopped", text = "The session was ended." });
            return;
        }

        s.Emit(new { type = "tool", text = "Bash: apt-get update" });
        s.Emit(new { type = "text", text = "Done, the cause is fixed." });
        s.Emit(new { type = "turn", outcome = "fixed" });
        s.Emit(new { type = "end", outcome = "fixed", text = "" });
    }
}

/// <summary>One fake guest session: what the engine wrote to it, and the lines it sends back.</summary>
public sealed class FakeGuestSession(SshTarget target, string command, Action<string> onLine) : IGuestSession
{
    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _emit = new();

    public SshTarget Target { get; } = target;
    public string Command { get; } = command;
    public ConcurrentQueue<string> Received { get; } = new();
    public bool Disposed { get; private set; }
    public Task Exited => _exited.Task;

    public void Emit(object line) => EmitRaw(line as string ?? JsonSerializer.Serialize(line));

    public void EmitRaw(string line)
    {
        lock (_emit)
        {
            if (!_exited.Task.IsCompleted) onLine(line);
        }
    }

    /// <summary>The next line the engine wrote, or null once stdin is closed.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct = default)
    {
        try { return await _input.Reader.ReadAsync(ct); }
        catch (ChannelClosedException) { return null; }
    }

    public void Exit() => _exited.TrySetResult();

    public Task WriteLineAsync(string line, CancellationToken ct)
    {
        if (Disposed || !_input.Writer.TryWrite(line)) throw new IOException("The session has ended.");
        Received.Enqueue(line);
        return Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        Disposed = true;
        _input.Writer.TryComplete();
        await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Exit();
    }
}
