using Constructd.Core.Abstractions;
using Constructd.Core.Logic;

namespace Constructd.Tests.Core;

public class PortAllocatorTests
{
    [Fact]
    public void Allocates_lowest_free_port_in_range()
    {
        var allocator = new PortAllocator(2201, 2299);

        Assert.Equal(2201, allocator.Allocate());
        Assert.Equal(2202, allocator.Allocate());
        Assert.Equal(2203, allocator.Allocate());
    }

    [Fact]
    public void Never_hands_out_the_same_port_twice()
    {
        var allocator = new PortAllocator(2300, 2320);

        var ports = Enumerable.Range(0, 21).Select(_ => allocator.Allocate()).ToList();

        Assert.Equal(21, ports.Distinct().Count());
        Assert.Equal(0, allocator.AvailableCount);
    }

    [Fact]
    public void Released_ports_are_reused()
    {
        var allocator = new PortAllocator(2201, 2203);
        var first = allocator.Allocate();
        allocator.Allocate();
        allocator.Allocate();

        Assert.True(allocator.Release(first));
        Assert.False(allocator.IsAllocated(first));
        Assert.Equal(first, allocator.Allocate());
    }

    [Fact]
    public void Releasing_an_unallocated_port_reports_false()
    {
        var allocator = new PortAllocator(2201, 2299);
        Assert.False(allocator.Release(2250));
    }

    [Fact]
    public void Exhausted_range_throws()
    {
        var allocator = new PortAllocator(2201, 2202);
        allocator.Allocate();
        allocator.Allocate();

        var ex = Assert.Throws<PortRangeExhaustedException>(() => allocator.Allocate());
        Assert.Equal(2201, ex.RangeStart);
        Assert.Equal(2202, ex.RangeEnd);
    }

    [Fact]
    public void Reserving_rebuilds_state_from_the_store()
    {
        var allocator = new PortAllocator(2201, 2299);

        Assert.True(allocator.TryReserve(2250));
        Assert.False(allocator.TryReserve(2250));
        Assert.False(allocator.TryReserve(3000));
        Assert.Equal(2201, allocator.Allocate());
        Assert.Contains(2250, allocator.Allocated);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(2300, 2200)]
    [InlineData(2201, 70000)]
    public void Invalid_ranges_are_rejected(int start, int end) =>
        Assert.ThrowsAny<ArgumentOutOfRangeException>(() => new PortAllocator(start, end));

    [Fact]
    public void Concurrent_allocation_hands_out_distinct_ports()
    {
        var allocator = new PortAllocator(2300, 2399);

        var ports = new System.Collections.Concurrent.ConcurrentBag<int>();
        Parallel.For(0, 100, _ => ports.Add(allocator.Allocate()));

        Assert.Equal(100, ports.Distinct().Count());
    }
}
