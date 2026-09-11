namespace Construct.Companion.Core.Abstractions;

public sealed partial record ProcessInvocation
{
    // Lifecycle consoles must stay visible; Electron/Desktop background starts opt in.
    public bool CreateNoWindow { get; init; }
}
