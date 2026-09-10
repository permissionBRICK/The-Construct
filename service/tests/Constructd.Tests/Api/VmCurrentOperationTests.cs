using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Delegation;
using Constructd.Tests.Support;

namespace Constructd.Tests.Api;

public sealed class VmCurrentOperationTests
{
    [Theory]
    [InlineData(JobState.Queued, true)]
    [InlineData(JobState.Running, true)]
    [InlineData(JobState.Succeeded, false)]
    [InlineData(JobState.Failed, false)]
    [InlineData(JobState.Cancelled, false)]
    public async Task InventoryExposesOnlyLiveOperationsAndRetainsJobHistory(JobState state, bool busy)
    {
        await using var app = new TestApp();
        using var owner = await LifecycleTests.Setup(app);
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var vm = (await app.Vms.GetAsync("child", default))!;
        var store = app.Service<IJobStore>();
        var job = (await store.GetAsync(vm.CurrentJobId!, default))!;
        // Failed/cancelled jobs may retain a work phase; state, not phase text,
        // determines whether an operation still blocks the panel's actions.
        await store.UpsertAsync(job with { State = state, Phase = state == JobState.Succeeded ? "done" : "wait" }, default);

        foreach (var route in new[] { "/api/v1/vms/child", "/api/v1/vms", "/api/v1/vms/parent/children" })
        {
            var body = await admin.GetFromJsonAsync<JsonElement>(route);
            var row = body.ValueKind == JsonValueKind.Array
                ? body.EnumerateArray().Single(v => v.GetProperty("name").GetString() == "child") : body;
            var operation = row.GetProperty("currentOperation");
            Assert.Equal(busy, operation.ValueKind != JsonValueKind.Null);
            if (busy) Assert.Equal(job.Id, operation.GetProperty("jobId").GetString());
            Assert.Contains(row.GetProperty("allowedActions").EnumerateArray(), a => a.GetString() == "shutdown");
            Assert.Contains(row.GetProperty("allowedActions").EnumerateArray(), a => a.GetString() == "delete");
        }
        var history = await admin.GetFromJsonAsync<JsonElement>("/api/v1/jobs/" + job.Id);
        Assert.Equal(job.Id, history.GetProperty("id").GetString());
        Assert.Equal(state, (await store.GetAsync(job.Id, default))!.State);
        Assert.Equal(job.Id, (await app.Vms.GetAsync("child", default))!.CurrentJobId);
    }
}
