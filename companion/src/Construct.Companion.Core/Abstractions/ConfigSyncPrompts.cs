namespace Construct.Companion.Core.Abstractions;
public sealed partial record PickItem
{
    public bool Disabled { get; init; }
    public bool Separator { get; init; }
}
public sealed partial record PickPrompt
{
    public string? Placeholder { get; init; }
}
