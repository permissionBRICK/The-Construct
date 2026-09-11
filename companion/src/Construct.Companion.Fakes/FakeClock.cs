using Construct.Companion.Core.Abstractions;

namespace Construct.Companion.Fakes;

public sealed class FakeClock : IClock
{
    private readonly object gate = new();
    private readonly List<(DateTimeOffset Due, TaskCompletionSource Completion)> pending = [];
    public int PendingDelays { get { lock (gate) return pending.Count; } }
    public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UnixEpoch;
    public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (delay < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        if (delay == TimeSpan.Zero) return;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (DateTimeOffset Due, TaskCompletionSource Completion) entry;
        lock (gate) { entry = (UtcNow + delay, completion); pending.Add(entry); }
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        try { await completion.Task.ConfigureAwait(false); }
        finally { lock (gate) pending.Remove(entry); }
    }
    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        lock (gate)
        {
            UtcNow += elapsed;
            foreach (var entry in pending.Where(p => p.Due <= UtcNow).ToArray()) entry.Completion.TrySetResult();
        }
    }
}
