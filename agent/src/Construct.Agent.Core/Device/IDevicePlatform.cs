using Construct.Companion.Core.Abstractions;

namespace Construct.Agent.Device;

/// <summary>
/// What only a device can do (5.1, 7.2): the OS parts behind the device routes. The Windows shell and
/// the Linux host each give one; tests give a fake. A member that is null, or a method that throws
/// <see cref="NotAvailable"/>, is a feature this platform does not have: its route answers 501.
/// </summary>
public interface IDevicePlatform
{
    /// <summary><c>windows</c> or <c>linux</c>.</summary>
    string Name { get; }

    /// <summary>The user's home, where <c>.ssh</c> lives.</summary>
    string Home { get; }

    /// <summary>VS Code's user settings directory, or null when VS Code is not installed.</summary>
    string? VsCodeUserDirectory { get; }

    /// <summary>The <c>ssh</c> client to run.</summary>
    string SshExecutable { get; }

    /// <summary>Desktop notifications, or null.</summary>
    IToastRaiser? Notifications { get; }

    /// <summary>The microphone, or null.</summary>
    IAudioCapture? Microphone { get; }

    /// <summary>Maps a VM's workspace share to a drive letter, or null.</summary>
    IShareMapper? Shares { get; }

    /// <summary>Opens a URL (http, https, vscode) in the program registered for it.</summary>
    Task OpenAsync(string uri, CancellationToken ct);

    /// <summary>Opens a terminal running <c>ssh &lt;alias&gt;</c>.</summary>
    Task OpenTerminalAsync(string alias, CancellationToken ct);

    /// <summary>
    /// Starts the T3 Code desktop app, or brings it to the front, when Construct installed its patched build on this
    /// device; false when there is none (the T3 Code web UI is opened instead). <paramref name="link"/>, when given, is a
    /// <c>t3code://threads/...</c> link the app opens (a running app gets it from the new instance's command line).
    /// </summary>
    Task<bool> OpenT3DesktopAsync(string? link, CancellationToken ct) => Task.FromResult(false);

    /// <summary>Install and update support for Construct's patched T3 Code Desktop app; null on platforms without it.</summary>
    IT3DesktopPlatform? T3Desktop => null;

    /// <summary>
    /// Trusts a VM's self-signed T3 Code HTTPS CA certificate (<c>/etc/construct/t3code-ca.crt</c>) on this device,
    /// storing it under the T3 artifacts directory and importing it into the OS Root store when missing or rotated.
    /// Best-effort: platforms without a local trust store do nothing.
    /// </summary>
    Task TrustT3CaAsync(string vmName, string pem, CancellationToken ct) => Task.CompletedTask;

    /// <summary>Whether the agent starts with the user's session; null when this platform cannot say.</summary>
    bool? StartWithOs { get; }

    /// <summary>Starts the agent at <paramref name="executable"/> with the session, or stops doing so.</summary>
    void SetStartWithOs(bool on, string executable);
}

/// <summary>OS operations for managing Construct's patched T3 Code Desktop app on this device (Windows).</summary>
public interface IT3DesktopPlatform
{
    /// <summary>Directory holding <c>installed.json</c> and cached <c>T3Code-Construct-Setup.exe</c>.</summary>
    string ArtifactDirectory { get; }

    /// <summary>True when T3 Code Desktop's executable is present on disk.</summary>
    bool IsInstalled();

    /// <summary>True when any process from the T3 Code Desktop install directory is currently running.</summary>
    bool IsRunning();

    /// <summary>
    /// Runs the silent NSIS installer (<c>T3Code-Construct-Setup.exe</c>). When <paramref name="allowCloseRunning"/> is
    /// true, passes <c>--updated /S</c> (which closes a running app); otherwise <c>/S</c>. Returns the process exit code.
    /// </summary>
    Task<int> RunInstallerAsync(string installerPath, bool allowCloseRunning, CancellationToken ct);

    /// <summary>Starts the installed T3 Code Desktop detached.</summary>
    Task<bool> LaunchAsync(CancellationToken ct);
}

/// <summary>The workspace share of a VM on a drive letter (Windows: <c>net use</c>).</summary>
public interface IShareMapper
{
    /// <summary>The letter the share is on now (<c>Z:</c>), or null.</summary>
    Task<string?> MappedAsync(string unc, CancellationToken ct);

    /// <summary>Maps <paramref name="unc"/> with the credential, reusing a letter that already shows it; returns the letter.</summary>
    Task<string> MapAsync(string unc, string user, string password, CancellationToken ct);

    Task UnmapAsync(string unc, CancellationToken ct);
}

/// <summary>A feature this platform does not have; the route answers 501 <c>not-available</c>.</summary>
public static class NotAvailable
{
    public static AgentFailure Feature(string what) => new(501, "not-available", $"{what} is not available on this device.");
}
