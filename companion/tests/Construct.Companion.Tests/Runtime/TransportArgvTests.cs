using Construct.Companion.Core;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Runtime;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Runtime;
namespace Construct.Companion.Tests.Runtime;

public sealed class TransportArgvTests
{
    [Fact]
    public async Task ConcreteTransportPinsScriptWatchAndTunnelArgv()
    {
        var runner = new FakeProcessRunner(); var probe = new FakePortProbe(); var config = new SshConfiguration(VmHost: "vm.test", HostAlias: "dev", SshPort: 2222);
        var ssh = new ProcessSshTransport(runner, probe, config, "ssh.exe", "/keys/dev");
        await ssh.RunRemoteScriptAsync("echo 'payload'", TimeSpan.FromSeconds(20));
        Assert.Equal(new[] { "-i", "/keys/dev", "-o", "IdentitiesOnly=yes", "-o", "BatchMode=yes", "-o", "StrictHostKeyChecking=accept-new", "-o", "ConnectTimeout=12", "-p", "2222", "root@vm.test", SshArgs.WrapScriptCommand("echo 'payload'") }, runner.Invocations[0].Arguments);
        Assert.Null(runner.Invocations[0].StandardInput); Assert.DoesNotContain("payload", string.Join(' ', runner.Invocations[0].Arguments));
        await using var watch = ssh.SpawnWatch("echo test"); Assert.Equal(RuntimeSshArgs.Watch(config, "echo test", "/keys/dev"), runner.Invocations[1].Arguments);
        await using var tunnel = ssh.SpawnTunnel(new(19000, 80, "0.0.0.0", "10.0.0.2", 8080));
        Assert.Contains("0.0.0.0:19000:10.0.0.2:8080", runner.Invocations[2].Arguments);
        await using var reverse = ssh.SpawnTunnel(new(30000, 8767, Direction: TunnelDirection.Reverse)); Assert.Contains("8767:127.0.0.1:30000", runner.Invocations[3].Arguments);
        Assert.True(await ssh.ProbePortAsync(19000, "0.0.0.0")); Assert.Equal((19000, "0.0.0.0"), probe.Probes.Single());
    }
    [Fact]
    public void ExecutableResolutionPrefersPathThenWindowsOpenSsh()
    {
        var files = new FakeFileSystem(); files.WriteFileAtomic("C:\\Windows\\System32\\OpenSSH\\ssh.exe", []);
        Assert.Equal("C:\\Windows\\System32\\OpenSSH\\ssh.exe", SshExecutable.Resolve(files, ["C:\\tools"], "C:\\Windows", true));
        files.WriteFileAtomic("C:\\tools\\ssh.exe", []); Assert.Equal("C:\\tools\\ssh.exe", SshExecutable.Resolve(files, ["C:\\tools"], "C:\\Windows", true));
    }
}
