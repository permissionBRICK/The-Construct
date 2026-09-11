using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Fakes;

public sealed class FakeSelfTestPlatform : ISelfTestPlatform
{
    public bool Health { get; set; }=true;
    public string? WebViewVersion { get; set; }="fixture";
    public bool Ssh { get; set; }
    public HypervisorState Hypervisor { get; set; }=HypervisorState.Off;
    public List<string> Calls { get; }=[];
    public Task<bool> IpcHealthAsync(CancellationToken cancellationToken) { Calls.Add("health"); return Task.FromResult(Health); }
    public Task<string?> WebViewVersionAsync(CancellationToken cancellationToken) { Calls.Add("webview"); return Task.FromResult(WebViewVersion); }
    public Task<bool> SshProbeAsync(JsonObject instance,CancellationToken cancellationToken) { Calls.Add("ssh:"+instance["name"]); return Task.FromResult(Ssh); }
    public Task<HypervisorState> HypervisorAsync(JsonObject instance,CancellationToken cancellationToken) { Calls.Add("hypervisor:"+instance["name"]); return Task.FromResult(Hypervisor); }
}
