using System.Diagnostics;
using Construct.Agent;
using Construct.Agent.Device;
using Construct.Agent.Http;
using Construct.Agent.Update;
using Microsoft.Extensions.Hosting;

// construct-agent [run|open|repair|autostart on|off|version|convert --from-legacy [--from DIR]] [--data-dir DIR] [--ui DIR]
//   run        the agent in the foreground (the default)
//   open       starts the agent when it is not running and opens the UI in the default browser
//   repair     reports what it finds and restarts the agent; works with no engine reachable (5.3)
//   autostart  installs (on) or removes (off) the systemd user unit that starts the agent with the session
//   version    prints the version
//   convert    the one-time conversion of the old client's state (13.1), through a running agent or in this process
const string usage = "Usage: construct-agent [run|open|repair|autostart on|off|version|convert --from-legacy [--from DIR]] [--data-dir DIR] [--ui DIR]";
var command = "run";
string? dataDirectory = null, ui = null, autostart = null, from = null;
var fromLegacy = false;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data-dir" when i + 1 < args.Length: dataDirectory = args[++i]; break;
        case "--ui" when i + 1 < args.Length: ui = args[++i]; break;
        case "run" or "open" or "repair" or "version" or "convert": command = args[i]; break;
        case "--from-legacy": fromLegacy = true; break;
        case "--from" when i + 1 < args.Length: from = args[++i]; break;
        case "autostart" when i + 1 < args.Length && args[i + 1] is "on" or "off": command = "autostart"; autostart = args[++i]; break;
        case "-h" or "--help":
            Console.WriteLine(usage);
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'. {usage}");
            return 2;
    }
}

var platform = new LinuxPlatform();
var options = new AgentOptions { DataDirectory = dataDirectory ?? AgentOptions.DefaultDataDirectory(), UiDirectory = ui ?? AgentOptions.DefaultUiDirectory(), Platform = platform };
var launch = AgentLaunch.Current(dataDirectory, ui);
var install = new AgentInstall(options.DataDirectory, "construct-agent");

switch (command)
{
    case "convert":
        if (!fromLegacy)
        {
            Console.Error.WriteLine(usage);
            return 2;
        }

        return await Construct.Agent.Upgrade.ConvertCommand.RunAsync(options, null, from, Console.Out, CancellationToken.None);

    case "open":
    {
        var endpoint = await Repair.RunningAsync(options, CancellationToken.None) ?? await Repair.StartAsync(options, launch, CancellationToken.None);
        if (endpoint is null)
        {
            Console.Error.WriteLine("The agent did not start; run `construct-agent repair`.");
            return 1;
        }

        // The token travels in the fragment, which the browser never sends to a server, and through a
        // 0600 page in the agent's directory, so it never shows in a process list as an argument.
        var url = $"{endpoint.BaseUrl}/#agent-token={endpoint.Token}";
        var page = Path.Combine(options.DataDirectory, "open.html");
        PrivateFiles.Write(page, System.Text.Encoding.UTF8.GetBytes(
            $"<!doctype html><meta charset=\"utf-8\"><title>Construct</title><script>location.replace({System.Text.Json.JsonSerializer.Serialize(url)})</script>"));
        try
        {
            using var browser = Process.Start(new ProcessStartInfo(OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open", new Uri(page).AbsoluteUri) { UseShellExecute = false });
            Console.WriteLine($"Opened {endpoint.BaseUrl}/ in the browser.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            Console.WriteLine($"No browser opener found. Open {new Uri(page).AbsoluteUri} in a browser.");
        }

        return 0;
    }

    case "repair":
        return await Repair.RunAsync(options, launch, Console.Out, CancellationToken.None);

    case "version":
        Console.WriteLine(options.Version);
        return 0;

    case "autostart":
    {
        if (platform.StartWithOs is null)
        {
            Console.Error.WriteLine("systemctl was not found: starting with the session is not available here.");
            return 1;
        }

        // An installed agent's unit names app/, whichever copy turned it on.
        var place = install.PlaceOf(Environment.ProcessPath);
        platform.SetStartWithOs(autostart == "on", place == AgentInstall.Place.Elsewhere ? Environment.ProcessPath! : install.AppExecutable);
        Console.WriteLine(autostart == "on" ? $"Installed {platform.UnitPath}; the agent starts with the session." : "Removed the user unit.");
        return 0;
    }

    default:
    {
        var shellLog = Path.Combine(options.DataDirectory, "shell.log");
        void Note(string line)
        {
            Console.WriteLine(line);
            ShellLog.Note(shellLog, line);
        }

        async Task WaitForPrevious()
        {
            for (var i = 0; i < 120 && await Repair.RunningAsync(options, CancellationToken.None) is not null; i++) await Task.Delay(250);
        }

        // A copy in next/, previous/ or an old versions/<v>/ never runs as the agent: it moves into app/ and starts it there
        // (under the systemd unit the unit is pointed at app/ and restarted, which also ends this process).
        if (install.PlaceOf(Environment.ProcessPath) == AgentInstall.Place.Staged)
        {
            PrivateFiles.EnsureDirectory(options.DataDirectory);
            if (await AgentUpdater.InstallStartedCopyAsync(install, Environment.ProcessPath!, [], platform, WaitForPrevious, line => Note("install: " + line)))
                return 0;
            // Nothing in app/ to start (the move of a first version failed): this copy runs as the agent, as before app/ existed.
        }

        Note($"start {options.Version} from {Environment.ProcessPath}");
        // A version the last update switched to proves itself here; when it does not, the previous one starts again (12.2).
        AgentHost? started = null;
        var ok = await AgentUpdater.StartupAsync(install, install.RunningVersion(Environment.ProcessPath, options.Version), async () =>
        {
            started = AgentHost.Create(options);
            await started.StartAsync();
            await started.SelfTestAsync();
        }, platform, waitForPrevious: WaitForPrevious, confirmed: directory => _ = VsCodeExtension.InstallAsync(directory), log: Note);
        if (!ok)
        {
            if (started is not null) await started.DisposeAsync();
            Note("exit: the self-test failed; the previous version was started again");
            return 1;
        }

        await using var host = started!;
        Console.WriteLine($"construct-agent {options.Version} on {host.BaseUrl} (device {host.Agent.DeviceId}); token in {options.EndpointPath}");
        // Not disposed: ProcessExit fires after Main returns, and a throw there would exit non-zero (the systemd unit restarts on failure).
        var stop = new CancellationTokenSource();
        string? signal = null;
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            signal ??= "Ctrl+C";
            stop.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            signal ??= "a stop signal (SIGTERM, e.g. systemctl stop)";
            stop.Cancel();
        };
        try
        {
            await host.App.WaitForShutdownAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        Note("exit: " + (host.StopReason ?? signal ?? "the host stopped without a recorded reason"));
        return 0;
    }
}
