namespace Construct.Testing;

/// <summary>
/// xUnit 2 disposes a test class through <see cref="IDisposable"/> or <see cref="IAsyncLifetime"/>, never through
/// <see cref="IAsyncDisposable"/> alone. A class that relies on the latter keeps its engines, agents and their
/// background work running, and its temporary directories on disk, for the rest of the run.
/// </summary>
public sealed class TestClassDisposalTests
{
    [Fact]
    public void Every_test_class_that_cleans_up_is_disposed_by_xunit()
    {
        var undisposed = typeof(TestClassDisposalTests).Assembly.GetTypes()
            .Where(type => type.GetMethods().Any(method => method.IsDefined(typeof(FactAttribute), true)))
            .Where(type => typeof(IAsyncDisposable).IsAssignableFrom(type)
                && !typeof(IAsyncLifetime).IsAssignableFrom(type) && !typeof(IDisposable).IsAssignableFrom(type))
            .Select(type => type.FullName);
        Assert.Empty(undisposed);
    }
}
