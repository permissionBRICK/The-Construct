namespace Construct.Agent;

/// <summary>A refusal the local API answers as a problem document <c>{title, status, code}</c>.</summary>
public sealed class AgentFailure(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;

    public string Code { get; } = code;
}
