using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Api.Admin;
using Constructd.Api.Composition;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// Adopting a VM the old client made (<see cref="LocalVmAdoption"/>), through the admin CLI and the
/// job, against the fake driver and a scripted guest. The VM must keep running with its hardware
/// untouched, and the guest must only be switched once it is proven to be that VM.
/// </summary>
public sealed class LocalVmAdoptionTests : IDisposable
{
    private const string Firmware = "0b9a2d11-1c2b-4f7e-9e5a-2f63a0c1d2e3";
    private const string MachineId = "0123456789abcdef0123456789abcdef";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "construct-local-adoption-" + Guid.NewGuid().ToString("N"));
    private readonly string _vmId = Guid.NewGuid().ToString("D");

    public LocalVmAdoptionTests()
    {
        Directory.CreateDirectory(_dir);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=pc.mshome.net", key, HashAlgorithmName.SHA256);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(CertPath, cert.Export(X509ContentType.Pfx));
        CertificatePem = cert.ExportCertificatePem();
        File.WriteAllText(SettingsPath, """
            {"installedCommit":"abc","gitUserName":"Ada","gitEmail":"ada@example.test","gitCredentialStore":true,
             "projects":["web","api"],"vsCodeTunnel":true,"smbShare":false,"t3code":true,"t3codeChannel":"nightly"}
            """);
        File.WriteAllText(KeyPath, "-----BEGIN OPENSSH PRIVATE KEY-----\nAAAA\n-----END OPENSSH PRIVATE KEY-----\n");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private string CertPath => Path.Combine(_dir, "service.pfx");
    private string SettingsPath => Path.Combine(_dir, ".construct-settings.json");
    private string KeyPath => Path.Combine(_dir, "agent_vm_ed25519");
    private string CertificatePem { get; }

    private ServiceProvider Services(FakeGuest guest, bool sqlite = false)
    {
        var options = new ConstructdOptions
        {
            Fake = true, Persistence = sqlite ? PersistenceMode.Sqlite : PersistenceMode.Memory,
            DatabasePath = Path.Combine(_dir, "data", "constructd.db"), CertPath = CertPath, PublicHost = "pc.mshome.net",
        };
        Directory.CreateDirectory(Path.Combine(_dir, "data"));
        var services = new ServiceCollection().AddLogging().AddSingleton(options);
        services.AddConstructdServices(options);
        services.AddSingleton<ISshRunner>(guest);
        var provider = services.BuildServiceProvider();
        var driver = provider.GetRequiredService<FakeHypervisorDriver>();
        driver.Facts["agent-vm"] = new HypervisorVmFacts(_vmId, VmState.Running, 6, 16L << 30, 256L << 30, "{" + Firmware.ToUpperInvariant() + "}");
        driver.SetState("agent-vm", VmState.Running);
        return provider;
    }

    private async Task<(int Exit, string Output, string Error)> AdoptAsync(ServiceProvider services, string owner = "alice", string? state = null)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        List<string> args = ["vms", "adopt-local", "agent-vm", "--owner", owner, "--settings", SettingsPath, "--ssh-key", KeyPath, "--json"];
        if (state is not null) args.AddRange(["--instance-state", state]);
        var exit = await AdminCli.RunAsync(args, services, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_cli_adopts_a_running_vm_with_its_real_hardware_and_switches_the_guest(bool sqlite)
    {
        var guest = new FakeGuest();
        await using var services = Services(guest, sqlite);
        await services.GetRequiredService<IUserStore>().CreateAsync(new User("alice", Role.Admin, 10, DateTimeOffset.UtcNow), CancellationToken.None);
        var driver = services.GetRequiredService<FakeHypervisorDriver>();

        var first = await AdoptAsync(services);

        Assert.True(first.Exit == 0, first.Error);
        var result = JsonDocument.Parse(first.Output).RootElement;
        var port = result.GetProperty("sshPort").GetInt32();
        var vm = Assert.Single(await services.GetRequiredService<IVmRepository>().ListAsync(null, CancellationToken.None));
        Assert.Equal(("agent-vm", "alice", 6, 16384, 256, _vmId), (vm.Name, vm.Owner, vm.Cpu, vm.RamMb, vm.DiskGb, vm.Incarnation));
        Assert.Equal(port, vm.SshForwardPort);
        Assert.Equal(VmState.Running, driver.StateOf("agent-vm"));
        // Read, never rebuilt, powered or resized.
        Assert.All(driver.Calls, call => Assert.StartsWith("facts:", call));

        // The old client's choices are the VM's desired state, plus the service settings.
        var desired = (await services.GetRequiredService<IVmDesiredStateSource>().GetDesiredAsync("agent-vm", CancellationToken.None))!.Desired;
        Assert.Equal(["web", "api"], desired.Projects);
        Assert.Equal(("Ada", "ada@example.test", (bool?)true), (desired.Settings.GitUserName, desired.Settings.GitEmail, desired.Settings.GitCredentialStore));
        Assert.Equal(("true", "false", "true", "nightly"), (desired.Settings.VsCodeTunnel, desired.Settings.SmbShare, desired.Settings.T3Code, desired.Settings.T3CodeChannel));
        Assert.Equal("https://pc.mshome.net:7462", desired.Settings.ServiceUrl);
        Assert.Equal(("pc.mshome.net", (int?)port), (desired.Settings.ExternalHost, desired.Settings.ExternalSshPort));
        Assert.Equal(CertificatePem, Encoding.UTF8.GetString(Convert.FromBase64String(desired.Settings.ServiceCaB64!)));

        // The guest: identity first, then bin/adopt-host.py with the settings on stdin, as root with the engine's copy of the key.
        Assert.Equal(2, guest.Calls.Count);
        Assert.StartsWith("cat /etc/machine-id /sys/class/dmi/id/product_uuid", guest.Calls[0].Command);
        var enroll = guest.Calls[1];
        Assert.Equal(("root", "agent-vm.fake.local"), (enroll.Target.User, enroll.Target.Host));
        Assert.Equal(Path.Combine(_dir, "data", "ssh", "agent-vm_root"), enroll.Target.IdentityFile);
        Assert.Equal(File.ReadAllText(KeyPath), File.ReadAllText(enroll.Target.IdentityFile!));
        var code = System.Text.RegularExpressions.Regex.Match(enroll.Command, "b64decode\\('([A-Za-z0-9+/=]+)'\\)").Groups[1].Value;
        Assert.Equal(File.ReadAllText(RepoFile("bin/adopt-host.py")), Encoding.UTF8.GetString(Convert.FromBase64String(code)));
        var payload = JsonDocument.Parse(enroll.StandardInput!).RootElement;
        Assert.Equal(("agent-vm", "alice", MachineId), (payload.GetProperty("name").GetString(), payload.GetProperty("owner").GetString(), payload.GetProperty("machineId").GetString()));
        Assert.Equal(("https://pc.mshome.net:7462", "pc.mshome.net", port),
            (payload.GetProperty("serviceUrl").GetString(), payload.GetProperty("publicHost").GetString(), payload.GetProperty("sshPort").GetInt32()));
        Assert.Equal(CertificatePem, payload.GetProperty("certificate").GetString());
        Assert.Equal(LocalVmAdoption.GuestHelpers.Order(), payload.GetProperty("files").EnumerateObject().Select(f => f.Name).Order());
        var token = payload.GetProperty("vmToken").GetString()!;
        Assert.NotNull(await services.GetRequiredService<ITokenService>().ValidateAsync(token, CancellationToken.None));
        Assert.DoesNotContain(token, first.Output + first.Error);

        // A repeat resumes the same VM: same forward, a fresh token, the guest switched again.
        var second = await AdoptAsync(services);
        Assert.True(second.Exit == 0, second.Error);
        Assert.Equal(port, JsonDocument.Parse(second.Output).RootElement.GetProperty("sshPort").GetInt32());
        Assert.Single(await services.GetRequiredService<IVmRepository>().ListAsync(null, CancellationToken.None));
    }

    [Fact]
    public async Task A_guest_that_is_not_this_vm_is_left_alone_and_gets_no_token()
    {
        var guest = new FakeGuest { Firmware = Guid.NewGuid().ToString("D") };
        await using var services = Services(guest);
        await services.GetRequiredService<IUserStore>().CreateAsync(new User("alice", Role.Admin, 10, DateTimeOffset.UtcNow), CancellationToken.None);

        var adopted = await AdoptAsync(services);

        Assert.Equal(AdminExitCode.Failed, adopted.Exit);
        Assert.Contains("not this Hyper-V VM", adopted.Error);
        Assert.Single(guest.Calls);
        var vm = await services.GetRequiredService<IVmRepository>().GetAsync("agent-vm", CancellationToken.None);
        Assert.Null(vm!.VmTokenHash);
        Assert.Equal(VmState.Running, services.GetRequiredService<FakeHypervisorDriver>().StateOf("agent-vm"));
    }

    [Fact]
    public async Task A_failed_guest_switch_fails_the_adoption()
    {
        var guest = new FakeGuest { EnrollExit = 1 };
        await using var services = Services(guest);
        await services.GetRequiredService<IUserStore>().CreateAsync(new User("alice", Role.Admin, 10, DateTimeOffset.UtcNow), CancellationToken.None);

        var adopted = await AdoptAsync(services);

        Assert.Equal(AdminExitCode.Failed, adopted.Exit);
        Assert.Contains("guest: Guest enrollment failed: host refused", adopted.Error);
    }

    [Theory]
    [InlineData("off")]
    [InlineData("missing")]
    [InlineData("user")]
    [InlineData("other-vm")]
    public async Task An_invalid_adoption_registers_nothing_and_touches_neither_vm_nor_guest(string scenario)
    {
        var guest = new FakeGuest();
        await using var services = Services(guest);
        var users = services.GetRequiredService<IUserStore>();
        await users.CreateAsync(new User("alice", scenario == "user" ? Role.User : Role.Admin, 10, DateTimeOffset.UtcNow), CancellationToken.None);
        var driver = services.GetRequiredService<FakeHypervisorDriver>();
        if (scenario == "off") driver.SetState("agent-vm", VmState.Off);
        if (scenario == "missing") driver.Facts.Clear();
        if (scenario == "other-vm")
            await services.GetRequiredService<IVmRepository>().AddAsync(new Vm("agent-vm", "alice", 2, 4, 50, DateTimeOffset.UtcNow, VmState.Running, null, null,
                IdlePolicy.Disabled, Vm.NoForwards, TokenKind: VmTokenKind.Primary, Incarnation: Guid.NewGuid().ToString("D")), 10, CancellationToken.None);

        var adopted = await AdoptAsync(services);

        Assert.Equal(scenario == "missing" ? AdminExitCode.NotFound : AdminExitCode.Conflict, adopted.Exit);
        Assert.Empty(guest.Calls);
        Assert.Equal(scenario == "other-vm" ? 1 : 0, (await services.GetRequiredService<IVmRepository>().ListAsync(null, CancellationToken.None)).Count);
        Assert.Null(await services.GetRequiredService<IVmDesiredStateSource>().GetDesiredAsync("agent-vm", CancellationToken.None));
        Assert.DoesNotContain(driver.Calls, call => !call.StartsWith("facts:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_job_adopts_from_the_content_the_agent_sends()
    {
        var guest = new FakeGuest();
        using var app = new TestApp(new Dictionary<string, string?>
        {
            ["Constructd:CertPath"] = CertPath, ["Constructd:PublicHost"] = "pc.mshome.net",
        }, services => services.AddSingleton<ISshRunner>(guest));
        var admin = await app.CreateUserClientAsync("alice", Role.Admin);
        var user = await app.CreateUserClientAsync("bob");
        app.Driver.Facts["agent-vm"] = new HypervisorVmFacts(_vmId, VmState.Running, 4, 8L << 30, 100L << 30, Firmware);
        app.Driver.SetState("agent-vm", VmState.Running);
        var body = new
        {
            settings = JsonNode.Parse(File.ReadAllText(SettingsPath)),
            instanceState = new { projects = new[] { "docs" }, t3code = false },
            sshPrivateKey = File.ReadAllText(KeyPath),
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await user.PostAsJsonAsync("/api/v1/vms/agent-vm/adopt", body)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/vms/agent-vm/adopt", new { settings = new { } })).StatusCode);
        var response = await admin.PostAsJsonAsync("/api/v1/vms/agent-vm/adopt", body);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var jobId = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("jobId").GetString()!;
        var job = await admin.WaitForJobAsync(jobId);
        Assert.Equal(JobState.Succeeded, job.State);
        Assert.Equal(LocalVmAdoption.JobKind, job.Kind);
        var vm = (await app.Vms.GetAsync("agent-vm", CancellationToken.None))!;
        Assert.Equal(("alice", 4, 8192, 100), (vm.Owner, vm.Cpu, vm.RamMb, vm.DiskGb));
        // A named instance's own file wins over the install's top level; the git identity stays install-wide.
        var desired = (await app.Service<IVmDesiredStateSource>().GetDesiredAsync("agent-vm", CancellationToken.None))!.Desired;
        Assert.Equal(["docs"], desired.Projects);
        Assert.Equal(("false", null, "Ada"), (desired.Settings.T3Code, desired.Settings.VsCodeTunnel, desired.Settings.GitUserName));
        Assert.Equal(2, guest.Calls.Count);
        Assert.DoesNotContain(job.Progress, line => line.Text.Contains("PRIVATE KEY", StringComparison.Ordinal));
    }

    [Fact]
    public void Settings_the_old_client_never_stored_stay_the_engines_defaults()
    {
        var desired = LegacyClientSettings.ToDesired(JsonNode.Parse("""{"t3codeChannel":"beta","smbShare":"yes","projects":[" a ","a",3]}""") as JsonObject, null);

        Assert.Equal(["a"], desired.Projects);
        Assert.Equal(new ProvisionSettings(), desired.Settings);
        Assert.Empty(LegacyClientSettings.ToDesired(null, null).Projects);
    }

    private static string RepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException(relative);
    }

    /// <summary>A guest that answers the identity read and the enrollment, and records both.</summary>
    private sealed class FakeGuest : ISshRunner
    {
        public string Firmware { get; init; } = LocalVmAdoptionTests.Firmware;
        public int EnrollExit { get; init; }
        public List<Provisioning.SshCall> Calls { get; } = [];

        public Task<int> RunAsync(SshTarget target, string command, string? standardInput, IProgress<string>? lines, TimeSpan timeout, CancellationToken ct)
        {
            lock (Calls) Calls.Add(new(target, command, standardInput));
            if (command.StartsWith("cat /etc/machine-id", StringComparison.Ordinal))
            {
                lines?.Report(MachineId);
                lines?.Report(Firmware);
                return Task.FromResult(0);
            }
            lines?.Report(EnrollExit == 0 ? "Guest enrolled; existing applications remain running." : "Guest enrollment failed: host refused");
            return Task.FromResult(EnrollExit);
        }

        public Task CopyAsync(SshTarget target, string localPath, string remotePath, CancellationToken ct) =>
            throw new InvalidOperationException("adoption copies nothing");
    }
}
