namespace Construct.Companion.Core.Abstractions;

public sealed partial record ProcessInvocation
{
    // Inherit the parent environment, then apply overrides (null removes a key).
    // Used by git's temporary index; values must never enter diagnostics.
    public IReadOnlyDictionary<string, string?>? EnvironmentOverrides { get; init; }
}
