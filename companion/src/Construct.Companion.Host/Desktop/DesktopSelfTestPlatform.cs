using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.Runtime;
using Construct.Companion.Core.State;
namespace Construct.Companion.Host.Desktop;

public sealed class DesktopSelfTestPlatform(IStateFileSystem files,IProcessRunner runner,IHypervisorState hypervisor,
    IDesktopProcess desktop,Func<string?> webViewVersion,
    Func<JsonObject,CancellationToken,Task<HypervisorState>>? remoteState=null, Func<CancellationToken,Task<bool>>? healthProbe=null) : ISelfTestPlatform
{
    public async Task<bool> IpcHealthAsync(CancellationToken cancellationToken)
    {
        if (healthProbe is not null) return await healthProbe(cancellationToken);
        await using var server=await DesktopActivationServer.StartAsync(files,null,new DiagnosticUi(),"selftest",cancellationToken);
        using var client=new HttpClient(new HttpClientHandler { UseProxy=false,AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(5) };
        var health=await client.GetFromJsonAsync<Health>($"http://127.0.0.1:{server.Endpoint.Port}/v1/health",IpcJson.Options,cancellationToken);
        return health is {Ok:true,IpcApiVersion:1} && health.Pid==server.Endpoint.Pid;
    }
    public Task<string?> WebViewVersionAsync(CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(webViewVersion()); }
    public static ProcessInvocation ProbeInvocation(JsonObject instance,IFileSystem files,string ssh)
    {
        var configuration=new SshConfiguration(StateJson.String(instance["vmHost"]),StateJson.String(instance["hostAlias"]),
            KeyName:StateJson.String(instance["keyName"]),SshPort:Instances.CoercePort(instance["sshPort"]) ?? 22,ConnectTimeout:8);
        var path=Path.Combine(files.GetRoot(FileSystemRoot.UserProfile) ?? "",".ssh",configuration.KeyName);
        var args=SshArgs.Build(configuration,SshArgs.WrapScriptCommand(GuestScripts.Render("probe")),files.FileExists(path) ? path : null);
        // A diagnostic must not add new host keys to the user's known_hosts file.
        args=args.Select(a=>a=="StrictHostKeyChecking=accept-new" ? "StrictHostKeyChecking=yes" : a).ToArray();
        return new(ssh,args,Timeout:TimeSpan.FromSeconds(15));
    }
    public async Task<bool> SshProbeAsync(JsonObject instance,CancellationToken cancellationToken)
    {
        var ssh=SshExecutable.Resolve(files,(desktop.EnvironmentValue("PATH") ?? "").Split(Path.PathSeparator),desktop.EnvironmentValue("SystemRoot"),OperatingSystem.IsWindows());
        var result=await runner.RunAsync(ProbeInvocation(instance,files,ssh),cancellationToken);
        return result.Code==0 && !string.IsNullOrWhiteSpace(result.Stdout);
    }
    public async Task<HypervisorState> HypervisorAsync(JsonObject instance,CancellationToken cancellationToken)
    {
        if (StateJson.Text(instance["backend"])=="hyperv-remote") return remoteState is null ? HypervisorState.Unknown : await remoteState(instance,cancellationToken);
        var state=await VmPower.QueryLocalAsync(hypervisor,runner,StateJson.String(instance["vmName"]),cancellationToken);
        return Enum.TryParse<HypervisorState>(state,true,out var result) ? result : HypervisorState.Unknown;
    }
    private sealed class DiagnosticUi : IUiActivation
    {
        public Task ActivateAsync(IReadOnlyList<UiActivation> activations,CancellationToken cancellationToken=default) => throw new NotSupportedException();
        public Task QuitAsync(CancellationToken cancellationToken=default) => throw new NotSupportedException();
    }
}
