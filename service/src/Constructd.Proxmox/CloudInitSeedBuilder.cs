using System.Text;
using System.Text.RegularExpressions;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Windows.Internal;
using Microsoft.Extensions.Logging;

namespace Constructd.Proxmox;

/// <summary>The per-VM seed files the driver removes with the VM.</summary>
public interface IProxmoxSeedFiles
{
    /// <summary>Deletes the VM's seed snippet, if it exists. Never throws for a missing file.</summary>
    void Remove(string vmName);
}

/// <summary>
/// The Proxmox platform's <see cref="IIsoBuilder"/>: instead of remastering an ISO it writes one
/// cloud-init <b>user-data</b> snippet per VM into the node's snippets directory and returns its
/// volume id, which the driver attaches with <c>--cicustom user=…</c>. The seed carries what the
/// autoinstall media carries on Hyper-V — the hostname, the seed user with passwordless sudo and the
/// bootstrap public key — plus the QEMU guest agent, which is how the node learns the guest's address.
///
/// The seed PASSWORD the job generates is deliberately not used: the account is created with a
/// locked password (like the generic Hyper-V media, whose seed password nobody knows), so the
/// bootstrap key the client holds is the guest's only credential until provisioning replaces it.
/// </summary>
public sealed partial class CloudInitSeedBuilder(ConstructdOptions options, ILogger<CloudInitSeedBuilder> logger)
    : IIsoBuilder, IProxmoxSeedFiles
{
    private static readonly Regex SeedUserRule = SeedUserRegex();

    private static readonly Regex PublicKeyRule = PublicKeyRegex();

    private static readonly Regex SnippetVolumeRule = SnippetVolumeRegex();

    /// <summary>The snippet's file name for a VM: one file per VM, named after it.</summary>
    public static string FileNameFor(string vmName) => $"construct-{ArgumentGuard.VmName(vmName)}-user.yaml";

    /// <summary>The volume id <c>qm</c> references the snippet by.</summary>
    public string VolumeIdFor(string vmName) =>
        $"{ArgumentGuard.Text(options.Proxmox.SnippetStorage, "Constructd:Proxmox:SnippetStorage", 64)}:snippets/{FileNameFor(vmName)}";

    /// <summary>Is this text a snippet volume id (<c>storage:snippets/file</c>)?</summary>
    public static bool IsSnippetVolume(string? value) => value is not null && SnippetVolumeRule.IsMatch(value);

    public Task<string> BuildAsync(
        string vmName,
        string seedUser,
        string seedPassword,
        string bootstrapPubKeyPath,
        IProgress<string>? progress,
        CancellationToken cancellationToken,
        bool redownload = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var name = ArgumentGuard.VmName(vmName);
        var user = SeedUser(seedUser);
        var key = ReadPublicKey(bootstrapPubKeyPath);
        var directory = options.Proxmox.SnippetDir;
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Constructd:Proxmox:SnippetDir is not set.");
        }

        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, FileNameFor(name));
        var text = Compose(name, user, key);

        // Root-only from the first byte: written to a temporary name, then moved into place.
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temporary, path, overwrite: true);

        logger.LogInformation("Wrote the cloud-init seed for {Vm} ({SeedUser}).", name, user);
        progress?.Report($"cloud-init seed written for '{name}' (user {user}, bootstrap key from {Path.GetFileName(bootstrapPubKeyPath)})");

        return Task.FromResult(VolumeIdFor(name));
    }

    public void Remove(string vmName)
    {
        var name = ArgumentGuard.VmName(vmName);
        var directory = options.Proxmox.SnippetDir;
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var path = Path.Combine(directory, FileNameFor(name));
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException ex)
        {
            logger.LogWarning("Could not remove the cloud-init seed of {Vm}: {Error}", name, ex.GetType().Name);
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning("Could not remove the cloud-init seed of {Vm}: {Error}", name, ex.GetType().Name);
        }
    }

    /// <summary>
    /// The seed as cloud-config. Pure, so the tests pin it byte for byte. Every interpolated value
    /// has been validated to a character set that needs no YAML escaping; the key is still quoted
    /// because an OpenSSH comment may follow a space, where YAML would otherwise see a comment marker.
    /// </summary>
    public static string Compose(string vmName, string seedUser, string publicKey)
    {
        var name = ArgumentGuard.VmName(vmName);
        var user = SeedUser(seedUser);
        var key = PublicKey(publicKey);

        var text = new StringBuilder();
        text.Append("#cloud-config\n");
        text.Append("# The Construct: per-VM seed written by constructd on this node. Do not edit by hand.\n");
        text.Append("hostname: ").Append(name).Append('\n');
        text.Append("manage_etc_hosts: true\n");
        text.Append("ssh_pwauth: false\n");
        text.Append("users:\n");
        text.Append("  - name: ").Append(user).Append('\n');
        text.Append("    gecos: The Construct\n");
        text.Append("    shell: /bin/bash\n");
        text.Append("    lock_passwd: true\n");
        text.Append("    sudo: \"ALL=(ALL) NOPASSWD:ALL\"\n");
        text.Append("    ssh_authorized_keys:\n");
        text.Append("      - \"").Append(key).Append("\"\n");
        text.Append("packages:\n");
        text.Append("  - qemu-guest-agent\n");
        text.Append("runcmd:\n");
        text.Append("  - [systemctl, enable, --now, qemu-guest-agent]\n");
        return text.ToString();
    }

    private static string SeedUser(string? value)
    {
        var user = value?.Trim() ?? string.Empty;
        return SeedUserRule.IsMatch(user)
            ? user
            : throw new ArgumentException("The seed user must be a lowercase Unix user name.", nameof(value));
    }

    private static string PublicKey(string? value)
    {
        var key = value?.Trim() ?? string.Empty;
        return PublicKeyRule.IsMatch(key)
            ? key
            : throw new ArgumentException("The bootstrap public key is not a single OpenSSH public-key line.", nameof(value));
    }

    private static string ReadPublicKey(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("Constructd:Iso:BootstrapPublicKeyPath is not set.");
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The bootstrap public key file does not exist.", path);
        }

        var lines = File.ReadAllLines(path).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        return lines.Length == 1
            ? PublicKey(lines[0])
            : throw new InvalidOperationException("The bootstrap public key file must hold exactly one key.");
    }

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$")]
    private static partial Regex SeedUserRegex();

    [GeneratedRegex(@"^(ssh-(ed25519|rsa)|ecdsa-sha2-nistp(256|384|521)|sk-(ssh-ed25519|ecdsa-sha2-nistp256)@openssh\.com) [A-Za-z0-9+/]+={0,3}( [A-Za-z0-9@._+-]{1,128})?$")]
    private static partial Regex PublicKeyRegex();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}:snippets/[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$")]
    private static partial Regex SnippetVolumeRegex();
}
