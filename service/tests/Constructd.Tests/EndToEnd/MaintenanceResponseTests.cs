using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Delegation;
using Constructd.Tests.Support;

namespace Constructd.Tests.EndToEnd;

public sealed class MaintenanceResponseTests
{
    [Theory]
    [InlineData("child-create", false)]
    [InlineData("child-delete", false)]
    [InlineData("primary-create", false)]
    [InlineData("sharing", true)]
    public async Task RefusalIncludesPhaseRetryAndUpdateIdentity(string operation, bool freeze)
    {
        await using var app = new TestApp();
        using var owner = await LifecycleTests.Setup(app);
        var state = freeze ? MaintenanceState.Maintenance : MaintenanceState.Draining;
        app.Service<IMaintenanceGate>().Enter(state, "update-e2e");
        await app.Service<IHostConfigStore>().SetAsync("maintenance", new MaintenanceMarker(state, "update-e2e", app.Clock.UtcNow), "system", default);
        using var response = operation switch
        {
            "child-create" => await owner.PostAsJsonAsync("/api/v1/vms/parent/children", LifecycleTests.Request("second")),
            "child-delete" => await owner.DeleteAsync("/api/v1/vms/child"),
            "primary-create" => await owner.PostAsJsonAsync("/api/v1/vms", new { name = "second", cpu = 1, ramGb = 1, diskGb = 8 }),
            _ => await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" }),
        };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TimeSpan.FromSeconds(30), response.Headers.RetryAfter?.Delta);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("maintenance", body.GetProperty("code").GetString());
        Assert.Equal(freeze ? "maintenance" : "draining", body.GetProperty("phase").GetString());
        Assert.Equal(30, body.GetProperty("retryAfterSeconds").GetInt32());
        Assert.Equal("update-e2e", body.GetProperty("updateId").GetString());
        app.Service<IMaintenanceGate>().Reopen();
        Assert.Equal(HttpStatusCode.OK, (await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" })).StatusCode);
    }
}
