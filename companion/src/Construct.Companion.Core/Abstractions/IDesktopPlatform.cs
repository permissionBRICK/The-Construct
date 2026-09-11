namespace Construct.Companion.Core.Abstractions;

public interface IDataProtection
{
    byte[] Protect(ReadOnlySpan<byte> plaintext);
    byte[] Unprotect(ReadOnlySpan<byte> ciphertext);
}
public sealed record CimVmState(ushort EnabledState);
public interface ICimVmQuery
{
    Task<CimVmState?> QueryAsync(string vmName, CancellationToken cancellationToken = default);
}
// Unlike IProcessRunner, these invocations intentionally outlive the caller.
public interface IDesktopProcess
{
    Task OpenAsync(string target, CancellationToken cancellationToken = default);
    Task StartAsync(ProcessInvocation invocation, CancellationToken cancellationToken = default);
    string? FindOnPath(string executable);
    string? EnvironmentValue(string name);
}
public interface IUiActivation
{
    Task ActivateAsync(IReadOnlyList<Construct.Companion.Core.Ipc.UiActivation> activations, CancellationToken cancellationToken = default);
    Task QuitAsync(CancellationToken cancellationToken = default);
}
public interface ISelfTestPlatform
{
    Task<bool> IpcHealthAsync(CancellationToken cancellationToken);
    Task<string?> WebViewVersionAsync(CancellationToken cancellationToken);
    Task<bool> SshProbeAsync(System.Text.Json.Nodes.JsonObject instance, CancellationToken cancellationToken);
    Task<HypervisorState> HypervisorAsync(System.Text.Json.Nodes.JsonObject instance, CancellationToken cancellationToken);
}
