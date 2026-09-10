using Constructd.Core.Abstractions;
namespace Constructd.Core.Services;

public sealed class UnsupportedInteractiveConsole : IInteractiveConsole
{
    public Task<ConsoleConnection> ConnectAsync(ConsoleSession session, CancellationToken ct) => throw new ConsoleTransportException();
    public Task RenewAsync(ConsoleSession session, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    public Task ReconcileAsync(CancellationToken ct) => Task.CompletedTask;
}
