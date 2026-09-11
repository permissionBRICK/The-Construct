using System.Reflection;
using System.Text.Json;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Audio;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Drivers;
using Construct.Companion.Core.Ipc;
using Construct.Companion.Core.State;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Desktop;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Host.Runtime;
using Construct.Companion.Windows;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using RemoteHost = Construct.Companion.Core.Remote.RemoteHost;
using RemoteHostClient = Construct.Companion.Core.Remote.RemoteHostClient;
namespace Construct.Companion;

// Composition root: the native seams go into AddCompanionHost; the tray and windows are clients
// of the same dispatcher the HTTP routes use.
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)) { Console.Error.WriteLine("Construct Companion needs Windows 10 version 1809 or later."); return 1; }
        ParentConsole.Attach();
        CommandLine command;
        try { command = CommandLine.Parse(args); }
        catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }
        var version = typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        if (command.Version) { Console.WriteLine(version); return 0; }
        var files = new HostFileSystem();
        if (new HostState(files).LocalAppData is not { } local) { Console.Error.WriteLine("No local application data path."); return 1; }
        var platform = new Platform(files, local, version);
        return command.SelfTest ? RunSelfTest(platform, command) : RunTray(platform, command);
    }

    private static int RunSelfTest(Platform platform, CommandLine command)
    {
        using var bridge = new DesktopHostBridge(diagnostic: true);
        var checks = new DesktopSelfTestPlatform(platform.Files, new RuntimeProcessRunner(), platform.Hypervisor,
            () => CoreWebView2Environment.GetAvailableBrowserVersionString(), ct => ProbeIpcHealthAsync(platform, bridge, ct), (definition, ct) => QueryRemoteStateAsync(platform, definition, ct));
        var report = Task.Run(() => new SelfTest(platform.Files, checks, platform.Capture, platform.Toast).RunAsync(command.Instance)).GetAwaiter().GetResult();
        Console.WriteLine(JsonSerializer.Serialize(report, IpcJson.Options));
        return report.ExitCode;
    }
    // A non-publishing host: the selftest must never claim runtime ownership from a running Companion.
    private static async Task<bool> ProbeIpcHealthAsync(Platform platform, DesktopHostBridge bridge, CancellationToken ct)
    {
        await using var probe = BuildHost(platform, bridge, runtimeJobs: false);
        await probe.StartAsync(ct);
        try
        {
            using var http = Platform.LoopbackClient(TimeSpan.FromSeconds(5));
            var response = await http.GetAsync(probe.Urls.Single() + "/v1/health", ct);
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return response.IsSuccessStatusCode && body.RootElement.GetProperty("ok").GetBoolean();
        }
        finally { await probe.StopAsync(ct); }
    }
    private static async Task<HypervisorState> QueryRemoteStateAsync(Platform platform, System.Text.Json.Nodes.JsonObject definition, CancellationToken ct)
    {
        var service = definition["service"]!;
        var client = new RemoteHostClient(new HttpRemoteApi(), platform.Files, platform.Tokens, StateJson.String(service["url"]),
            StateJson.Text(service["auth"]) == "token" ? RemoteAuthentication.Token : RemoteAuthentication.Negotiate);
        var state = await VmPower.QueryRemoteAsync(client, StateJson.String(definition["vmName"]), ct);
        return Enum.TryParse<HypervisorState>(state, true, out var result) ? result : HypervisorState.Unknown;
    }

    private static int RunTray(Platform platform, CommandLine command)
    {
        try
        {
            var registry = InstanceRegistry.LoadUsable(platform.Files, new IpcSettings(platform.Files, new IpcEvents()).Read().ScriptsDir);
            var plan = Activation.Resolve(command, registry.ByName.Keys.ToArray(), RemoteHost.KnownHostSlugs(registry, RemoteHost.EnrolledHosts(platform.Files, platform.StateDirectory)));
            using var instance = new SingleInstance();
            if (!instance.IsPrimary)
            {
                // Hand the command line to the running Companion over its authenticated loopback routes.
                using var client = Platform.LoopbackClient(TimeSpan.FromSeconds(2));
                new ActivationClient(platform.Files, client, platform.Clock).SendAsync(Path.Combine(platform.StateDirectory, "endpoint.json"), plan, command.Quit).GetAwaiter().GetResult();
                return 0;
            }
            if (command.Quit) return 0;
            ApplicationConfiguration.Initialize();
            using var bridge = new DesktopHostBridge();
            var server = BuildHost(platform, bridge);
            using var tray = new TrayContext(platform, server.Services.GetRequiredService<IMessageSink>(), server.Services.GetRequiredService<IpcSettings>(), bridge.Prompts, plan);
            bridge.Tray = tray;
            server.Lifetime.ApplicationStopping.Register(() => { _ = tray.QuitAsync(); });
            // The STA thread must not block on a context-capturing await: hop to the pool for the async host calls.
            Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
            Application.ThreadException += (_, e) => platform.Log.Write(DesktopLogEvent.UnhandledException, e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (_, e) => platform.Log.Write(DesktopLogEvent.UnhandledException, e.ExceptionObject as Exception);
            platform.Log.Write(DesktopLogEvent.Started);
            try { Application.Run(tray); }
            finally { Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult(); platform.Log.Write(DesktopLogEvent.Stopped); }
            return 0;
        }
        catch (ArgumentException) { Console.Error.WriteLine("Invalid Construct activation."); return 2; }
        catch (Exception error)
        {
            platform.Log.Write(DesktopLogEvent.UnhandledException, error);
            Console.Error.WriteLine("Construct Companion could not start.");
            return 1;
        }
    }
    private static WebApplication BuildHost(Platform platform, DesktopHostBridge bridge, bool runtimeJobs = true) => IpcServer.Build(services =>
    {
        services.AddSingleton<IStateFileSystem>(platform.Files).AddSingleton<IFileSystem>(platform.Files);
        services.AddSingleton<IClock>(platform.Clock);
        services.AddSingleton<ILauncher>(platform.Launcher).AddSingleton<IHypervisorState>(platform.Hypervisor);
        services.AddSingleton<IAudioCapture>(platform.Capture).AddSingleton<IToastRaiser>(platform.Toast);
        services.AddSingleton(p => new SharedAudioCapture(platform.Capture, selectDevice: () => p.GetRequiredService<IpcSettings>().Read().MicDevice));
        services.AddSingleton<ITokenStore>(platform.Tokens);
        services.AddSingleton<IRemoteApi, HttpRemoteApi>().AddSingleton<IUpdateSource, HttpUpdateSource>();
        services.AddSingleton<IPrompts>(bridge.Prompts).AddSingleton<IClipboard>(bridge).AddSingleton<ICompanionDesktop>(bridge);
        services.AddCompanionHost(runtimeJobs);
    }, new(platform.Version, PublishEndpoint: runtimeJobs));
}
