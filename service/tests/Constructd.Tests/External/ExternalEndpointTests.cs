using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.External;

/// <summary>
/// The external-endpoint platform (<c>Constructd:Backend = external</c>): the real host, not in fake
/// mode, on Linux, managing a "VM" that is a local TCP listener standing in for its SSH port.
/// </summary>
public sealed class ExternalEndpointTests : IDisposable
{
    private readonly TcpListener _ssh = new(IPAddress.Loopback, 0);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "constructd-external-" + Guid.NewGuid().ToString("n"));

    public ExternalEndpointTests() => _ssh.Start();

    private int SshPort => ((IPEndPoint)_ssh.LocalEndpoint).Port;

    public void Dispose()
    {
        _ssh.Stop();
        _ssh.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private TestApp App() => new(new Dictionary<string, string?>
    {
        ["Constructd:Fake"] = "false",
        ["Constructd:Backend"] = "external",
        ["Constructd:DatabasePath"] = Path.Combine(_root, "constructd.db"),
        ["Constructd:Backups:RootDir"] = Path.Combine(_root, "backups"),
        ["Constructd:HostAdmin:Media:RootDir"] = Path.Combine(_root, "media"),
        ["Constructd:HostAdmin:Source:RootDir"] = Path.Combine(_root, "source"),
        ["Constructd:Iso:CacheDir"] = Path.Combine(_root, "iso"),
        ["Constructd:ListenAddress"] = "127.0.0.1",
        ["Constructd:External:Vms:rig:SshHost"] = "127.0.0.1",
        ["Constructd:External:Vms:rig:SshPort"] = SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["Constructd:External:Vms:rig:User"] = "construct",
    });

    private static async Task<JobResponse> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostJsonAsync("/api/v1/vms", new { name, cpu = 1, ramGb = 1, diskGb = 8 });
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return await client.WaitForJobAsync((await response.ReadAsync<JobAcceptedResponse>()).JobId);
    }

    [Fact]
    public async Task Registers_a_vm_without_a_cpu_count()
    {
        using var app = App();
        var client = await app.CreateUserClientAsync("alice");
        var response = await client.PostJsonAsync("/api/v1/vms", new { name = "rig", ramGb = 1, diskGb = 8 });
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var job = await client.WaitForJobAsync((await response.ReadAsync<JobAcceptedResponse>()).JobId);
        Assert.True(job.State == JobState.Succeeded, job.Error);
    }

    [Fact]
    public async Task Starts_without_fake_mode_and_registers_reaches_and_unregisters_a_listed_vm()
    {
        using var app = App();
        var client = await app.CreateUserClientAsync("alice");

        var health = await (await client.GetAsync("/api/v1/health")).ReadAsync<JsonElement>();
        Assert.Equal("ok", health.GetProperty("status").GetString());
        Assert.DoesNotContain("children", health.GetProperty("apiFeatures").EnumerateArray().Select(f => f.GetString()));

        var created = await CreateAsync(client, "rig");
        Assert.True(created.State == JobState.Succeeded, created.Error);
        var endpoint = created.ResultElement("endpoint");
        var port = endpoint.GetProperty("sshPort").GetInt32();
        Assert.False(string.IsNullOrEmpty(created.VmToken()));

        // The SSH port the host advertises relays to the VM's own SSH port.
        using (var relayed = new TcpClient())
        {
            var accept = _ssh.AcceptTcpClientAsync();
            await relayed.ConnectAsync(IPAddress.Loopback, port);
            using var reached = await accept.WaitAsync(TimeSpan.FromSeconds(10));
        }

        var state = await (await client.GetAsync("/api/v1/vms/rig/state")).ReadAsync<VmStateResponse>();
        Assert.Equal(VmState.Running, state.State);

        var deleted = await client.DeleteAsync("/api/v1/vms/rig");
        Assert.Equal(HttpStatusCode.Accepted, deleted.StatusCode);
        var job = await client.WaitForJobAsync((await deleted.ReadAsync<JobAcceptedResponse>()).JobId);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/vms/rig")).StatusCode);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("start")]
    public async Task Power_operations_report_unsupported(string action)
    {
        using var app = App();
        var client = await app.CreateUserClientAsync("alice");
        Assert.Equal(JobState.Succeeded, (await CreateAsync(client, "rig")).State);

        var power = await client.PostJsonAsync("/api/v1/vms/rig/power", new { action });
        Assert.Equal(HttpStatusCode.Conflict, power.StatusCode);
        Assert.Equal("unsupported-on-host", (await power.ReadAsync<JsonElement>()).GetProperty("code").GetString());

        var lifecycle = await client.PostJsonAsync("/api/v1/vms/rig/lifecycle", new { action = action == "stop" ? "shutdown" : action });
        Assert.Equal(HttpStatusCode.Conflict, lifecycle.StatusCode);
        Assert.Equal("unsupported-on-host", (await lifecycle.ReadAsync<JsonElement>()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Creating_a_vm_that_is_not_listed_fails_the_job()
    {
        using var app = App();
        var client = await app.CreateUserClientAsync("alice");

        var job = await CreateAsync(client, "unlisted");

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("external-vm-not-configured", job.Error);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/v1/vms/unlisted")).StatusCode);
    }

    [Fact]
    public async Task Driver_reports_the_listed_endpoint_and_unknown_state_when_ssh_does_not_answer()
    {
        var options = new ConstructdOptions();
        options.External.Vms["rig"] = new ExternalVmOptions { SshHost = "127.0.0.1", SshPort = SshPort, User = "construct" };
        var vms = new InMemoryVmRepository();
        var driver = new ExternalEndpointDriver(options, vms);

        Assert.False(driver.Capabilities.Power);
        Assert.Equal(new Endpoint("127.0.0.1", SshPort, "127.0.0.1"), await driver.GetEndpointAsync("RIG", CancellationToken.None));
        Assert.Null(await driver.GetEndpointAsync("other", CancellationToken.None));

        // Absent until the engine has registered it, so creation may proceed.
        Assert.Equal(VmState.Absent, await driver.GetStateAsync("rig", CancellationToken.None));
        Assert.Equal(VmAddOutcome.Added, await vms.AddAsync(
            new Vm("rig", "alice", 1, 1, 8, DateTimeOffset.UtcNow, VmState.Unknown, null, null, IdlePolicy.Disabled, []), 5, CancellationToken.None));
        Assert.Equal(VmState.Running, await driver.GetStateAsync("rig", CancellationToken.None));
        Assert.Equal(VmState.Absent, await driver.GetStateAsync("other", CancellationToken.None));

        _ssh.Stop();
        Assert.Equal(VmState.Unknown, await driver.GetStateAsync("rig", CancellationToken.None));
        Assert.False(await driver.WaitReachableAsync("rig", TimeSpan.Zero, null, CancellationToken.None));
        await Assert.ThrowsAsync<ExternalEndpointException>(() => driver.StopAsync("rig", CancellationToken.None));
    }

    [Theory]
    [InlineData("", 22, "u", "SshHost")]
    [InlineData("h", 0, "u", "SshPort")]
    [InlineData("h", 22, "", "User")]
    public void Validation_names_the_missing_setting(string host, int port, string user, string setting)
    {
        var external = new ExternalOptions();
        external.Vms["rig"] = new ExternalVmOptions { SshHost = host, SshPort = port, User = user };

        Assert.Contains(setting, ExternalEndpointDriver.Validate(external));
    }
}
