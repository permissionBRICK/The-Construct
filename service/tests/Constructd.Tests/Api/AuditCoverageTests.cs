using System.Net;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// "Every mutating call is audited" — including the ones that fail validation, hit a missing object,
/// or are refused by a policy before the handler ever runs.
/// </summary>
public class AuditCoverageTests
{
    [Fact]
    public void Every_mutating_route_is_marked_auditable()
    {
        using var app = new TestApp();

        var unaudited = app.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods
                .Any(method => method is "POST" or "PUT" or "DELETE" or "PATCH") == true)
            .Where(endpoint => endpoint.Metadata.GetMetadata<AuditActionMetadata>() is null)
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();

        Assert.Empty(unaudited);
    }

    [Fact]
    public async Task A_heartbeat_is_audited()
    {
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        var job = await bob.CreateVmAsync("work-vm");
        using var guest = app.CreateVmTokenClient(job.VmToken());

        await guest.PostJsonAsync("/api/v1/vms/work-vm/activity", new { busy = true, reasons = new[] { "agent" } });

        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();

        var entry = Assert.Single(entries, e => e.Action == "vm.activity");
        Assert.Equal("work-vm", entry.Target);
        Assert.Equal("vm:work-vm", entry.Actor);
        Assert.Equal(AuditOutcome.Success, entry.Outcome);
        Assert.Contains("busy=True", entry.Detail);
    }

    [Fact]
    public async Task Validation_errors_and_missing_objects_are_audited_as_failures()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.CreateVmAsync("work-vm");

        // 400: a nonsense power action.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostJsonAsync("/api/v1/vms/work-vm/power", new { action = "explode" })).StatusCode);

        // 404: a VM that does not exist.
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/v1/vms/ghost-vm")).StatusCode);

        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();

        Assert.Contains(entries, e => e is
        {
            Action: "vm.power", Target: "work-vm", Outcome: AuditOutcome.Failure,
        });
        Assert.Contains(entries, e => e is
        {
            Action: "vm.delete", Target: "ghost-vm", Outcome: AuditOutcome.Failure,
        });
    }

    [Fact]
    public async Task A_refusal_by_policy_is_audited_even_though_the_handler_never_runs()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        using var bob = await app.CreateUserClientAsync("bob");

        // A plain user reaching for the admin surface.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await bob.PostJsonAsync("/api/v1/users", new { name = "eve", role = "admin", maxVms = 9 })).StatusCode);

        // ...and an anonymous mutation.
        using var anonymous = app.CreateAnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await anonymous.DeleteAsync("/api/v1/vms/work-vm")).StatusCode);

        var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();

        Assert.Contains(entries, e => e is
        {
            Action: "user.create", Actor: "bob", Outcome: AuditOutcome.Denied,
        });
        Assert.Contains(entries, e => e is
        {
            Action: "vm.delete", Actor: "anonymous", Target: "work-vm", Outcome: AuditOutcome.Denied,
        });
    }

    /// <remarks>
    /// Malformed JSON fails during model binding, before the handler runs, and a driver that throws
    /// fails after it — neither may slip past the audit trail.
    /// </remarks>
    [Fact]
    public async Task Requests_that_never_reach_or_never_leave_the_handler_are_audited()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.CreateVmAsync("work-vm");

        // Malformed body: binding throws before the handler.
        using var malformed = new HttpRequestMessage(HttpMethod.Post, "/api/v1/vms/work-vm/power")
        {
            Content = new StringContent("{ this is not json", System.Text.Encoding.UTF8, "application/json"),
        };
        using var malformedResponse = await admin.SendAsync(malformed);
        Assert.Equal(HttpStatusCode.BadRequest, malformedResponse.StatusCode);

        // A hypervisor that throws: the handler never returns.
        app.Driver.PowerFailure = new InvalidOperationException("hyper-v refused: SECRET-abc123");
        using var brokenDriver = await admin.PostJsonAsync("/api/v1/vms/work-vm/power", new { action = "stop" });
        Assert.Equal(HttpStatusCode.InternalServerError, brokenDriver.StatusCode);

        var entries = await (await admin.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();
        var powerEntries = entries.Where(e => e.Action == "vm.power").ToList();

        Assert.Equal(2, powerEntries.Count);
        Assert.All(powerEntries, e => Assert.Equal(AuditOutcome.Failure, e.Outcome));
        // Minimal APIs answer a binding failure with a 400 rather than throwing; either way the
        // middleware sees the outcome and records it.
        Assert.Contains(powerEntries, e => e.Detail!.Contains("status=400"));
        Assert.Contains(powerEntries, e => e.Detail!.Contains("status=500") &&
                                           e.Detail.Contains("exception=InvalidOperationException"));

        // The exception message could contain anything; only its type is recorded.
        Assert.All(entries, e => Assert.DoesNotContain("SECRET-abc123", e.Detail ?? string.Empty));
    }

    [Fact]
    public async Task Audit_details_never_carry_a_secret()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.PostJsonAsync("/api/v1/users", new { name = "bob", role = "user", maxVms = 1 });

        var issued = await (await admin.PostJsonAsync("/api/v1/users/bob/tokens", new { label = "laptop" }))
            .ReadAsync<TokenIssuedResponse>();
        var job = await admin.CreateVmAsync("work-vm");
        var vmToken = job.VmToken();

        var entries = await (await admin.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();
        var text = string.Join("\n", entries.Select(e => $"{e.Action} {e.Target} {e.Detail}"));

        Assert.DoesNotContain(issued.Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(vmToken, text, StringComparison.Ordinal);
    }
}
