using System.Diagnostics;
using System.Xml.Linq;
using Construct.Agent.Update;
using Construct.Companion.Core.Abstractions;

namespace Construct.Agent.Device;

/// <summary>
/// The Linux device (S18): notifications through <c>notify-send</c>, links through <c>xdg-open</c>, a
/// terminal through <c>x-terminal-emulator</c>, start with the session as a systemd user unit. No
/// microphone and no workspace share: those routes answer "not available".
/// </summary>
public sealed class LinuxPlatform : IDevicePlatform, IAgentLauncher
{
    public const string UnitName = "construct-agent.service";

    public LinuxPlatform()
    {
        Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Home, ".config");
        VsCodeUserDirectory = Path.Combine(config, "Code", "User");
        UnitPath = Path.Combine(config, "systemd", "user", UnitName);
        SshExecutable = Find("ssh") ?? "ssh";
        Notifications = Find("notify-send") is { } notify ? new NotifySend(notify) : null;
    }

    public string Name => "linux";

    public string Home { get; }

    public string? VsCodeUserDirectory { get; }

    public string SshExecutable { get; }

    public bool DirectAddresses => false;

    public IToastRaiser? Notifications { get; }

    public IAudioCapture? Microphone => null;

    public IShareMapper? Shares => null;

    public string UnitPath { get; }

    public Task OpenAsync(string uri, CancellationToken ct)
    {
        if (Find("xdg-open") is not { } opener) throw NotAvailable.Feature("Opening links (xdg-open)");
        Detached(opener, uri);
        return Task.CompletedTask;
    }

    public Task OpenTerminalAsync(string alias, CancellationToken ct)
    {
        if (Find("x-terminal-emulator") is not { } terminal) throw NotAvailable.Feature("A terminal (x-terminal-emulator)");
        Detached(terminal, "-e", SshExecutable, alias);
        return Task.CompletedTask;
    }

    public bool? StartWithOs => Find("systemctl") is null ? null : File.Exists(UnitPath);

    public void SetStartWithOs(bool on, string executable)
    {
        if (Find("systemctl") is not { } systemctl) throw NotAvailable.Feature("Start with the session (systemd)");
        if (on)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(UnitPath)!);
            File.WriteAllText(UnitPath, Unit(executable));
            Run(systemctl, "--user", "daemon-reload");
            Run(systemctl, "--user", "enable", UnitName);
        }
        else if (File.Exists(UnitPath))
        {
            Run(systemctl, "--user", "disable", UnitName);
            File.Delete(UnitPath);
            Run(systemctl, "--user", "daemon-reload");
        }
    }

    /// <summary>
    /// Starts an installed version. Under the systemd unit the unit is pointed at it and restarted (a
    /// child started here would end with the unit's process group); otherwise it starts in its own session.
    /// </summary>
    public Task LaunchAsync(string executable, CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("INVOCATION_ID") is { Length: > 0 } && File.Exists(UnitPath) && Find("systemctl") is { } systemctl)
        {
            File.WriteAllText(UnitPath, Unit(executable));
            Run(systemctl, "--user", "daemon-reload");
            // --no-block: systemd stops this process as part of the restart.
            Run(systemctl, "--user", "--no-block", "restart", UnitName);
            return Task.CompletedTask;
        }

        if (File.Exists(UnitPath)) File.WriteAllText(UnitPath, Unit(executable));
        Process.Start(new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            ArgumentList = { "-c", "if command -v setsid >/dev/null; then setsid \"$0\" run </dev/null >/dev/null 2>&1 & else \"$0\" run </dev/null >/dev/null 2>&1 & fi", executable },
        })?.WaitForExit();
        return Task.CompletedTask;
    }

    /// <summary>The user unit: the agent in the foreground, restarted when it fails (the way back exits 1 after starting the previous one).</summary>
    public static string Unit(string executable) => $"""
        [Unit]
        Description=Construct device agent
        After=graphical-session.target

        [Service]
        ExecStart="{executable.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}" run
        Restart=on-failure
        RestartSec=5

        [Install]
        WantedBy=default.target

        """;

    private static void Run(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.WaitForExit(TimeSpan.FromSeconds(30));
    }

    private static void Detached(string file, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var _ = Process.Start(start);
    }

    private static string? Find(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name)).FirstOrDefault(File.Exists);

    /// <summary>A Companion toast (<see cref="Companion.Core.Notifications.NotificationProtocol.Toast"/>) as a <c>notify-send</c> call.</summary>
    public sealed class NotifySend(string executable) : IToastRaiser
    {
        public Task<ToastAvailability> GetAvailabilityAsync(CancellationToken cancellationToken = default) => Task.FromResult(ToastAvailability.Available);

        public Task RaiseAsync(ToastDocument toast, CancellationToken cancellationToken = default)
        {
            var arguments = Arguments(toast);
            Detached(executable, arguments);
            return Task.CompletedTask;
        }

        /// <summary><c>-a The Construct -u normal|critical title body</c>: a long toast (warning, error) is critical.</summary>
        public static string[] Arguments(ToastDocument toast)
        {
            var root = XElement.Parse(toast.Xml);
            var texts = root.Descendants("text").Where(t => t.Attribute("placement") is null).Select(t => t.Value).ToArray();
            var source = root.Descendants("text").FirstOrDefault(t => (string?)t.Attribute("placement") == "attribution")?.Value;
            var urgency = (string?)root.Attribute("duration") == "long" ? "critical" : "normal";
            var body = texts.ElementAtOrDefault(1) ?? "";
            if (source is { Length: > 0 }) body += $" ({source})";
            return ["-a", "The Construct", "-u", urgency, "--", texts.ElementAtOrDefault(0) ?? "The Construct", body];
        }
    }
}
