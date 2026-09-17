using Constructd.Core.Abstractions;

namespace Constructd.Core.Services;

public sealed record MemoryPressureAction(string VmName, DateTimeOffset At, string Reason);
public sealed record MemoryPressureStatus(bool Enabled, string State, MemoryPressureAction? LastAction,
    decimal? UsedPercent = null, string? Detail = null);

/// <summary>Immutable snapshots shared between the scheduler and status readers.</summary>
public sealed class MemoryPressureState
{
    private MemoryPressureStatus _status = new(true, "idle", null);
    public MemoryPressureStatus Status => Volatile.Read(ref _status);
    public void Set(MemoryPressureStatus value) => Volatile.Write(ref _status, value);
}

public sealed record MemoryPressureServices(IHostConfigStore Config, ICapacityLedger Capacity, IJobQueryStore Jobs,
    IVmMetadataStore Metadata, IClock Clock, MemoryPressureState State, bool UseGuestMemoryDemand);
