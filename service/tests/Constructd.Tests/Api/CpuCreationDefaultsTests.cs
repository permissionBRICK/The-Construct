using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Delegation;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public sealed class CpuCreationDefaultsTests
{
    public static IEnumerable<object[]> Allowances()
    {
        foreach (var child in new[] { false, true })
        {
            yield return [child, 8, null!, null!, null!, 8];
            yield return [child, 8, 3, null!, null!, 3];
            yield return [child, 8, null!, 6, null!, 4]; // Two CPUs already reserved by this owner.
            yield return [child, 8, null!, null!, 5, 3]; // Two CPUs already reserved on this host.
            yield return [child, 8, 7, 6, 5, 3];
            yield return [child, 128, null!, null!, null!, child ? 128 : 64];
        }
    }

    [Theory, MemberData(nameof(Allowances))]
    public async Task OmittedCpuUsesHostAllowance(bool child, int logical, int? perVm, int? userBudget, int? hostBudget, int expected)
    {
        await using var app = new TestApp();
        using var client = child ? await LifecycleTests.Setup(app, false) : await app.CreateUserClientAsync("alice");
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Mode = CapacityMode.Observe;
        ledger.Inventory = ledger.Inventory with { Complete = true, CpuLogical = logical, CpuBudget = hostBudget };
        await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with
            { AllowChildCreation = true, MaxRetainedChildren = 5, CpuBudget = userBudget }, default);
        await app.Service<IHostConfigStore>().SetAsync("capacity", new CapacityConfig(CapacityMode.Observe, null, 0, hostBudget, perVm, 30, 60), "test", default);
        Assert.True((await ledger.TryReserveAsync(new("alice", "existing", "existing-job",
            [new(ReservationResource.Cpu, 2, null, null)], TimeSpan.FromMinutes(10)), default)).Allowed);
        var job = await LifecycleTests.Finish(app, await client.PostAsJsonAsync(Route(child), Request(child)));
        Assert.Equal(JobState.Succeeded, job.State);
        var vm = (await app.Vms.GetAsync("default-cpu", default))!;
        Assert.Equal(expected, vm.Cpu);
        if (child) Assert.Equal(expected, vm.Hardware!.Cpus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReplayKeepsOriginalDefaultWhenAllowanceChanges(bool child)
    {
        await using var app = new TestApp();
        using var client = child ? await LifecycleTests.Setup(app, false) : await app.CreateUserClientAsync("alice");
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Mode = CapacityMode.Observe;
        ledger.Inventory = ledger.Inventory with { Complete = true, CpuLogical = 8 };
        client.DefaultRequestHeaders.Add("X-Construct-Operation-Key", "default-cpu-replay");
        var job = await LifecycleTests.Finish(app, await client.PostAsJsonAsync(Route(child), Request(child)));
        Assert.Equal(JobState.Succeeded, job.State);
        ledger.Inventory = ledger.Inventory with { Complete = false, CpuLogical = 0, CpuBudget = 0 };
        var replay = await client.PostAsJsonAsync(Route(child), Request(child));
        replay.EnsureSuccessStatusCode();
        Assert.Equal(job.Id, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString());
        Assert.Equal(8, (await app.Vms.GetAsync("default-cpu", default))!.Cpu);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(true, false)]
    public async Task MissingCapacityOrExhaustedAllowanceDoesNotGuess(bool child, bool complete)
    {
        await using var app = new TestApp();
        using var client = child ? await LifecycleTests.Setup(app, false) : await app.CreateUserClientAsync("alice");
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Mode = CapacityMode.Observe;
        ledger.Inventory = ledger.Inventory with { Complete = complete, CpuLogical = 8, CpuBudget = 0 };
        var response = await client.PostAsJsonAsync(Route(child), Request(child));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(complete ? "capacity-exhausted" : "capacity-unavailable", await response.Content.ReadAsStringAsync());
        Assert.Null(await app.Vms.GetAsync("default-cpu", default));
    }

    [Fact]
    public async Task PrimaryTokenUsesOwnersDefaultAndExplicitCpuStillOverrides()
    {
        await using var app = new TestApp();
        using var owner = await LifecycleTests.Setup(app, false);
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.Inventory = ledger.Inventory with { Complete = true, CpuLogical = 8 };
        await app.Service<IUserAllowanceStore>().SetAllowanceAsync("alice", UserAllowance.Unset with
            { AllowChildCreation = true, MaxRetainedChildren = 5, CpuBudget = 3 }, default);
        using var token = app.CreateVmTokenClient(await app.Service<IVmTokenIssuer>().IssueVmTokenAsync("parent", VmTokenKind.Primary, default));
        Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await token.PostAsJsonAsync(Route(true), Request(true)))).State);
        Assert.Equal(3, (await app.Vms.GetAsync("default-cpu", default))!.Cpu);
        Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await token.PostAsJsonAsync(Route(true), LifecycleTests.Request("explicit-cpu")))).State);
        Assert.Equal(1, (await app.Vms.GetAsync("explicit-cpu", default))!.Cpu);
        var invalid = await token.PostAsJsonAsync(Route(true), new { name = "invalid-cpu", cpus = 0, ramMb = 512, diskGb = 1,
            lifetime = "10m", media = new { installMediaId = "install" }, start = false });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private static string Route(bool child) => child ? "/api/v1/vms/parent/children" : "/api/v1/vms";
    private static object Request(bool child) => child
        ? new { name = "default-cpu", ramMb = 512, diskGb = 1, lifetime = "10m", media = new { installMediaId = "install" }, start = false }
        : new { name = "default-cpu", ramGb = 4, diskGb = 32 };
}
