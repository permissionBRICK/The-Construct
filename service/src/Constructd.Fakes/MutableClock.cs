using Constructd.Core.Abstractions;

namespace Constructd.Fakes;

/// <summary>A clock tests can move. Defaults to a fixed, readable instant.</summary>
public sealed class MutableClock : IClock
{
    public MutableClock()
        : this(new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero))
    {
    }

    public MutableClock(DateTimeOffset now) => UtcNow = now;

    public DateTimeOffset UtcNow { get; set; }

    public void Advance(TimeSpan by) => UtcNow += by;
}
