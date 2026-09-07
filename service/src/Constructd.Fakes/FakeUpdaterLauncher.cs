using Constructd.Core.Abstractions;
namespace Constructd.Fakes;

public sealed class FakeUpdaterLauncher : IUpdaterLauncher
{
    // Do not record or print the handoff: it contains the one-time health credential.
    private UpdateHandoff? _handoff;
    public RecoveryRecord? Recovery { get; set; }
    public UpdateFence? Fence { get; private set; }
    public bool UpdaterHoldsLock { get; set; }
    public int LaunchCount { get; private set; }
    public Task LaunchAsync(UpdateHandoff handoff, CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); _handoff = handoff; LaunchCount++; return Task.CompletedTask; }
    public Task<UpdateHandoff?> ReadHandoffAsync(CancellationToken ct) => Task.FromResult(_handoff);
    public Task<RecoveryRecord?> ReadRecoveryRecordAsync(CancellationToken ct) => Task.FromResult(Recovery);
    public Task<bool> TryWriteFenceAsync(UpdateFence fence, CancellationToken ct)
    { if (UpdaterHoldsLock) return Task.FromResult(false); Fence = fence; return Task.FromResult(true); }
    public Task<UpdateHandoff?> ReadOwnHandoffAsync(string expectedCommit, CancellationToken ct) => Task.FromResult(_handoff?.Commit == expectedCommit ? _handoff : null);
}
