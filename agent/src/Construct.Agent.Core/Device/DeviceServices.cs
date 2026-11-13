using Construct.Agent.Device.Runtime;
using Construct.Agent.Update;
using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;

namespace Construct.Agent.Device;

/// <summary>Opens a surface of the desktop shell (the Windows tray app); absent on the Linux console host.</summary>
public interface IAgentShell
{
    /// <param name="surface"><c>pop</c>, <c>win</c> or <c>alone</c> (the UI's <c>surf</c> parameter).</param>
    /// <param name="route">The UI route for the window (<c>#/vault</c>), or null.</param>
    void Open(string surface, string? route);
}

/// <summary>
/// The device features of one agent (5.1): VM links, open actions, SSH configuration, the device's
/// settings, its own updates and "update everything". Built by <see cref="Http.AgentHost"/> from the
/// platform in <see cref="AgentOptions.Platform"/>; tests build it with fakes.
/// </summary>
public sealed class DeviceServices : IAsyncDisposable
{
    public DeviceServices(IDevicePlatform platform, DeviceSettings settings, SshClientConfig ssh, VmLinks links, AgentUpdater updater, UpdateEverything everything)
    {
        Platform = platform;
        Settings = settings;
        Ssh = ssh;
        Links = links;
        Actions = new VmActions(links, platform);
        Updater = updater;
        Everything = everything;
    }

    public IDevicePlatform Platform { get; }

    public DeviceSettings Settings { get; }

    public SshClientConfig Ssh { get; }

    public VmLinks Links { get; }

    public VmActions Actions { get; }

    public AgentUpdater Updater { get; }

    public UpdateEverything Everything { get; }

    /// <summary>Set by the Windows shell once its windows exist.</summary>
    public IAgentShell? Shell { get; set; }

    /// <summary>The installed executable that starts with the session (the current version's), or this process.</summary>
    public string? StartExecutable { get; init; }

    /// <summary>The features with real processes and the system clock.</summary>
    public static DeviceServices Create(DeviceAgent agent, AgentOptions options, IDevicePlatform platform, Action stop)
    {
        var settings = new DeviceSettings(Path.Combine(options.DataDirectory, "device.json"));
        var ssh = new SshClientConfig(platform);
        var clock = new SystemClock(options.Time);
        var runner = new RuntimeProcessRunner();
        var probe = new SocketPortProbe();
        var engines = new AgentEngines(agent);
        var links = new VmLinks(engines, platform, ssh, settings,
            target => new ProcessSshTransport(runner, probe,
                new SshConfiguration(target.Address!.Host, target.Alias, "root", "", target.Address.Port, 12, target.Ipv4), platform.SshExecutable, ssh.KeyFor(target.Alias)),
            new SshProcessSupervisor(clock), new LoopbackAudioServerFactory(), clock, agent.Events);
        var install = new AgentInstall(options.DataDirectory, OperatingSystem.IsWindows() ? "construct-agent.exe" : "construct-agent");
        var running = install.RunningVersion(Environment.ProcessPath);
        UpdateEverything? everything = null;
        var updater = new AgentUpdater(options, install, settings, new HttpClient { Timeout = TimeSpan.FromMinutes(10) },
            OperatingSystem.IsWindows() ? "agentWindows" : "agentLinux",
            () => everything?.Running != true && agent.Pairing() is null,
            platform as IAgentLauncher ?? throw new InvalidOperationException("The platform cannot start an agent."), stop, () => agent.Events.Publish("update"))
        { Running = running };
        everything = new UpdateEverything(engines, updater, () => agent.Events.Publish("update"), options.Time);
        return new DeviceServices(platform, settings, ssh, links, updater, everything)
        {
            StartExecutable = running is not null ? install.ExecutableOf(running) : Environment.ProcessPath,
        };
    }

    public void Start()
    {
        Links.Start();
        Updater.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await Updater.DisposeAsync().ConfigureAwait(false);
        await Links.DisposeAsync().ConfigureAwait(false);
    }
}

/// <summary>The Companion's <see cref="IClock"/> over a <see cref="TimeProvider"/>.</summary>
public sealed class SystemClock(TimeProvider time) : IClock
{
    public DateTimeOffset UtcNow => time.GetUtcNow();

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken = default) => Task.Delay(delay, time, cancellationToken);
}
