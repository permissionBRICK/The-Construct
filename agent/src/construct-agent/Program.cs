using System.Diagnostics;
using Construct.Agent;
using Construct.Agent.Http;
using Microsoft.Extensions.Hosting;

// construct-agent [run|open|repair] [--data-dir DIR] [--ui DIR]
//   run     the agent in the foreground (the default)
//   open    starts the agent when it is not running and opens the UI in the default browser
//   repair  reports what it finds and restarts the agent; works with no engine reachable (5.3)
var command = "run";
string? dataDirectory = null, ui = null;
for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--data-dir" when i + 1 < args.Length: dataDirectory = args[++i]; break;
        case "--ui" when i + 1 < args.Length: ui = args[++i]; break;
        case "run" or "open" or "repair": command = args[i]; break;
        case "-h" or "--help":
            Console.WriteLine("Usage: construct-agent [run|open|repair] [--data-dir DIR] [--ui DIR]");
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'. Usage: construct-agent [run|open|repair] [--data-dir DIR] [--ui DIR]");
            return 2;
    }
}

var options = new AgentOptions { DataDirectory = dataDirectory ?? AgentOptions.DefaultDataDirectory(), UiDirectory = ui ?? AgentOptions.DefaultUiDirectory() };
var launch = AgentLaunch.Current(dataDirectory, ui);

switch (command)
{
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

    default:
    {
        await using var host = AgentHost.Create(options);
        await host.StartAsync();
        Console.WriteLine($"construct-agent {options.Version} on {host.BaseUrl} (device {host.Agent.DeviceId}); token in {options.EndpointPath}");
        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
        try
        {
            await host.App.WaitForShutdownAsync(stop.Token);
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }
}
