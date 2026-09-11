using System.Text.Json.Nodes;
namespace Construct.Companion.Core.Abstractions;

// Checks that --selftest can only make through the running host or the native platform.
public interface ISelfTestPlatform
{
    Task<bool> IpcHealthAsync(CancellationToken cancellationToken);
    Task<string?> WebViewVersionAsync(CancellationToken cancellationToken);
    Task<bool> SshProbeAsync(JsonObject instance, CancellationToken cancellationToken);
    Task<HypervisorState> HypervisorAsync(JsonObject instance, CancellationToken cancellationToken);
}
