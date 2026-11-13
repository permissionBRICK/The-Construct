using System.Drawing.Imaging;
using System.Net.Http.Json;
using Construct.Agent.Http;
using Construct.Agent.Update;
using Construct.Companion.Windows;

namespace Construct.Agent.Windows;

/// <summary>
/// <c>construct-agent.exe [--background | construct://... | open | repair | version]</c>: the device agent
/// as the Windows tray app. One instance per user; a second start hands its request (open the window)
/// to the running one over the local API and ends.
/// </summary>
internal static class Program
{
    private const string MutexName = @"Local\ConstructAgent";

    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            ParentConsole.Attach();
            Console.Error.WriteLine("Construct needs Windows 10 version 1809 or later.");
            return 1;
        }

        var executable = Environment.ProcessPath!;
        var dataDirectory = AgentOptions.DefaultDataDirectory();
        var install = new AgentInstall(dataDirectory, "construct-agent.exe");
        var running = install.RunningVersion(executable);
        var upgrade = new WindowsUpgradePlatform(Path.Combine(dataDirectory, "legacy_known_hosts"));
        var platformOptions = new AgentOptions { DataDirectory = dataDirectory, UiDirectory = AgentOptions.DefaultUiDirectory(), Upgrade = upgrade };
        var background = args.Contains("--background");
        var command = args.FirstOrDefault(a => a is "repair" or "version" or "open" or "convert");

        switch (command)
        {
            case "version":
                ParentConsole.Attach();
                Console.WriteLine(platformOptions.Version);
                return 0;
            case "convert":
            {
                ParentConsole.Attach();
                var at = Array.IndexOf(args, "--from");
                if (!args.Contains("--from-legacy")) { Console.Error.WriteLine("Usage: construct-agent convert --from-legacy [--from DIR]"); return 2; }
                return Construct.Agent.Upgrade.ConvertCommand.RunAsync(platformOptions, _ => new DpapiProtection(), at >= 0 && at + 1 < args.Length ? args[at + 1] : null,
                    Console.Out, CancellationToken.None).GetAwaiter().GetResult();
            }
            case "repair":
                ParentConsole.Attach();
                return Repair.RunAsync(platformOptions, new AgentLaunch(executable, ["--background"]), Console.Out, CancellationToken.None).GetAwaiter().GetResult();
        }

        using var mutex = new Mutex(false, MutexName);
        var state = install.Read();
        // The version an update just switched to waits for the previous one to let go, and so does a background
        // start (the way back after a failed self-test starts the previous version while the failed one still exits).
        var primary = Acquire(mutex, background || state.Pending && running is not null && running == state.Current ? TimeSpan.FromSeconds(30) : TimeSpan.Zero);
        if (!primary)
        {
            if (!background) HandOver(platformOptions);
            return 0;
        }

        // The old Companion's single-instance mutex (SingleInstance.cs): held for this agent's life, so a
        // Companion started later ends at once and cannot write the old state the converter reads (13.2).
        upgrade.HoldCompanionMutex();
        try
        {
            return Run(executable, platformOptions, install, running, background);
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    private static int Run(string executable, AgentOptions defaults, AgentInstall install, string? running, bool background)
    {
        var log = Path.Combine(defaults.DataDirectory, "shell.log");
        void Log(Exception ex)
        {
            try
            {
                if (File.Exists(log) && new FileInfo(log).Length > 1_000_000) File.Delete(log);
                File.AppendAllText(log, $"{DateTimeOffset.UtcNow:O} {ex.GetType().Name}: {ex.Message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
        }

        PrivateFiles.EnsureDirectory(defaults.DataDirectory);
        var icon = Path.Combine(defaults.DataDirectory, "construct.png");
        WriteIcon(icon);
        var platform = new WindowsPlatform(executable, icon);
        var options = new AgentOptions { DataDirectory = defaults.DataDirectory, UiDirectory = defaults.UiDirectory, Platform = platform, Upgrade = defaults.Upgrade };

        AgentHost? host = null;
        var ok = AgentUpdater.StartupAsync(install, running, async () =>
        {
            host = AgentHost.Create(options, new DeviceAgent(options, _ => new DpapiProtection()));
            await host.StartAsync();
            await host.SelfTestAsync();
        }, platform).GetAwaiter().GetResult();
        if (!ok || host is null)
        {
            if (host is not null) Task.Run(() => host.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            return 1;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log(ex); };
        var origin = new Uri(host.BaseUrl);
        using var tray = new TrayShell(host.Agent, host.Device, origin, host.Token, Path.Combine(defaults.DataDirectory, "webview2"), Log);
        host.Device.Shell = tray;
        host.App.Lifetime.ApplicationStopping.Register(tray.Quit);
        if (!background) tray.Open("win", null);
        try
        {
            Application.Run(tray);
        }
        finally
        {
            host.Device.Shell = null;
            // The STA thread must not block on a context-capturing await: the host stops on the pool.
            Task.Run(() => host.DisposeAsync().AsTask()).GetAwaiter().GetResult();
        }

        return 0;
    }

    private static bool Acquire(Mutex mutex, TimeSpan wait)
    {
        try { return mutex.WaitOne(wait); }
        catch (AbandonedMutexException) { return true; } // the previous owner died holding it
    }

    /// <summary>A second start: the running agent opens its window (a <c>construct://</c> link from a toast does the same).</summary>
    private static void HandOver(AgentOptions options)
    {
        if (EndpointFile.Read(options.EndpointPath) is not { } endpoint) return;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.BaseUrl + "/api/shell/open") { Content = JsonContent.Create(new ShellOpenRequest("win")) };
            request.Headers.Authorization = new("Bearer", endpoint.Token);
            using var _ = http.Send(request);
        }
        catch (HttpRequestException)
        {
        }
    }

    private static void WriteIcon(string path)
    {
        if (File.Exists(path)) return;
        using var bitmap = new Bitmap(64, 64);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(Color.FromArgb(61, 211, 107));
            graphics.FillEllipse(brush, 4, 4, 56, 56);
        }

        bitmap.Save(path, ImageFormat.Png);
    }
}
