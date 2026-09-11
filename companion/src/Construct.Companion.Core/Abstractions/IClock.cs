namespace Construct.Companion.Core.Abstractions;

// Supplies UTC time and cancellable delays for timers, leases, and backoff.
// Fake time advances explicitly; Core never consults the system clock directly.
public interface IClock
{
    DateTimeOffset UtcNow { get; }
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default);
}
