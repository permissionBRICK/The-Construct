namespace Constructd.Fakes;

internal static class InMemoryTransaction
{
    internal static Lock Gate { get; } = new();
}
