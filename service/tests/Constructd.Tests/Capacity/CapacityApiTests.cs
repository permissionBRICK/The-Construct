using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
namespace Constructd.Tests.Capacity;

public class CapacityApiTests
{
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
