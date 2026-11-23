using System.Diagnostics;
using System.Net.Http.Json;
using System.Security.Authentication;
using Construct.Agent.Engines;
using Construct.Agent.Http;
using Construct.Agent.State;

namespace Construct.Agent;

/// <summary>How to start this agent again: its executable and the arguments that name the same directories.</summary>
public sealed record AgentLaunch(string FileName, IReadOnlyList<string> Arguments)
{
    public static AgentLaunch Current(string? dataDirectory, string? ui)
    {
        var process = Environment.ProcessPath ?? throw new InvalidOperationException("The agent's executable is unknown.");
        var arguments = new List<string>();
        // Started as `dotnet construct-agent.dll`: the assembly comes first.
        if (Path.GetFileNameWithoutExtension(process) == "dotnet") arguments.Add(Environment.GetCommandLineArgs()[0]);
        arguments.Add("run");
        if (dataDirectory is not null) arguments.AddRange(["--data-dir", Path.GetFullPath(dataDirectory)]);
        if (ui is not null) arguments.AddRange(["--ui", Path.GetFullPath(ui)]);
        return new AgentLaunch(process, arguments);
    }
}

/// <summary>
/// <c>construct-agent repair</c> (5.3, S4): works without any engine. It reports what it finds (the
/// agent running, the token file, the device registered, each engine reachable over its pin) and
/// restarts the agent. Re-applying a staged package or re-running the installer belongs to the
/// updater and installer blocks.
/// </summary>
public static class Repair
{
    public static async Task<int> RunAsync(AgentOptions options, AgentLaunch launch, TextWriter output, CancellationToken ct)
    {
        output.WriteLine($"Agent directory: {options.DataDirectory}{(Directory.Exists(options.DataDirectory) ? "" : " (missing)")}");

        AgentState? state = null;
        try
        {
            state = File.Exists(options.StatePath) ? StateFile.Load(options.StatePath) : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            output.WriteLine($"State: unreadable ({ex.Message})");
        }

        if (state is not null)
        {
            output.WriteLine(state.Registered
                ? $"Device: registered in account '{state.Account}' (secrets protected by {state.Protection})"
                : $"Device: key created, not registered yet (secrets protected by {state.Protection})");
        }
        else
        {
            output.WriteLine("Device: no state yet (the agent creates it at first start)");
        }

        var endpoint = EndpointFile.Read(options.EndpointPath);
        output.WriteLine(endpoint is null ? "Token file: none" : $"Token file: present{FileModeNote(options.EndpointPath)}");
        var running = await RunningAsync(options, ct).ConfigureAwait(false);
        output.WriteLine(running is not null
            ? $"Agent: running, pid {running.Pid}, version {running.AgentVersion}, {running.BaseUrl}"
            : endpoint is not null ? $"Agent: not answering (pid {endpoint.Pid} in the token file)" : "Agent: not running");

        foreach (var engine in state?.Engines ?? [])
        {
            output.WriteLine($"Engine {engine.Name} ({engine.Url}): {await EngineStatusAsync(engine, ct).ConfigureAwait(false)}" +
                (engine.DeviceToken is not null ? ", device enrolled" : engine.UserToken is not null ? ", signed in, device not enrolled" : ", no credential"));
        }

        output.WriteLine("Restarting the agent ...");
        if (running is not null) await StopAsync(running, ct).ConfigureAwait(false);
        else if (endpoint is not null) Kill(endpoint.Pid, endpoint.StartedAt);

        var started = await StartAsync(options, launch, ct).ConfigureAwait(false);
        output.WriteLine(started is not null ? $"Agent: running again, pid {started.Pid}, {started.BaseUrl}" : "Agent: did not come up; run `construct-agent run` to see why.");
        return started is not null ? 0 : 1;
    }

    /// <summary>The running agent's token file, when the agent behind it answers.</summary>
    public static async Task<EndpointFile?> RunningAsync(AgentOptions options, CancellationToken ct)
    {
        if (EndpointFile.Read(options.EndpointPath) is not { } endpoint) return null;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var health = await http.GetFromJsonAsync<AgentHealth>($"{endpoint.BaseUrl}/api/health", AgentJson.Options, ct).ConfigureAwait(false);
            return health?.Pid == endpoint.Pid ? endpoint : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Starts the agent detached from this process and waits until it answers.</summary>
    public static async Task<EndpointFile?> StartAsync(AgentOptions options, AgentLaunch launch, CancellationToken ct)
    {
        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? launch.FileName : "/bin/sh") { UseShellExecute = false, CreateNoWindow = true };
        if (OperatingSystem.IsWindows())
        {
            foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        }
        else
        {
            // A new session, no terminal, so the agent outlives the shell that ran repair or open.
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add("if command -v setsid >/dev/null; then setsid \"$0\" \"$@\" </dev/null >/dev/null 2>&1 & else \"$0\" \"$@\" </dev/null >/dev/null 2>&1 & fi");
            start.ArgumentList.Add(launch.FileName);
            foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        }

        using (var process = Process.Start(start))
        {
            // On Linux the shell that detaches the agent ends at once; on Windows the process is the agent itself.
            if (!OperatingSystem.IsWindows()) await process!.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        for (var i = 0; i < 60; i++)
        {
            if (await RunningAsync(options, ct).ConfigureAwait(false) is { } endpoint) return endpoint;
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        return null;
    }

    private static async Task StopAsync(EndpointFile endpoint, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{endpoint.BaseUrl}/api/shutdown");
            request.Headers.Authorization = new("Bearer", endpoint.Token);
            using var _ = await http.SendAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
        }

        for (var i = 0; i < 40 && Alive(endpoint.Pid); i++) await Task.Delay(250, ct).ConfigureAwait(false);
        Kill(endpoint.Pid, endpoint.StartedAt);
    }

    private static async Task<string> EngineStatusAsync(EngineEntry engine, CancellationToken ct)
    {
        try
        {
            using var http = EngineHttp.Client(new Uri(engine.Url), engine.Pin, TimeSpan.FromSeconds(5));
            using var response = await http.GetAsync("api/v1/health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? "reachable" : $"answers {(int)response.StatusCode}";
        }
        catch (HttpRequestException ex) when (ex.InnerException is AuthenticationException)
        {
            return "its certificate does not match the pin";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return "not reachable";
        }
    }

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// Ends a process only when it is the agent of the token file: its name says agent and it started
    /// shortly before the file's start time, so a later reuse of the pid is left alone.
    /// </summary>
    private static void Kill(int pid, DateTimeOffset startedAt)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            if (process.ProcessName is "construct-agent" or "dotnet" && started <= startedAt && started > startedAt - TimeSpan.FromMinutes(2)) process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private static string FileModeNote(string path) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) == 0
            ? ""
            : " (readable by others: it is rewritten 0600 at the next start)";
}
