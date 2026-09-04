using Constructd.Core.Services;

namespace Constructd.Fakes;

/// <summary>
/// Records what the reconciler asked for instead of talking to the operating system. It derives from
/// <see cref="HostPowerGuardBase"/> on purpose, so the parts the real guard relies on — one request
/// at a time, no platform call without a transition, release on dispose — are the ones under test.
/// </summary>
public sealed class FakeHostPowerGuard : HostPowerGuardBase
{
    private readonly List<(bool Required, string Reason)> _transitions = [];

    /// <summary>Every acquire (true) and release (false) that actually reached the platform, in order.</summary>
    public IReadOnlyList<(bool Required, string Reason)> Transitions
    {
        get { lock (_transitions) { return _transitions.ToArray(); } }
    }

    public int AcquireCount => Transitions.Count(t => t.Required);

    public int ReleaseCount => Transitions.Count(t => !t.Required);

    public bool DisposedCore { get; private set; }

    protected override void Acquire(string reason) => Record(true, reason);

    protected override void Release(string reason) => Record(false, reason);

    protected override void DisposeCore() => DisposedCore = true;

    private void Record(bool required, string reason)
    {
        lock (_transitions)
        {
            _transitions.Add((required, reason));
        }
    }
}
