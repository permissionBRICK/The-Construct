namespace Constructd.Core.Domain;

/// <summary>
/// Per-VM idle policy (plan §4.7). <paramref name="TimeoutMinutes"/> 0 means "off"; so does
/// <see cref="IdleAction.Off"/>.
/// </summary>
public sealed record IdlePolicy(int TimeoutMinutes, IdleAction Action)
{
    /// <summary>A policy that never idles a VM out.</summary>
    public static IdlePolicy Disabled { get; } = new(0, IdleAction.Off);

    /// <summary>True when this policy can never act.</summary>
    public bool IsDisabled => TimeoutMinutes <= 0 || Action == IdleAction.Off;
}
