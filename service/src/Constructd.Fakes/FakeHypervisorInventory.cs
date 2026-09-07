using Constructd.Core.Abstractions;
namespace Constructd.Fakes;

public sealed class FakeHypervisorInventory : IHypervisorInventory
{
    public InventorySnapshot Snapshot { get; set; } = new(1, DateTimeOffset.MinValue, new(8, 16L << 30, 12L << 30, [new("fake:", 100L << 30, 80L << 30)], DateTimeOffset.MinValue), [], true, []);
    public int Reads { get; private set; }
    public Exception? Failure { get; set; }
    public Task<InventorySnapshot> ReadAsync(CancellationToken ct)
    { ct.ThrowIfCancellationRequested(); Reads++; if (Failure is not null) throw Failure; return Task.FromResult(Snapshot); }
}
