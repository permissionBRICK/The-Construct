using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Construct.Agent.Device;

/// <summary>
/// The <c>Host</c> block per VM in the user's <c>~/.ssh/config</c>, as <c>Set-HostSshConfig</c>
/// (Provision-AgentVM.ps1) writes it and <c>Remove-ConstructSshConfigBlock</c> (lib/AgentVm.Cleanup.ps1)
/// removes it: the block whose Host line names exactly this one alias is replaced; every other line
/// stays as it is. Two differences, both to leave the user's file alone: a <c>Match</c> line also ends
/// a block (the script ran on to the next <c>Host</c> and dropped a following <c>Match</c> block), and
/// the file keeps its own line endings.
/// </summary>
public static partial class SshConfigText
{
    public static string Block(string alias, string host, int port, string user, string identityFile, bool ipv4, string newline)
    {
        var lines = new List<string> { $"Host {alias}", $"    HostName {host}", $"    User {user}", $"    IdentityFile {identityFile}", "    IdentitiesOnly yes" };
        if (port != 22) lines.Add($"    Port {port}");
        if (ipv4) lines.Add("    AddressFamily inet");
        return string.Join(newline, lines);
    }

    /// <summary>The newline the file uses; a new file takes the platform's.</summary>
    public static string NewlineOf(string? text) =>
        text is null || text.Length == 0 ? (OperatingSystem.IsWindows() ? "\r\n" : "\n") : text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    /// <summary>The file with <paramref name="alias"/>'s block replaced by (or ended with) <paramref name="block"/>.</summary>
    public static (string Text, bool Replaced) Upsert(string? existing, string alias, string block)
    {
        var newline = NewlineOf(existing);
        var (kept, replaced) = Without(existing ?? "", alias);
        var text = string.Join(newline, kept).TrimEnd('\r', '\n');
        if (text.Length > 0) text += newline + newline;
        return (text + block + newline, replaced);
    }

    /// <summary>The file without <paramref name="alias"/>'s block.</summary>
    public static (string Text, bool Removed) Remove(string existing, string alias)
    {
        var newline = NewlineOf(existing);
        var (kept, removed) = Without(existing, alias);
        if (!removed) return (existing, false);
        var text = string.Join(newline, kept).TrimEnd('\r', '\n');
        return (text.Length > 0 ? text + newline : "", true);
    }

    /// <summary>The identity file named in <paramref name="alias"/>'s block, or null.</summary>
    public static string? IdentityFileOf(string? existing, string alias)
    {
        var inside = false;
        foreach (var line in Lines(existing ?? ""))
        {
            if (Boundary().Match(line) is { Success: true } header)
            {
                inside = IsOurs(header, alias);
                continue;
            }

            if (inside && IdentityLine().Match(line) is { Success: true } identity) return identity.Groups[1].Value.Trim().Trim('"');
        }

        return null;
    }

    /// <summary>
    /// A Host alias the agent writes: a VM name is letters, digits and dashes; anything else (spaces,
    /// wildcards, <c>!</c>) would make the line a pattern list, which this writer never touches.
    /// </summary>
    public static bool IsUsableAlias(string alias) => alias.Length is > 0 and <= 128 && alias.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    private static (List<string> Kept, bool Found) Without(string existing, string alias)
    {
        var kept = new List<string>();
        var skipping = false;
        var found = false;
        foreach (var line in Lines(existing))
        {
            if (Boundary().Match(line) is { Success: true } header)
            {
                skipping = IsOurs(header, alias);
                found |= skipping;
                if (skipping) continue;
            }

            if (!skipping) kept.Add(line);
        }

        return (kept, found);
    }

    private static bool IsOurs(Match header, string alias)
    {
        if (!header.Groups[1].Value.Equals("Host", StringComparison.OrdinalIgnoreCase)) return false;
        var patterns = header.Groups[2].Value.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return patterns.Length == 1 && patterns[0].Equals(alias, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> Lines(string text)
    {
        if (text.Length == 0) yield break;
        var parts = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        // A trailing newline is not a last empty line.
        var count = parts[^1].Length == 0 ? parts.Length - 1 : parts.Length;
        for (var i = 0; i < count; i++) yield return parts[i];
    }

    [GeneratedRegex(@"^\s*(Host|Match)\s+(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Boundary();

    [GeneratedRegex(@"^\s*IdentityFile\s+(.+?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex IdentityLine();
}

/// <summary>What a write of the user's SSH or VS Code configuration did.</summary>
public sealed record ConfigWrite(string Item, string Status, string Path, string? Message = null);

/// <summary>
/// The user's SSH client configuration for a VM (5.1, 7.2): the <c>Host</c> alias in <c>~/.ssh/config</c>,
/// the device's own SSH key, and VS Code Remote-SSH's <c>remote.SSH.remotePlatform</c> entry.
/// </summary>
public sealed class SshClientConfig(IDevicePlatform platform)
{
    public const string DeviceKeyName = "construct_ed25519";

    public string SshDirectory => Path.Combine(platform.Home, ".ssh");

    public string ConfigPath => Path.Combine(SshDirectory, "config");

    /// <summary>The device's key for its VMs; the engine has to authorize its public half on a VM.</summary>
    public string DeviceKeyPath => Path.Combine(SshDirectory, DeviceKeyName);

    /// <summary>The key a VM is dialled with: the one its existing block names (an install moved over keeps its key), else the device's.</summary>
    public string KeyFor(string alias)
    {
        var named = SshConfigText.IdentityFileOf(Read(ConfigPath), alias);
        if (named is null) return DeviceKeyPath;
        var expanded = named.StartsWith("~/", StringComparison.Ordinal) || named.StartsWith("~\\", StringComparison.Ordinal) ? Path.Combine(platform.Home, named[2..]) : named;
        return File.Exists(expanded) ? expanded : DeviceKeyPath;
    }

    /// <summary>Writes the alias block when it differs from what the file has.</summary>
    public ConfigWrite WriteAlias(string alias, string host, int port, bool ipv4)
    {
        if (!SshConfigText.IsUsableAlias(alias)) throw new AgentFailure(400, "invalid-alias", "Not a usable SSH alias.");
        var existing = Read(ConfigPath);
        var block = SshConfigText.Block(alias, host, port, "root", KeyFor(alias), ipv4, SshConfigText.NewlineOf(existing));
        var (text, replaced) = SshConfigText.Upsert(existing, alias, block);
        if (text == existing) return new("ssh-config", "unchanged", ConfigPath);
        WriteKeepingFile(ConfigPath, text);
        return new("ssh-config", replaced ? "replaced" : "added", ConfigPath);
    }

    public ConfigWrite RemoveAlias(string alias)
    {
        if (Read(ConfigPath) is not { } existing) return new("ssh-config", "skipped", ConfigPath, "No ssh config.");
        var (text, removed) = SshConfigText.Remove(existing, alias);
        if (!removed) return new("ssh-config", "skipped", ConfigPath, $"No Host '{alias}' block.");
        WriteKeepingFile(ConfigPath, text);
        return new("ssh-config", "removed", ConfigPath);
    }

    /// <summary>
    /// Sets <c>remote.SSH.remotePlatform[alias] = "linux"</c> in VS Code's user settings, as
    /// <c>Set-VsCodeRemotePlatform</c> did: skipped without VS Code, and a file that is not plain JSON
    /// (comments) is left untouched, because rewriting it would drop them.
    /// </summary>
    public ConfigWrite WriteVsCodePlatform(string alias, bool remove = false)
    {
        var directory = platform.VsCodeUserDirectory;
        if (directory is null || !Directory.Exists(directory))
            return new("vscode-remote-platform", "skipped", directory ?? "", "VS Code is not installed.");
        var path = Path.Combine(directory, "settings.json");
        const string key = "remote.SSH.remotePlatform";
        JsonObject settings;
        var text = Read(path);
        try
        {
            settings = text is null || text.Trim().Length == 0 ? new JsonObject() : JsonNode.Parse(text) as JsonObject ?? throw new JsonException();
        }
        catch (JsonException)
        {
            return new("vscode-remote-platform", "not-modified", path, $"The file is not plain JSON (comments?); add \"{key}\": {{ \"{alias}\": \"linux\" }} by hand.");
        }

        var platforms = settings[key] as JsonObject;
        if (remove)
        {
            if (platforms is null || !platforms.Remove(alias)) return new("vscode-remote-platform", "skipped", path);
        }
        else
        {
            if (platforms?[alias]?.GetValueKind() == JsonValueKind.String && platforms[alias]!.GetValue<string>() == "linux") return new("vscode-remote-platform", "unchanged", path);
            if (platforms is null) settings[key] = platforms = new JsonObject();
            platforms[alias] = "linux";
        }

        if (text is not null) File.Copy(path, path + ".bak", overwrite: true);
        WriteKeepingFile(path, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 4 }) + Environment.NewLine);
        return new("vscode-remote-platform", remove ? "removed" : "set", path);
    }

    /// <summary>Creates the device's SSH key with <c>ssh-keygen</c> when it is missing; returns the public key line.</summary>
    public async Task<string> EnsureDeviceKeyAsync(string comment, CancellationToken ct)
    {
        var publicPath = DeviceKeyPath + ".pub";
        if (!File.Exists(DeviceKeyPath))
        {
            EnsureSshDirectory();
            var keygen = Path.Combine(Path.GetDirectoryName(platform.SshExecutable) ?? "", OperatingSystem.IsWindows() ? "ssh-keygen.exe" : "ssh-keygen");
            if (!Path.IsPathRooted(keygen)) keygen = "ssh-keygen";
            var start = new ProcessStartInfo(keygen) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "-q", "-t", "ed25519", "-N", "", "-C", comment, "-f", DeviceKeyPath }) start.ArgumentList.Add(argument);
            try
            {
                using var process = Process.Start(start)!;
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                if (process.ExitCode != 0) throw new AgentFailure(502, "ssh-keygen-failed", "ssh-keygen could not create the device's SSH key.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                throw NotAvailable.Feature("ssh-keygen");
            }
        }

        return File.Exists(publicPath) ? (await File.ReadAllTextAsync(publicPath, ct).ConfigureAwait(false)).Trim() : throw new AgentFailure(500, "no-public-key", $"{publicPath} is missing.");
    }

    private void EnsureSshDirectory()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(SshDirectory);
        else if (!Directory.Exists(SshDirectory)) Directory.CreateDirectory(SshDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string? Read(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>
    /// Replaces the file's content through a temporary file next to it. A symbolic link is written
    /// through (the user's dotfiles stay linked), and the file keeps its mode; a new file is 0600.
    /// </summary>
    private void WriteKeepingFile(string path, string text)
    {
        if (path == ConfigPath) EnsureSshDirectory();
        var target = File.Exists(path) ? new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path : path;
        var temporary = target + "." + Guid.NewGuid().ToString("n")[..8] + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = File.Exists(target) ? File.GetUnixFileMode(target) : UnixFileMode.UserRead | UnixFileMode.UserWrite;
        try
        {
            using (var stream = new FileStream(temporary, options))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(text));
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
