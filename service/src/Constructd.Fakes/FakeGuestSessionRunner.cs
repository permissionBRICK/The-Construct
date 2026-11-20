using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Constructd.Core.Abstractions;

namespace Constructd.Fakes;

/// <summary>
/// A guest that answers like <c>bin/construct-repair-agent.sh</c> and <c>bin/construct-agent-sign-in.py</c>, for fake
/// mode and the tests. By default the repair agent looks at the log, asks a question, and on the first reply says it
/// fixed the cause; a sign-in shows a link and a prompt and is done with the first code typed. The end of stdin ends
/// either as "stopped". <see cref="Script"/> and <see cref="SignInScript"/> replace them.
/// </summary>
public sealed class FakeGuestSessionRunner : IGuestSessionRunner
{
    public ConcurrentQueue<FakeGuestSession> Sessions { get; } = new();

    /// <summary>What the fake repair agent does; the default asks once and then fixes.</summary>
    public Func<FakeGuestSession, Task> Script { get; set; } = AskThenFix;

    /// <summary>What a fake sign-in does; the default asks for a code and signs in with it.</summary>
    public Func<FakeGuestSession, Task> SignInScript { get; set; } = CodeThenDone;

    public IGuestSession Start(SshTarget target, string command, Action<string> onLine)
    {
        var session = new FakeGuestSession(target, command, onLine);
        Sessions.Enqueue(session);
        var script = command.Contains("construct-agent-sign-in.py", StringComparison.Ordinal) ? SignInScript : Script;
        _ = Task.Run(async () =>
        {
            try { await script(session); }
            finally { session.Exit(); }
        });
        return session;
    }

    /// <summary>The agent named on a sign-in's command line.</summary>
    public static string SignInAgent(FakeGuestSession s) => s.Command.Split(' ', '\'').Reverse().FirstOrDefault(w => w is "claude-code" or "codex" or "opencode" or "antigravity") ?? "claude-code";

    public static async Task CodeThenDone(FakeGuestSession s)
    {
        var agent = SignInAgent(s);
        s.Emit(new { type = "start", agent });
        var screen = $"Sign in to {agent}:\n  https://sign-in.example/{agent}?state=fake\n\nPaste code here if prompted > ";
        s.Emit(new { type = "screen", text = screen });
        // As the real CLIs: Claude Code opens a link that returns to its own port; Codex does with --browser.
        if (agent == "claude-code" || (agent == "codex" && s.Command.Contains(" --browser ", StringComparison.Ordinal)))
        {
            var port = agent == "codex" ? 1455 : 45678;
            s.Emit(new { type = "browser", url = $"https://sign-in.example/{agent}/authorize?redirect_uri=http%3A%2F%2Flocalhost%3A{port}%2Fcallback&state=fake", port });
        }
        if (await s.ReadLineAsync() is not { } line)
        {
            s.Emit(new { type = "end", outcome = "stopped", signedIn = false, text = "" });
            return;
        }

        using var input = JsonDocument.Parse(line);
        var code = input.RootElement.TryGetProperty("text", out var t) ? t.GetString() : "";
        s.Emit(new { type = "screen", text = screen + code + "\nSigned in." });
        s.Emit(new { type = "end", outcome = "done", signedIn = true, version = "1.0.0", text = "" });
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
