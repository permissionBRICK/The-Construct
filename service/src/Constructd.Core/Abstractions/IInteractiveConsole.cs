namespace Constructd.Core.Abstractions;

// This credential may only travel over the authenticated host API to the trusted gateway.
// Never serialize it into a browser response or a log message.
public sealed class ConsoleConnection
{
    public required string VmId { get; init; }
    public required string Username { get; init; }
    public required string Password { get; init; }
    public required string Domain { get; init; }
    public required string CertificateFingerprint { get; init; }
    public override string ToString() => "ConsoleConnection [redacted]";
}

public interface IInteractiveConsole
{
    Task<ConsoleConnection> ConnectAsync(ConsoleSession session, CancellationToken ct);
    Task RenewAsync(ConsoleSession session, CancellationToken ct);
    Task RemoveAsync(string sessionId, CancellationToken ct);
    Task ReconcileAsync(CancellationToken ct);
}
