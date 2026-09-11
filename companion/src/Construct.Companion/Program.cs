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
        var files=new DesktopFileSystem(); var clock=new SystemClock(); var keys=new CurrentUserRegistry();
        var desktop=new DesktopProcess(); var launcher=new DesktopLauncher(desktop,files);
        var hypervisor=new HypervisorQuery(new CimVmQuery()); var capture=new WasapiAudioCapture(); var toast=new WinRtToastRaiser(keys);
        if (command.SelfTest)
        {
            var platform=new DesktopSelfTestPlatform(files,new RuntimeProcessRunner(),hypervisor,desktop,()=>CoreWebView2Environment.GetAvailableBrowserVersionString());
            var report=new SelfTest(files,platform,capture,toast).RunAsync(command.Instance).GetAwaiter().GetResult();
            Console.WriteLine(JsonSerializer.Serialize(report,IpcJson.Options)); return report.ExitCode;
        }
        var local=new HostState(files).LocalAppData;
        if (local is null) { Console.Error.WriteLine("No local application data path."); return 1; }
        var stateDirectory=Path.Combine(local,"The-Construct","companion");
        var settings=new SettingsStore(files,Path.Combine(stateDirectory,"settings.json"));
        var log=new RollingLog(files,clock,Path.Combine(stateDirectory,"logs"));
        try
        {
            var registry=InstanceRegistry.Load(files);
            if (registry.Synthesized && new HostState(files).ResolveScriptsDirectory(overrideDirectory:settings.Read().ScriptsDir) is null) registry.ByName.Clear();
            var hosts=registry.List().Select(i=>StateJson.Text(i["service"]?["url"])).Where(u=>u is not null).Select(u=>RemoteHost.HostSlug(u!)).ToArray();
            var plan=Activation.Resolve(command,registry.ByName.Keys.ToArray(),hosts);
            using var instance=new SingleInstance();
            if (!instance.IsPrimary)
            {
                var published=Path.Combine(stateDirectory,"endpoint.json");
                var endpoints=new[] { published,Path.Combine(stateDirectory,"ui-endpoint.json") };
                using var client=new HttpClient(new HttpClientHandler { UseProxy=false,AllowAutoRedirect=false }) { Timeout=TimeSpan.FromSeconds(2) };
                new ActivationClient(files,client,clock).SendAsync(endpoints,plan,command.Quit).GetAwaiter().GetResult(); return 0;
            }
            if (command.Quit) return 0;
            ApplicationConfiguration.Initialize();
            var registration=new DesktopRegistration(keys,Application.ExecutablePath,Application.ExecutablePath);
            // S3 replaces this single factory call with its dispatcher-backed sink.
            var bus=new RuntimeMessageBus();
            TrayContext? context=null;
            var sink=new InProcessMessageSink(bus,async (scope,message,cancellationToken)=>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (scope.Length>0 && !scope.StartsWith("host:",StringComparison.Ordinal) && !registry.ByName.ContainsKey(scope)) throw new ArgumentException("Instance is not registered.");
                if (message.TryGetProperty("type",out var type) && type.GetString()=="hostadmin.ready")
                    bus.Publish(scope,new {type="hostadmin.state",state=new {mode="unavailable",message="Runtime services are not connected in this build."}});
                else if (type.ValueKind==JsonValueKind.String && type.GetString()=="ready")
                    bus.Publish(scope,new { type="state",state=new { instance=scope,online=false,vmState="unknown",connectedInstance=(string?)null } });
                else
                {
                    bus.Publish(scope,new { type="lifecyclePrepared",ok=false,id=message.TryGetProperty("id",out var id) ? id.GetString() : "",error="Runtime services are not connected in this build." });
                    if (context is not null) await context.ShowRuntimeUnavailableAsync(cancellationToken);
                }
            });
            using var tray=new TrayContext(files,sink,settings,launcher,registration,log,clock,capture,stateDirectory,version,plan);
            context=tray;
            var server=DesktopActivationServer.StartAsync(files,Path.Combine(stateDirectory,"ui-endpoint.json"),tray,version,messages:sink).GetAwaiter().GetResult();
            Application.ThreadException+=(_,e)=>log.Write(DesktopLogEvent.UnhandledException,e.Exception);
            AppDomain.CurrentDomain.UnhandledException+=(_,e)=>log.Write(DesktopLogEvent.UnhandledException,e.ExceptionObject as Exception);
            log.Write(DesktopLogEvent.Started);
            try { Application.Run(tray); }
            finally { server.DisposeAsync().AsTask().GetAwaiter().GetResult(); log.Write(DesktopLogEvent.Stopped); }
            return 0;
        }
        catch (ArgumentException) { Console.Error.WriteLine("Invalid Construct activation."); return 2; }
        catch (Exception error) { try { log.Write(DesktopLogEvent.UnhandledException,error); } catch (IOException) { } Console.Error.WriteLine("Construct Companion could not start."); return 1; }
    }
}
