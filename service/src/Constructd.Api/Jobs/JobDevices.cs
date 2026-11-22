namespace Constructd.Api.Jobs;

/// <summary>
/// The device whose device token asked for a <c>provision</c>, <c>reinstall</c> or <c>restore</c> job, so that the
/// job's repair opens T3 Desktop on that PC only (design 11.5). A job asked for without a device token, or started by
/// the engine itself, has none. Kept in memory for the latest <see cref="Kept"/> jobs, like the repair sessions, which
/// an engine restart ends too; a device id never leaves the engine.
/// </summary>
public sealed class JobDevices
{
    public const int Kept = 500;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _byJob = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();

    /// <summary>Remembers the device of a job that is about to run; a job keeps the first device it was given.</summary>
    public void Remember(string jobId, string? device)
    {
        if (device is null) return;
        lock (_gate)
        {
            if (!_byJob.TryAdd(jobId, device)) return;
            _order.Enqueue(jobId);
            while (_order.Count > Kept) _byJob.Remove(_order.Dequeue());
        }
    }

    /// <summary>The device that asked for the job, or null.</summary>
    public string? Of(string jobId)
    {
        lock (_gate) return _byJob.GetValueOrDefault(jobId);
    }
}
