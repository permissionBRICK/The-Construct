using System.Text.Json;
using Constructd.Api.Admin;
using Constructd.Api.Composition;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

public sealed class VmAdoptionTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "construct-adoption-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    private ServiceProvider Services(bool sqlite = false)
    {
        var options = new ConstructdOptions { Fake = true, Persistence = sqlite ? PersistenceMode.Sqlite : PersistenceMode.Memory,
            DatabasePath = Path.Combine(directory, "constructd.db") };
        var services = new ServiceCollection().AddLogging().AddSingleton(options);
        services.AddConstructdServices(options);
        return services.BuildServiceProvider();
    }

    private static async Task<(int Exit, string Output)> Adopt(ServiceProvider services, string id, string owner = "alice")
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await AdminCli.RunAsync(["vms", "adopt", "agent-vm", "--owner", owner,
            "--cpu", "4", "--ram-mb", "8192", "--disk-gb", "100", "--incarnation", id, "--json"],
            services, output, error, CancellationToken.None);
        return (exit, exit == 0 ? output.ToString() : error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Adoption_preserves_running_vm_and_resumes_same_identity_with_one_forward(bool sqlite)
    {
        await using var services = Services(sqlite);
        var users = services.GetRequiredService<IUserStore>();
        await users.CreateAsync(new User("alice", Role.Admin, 10, DateTimeOffset.UtcNow), CancellationToken.None);
        var driver = services.GetRequiredService<FakeHypervisorDriver>();
        driver.SetState("agent-vm", VmState.Running);
        var id = Guid.NewGuid().ToString("D");
        var first = await Adopt(services, id);
        Assert.Equal(0, first.Exit);
        var payload = JsonDocument.Parse(first.Output).RootElement;
        var port = payload.GetProperty("sshPort").GetInt32();
        var firstToken = payload.GetProperty("vmToken").GetString()!;
        var second = await Adopt(services, id);
        Assert.Equal(0, second.Exit);
        Assert.Equal(port, JsonDocument.Parse(second.Output).RootElement.GetProperty("sshPort").GetInt32());
        var vms = services.GetRequiredService<IVmRepository>();
        var vm = Assert.Single(await vms.ListAsync(null, CancellationToken.None));
        Assert.Equal(id, vm.Incarnation);
        Assert.Equal(8192, vm.RamMb);
        Assert.Equal(VmTokenKind.Primary, vm.TokenKind);
        Assert.Equal(SharingScope.Private, vm.Sharing);
        Assert.True(vm.IdlePolicy.IsDisabled);
        Assert.Equal(VmState.Running, driver.StateOf("agent-vm"));
        Assert.DoesNotContain(driver.Calls, call => call.StartsWith("create:") || call.StartsWith("remove:") || call.StartsWith("stop:") || call.StartsWith("save:"));
        Assert.Null(await services.GetRequiredService<ITokenService>().ValidateAsync(firstToken, CancellationToken.None));
        var token = JsonDocument.Parse(second.Output).RootElement.GetProperty("vmToken").GetString()!;
        Assert.NotNull(await services.GetRequiredService<ITokenService>().ValidateAsync(token, CancellationToken.None));
        var conflict = await Adopt(services, Guid.NewGuid().ToString("D"));
        Assert.Equal(AdminExitCode.Conflict, conflict.Exit);
        Assert.NotNull(await services.GetRequiredService<ITokenService>().ValidateAsync(token, CancellationToken.None));
    }

    [Theory]
    [InlineData(VmState.Absent, Role.Admin)]
    [InlineData(VmState.Off, Role.Admin)]
    [InlineData(VmState.Running, Role.User)]
    public async Task Invalid_adoption_never_registers_or_changes_the_vm(VmState state, Role role)
    {
        await using var services = Services();
        await services.GetRequiredService<IUserStore>().CreateAsync(new User("alice", role, 10, DateTimeOffset.UtcNow), CancellationToken.None);
        var driver = services.GetRequiredService<FakeHypervisorDriver>();
        driver.SetState("agent-vm", state);
        Assert.Equal(AdminExitCode.Conflict, (await Adopt(services, Guid.NewGuid().ToString("D"))).Exit);
        Assert.Empty(await services.GetRequiredService<IVmRepository>().ListAsync(null, CancellationToken.None));
        Assert.Equal(state, driver.StateOf("agent-vm"));
    }

    [Fact]
    public async Task A_registered_vm_is_never_taken_over()
    {
        await using var services = Services();
        await services.GetRequiredService<IUserStore>().CreateAsync(new User("alice", Role.Admin, 10, DateTimeOffset.UtcNow), CancellationToken.None);
        await services.GetRequiredService<IVmRepository>().AddAsync(new Vm("agent-vm", "bob", 2, 4, 50,
            DateTimeOffset.UtcNow, VmState.Running, null, "unchanged", IdlePolicy.Disabled, Vm.NoForwards), 10, CancellationToken.None);
        Assert.Equal(AdminExitCode.Conflict, (await Adopt(services, Guid.NewGuid().ToString("D"))).Exit);
        var vm = await services.GetRequiredService<IVmRepository>().GetAsync("agent-vm", CancellationToken.None);
        Assert.Equal("bob", vm!.Owner);
        Assert.Equal("unchanged", vm.VmTokenHash);
    }
}
