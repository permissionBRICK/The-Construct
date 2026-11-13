using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Construct.Agent.Device;
using Construct.Agent.Update;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Windows;
using Microsoft.Win32;

namespace Construct.Agent.Windows;

/// <summary>
/// The Windows device: toasts under the Construct AUMID (the Companion's <see cref="WinRtToastRaiser"/>),
/// the microphone through WASAPI, the workspace share through <c>net use</c>, start with Windows as the
/// current user's Run value, and Hyper-V VMs on this PC's engine dialled at their own address.
/// </summary>
internal sealed partial class WindowsPlatform : IDevicePlatform, IAgentLauncher
{
    public const string RunValue = "ConstructAgent";
    private readonly CurrentUserRegistry _registry = new();
    private readonly string _executable;

    public WindowsPlatform(string executable, string iconPath)
    {
        _executable = executable;
        Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        VsCodeUserDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Code", "User");
        SshExecutable = FindSsh();
        Register(iconPath);
        Notifications = new WinRtToastRaiser(new DesktopRegistration(_registry, executable));
        Microphone = new WasapiAudioCapture();
        Shares = new NetUseShares();
    }

    public string Name => "windows";

    public string Home { get; }

    public string? VsCodeUserDirectory { get; }

    public string SshExecutable { get; }

    public bool DirectAddresses => true;

    public IToastRaiser? Notifications { get; }

    public IAudioCapture? Microphone { get; }

    public IShareMapper? Shares { get; }

    public Task OpenAsync(string uri, CancellationToken ct)
    {
        // Only links the agent built itself reach here; a scheme check keeps a file path or a program out.
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme is not ("http" or "https" or "vscode"))
            throw new AgentFailure(400, "invalid-link", "Only http, https and vscode links are opened.");
        using var _ = Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    public Task OpenTerminalAsync(string alias, CancellationToken ct)
    {
        // A console window running ssh with the alias from ~/.ssh/config.
        using var _ = Process.Start(new ProcessStartInfo(SshExecutable) { UseShellExecute = true, ArgumentList = { alias } });
        return Task.CompletedTask;
    }

    public bool? StartWithOs => _registry.ReadString(DesktopRegistration.RunKey, RunValue) is { Length: > 0 };

    public void SetStartWithOs(bool on, string executable)
    {
        if (on) _registry.WriteString(DesktopRegistration.RunKey, RunValue, $"\"{executable}\" --background");
        else _registry.DeleteValue(DesktopRegistration.RunKey, RunValue);
    }

    /// <summary>Starts an installed version; the Run value follows it so the next sign-in starts the same one.</summary>
    public Task LaunchAsync(string executable, CancellationToken ct)
    {
        if (StartWithOs == true) SetStartWithOs(true, executable);
        using var _ = Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false, ArgumentList = { "--background" } });
        return Task.CompletedTask;
    }

    /// <summary>
    /// The current user's registration of the toast sender and the <c>construct://</c> links a toast
    /// opens (the Companion's installer wrote these for the Companion; the agent writes its own).
    /// </summary>
    private void Register(string iconPath)
    {
        _registry.WriteString(DesktopRegistration.ToastKey, "DisplayName", "The Construct");
        _registry.WriteString(DesktopRegistration.ToastKey, "IconUri", iconPath);
        _registry.WriteString(DesktopRegistration.ProtocolKey, null, "URL:Construct");
        _registry.WriteString(DesktopRegistration.ProtocolKey, "URL Protocol", "");
        _registry.WriteString(DesktopRegistration.ProtocolKey + @"\shell\open\command", null, $"\"{_executable}\" \"%1\"");
    }

    private static string FindSsh()
    {
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var path = Path.Combine(directory, "ssh.exe");
            if (File.Exists(path)) return path;
        }

        var system = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows", "System32", "OpenSSH", "ssh.exe");
        return File.Exists(system) ? system : "ssh";
    }

    /// <summary>
    /// <c>Mount-RepoShare</c> (Provision-AgentVM.ps1) without its prompt: a letter that already shows the
    /// share is reused, else the first free one from Z down; the credential goes to the credential manager
    /// (<c>cmdkey</c>) and the mapping is persistent.
    /// </summary>
    private sealed partial class NetUseShares : IShareMapper
    {
        public async Task<string?> MappedAsync(string unc, CancellationToken ct) =>
            Mappings(await NetAsync(ct, "use").ConfigureAwait(false)).FirstOrDefault(m => m.Unc.Equals(unc, StringComparison.OrdinalIgnoreCase)).Letter;

        public async Task<string> MapAsync(string unc, string user, string password, CancellationToken ct)
        {
            var mappings = Mappings(await NetAsync(ct, "use").ConfigureAwait(false));
            var letter = mappings.FirstOrDefault(m => m.Unc.Equals(unc, StringComparison.OrdinalIgnoreCase)).Letter;
            if (letter is null)
            {
                var used = DriveInfo.GetDrives().Select(d => d.Name[..2].ToUpperInvariant()).Concat(mappings.Select(m => m.Letter)).ToHashSet();
                letter = Enumerable.Range('D', 'Z' - 'D' + 1).Reverse().Select(c => $"{(char)c}:").FirstOrDefault(l => !used.Contains(l))
                    ?? throw new AgentFailure(409, "no-drive-letter", "No drive letter is free.");
            }
            else
            {
                await NetAsync(ct, "use", letter, "/delete", "/y").ConfigureAwait(false);
            }

            var server = unc.TrimStart('\\').Split('\\')[0];
            await RunAsync("cmdkey.exe", ct, $"/add:{server}", $"/user:{user}", $"/pass:{password}").ConfigureAwait(false);
            var (code, _) = await NetAsync(ct, "use", letter, unc, "/persistent:yes").ConfigureAwait(false);
            if (code != 0) throw new AgentFailure(502, "share-failed", $"Windows could not map {unc}; is the VM running?");
            return letter;
        }

        public async Task UnmapAsync(string unc, CancellationToken ct)
        {
            if (await MappedAsync(unc, ct).ConfigureAwait(false) is { } letter) await NetAsync(ct, "use", letter, "/delete", "/y").ConfigureAwait(false);
        }

        private static IEnumerable<(string Letter, string Unc)> Mappings((int Code, string Output) net) =>
            net.Output.Split('\n').Select(l => Mapping().Match(l)).Where(m => m.Success).Select(m => (m.Groups[1].Value.ToUpperInvariant(), m.Groups[2].Value));

        private static Task<(int, string)> NetAsync(CancellationToken ct, params string[] arguments) => RunAsync("net.exe", ct, arguments);

        private static async Task<(int, string)> RunAsync(string file, CancellationToken ct, params string[] arguments)
        {
            var start = new ProcessStartInfo(file) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return (process.ExitCode, output);
        }

        [GeneratedRegex(@"\s([A-Za-z]:)\s+(\\\\\S+)")]
        private static partial Regex Mapping();
    }
}

/// <summary>The device's secrets under DPAPI for the current user (11.1), through the agent's protection interface.</summary>
internal sealed class DpapiProtection : Construct.Agent.Protection.IDataProtection
{
    public string Name => "dpapi";

    public bool Weak => false;

    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        var bytes = plaintext.ToArray();
        try { return ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes) => ProtectedData.Unprotect(protectedBytes.ToArray(), null, DataProtectionScope.CurrentUser);
}
