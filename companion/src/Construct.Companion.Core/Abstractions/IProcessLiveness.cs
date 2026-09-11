namespace Construct.Companion.Core.Abstractions;

// The config-sync lock file names its owner pid so a crashed engine's lock can be broken.
public interface IProcessLiveness
{
    int ProcessId { get; }
    bool ProcessIsDefinitelyDead(int pid);
}
