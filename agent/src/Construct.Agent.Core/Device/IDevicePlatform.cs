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

    /// <summary>
    /// True where a VM on the engine of this very machine is dialled at its own address
    /// (<c>&lt;name&gt;.mshome.net:22</c>, Hyper-V's default switch) rather than the engine's forward (7.2).
    /// </summary>
    bool DirectAddresses { get; }

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

    /// <summary>Whether the agent starts with the user's session; null when this platform cannot say.</summary>
    bool? StartWithOs { get; }

    /// <summary>Starts the agent at <paramref name="executable"/> with the session, or stops doing so.</summary>
    void SetStartWithOs(bool on, string executable);
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
