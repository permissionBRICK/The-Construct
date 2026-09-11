using System.Reflection;
using System.Text.Json;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
using RemoteHost = Construct.Companion.Core.Remote.RemoteHost;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Desktop;
using Construct.Companion.Host.Runtime;
using Construct.Companion.Windows;
using Microsoft.Web.WebView2.Core;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Core.Audio;
using Microsoft.Extensions.DependencyInjection;
namespace Construct.Companion;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,17763)) return 1;
        DesktopDisplay.AttachParentConsole();
        CommandLine command;
        try { command=CommandLine.Parse(args); }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
        var version=typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        if (command.Version) { Console.WriteLine(version); return 0; }
        var files=new HostFileSystem(); var clock=new SystemClock(); var keys=new CurrentUserRegistry();
        var desktop=new DesktopProcess(); var launcher=new DesktopLauncher(desktop,files);
        var hypervisor=new HypervisorQuery(new CimVmQuery()); var capture=new WasapiAudioCapture(); var toast=new WinRtToastRaiser(keys);
        var local=new HostState(files).LocalAppData;
        if (local is null) { Console.Error.WriteLine("No local application data path."); return 1; }
        var stateDirectory=Path.Combine(local,"The-Construct","companion");
        var settings=new SettingsStore(files,Path.Combine(stateDirectory,"settings.json"));
        var log=new RollingLog(files,clock,Path.Combine(stateDirectory,"logs"));
        Microsoft.AspNetCore.Builder.WebApplication BuildHost(DesktopHostBridge bridge, bool runtimeJobs = true) => IpcServer.Build(services =>
            {
                services.AddSingleton<IStateFileSystem>(files).AddSingleton<IFileSystem>(files);
                services.AddSingleton<IClock>(clock).AddSingleton(settings);
                services.AddSingleton<ILauncher>(launcher).AddSingleton<IHypervisorState>(hypervisor);
                services.AddSingleton<IAudioCapture>(capture).AddSingleton<IToastRaiser>(toast);
                services.AddSingleton(new SharedAudioCapture(capture, selectDevice:()=>settings.Read().MicDevice));
                services.AddSingleton<ITokenStore>(new ProtectedTokenStore(files,new DpapiProtection(),Path.Combine(local,"The-Construct","remote")));
                services.AddSingleton<IRemoteApi,HttpRemoteApi>().AddSingleton<IUpdateSource,HttpUpdateSource>();
                services.AddSingleton<IPrompts>(bridge.Prompts).AddSingleton<IClipboard>(bridge).AddSingleton<ICompanionDesktop>(bridge);
                services.AddCompanionHost(runtimeJobs);
            },new(version, PublishEndpoint:runtimeJobs));
        if (command.SelfTest)
        {
            using var diagnosticBridge=new DesktopHostBridge(diagnostic:true);
            var platform=new DesktopSelfTestPlatform(files,new RuntimeProcessRunner(),hypervisor,()=>CoreWebView2Environment.GetAvailableBrowserVersionString(), async ct =>
            {
                await using var probeHost=BuildHost(diagnosticBridge, runtimeJobs:false);
                await probeHost.StartAsync(ct);
                try
                {
                    using var http=new HttpClient(new HttpClientHandler { UseProxy=false,AllowAutoRedirect=false });
                    var response=await http.GetAsync(probeHost.Urls.Single()+"/v1/health",ct);
                    using var body=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    return response.IsSuccessStatusCode && body.RootElement.GetProperty("ok").GetBoolean();
                }
                finally { await probeHost.StopAsync(ct); }
            }, async (definition,ct)=>
            {
                var service=definition["service"]!;
                var tokens=new ProtectedTokenStore(files,new DpapiProtection(),Path.Combine(new HostState(files).LocalAppData!,"The-Construct","remote"));
                var client=new Core.Remote.RemoteHostClient(new HttpRemoteApi(),files,tokens,StateJson.String(service["url"]),StateJson.Text(service["auth"])=="token" ? RemoteAuthentication.Token : RemoteAuthentication.Negotiate);
                var state=await Core.Drivers.VmPower.QueryRemoteAsync(client,StateJson.String(definition["vmName"]),ct);
                return Enum.TryParse<HypervisorState>(state,true,out var result) ? result : HypervisorState.Unknown;
            });
            var report=Task.Run(()=>new SelfTest(files,platform,capture,toast).RunAsync(command.Instance)).GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(report,IpcJson.Options)); return report.ExitCode;
        }
        try
        {
            var registry=InstanceRegistry.Load(files);
            if (registry.Synthesized && new HostState(files).ResolveScriptsDirectory(overrideDirectory:settings.Read().ScriptsDir) is null) registry.ByName.Clear();
            var hosts=registry.List().Select(i=>StateJson.Text(i["service"]?["url"])).Where(u=>u is not null).Concat((StateJson.ReadObject(files,Path.Combine(stateDirectory,"hosts.json"))?["hosts"] as System.Text.Json.Nodes.JsonArray ?? []).Select(h=>StateJson.Text(h?["url"])).Where(u=>u is not null)).Select(u=>RemoteHost.HostSlug(u!)).Distinct().ToArray();
            var plan=Activation.Resolve(command,registry.ByName.Keys.ToArray(),hosts);
            using var instance=new SingleInstance();
            if (!instance.IsPrimary)
            {
                using var client=new HttpClient(new HttpClientHandler { UseProxy=false,AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(2) };
                new ActivationClient(files,client,clock).SendAsync(Path.Combine(stateDirectory,"endpoint.json"),plan,command.Quit).GetAwaiter().GetResult(); return 0;
            }
            if (command.Quit) return 0;
            ApplicationConfiguration.Initialize();
            var registration=new DesktopRegistration(keys,Application.ExecutablePath,Application.ExecutablePath);
            using var bridge=new DesktopHostBridge();
            var server=BuildHost(bridge);
            using var tray=new TrayContext(files,server.Services.GetRequiredService<IMessageSink>(),settings,launcher,registration,log,clock,capture,stateDirectory,version,plan);
            bridge.Tray=tray;
            server.Lifetime.ApplicationStopping.Register(()=> { _=tray.QuitAsync(); });
            Task.Run(()=>server.StartAsync()).GetAwaiter().GetResult();
            Application.ThreadException+=(_,e)=>log.Write(DesktopLogEvent.UnhandledException,e.Exception);
            AppDomain.CurrentDomain.UnhandledException+=(_,e)=>log.Write(DesktopLogEvent.UnhandledException,e.ExceptionObject as Exception);
            log.Write(DesktopLogEvent.Started);
            try { Application.Run(tray); }
            finally { Task.Run(async ()=> { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult(); log.Write(DesktopLogEvent.Stopped); }
            return 0;
        }
        catch (ArgumentException) { Console.Error.WriteLine("Invalid Construct activation."); return 2; }
        catch (Exception error) { try { log.Write(DesktopLogEvent.UnhandledException,error); } catch (IOException) { } Console.Error.WriteLine("Construct Companion could not start."); return 1; }
    }
}
