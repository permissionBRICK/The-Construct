using Constructd.Core.Abstractions;

namespace Constructd.Core.Logic;

/// <summary>
/// Allocates ports out of a configured inclusive range, lowest free first. Modelled on the
/// extension's mic-tunnel range allocator (plan §3.2). Thread-safe; the same instance is shared by
/// the API, the job engine and the reconciler.
///
/// This is a bookkeeping allocator: it hands out ports the service considers free. A real
/// implementation additionally probes whether the port is in use on the host before returning it —
/// that check belongs in the platform layer, not here.
/// </summary>
public sealed class PortAllocator
{
    private readonly Lock _gate = new();
    private readonly SortedSet<int> _allocated = [];

    public PortAllocator(int start, int end)
    {
        if (start < 1 || start > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(start), start, "Port range start must be 1-65535.");
        }

        if (end < start || end > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(end), end, "Port range end must be >= start and <= 65535.");
        }

        Start = start;
        End = end;
    }

    public int Start { get; }

    public int End { get; }

    public int Capacity => End - Start + 1;

    public int AvailableCount
    {
        get
        {
            lock (_gate)
            {
                return Capacity - _allocated.Count;
            }
        }
    }

    public IReadOnlyList<int> Allocated
    {
        get
        {
            lock (_gate)
            {
                return [.. _allocated];
            }
        }
    }

    /// <summary>Takes the lowest free port.</summary>
    /// <exception cref="PortRangeExhaustedException">The range is full.</exception>
    public int Allocate()
    {
        lock (_gate)
        {
            for (var port = Start; port <= End; port++)
            {
                if (_allocated.Add(port))
                {
                    return port;
                }
            }

            throw new PortRangeExhaustedException(Start, End);
        }
    }

    /// <summary>
    /// Re-takes a specific port — used when rebuilding allocator state from the store at startup.
    /// Returns false when the port is outside the range or already allocated.
    /// </summary>
    public bool TryReserve(int port)
    {
        if (port < Start || port > End)
        {
            return false;
        }

        lock (_gate)
        {
            return _allocated.Add(port);
        }
    }

    /// <summary>Returns the port to the pool. False when it was not allocated.</summary>
    public bool Release(int port)
    {
        lock (_gate)
        {
            return _allocated.Remove(port);
        }
    }

    public bool IsAllocated(int port)
    {
        lock (_gate)
        {
            return _allocated.Contains(port);
        }
    }
}
