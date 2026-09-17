using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;

namespace Constructd.Proxmox;

/// <summary>
/// Native noVNC needs a Proxmox login. The VMConnect gateway cannot consume a PVE VNC ticket;
/// vncwebsocket also checks the authenticated PVE user, so a root-issued ticket is not a delegated login.
/// </summary>
public sealed class ProxmoxInteractiveConsole(IProcessRunner runner, ConstructdOptions options)
    : IInteractiveConsole, IInteractiveConsoleLink
{
    private readonly ProxmoxCommands commands = new(runner, options);
    public async Task<string> GetLaunchUrlAsync(string vmName, CancellationToken ct)
    {
        try
        {
            var id = await commands.RequireAsync(vmName, ct);
            var host = string.IsNullOrWhiteSpace(options.PublicHost) ? ProxmoxDriver.ResolveNode(options) : options.PublicHost;
            return new UriBuilder("https", host, 8006) { Query = "console=kvm&novnc=1&vmid=" + ProxmoxCommands.Number(id) +
                "&node=" + Uri.EscapeDataString(ProxmoxDriver.ResolveNode(options)) + "&resize=off" }.Uri.AbsoluteUri;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { throw new ConsoleTransportException(); }
    }
    public Task<ConsoleConnection> ConnectAsync(ConsoleSession session, CancellationToken ct) =>
        throw new NotSupportedException("The Construct gateway requires VMConnect. Use the native noVNC URL with a Proxmox login.");
    public Task RenewAsync(ConsoleSession session, CancellationToken ct) => Task.CompletedTask;
    public Task RemoveAsync(string sessionId, CancellationToken ct) => Task.CompletedTask;
    public Task ReconcileAsync(CancellationToken ct) => Task.CompletedTask;
}
