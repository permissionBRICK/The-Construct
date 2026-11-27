using System.Drawing.Imaging;
using System.Net.Http.Json;
using Construct.Agent.Http;
using Construct.Agent.Update;
using Construct.Companion.Windows;

namespace Construct.Agent.Windows;

/// <summary>
/// <c>construct-agent.exe [--background [--open pop,win] | construct://... | open | repair | version]</c>: the device
/// agent as the Windows tray app. One instance per user; a second start hands its request (open the window)
/// to the running one over the local API and ends. <c>--open</c> names the surfaces to show at start: a self-update
/// opens again what was open. The agent runs from <c>app\</c> of its folder; a copy started anywhere else in the folder
/// (an update in <c>next\</c>, the way back in <c>previous\</c>, an old <c>versions\&lt;v&gt;\</c>) moves itself there
/// first (<see cref="AgentUpdater.InstallStartedCopyAsync"/>). Every start and stop is a line in <c>shell.log</c>.
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
        var running = install.RunningVersion(executable, AgentOptions.CurrentVersion);
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

        var shellLog = Path.Combine(dataDirectory, "shell.log");
        // Never app\ as the working directory: a program the agent starts (an ssh forward, T3 Desktop) inherits it and
        // would hold the folder, which an update renames.
        PrivateFiles.EnsureDirectory(dataDirectory);
        Directory.SetCurrentDirectory(dataDirectory);
        using var mutex = new Mutex(false, MutexName);
        var state = install.Read();
        var staged = install.PlaceOf(executable) == AgentInstall.Place.Staged;
        // The version an update just switched to waits for the previous one to let go, and so does a background
        // start (the way back after a failed self-test starts the previous version while the failed one still exits).
        // A copy to move into app\ waits longer: the agent there has to be gone before its folder is swapped.
        var wait = staged ? TimeSpan.FromMinutes(2)
            : background || state.Pending && running is not null && running == state.Current ? TimeSpan.FromSeconds(30) : TimeSpan.Zero;
        var primary = Acquire(mutex, wait);
        if (!primary)
        {
            if (staged) ShellLog.Note(shellLog, $"install: {executable} was not moved into app\\: the agent there did not exit within two minutes");
            else if (!background) HandOver(platformOptions);
            return 0;
        }

        if (staged)
        {
            var icon = Path.Combine(dataDirectory, "construct.png");
            PrivateFiles.EnsureDirectory(dataDirectory);
            WriteIcon(icon);
            // A start the user made (the setup's, a double click) opens the window; LaunchAsync starts the agent in the background.
            var open = background ? Surfaces(args) : ["win", .. Surfaces(args).Where(s => s != "win")];
            var started = AgentUpdater.InstallStartedCopyAsync(install, executable, open, new WindowsPlatform(install.AppExecutable, icon), null,
                line => ShellLog.Note(shellLog, "install: " + line)).GetAwaiter().GetResult();
            if (started)
            {
                mutex.ReleaseMutex();
                return 0;
            }

            // Nothing in app\ to start (the move of a first version failed): this copy runs as the agent, as before app\ existed.
        }

        // The old Companion's single-instance mutex (SingleInstance.cs): held for this agent's life, so a
        // Companion started later ends at once and cannot write the old state the converter reads (13.2).
        upgrade.HoldCompanionMutex();
        try
        {
            return Run(executable, platformOptions, install, running, background, Surfaces(args));
        }
        finally
        {
            mutex.ReleaseMutex();
        }
    }

    /// <summary>The surfaces <c>--open</c> names (<c>pop</c>, <c>win</c>), in order.</summary>
    internal static string[] Surfaces(string[] args)
    {
        var at = Array.IndexOf(args, "--open");
        return at >= 0 && at + 1 < args.Length
            ? args[at + 1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(s => s is "pop" or "win").Distinct().ToArray()
            : [];
    }

    private static int Run(string executable, AgentOptions defaults, AgentInstall install, string? running, bool background, string[] surfaces)
    {
        var log = Path.Combine(defaults.DataDirectory, "shell.log");
        void Log(Exception ex) => ShellLog.Note(log, $"0x{ex.HResult:X8} {ex}");
        ShellLog.Note(log, $"start {defaults.Version} from {executable}{(background ? " --background" : "")}{(surfaces.Length > 0 ? " --open " + string.Join(',', surfaces) : "")}");

        PrivateFiles.EnsureDirectory(defaults.DataDirectory);
        var icon = Path.Combine(defaults.DataDirectory, "construct.png");
        WriteIcon(icon);
        var platform = new WindowsPlatform(executable, icon);
        var options = new AgentOptions { DataDirectory = defaults.DataDirectory, UiDirectory = defaults.UiDirectory, Platform = platform, Upgrade = defaults.Upgrade };

        // Read before the start confirms this version and forgets the previous one: the tray's first place comes from it.
        var before = install.Read().Previous is { } previousVersion
            ? Path.Combine(install.LegacyVersionsDirectory, AgentInstall.DirectoryName(previousVersion), install.ExecutableName) : null;
        AgentHost? host = null;
        var ok = AgentUpdater.StartupAsync(install, running, async () =>
        {
            host = AgentHost.Create(options, new DeviceAgent(options, _ => new DpapiProtection()));
            await host.StartAsync();
            await host.SelfTestAsync();
        }, platform, confirmed: directory => _ = VsCodeExtension.InstallAsync(directory), log: line => ShellLog.Note(log, line)).GetAwaiter().GetResult();
        if (!ok || host is null)
        {
            if (host is not null) Task.Run(() => host.DisposeAsync().AsTask()).GetAwaiter().GetResult();
            ShellLog.Note(log, "exit: the self-test failed");
            return 1;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { if (e.ExceptionObject is Exception ex) Log(ex); };
        var origin = new Uri(host.BaseUrl);
        using var tray = new TrayShell(host.Agent, host.Device, origin, host.Token, Path.Combine(defaults.DataDirectory, "webview2"), Log);
        host.Device.Shell = tray;
        // The tray icon's place is chosen once, on the first start from app\: the place an older install had, or the
        // taskbar; after that it stays wherever the user puts it (Windows keeps it per path, and app\ never changes).
        var placed = Path.Combine(defaults.DataDirectory, TrayIconCarryOver.DoneFile);
        if (running is not null && !File.Exists(placed))
        {
            tray.KeepTrayPlace(() =>
            {
                var outcome = TrayIconCarryOver.Apply(new NotifyIconSettingsRegistry(), executable, defaults.DataDirectory, before);
                if (outcome != TrayIconCarryOver.Outcome.NotYet) File.WriteAllText(placed, "");
                return outcome;
            });
        }

        string? sessionEnd = null;
        Microsoft.Win32.SessionEndingEventHandler ending = (_, e) =>
        {
            sessionEnd = $"Windows ended the session ({e.Reason})";
            ShellLog.Note(log, "exit: " + sessionEnd);
        };
        Microsoft.Win32.SystemEvents.SessionEnding += ending;
        host.App.Lifetime.ApplicationStopping.Register(tray.Quit);
        if (!background) tray.Open("win", null);
        foreach (var surface in surfaces) tray.Open(surface, null);
        try
        {
            Application.Run(tray);
        }
        finally
        {
            Microsoft.Win32.SystemEvents.SessionEnding -= ending;
            if (sessionEnd is null)
                ShellLog.Note(log, "exit: " + (host.StopReason ?? tray.QuitReason ?? "the tray's message loop ended without a recorded reason"));
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
