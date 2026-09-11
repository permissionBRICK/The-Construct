using Construct.Companion.Core.Abstractions;
using System.Text.Json.Nodes;
using System.Text.Json;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Runtime;
using Microsoft.Extensions.DependencyInjection;
namespace Construct.Companion.Host.Composition;

// The Linux stand-in for the Windows app's seam registrations; the extension's integration test
// spawns the Host with --fake and drives this composition over HTTP.
public static class FakeComposition
{
    public static IServiceCollection AddCompanionFakes(this IServiceCollection services, bool integrationRegistry = false, bool remoteOnly = false)
    {
        var clock = new SystemClock(); var files = new FakeFileSystem(clock); files.Roots[FileSystemRoot.LocalAppData] = "/fake/local"; files.Roots[FileSystemRoot.Temp] = "/fake/temp";
        files.CreateDirectory("/fake/scripts"); files.WriteFileAtomic("/fake/scripts/Auto-Install.ps1", "param($InstanceName,$ConfigBranch)"u8);
        files.WriteFileAtomic("/fake/scripts/Provision-AgentVM.ps1", "param($InstanceName,$ConfigBranch)"u8);
        files.WriteFileAtomic("/fake/local/The-Construct/instances.json", "{\"version\":1,\"defaultInstance\":\"agent-vm\",\"instances\":{\"agent-vm\":{\"scriptsDir\":\"/fake/scripts\"}}}"u8);
        files.Roots[FileSystemRoot.InstallDirectory] = "/fake/app";
        if (integrationRegistry)
        {
            var entries = new JsonObject();
            if (!remoteOnly) entries["agent-vm"] = new JsonObject { ["scriptsDir"] = "/fake/scripts" };
            else entries["agent-vm"] = null; // suppress the legacy synthesized default
            entries["remote-vm"] = new JsonObject { ["backend"] = "hyperv-remote", ["vmName"] = "remote-vm", ["sshHost"] = "guest.host.example", ["scriptsDir"] = "/fake/scripts",
                ["service"] = new JsonObject { ["url"] = "https://host.example:7462", ["auth"] = "negotiate" } };
            files.WriteFileAtomic("/fake/local/The-Construct/instances.json", JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["version"] = 1, ["defaultInstance"] = remoteOnly ? "remote-vm" : "agent-vm", ["instances"] = entries }));
        }
        services.AddSingleton<IStateFileSystem>(files).AddSingleton<IFileSystem>(files);
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IProcessRunner>(new FakeProcessRunner { Handler = invocation => new ProcessResult(invocation.FileName == "git" ? 1 : 0) }); services.AddSingleton<IPrompts, FakePrompts>();
        services.AddSingleton<ILauncher, FakeLauncher>(); services.AddSingleton<ICompanionDesktop, FakeCompanionDesktop>();
        services.AddSingleton<IHostConversionCrypto, FakeHostConversionCrypto>();
        services.AddSingleton<ITokenStore, FakeTokenStore>(); services.AddSingleton<IRemoteApi, FakeRemoteApi>();
        services.AddSingleton<IHypervisorState, FakeHypervisorState>(); services.AddSingleton<IAudioCapture, FakeAudioCapture>();
        services.AddSingleton<IToastRaiser, FakeToastRaiser>(); services.AddSingleton<IAudioServerFactory, FakeAudioServerFactory>();
        services.AddSingleton<IPortProbe, FakePortProbe>(); services.AddSingleton<IInstanceConnections, FakeInstanceConnections>();
        services.AddSingleton<IClipboard, FakeClipboard>(); services.AddSingleton<IProcessLiveness, FakeProcessLiveness>();
        services.AddSingleton<IUpdateSource, FakeUpdateSource>(); return services;
    }
}
