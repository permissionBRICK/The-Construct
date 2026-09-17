using Constructd.Core.Configuration;
using Constructd.Proxmox;
using Microsoft.Extensions.Logging.Abstractions;

namespace Constructd.Tests.Proxmox;

/// <summary>
/// The per-VM cloud-init seed: pinned byte for byte, written root-only into the snippets directory,
/// named after the VM, removed with it. The seed password the job generates is never written.
/// </summary>
public sealed class CloudInitSeedBuilderTests : IDisposable
{
    private const string Key = "ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIEQKj5fCzJY2Rvk8d7dSfKgrW08S2kkxAHKDqmCKnkvR bootstrap@construct";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "construct-seed-" + Guid.NewGuid().ToString("n"));

    [Fact]
    public void Compose_is_the_expected_cloud_config()
    {
        var text = CloudInitSeedBuilder.Compose("work-vm", "construct", Key);

        Assert.Equal(
            "#cloud-config\n" +
            "# The Construct: per-VM seed written by constructd on this node. Do not edit by hand.\n" +
            "hostname: work-vm\n" +
            "manage_etc_hosts: true\n" +
            "ssh_pwauth: false\n" +
            "users:\n" +
            "  - name: construct\n" +
            "    gecos: The Construct\n" +
            "    shell: /bin/bash\n" +
            "    lock_passwd: true\n" +
            "    sudo: \"ALL=(ALL) NOPASSWD:ALL\"\n" +
            "    ssh_authorized_keys:\n" +
            $"      - \"{Key}\"\n" +
            "packages:\n" +
            "  - qemu-guest-agent\n" +
            "runcmd:\n" +
            "  - [systemctl, enable, --now, qemu-guest-agent]\n",
            text);
    }

    [Theory]
    [InlineData("Construct")]
    [InlineData("a b")]
    [InlineData("")]
    public void Compose_refuses_an_unusable_seed_user(string user)
    {
        Assert.ThrowsAny<ArgumentException>(() => CloudInitSeedBuilder.Compose("work-vm", user, Key));
    }

    [Theory]
    [InlineData("ssh-ed25519 AAAA\nssh-rsa BBBB")]
    [InlineData("ssh-ed25519 AAAA comment with \"quote\"")]
    [InlineData("not a key")]
    public void Compose_refuses_a_key_that_is_not_one_openssh_line(string key)
    {
        Assert.ThrowsAny<ArgumentException>(() => CloudInitSeedBuilder.Compose("work-vm", "construct", key));
    }

    [Fact]
    public async Task Build_writes_the_snippet_and_returns_its_volume_id_and_remove_deletes_it()
    {
        var keyFile = Path.Combine(_dir, "bootstrap.pub");
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(keyFile, Key + "\n");
        var builder = Builder();
        var progress = new List<string>();

        var volume = await builder.BuildAsync("work-vm", "construct", "s3cret-seed-password", keyFile,
            new Progress<string>(progress.Add), CancellationToken.None);

        Assert.Equal("local:snippets/construct-work-vm-user.yaml", volume);
        var path = Path.Combine(_dir, "snippets", "construct-work-vm-user.yaml");
        var text = await File.ReadAllTextAsync(path);
        Assert.Equal(CloudInitSeedBuilder.Compose("work-vm", "construct", Key), text);
        Assert.DoesNotContain("s3cret", text, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }

        builder.Remove("work-vm");
        Assert.False(File.Exists(path));
        builder.Remove("work-vm");
    }

    [Fact]
    public async Task Build_refuses_a_missing_or_multi_key_file()
    {
        var builder = Builder();
        await Assert.ThrowsAsync<FileNotFoundException>(() => builder.BuildAsync("work-vm", "construct", "x",
            Path.Combine(_dir, "absent.pub"), null, CancellationToken.None));

        Directory.CreateDirectory(_dir);
        var two = Path.Combine(_dir, "two.pub");
        await File.WriteAllTextAsync(two, Key + "\n" + Key + "\n");
        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync("work-vm", "construct", "x",
            two, null, CancellationToken.None));
    }

    [Theory]
    [InlineData("local:snippets/construct-work-vm-user.yaml", true)]
    [InlineData("data:snippets/x.yaml", true)]
    [InlineData("local:iso/ubuntu.iso", false)]
    [InlineData("/var/lib/vz/snippets/x.yaml", false)]
    [InlineData("local:snippets/../etc/passwd", false)]
    public void Snippet_volume_ids_are_recognized(string value, bool expected)
    {
        Assert.Equal(expected, CloudInitSeedBuilder.IsSnippetVolume(value));
    }

    private CloudInitSeedBuilder Builder() => new(
        new ConstructdOptions
        {
            Backend = "proxmox",
            Proxmox = new ProxmoxOptions { SnippetStorage = "local", SnippetDir = Path.Combine(_dir, "snippets") },
        },
        NullLogger<CloudInitSeedBuilder>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
