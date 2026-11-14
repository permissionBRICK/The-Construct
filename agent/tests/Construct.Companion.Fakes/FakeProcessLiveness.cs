using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Fakes;

public sealed class FakeProcessLiveness : IProcessLiveness
{
    public HashSet<int> DeadProcesses { get; } = [];
    public int ProcessId { get; set; } = 42;
    public bool ProcessIsDefinitelyDead(int pid) => DeadProcesses.Contains(pid);
}
