using System.Reflection;

namespace Construct.Agent;

/// <summary>How one agent runs: where it keeps its files, where the UI bundle is, and the few things tests replace.</summary>
public sealed class AgentOptions
{
    /// <summary>The engine's default port (constructd's ListenUrl default), where the solo engine on this machine listens.</summary>
    public const int DefaultEnginePort = 7462;

    /// <summary>The agent's directory: state, token file, protection key. Created 0700.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>The built UI bundle (<c>ui/dist</c>); null or missing serves a short notice instead.</summary>
    public string? UiDirectory { get; init; }

    /// <summary>The loopback port of the local API; 0 picks a free one, written to the token file.</summary>
    public int Port { get; init; }

    /// <summary>The port the solo engine on this machine is looked for on.</summary>
    public int LocalEnginePort { get; init; } = DefaultEnginePort;

    /// <summary>Where the device's secrets are protected: null picks libsecret when it works, else the file.</summary>
    public string? Protection { get; init; }

    /// <summary>Writes the token file; tests that read the token in process may leave it out.</summary>
    public bool PublishEndpoint { get; init; } = true;

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>Signing in at an engine as the Windows user (Negotiate); null where the platform has none (Linux).</summary>
    public Engines.IWindowsSignIn? WindowsSignIn { get; init; } = OperatingSystem.IsWindows() ? new Engines.NegotiateSignIn() : null;

    /// <summary>The device's OS parts (5.1); null picks the Linux one. The Windows shell passes its own.</summary>
    public Device.IDevicePlatform? Platform { get; init; }

    /// <summary>Where <c>git</c> is looked for, for job clone credentials (PATH syntax); null is the agent's <c>PATH</c>.</summary>
    public string? GitSearchPath { get; init; }

    /// <summary>The one-time upgrade's OS parts (11.6); null picks the default (no DPAPI: the old files cannot be opened).</summary>
    public Upgrade.IUpgradePlatform? Upgrade { get; init; }

    public string Version { get; init; } =
        typeof(AgentOptions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";

    public string StatePath => Path.Combine(DataDirectory, "state.json");

    public string EndpointPath => Path.Combine(DataDirectory, EndpointFile.FileName);

    /// <summary>The default directory: <c>$CONSTRUCT_AGENT_HOME</c>, else <c>$XDG_DATA_HOME/construct-agent</c>, else <c>~/.local/share/construct-agent</c>.</summary>
    public static string DefaultDataDirectory()
    {
        if (Environment.GetEnvironmentVariable("CONSTRUCT_AGENT_HOME") is { Length: > 0 } home) return home;
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Construct", "agent");
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = xdg is { Length: > 0 } ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
        return Path.Combine(root, "construct-agent");
    }

    /// <summary>
    /// The default UI bundle: <c>$CONSTRUCT_AGENT_UI</c>, else <c>ui</c> next to the executable, else the
    /// first <c>ui/dist</c> above it (a source checkout).
    /// </summary>
    public static string? DefaultUiDirectory()
    {
        if (Environment.GetEnvironmentVariable("CONSTRUCT_AGENT_UI") is { Length: > 0 } configured) return configured;
        var beside = Path.Combine(AppContext.BaseDirectory, "ui");
        if (File.Exists(Path.Combine(beside, "index.html"))) return beside;
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var dist = Path.Combine(directory.FullName, "ui", "dist");
            if (File.Exists(Path.Combine(dist, "index.html"))) return dist;
        }

        return null;
    }
}
