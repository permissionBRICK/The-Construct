using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Runtime;
namespace Construct.Companion.Host.Runtime;

public sealed class ProcessSshTransport(IProcessRunner runner, IPortProbe probe, SshConfiguration configuration,
    string executable = "ssh", string? keyPath = null) : ISshTransport
{
    public Task<ProcessResult> RunRemoteScriptAsync(string script, TimeSpan? timeout = null, CancellationToken cancellationToken = default, Secret? standardInput = null)
        => runner.RunAsync(new(executable, SshArgs.Build(configuration, SshArgs.WrapScriptCommand(script), keyPath), StandardInput: standardInput, Timeout: timeout ?? TimeSpan.FromSeconds(20)), cancellationToken);
    public async Task<bool> ProbeListeningPortAsync(int port, CancellationToken cancellationToken = default)
    {
        using var socket = new System.Net.Sockets.TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(1500));
        try { await socket.ConnectAsync("127.0.0.1", port, timeout.Token); return true; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch { return false; }
    }
    public IRunningProcess SpawnWatch(string script, CancellationToken cancellationToken = default)
        => runner.Start(new(executable, SshArgs.BuildWatch(configuration, script, keyPath)), cancellationToken);
    public IRunningProcess SpawnTunnel(TunnelSpec tunnel, CancellationToken cancellationToken = default)
        => runner.Start(new(executable, tunnel.Direction == TunnelDirection.Reverse
            ? SshArgs.BuildReverseForward(configuration, tunnel.VmPort, tunnel.LocalPort, keyPath)
            : SshArgs.BuildLocalForward(configuration, tunnel.LocalPort, tunnel.VmPort, keyPath, tunnel.BindHost, tunnel.ConnectAddress, tunnel.ConnectPort)), cancellationToken);
    public Task<bool> ProbePortAsync(int port, string bindHost = "127.0.0.1", CancellationToken cancellationToken = default) => probe.IsFreeAsync(port, bindHost, cancellationToken);
}
