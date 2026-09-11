using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeRunningProcess : IRunningProcess
{
    private CancellationTokenRegistration cancellation;
    public FakeRunningProcess(CancellationToken cancellationToken = default)
    {
        cancellation = cancellationToken.Register(() => StopAsync());
        if (Completion.IsCompleted) cancellation.Dispose();
    }
    private readonly System.Threading.Channels.Channel<string> stdout = System.Threading.Channels.Channel.CreateUnbounded<string>();
    private readonly System.Threading.Channels.Channel<string> stderr = System.Threading.Channels.Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IAsyncEnumerable<string> StandardOutput => stdout.Reader.ReadAllAsync();
    public IAsyncEnumerable<string> StandardError => stderr.Reader.ReadAllAsync();
    public Task<int> Completion => completion.Task;
    public bool Stopped { get; private set; }
    public void Emit(string text) => stdout.Writer.TryWrite(text);
    public void Exit(int code = 0)
    {
        stdout.Writer.TryComplete(); stderr.Writer.TryComplete(); completion.TrySetResult(code); cancellation.Dispose();
    }
    public void Fail(Exception error)
    {
        stdout.Writer.TryComplete(error); stderr.Writer.TryComplete(error); completion.TrySetException(error); cancellation.Dispose();
    }
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Stopped = true; Exit(); return Task.CompletedTask;
    }
    public ValueTask DisposeAsync() => new(StopAsync());
}
