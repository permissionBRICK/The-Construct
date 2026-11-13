using System.Text.Json.Nodes;
using Construct.Agent.Device;
using Construct.Agent.Tests.Support;

namespace Construct.Agent.Tests;

/// <summary>The VM's Host alias in the user's ~/.ssh/config and VS Code's remote platform entry, in a temporary home.</summary>
public sealed class SshConfigTests : IDisposable
{
    private readonly FakePlatform _platform = new();
    private readonly SshClientConfig _ssh;

    public SshConfigTests()
    {
        _ssh = new SshClientConfig(_platform);
        Directory.CreateDirectory(_ssh.SshDirectory);
    }

    private string Config
    {
        get => File.ReadAllText(_ssh.ConfigPath);
        set => File.WriteAllText(_ssh.ConfigPath, value);
    }

    [Fact]
    public void Only_the_block_naming_exactly_this_alias_is_replaced_and_everything_else_stays_as_written()
    {
        Config = "Include config.d/*\r\n\r\nHost work\r\n    HostName old.example\r\n    User root\r\n\r\nMatch host *.corp exec \"true\"\r\n    ForwardAgent yes\r\n\r\nHost work other\r\n    User me\r\n\r\nhost github.com\r\n  IdentityFile ~/.ssh/gh\r\n";

        var write = _ssh.WriteAlias("work", "host.example", 2201, ipv4: true);

        Assert.Equal("replaced", write.Status);
        var expected = "Include config.d/*\r\n\r\nMatch host *.corp exec \"true\"\r\n    ForwardAgent yes\r\n\r\nHost work other\r\n    User me\r\n\r\nhost github.com\r\n  IdentityFile ~/.ssh/gh\r\n\r\n" +
            $"Host work\r\n    HostName host.example\r\n    User root\r\n    IdentityFile {_ssh.DeviceKeyPath}\r\n    IdentitiesOnly yes\r\n    Port 2201\r\n    AddressFamily inet\r\n";
        Assert.Equal(expected, Config);
        // The same write again changes nothing.
        Assert.Equal("unchanged", _ssh.WriteAlias("work", "host.example", 2201, ipv4: true).Status);
        Assert.Equal(expected, Config);
    }

    [Fact]
    public void A_new_file_gets_the_block_alone_and_removing_it_leaves_the_rest()
    {
        Assert.Equal("added", _ssh.WriteAlias("work", "work.mshome.net", 22, ipv4: false).Status);
        Assert.Equal($"Host work\n    HostName work.mshome.net\n    User root\n    IdentityFile {_ssh.DeviceKeyPath}\n    IdentitiesOnly yes\n", Config);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_ssh.ConfigPath));

        Config = "Host a\n    User x\n\n" + Config + "\nHost b\n    User y\n";
        Assert.Equal("removed", _ssh.RemoveAlias("work").Status);
        Assert.Equal("Host a\n    User x\n\nHost b\n    User y\n", Config);
        Assert.Equal("skipped", _ssh.RemoveAlias("work").Status);
    }

    [Fact]
    public void An_existing_block_keeps_its_key_when_the_key_is_there()
    {
        var key = Path.Combine(_ssh.SshDirectory, "agent_vm_ed25519");
        File.WriteAllText(key, "key");
        Config = $"Host work\n    HostName old\n    IdentityFile {key}\n";

        _ssh.WriteAlias("work", "host.example", 22, ipv4: false);

        Assert.Contains($"IdentityFile {key}\n", Config, StringComparison.Ordinal);
        Assert.Equal(key, _ssh.KeyFor("work"));
        Assert.Equal(_ssh.DeviceKeyPath, _ssh.KeyFor("other"));
    }

    [Fact]
    public void A_linked_config_is_written_through_the_link_and_keeps_its_mode()
    {
        if (OperatingSystem.IsWindows()) return;
        var target = Path.Combine(_platform.Home, "dotfiles-ssh-config");
        File.WriteAllText(target, "Host a\n    User x\n");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.CreateSymbolicLink(_ssh.ConfigPath, target);

        _ssh.WriteAlias("work", "host.example", 22, ipv4: false);

        Assert.NotNull(new FileInfo(_ssh.ConfigPath).LinkTarget);
        Assert.StartsWith("Host a\n    User x\n\nHost work\n", File.ReadAllText(target), StringComparison.Ordinal);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(target));
    }

    [Fact]
    public void A_host_line_written_with_an_equals_sign_ends_the_block_too()
    {
        Config = "Host work\n    User old\nHost=other\n    User keep\nHost = third\n    User keep3\n";

        _ssh.WriteAlias("work", "h", 22, ipv4: false);

        Assert.StartsWith("Host=other\n    User keep\nHost = third\n    User keep3\n\nHost work\n", Config, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_path_with_a_space_is_quoted_and_read_back()
    {
        var key = Path.Combine(_ssh.SshDirectory, "my key");
        File.WriteAllText(key, "key");
        Config = $"Host work\n    IdentityFile \"{key}\"\n";

        _ssh.WriteAlias("work", "h", 22, ipv4: false);

        Assert.Contains($"    IdentityFile \"{key}\"\n", Config, StringComparison.Ordinal);
        Assert.Equal(key, _ssh.KeyFor("work"));
    }

    [Fact]
    public void A_pattern_or_odd_alias_is_refused()
    {
        Assert.Equal(400, Assert.Throws<AgentFailure>(() => _ssh.WriteAlias("a b", "h", 22, false)).Status);
        Assert.Equal(400, Assert.Throws<AgentFailure>(() => _ssh.WriteAlias("*", "h", 22, false)).Status);
    }

    [Fact]
    public void Vscode_gets_the_remote_platform_entry_and_a_file_with_comments_is_left_alone()
    {
        Assert.Equal("skipped", _ssh.WriteVsCodePlatform("work").Status);
        Directory.CreateDirectory(_platform.VsCodeUserDirectory!);
        var settings = Path.Combine(_platform.VsCodeUserDirectory!, "settings.json");
        File.WriteAllText(settings, """{ "editor.fontSize": 14, "remote.SSH.remotePlatform": { "other": "windows" } }""");

        Assert.Equal("set", _ssh.WriteVsCodePlatform("work").Status);

        var written = JsonNode.Parse(File.ReadAllText(settings))!;
        Assert.Equal(14, written["editor.fontSize"]!.GetValue<int>());
        Assert.Equal("windows", written["remote.SSH.remotePlatform"]!["other"]!.GetValue<string>());
        Assert.Equal("linux", written["remote.SSH.remotePlatform"]!["work"]!.GetValue<string>());
        Assert.True(File.Exists(settings + ".bak"));
        Assert.Equal("unchanged", _ssh.WriteVsCodePlatform("work").Status);

        const string commented = "{\n  // mine\n  \"editor.fontSize\": 14\n}\n";
        File.WriteAllText(settings, commented);
        Assert.Equal("not-modified", _ssh.WriteVsCodePlatform("work").Status);
        Assert.Equal(commented, File.ReadAllText(settings));
    }

    public void Dispose() => _platform.Dispose();
}
