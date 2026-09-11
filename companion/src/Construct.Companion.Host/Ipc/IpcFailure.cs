namespace Construct.Companion.Host.Ipc;

// Becomes an RFC 7807 problem on the wire and a visible refusal in the panel.
public sealed class IpcFailure(int status, string code, string title) : Exception(title)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
