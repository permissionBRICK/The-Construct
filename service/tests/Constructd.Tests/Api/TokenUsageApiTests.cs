using System.Net;
using System.Text.Json.Nodes;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
namespace Constructd.Tests.Api;

public sealed class TokenUsageApiTests
{
    private static JsonObject Row() => new() { ["day"] = "2026-09-17", ["tool"] = "claude", ["inputTokens"] = 60,
        ["outputTokens"] = 40, ["cacheCreateTokens"] = 0, ["cacheReadTokens"] = 0, ["totalTokens"] = 100, ["costUsd"] = 1.234567m,
        ["models"] = new JsonObject { ["model"] = new JsonObject { ["totalTokens"] = 100, ["costUsd"] = 1.234567m } } };
    private static object Body(params JsonObject[] rows) => new { generatedAt = "2026-09-17T12:00:00Z", days = rows };

    [Fact]
    public async Task Intake_is_scoped_idempotent_and_does_not_log_secrets_or_success()
    {
        using var app = new TestApp();
        using var alice = await app.CreateUserClientAsync("alice");
        using var bob = await app.CreateUserClientAsync("bob");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var job = await alice.CreateVmAsync("vm"); await bob.CreateVmAsync("other");
        using var guest = app.CreateVmTokenClient(job.VmToken());
        var yesterday = Row(); yesterday["day"] = "2026-09-16";
        for (var i = 0; i < 2; i++) Assert.Equal(HttpStatusCode.NoContent, (await guest.PostJsonAsync("/api/v1/vms/vm/usage", Body(Row(), yesterday))).StatusCode);
        var rows = await app.Service<ITokenUsageStore>().ListAsync("alice", "vm", default);
        Assert.Equal(2, rows.Count); Assert.Equal(200, rows.Sum(r => r.Usage.TotalTokens));
        Assert.All(rows, r => Assert.Equal(app.Clock.UtcNow, r.ReportedAt));
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostJsonAsync("/api/v1/vms/other/usage", Body(Row()))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await bob.PostJsonAsync("/api/v1/vms/vm/usage", Body(Row()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await alice.PostJsonAsync("/api/v1/vms/missing/usage", Body(Row()))).StatusCode);
        using var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostJsonAsync("/api/v1/vms/vm/usage", Body(Row()))).StatusCode);
        var audit = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();
        Assert.DoesNotContain(audit, a => a.Action == "vm.usage" && a.Outcome == AuditOutcome.Success);
        Assert.DoesNotContain(job.VmToken(), app.Logs.AllText());
        Assert.DoesNotContain(job.VmToken(), System.Text.Json.JsonSerializer.Serialize(audit));
    }

    [Theory]
    [InlineData("day", "2026-02-30")] [InlineData("day", "2026-9")]
    [InlineData("tool", "unknown")] [InlineData("inputTokens", -1)] [InlineData("outputTokens", -1)]
    [InlineData("cacheCreateTokens", -1)] [InlineData("cacheReadTokens", -1)] [InlineData("totalTokens", -1)]
    [InlineData("costUsd", -1)] [InlineData("totalTokens", null)]
    public async Task Invalid_rows_are_rejected_without_partial_writes(string key, object? value)
    {
        using var app = new TestApp(); using var user = await app.CreateUserClientAsync("alice"); await user.CreateVmAsync("vm");
        var invalid = Row(); invalid[key] = System.Text.Json.JsonSerializer.SerializeToNode(value);
        var valid = Row(); valid["day"] = "2026-09-16";
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostJsonAsync("/api/v1/vms/vm/usage", Body(valid, invalid))).StatusCode);
        Assert.Empty(await app.Service<ITokenUsageStore>().ListAsync(null, null, default));
    }

    [Fact]
    public async Task Caps_models_missing_body_fractional_counts_overflow_and_deleting_fence()
    {
        using var app = new TestApp(); using var user = await app.CreateUserClientAsync("alice"); var job = await user.CreateVmAsync("vm");
        var url = "/api/v1/vms/vm/usage";
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostJsonAsync(url, Body(Enumerable.Range(0, 201).Select(_ => Row()).ToArray()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostJsonAsync(url, Body(Row(), Row()))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostJsonAsync(url, new { days = new[] { Row() } })).StatusCode);
        var models = new JsonObject(); for (var i = 0; i < 65; i++) models["m" + i] = new JsonObject { ["totalTokens"] = 1, ["costUsd"] = 0 };
        var row = Row(); row["models"] = models;
        Assert.Equal(HttpStatusCode.BadRequest, (await user.PostJsonAsync(url, Body(row))).StatusCode);
        models.Remove("m64"); Assert.Equal(HttpStatusCode.NoContent, (await user.PostJsonAsync(url, Body(row))).StatusCode);
        models["m0"] = null; Assert.Equal(HttpStatusCode.BadRequest, (await user.PostJsonAsync(url, Body(row))).StatusCode);
        row = Row(); row["totalTokens"] = 0.5; Assert.Equal(HttpStatusCode.BadRequest, (await user.PostJsonAsync(url, Body(row))).StatusCode);
        row = Row(); row["costUsd"] = decimal.MaxValue; Assert.Equal(HttpStatusCode.BadRequest, (await user.PostJsonAsync(url, Body(row))).StatusCode);
        var vm = (await app.Vms.GetAsync("vm", default))!; await app.Vms.UpdateAsync(vm with { Deleting = true }, default);
        Assert.Equal(HttpStatusCode.Conflict, (await user.PostJsonAsync(url, Body(Row()))).StatusCode);
        using var guest = app.CreateVmTokenClient(job.VmToken());
        Assert.Equal(HttpStatusCode.Unauthorized, (await guest.PostJsonAsync(url, Body(Row()))).StatusCode);
    }
}
