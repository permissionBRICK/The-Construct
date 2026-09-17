using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Domain;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using static Constructd.Tests.Capacity.CapacityMathTests;
using Constructd.Tests.Support;
namespace Constructd.Tests.Capacity;

public class CapacityApiTests
{
    [Theory]
    [InlineData("proxmox", CapacityMode.Observe, true)]
    [InlineData("proxmox", CapacityMode.Observe, false)]
    [InlineData("proxmox", CapacityMode.Enforce, true)]
    [InlineData("proxmox", CapacityMode.Enforce, false)]
    [InlineData("hyperv", CapacityMode.Observe, true)]
    [InlineData("hyperv", CapacityMode.Observe, false)]
    [InlineData("hyperv", CapacityMode.Enforce, true)]
    [InlineData("hyperv", CapacityMode.Enforce, false)]
    public async Task MeasuredRamIsAdditiveOnBothEndpoints(string backend, CapacityMode mode, bool swap)
    {
        using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:Backend"] = backend });
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var config = HostAdminDefaults.Capacity with { Mode = mode, StorageHeadroomBytes = 0 };
        await app.Service<IHostConfigStore>().SetAsync("capacity", config, "admin", default);
        var ledger = app.Service<InMemoryCapacityLedger>();
        ledger.ReadConfig = () => config;
        ledger.ReadManagedVms = () => [Vm(ram: 4)];
        ledger.ReadInventory = () => Inventory(4 * Gb,
            Actual("a", VmState.Running, 2 * Gb, 4) with { MemoryDemandBytes = Gb },
            Actual("external", VmState.Running, Gb, 8) with { MemoryDemandBytes = Gb }) with
        {
            Host = Inventory().Host with { TotalRamBytes = 8 * Gb, FreeRamBytes = 4 * Gb, UsedRamBytes = 4 * Gb,
                SwapTotalBytes = swap ? 2 * Gb : null, SwapUsedBytes = swap ? Gb : null }
        };
        foreach (var endpoint in new[] { "capacity?refresh=true", "status" })
        {
            var response = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/" + endpoint);
            var summary = response.GetProperty(endpoint == "status" ? "capacity" : "summary");
            var ram = summary.GetProperty("ram");
            Assert.Equal(8 * Gb, ram.GetProperty("totalBytes").GetInt64());
            Assert.Equal(4 * Gb, ram.GetProperty("usedBytes").GetInt64());
            Assert.Equal((backend == "proxmox" ? 2 : 3) * Gb, ram.GetProperty("vmResidentBytes").GetInt64());
            Assert.Equal((backend == "proxmox" ? 2 : 1) * Gb, ram.GetProperty("hostOwnBytes").GetInt64());
            Assert.Equal(4 * Gb, ram.GetProperty("reservedBytes").GetInt64());
            Assert.Equal(8 * Gb, ram.GetProperty("unmanagedBytes").GetInt64());
            Assert.Equal(12 * Gb, ram.GetProperty("committedBytes").GetInt64());
            Assert.Equal(4 * Gb, ram.GetProperty("physicalFreeBytes").GetInt64());
            Assert.Equal(0, ram.GetProperty("availableBytes").GetInt64());
            var headroom = (backend == "proxmox" ? 1 : 4) * Gb;
            Assert.Equal(headroom, ram.GetProperty("headroomBytes").GetInt64());
            var admission = ram.GetProperty("admission");
            Assert.Equal(mode == CapacityMode.Enforce, admission.GetProperty("enforced").GetBoolean());
            Assert.Equal(8 * Gb - headroom, admission.GetProperty("lineBytes").GetInt64());
            Assert.Equal(0, admission.GetProperty("availableBytes").GetInt64());
            if (swap)
            {
                Assert.Equal(2 * Gb, ram.GetProperty("swap").GetProperty("totalBytes").GetInt64());
                Assert.Equal(Gb, ram.GetProperty("swap").GetProperty("usedBytes").GetInt64());
            }
            else Assert.Equal(JsonValueKind.Null, ram.GetProperty("swap").ValueKind);
        }
    }

    [Fact]
    public void RefusalsCarryFrozenFieldsAndCode()
    {
        var result = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(Constructd.Api.Endpoints.CapacityEndpoints.Refusal(
            new(false, [], "ram", "user", 8, 10, 2, "user-budget", 42)));
        Assert.Equal(409, result.StatusCode); var details = result.ProblemDetails;
        Assert.Equal("capacity-exhausted", details.Extensions["code"]); Assert.Equal(10L, details.Extensions["allowed"]);
        Assert.Equal(8L, details.Extensions["requested"]); Assert.Equal(2L, details.Extensions["available"]); Assert.Equal(42L, details.Extensions["epoch"]);
    }
    [Fact]
    public async Task FakeAllowanceUsageUsesPendingLedgerReservations()
    {
        using var app = new TestApp(); using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await app.AddUserAsync("alice");
        var ledger = app.Service<Constructd.Fakes.InMemoryCapacityLedger>(); ledger.Mode = CapacityMode.Observe;
        await ledger.TryReserveAsync(new("alice", "pending-child", "create-op", [new(Constructd.Core.Domain.ReservationResource.Ram, 1234, null, null)], TimeSpan.FromMinutes(10)), default);
        var response = await admin.GetFromJsonAsync<JsonElement>("/api/v1/users/alice/allowance");
        Assert.Equal(1234, response.GetProperty("effective").GetProperty("usage").GetProperty("ramBytes").GetInt64());
    }
    [Fact]
    public async Task CapacityIsAdminOnlyAndUsesFrozenShape()
    {
        using var app = new TestApp(); using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        using var user = await app.CreateUserClientAsync("ordinary"); using var anonymous = app.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/host/capacity")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await user.GetAsync("/api/v1/host/capacity")).StatusCode);
        var response = await admin.GetFromJsonAsync<JsonElement>("/api/v1/host/capacity?refresh=true");
        Assert.Equal(JsonValueKind.Object, response.GetProperty("summary").GetProperty("ram").ValueKind);
        Assert.Equal(JsonValueKind.Array, response.GetProperty("reservations").ValueKind);
        Assert.Equal(JsonValueKind.Array, response.GetProperty("unmanaged").ValueKind);
        Assert.Equal(JsonValueKind.Array, response.GetProperty("perUser").ValueKind);
    }
}
