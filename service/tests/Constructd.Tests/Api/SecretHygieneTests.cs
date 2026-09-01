using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// A dependency's exception can carry a command line — and with it a VM's seed password — in its
/// message, stack trace or <c>Data</c>. Nothing derived from it may reach durable state, a response,
/// or a log sink: the service reduces it to a type name at its own boundary rather than trusting the
/// implementations behind the interfaces to be careful.
/// </summary>
public class SecretHygieneTests
{
    private const string Sentinel = "SEED-PASSWORD-s3cr3t";

    private static Exception Leaky() =>
        new InvalidOperationException($"powershell.exe -Command \"New-VM -Pass {Sentinel}\" failed");

    [Fact]
    public async Task A_failing_driver_in_the_request_path_leaks_nothing()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.CreateVmAsync("work-vm");

        app.Driver.PowerFailure = Leaky();

        using var response = await admin.PostJsonAsync("/api/v1/vms/work-vm/power", new { action = "stop" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(Sentinel, body, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", body, StringComparison.Ordinal);

        var entries = await (await admin.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();
        Assert.All(entries, e => Assert.DoesNotContain(Sentinel, e.Detail ?? string.Empty));

        // ...and no log sink saw it either — the service logs a description, never the exception.
        Assert.DoesNotContain(Sentinel, app.Logs.AllText(), StringComparison.Ordinal);
        Assert.Contains("failed: InvalidOperationException", app.Logs.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_background_job_leaks_nothing()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        app.IsoBuilder.Failure = Leaky();

        var jobId = await admin.StartCreateVmAsync("work-vm");
        var job = await admin.WaitForJobAsync(jobId);

        Assert.Equal(JobState.Failed, job.State);
        Assert.Equal("InvalidOperationException", job.Error);
        Assert.DoesNotContain(Sentinel, string.Join("\n", job.Progress.Select(p => p.Text)));

        var stream = await (await admin.GetAsync($"/api/v1/jobs/{jobId}/events")).Content.ReadAsStringAsync();
        Assert.DoesNotContain(Sentinel, stream, StringComparison.Ordinal);

        var entries = await (await admin.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();
        Assert.All(entries, e => Assert.DoesNotContain(Sentinel, e.Detail ?? string.Empty));

        Assert.DoesNotContain(Sentinel, app.Logs.AllText(), StringComparison.Ordinal);
        // The job failure was logged — as a description.
        Assert.Contains("failed: InvalidOperationException", app.Logs.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_idle_action_leaks_nothing()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.CreateVmAsync("work-vm");
        await admin.PutJsonAsync("/api/v1/vms/work-vm/idle-policy", new { timeoutMinutes = 1, action = "save" });

        app.Driver.PowerFailure = Leaky();

        var engine = app.Service<IIdlePolicyEngine>();
        await engine.EvaluateAsync(app.Clock.UtcNow, CancellationToken.None);
        var outcomes = await engine.EvaluateAsync(app.Clock.UtcNow.AddHours(2), CancellationToken.None);

        var outcome = Assert.Single(outcomes);
        Assert.Equal("InvalidOperationException", outcome.Error);

        var entries = await (await admin.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();
        Assert.Contains(entries, e => e is { Action: "vm.idle-save", Outcome: AuditOutcome.Failure });
        Assert.All(entries, e => Assert.DoesNotContain(Sentinel, e.Detail ?? string.Empty));
        Assert.DoesNotContain(Sentinel, app.Logs.AllText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_forward_manager_leaks_nothing()
    {
        using var app = new TestApp();
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.CreateVmAsync("work-vm");

        app.Forwards.Failure = Leaky();

        using var response = await admin.PostJsonAsync("/api/v1/vms/work-vm/forwards",
            new { vmPort = 3000, label = "vite", target = "host" });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain(Sentinel, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, app.Logs.AllText(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Authentication is where the presented plaintext token is in scope, so a failure there is the
    /// most sensitive of all: the sanitizing boundary has to sit outside it, not inside.
    /// </summary>
    [Fact]
    public async Task A_failing_token_validation_never_leaks_the_presented_token()
    {
        const string LeakyToken = "leak-PLAINTEXT-TOKEN-abc123";

        using var app = new TestApp(configureServices: services =>
            services.AddSingleton<ITokenService>(sp =>
                new LeakyTokenService(sp.GetRequiredService<Fakes.InMemoryTokenService>())));

        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.CreateVmAsync("work-vm");

        // A read with a token whose lookup blows up...
        using var anonymous = app.CreateAnonymousClient();
        anonymous.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", LeakyToken);

        using var read = await anonymous.GetAsync("/api/v1/whoami");
        Assert.Equal(HttpStatusCode.InternalServerError, read.StatusCode);
        Assert.DoesNotContain(LeakyToken, await read.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        // ...and a mutation with a VM token whose lookup blows up, which is also audited.
        using var guest = app.CreateVmTokenClient(LeakyToken);
        using var write = await guest.PostJsonAsync("/api/v1/vms/work-vm/activity", new { busy = true });
        Assert.Equal(HttpStatusCode.InternalServerError, write.StatusCode);
        Assert.DoesNotContain(LeakyToken, await write.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        var entries = await (await admin.GetAsync("/api/v1/audit?limit=1000")).ReadAsync<List<AuditResponse>>();
        Assert.Contains(entries, e => e is { Action: "vm.activity", Outcome: AuditOutcome.Failure });
        Assert.All(entries, e => Assert.DoesNotContain(LeakyToken, e.Detail ?? string.Empty));

        // Nothing rendered into a log entry either — message, state, exception or Data.
        Assert.DoesNotContain(LeakyToken, app.Logs.AllText(), StringComparison.Ordinal);
        Assert.Contains("failed: InvalidOperationException", app.Logs.AllText(), StringComparison.Ordinal);
    }

    /// <summary>A token service whose lookup fails with the presented secret in the message.</summary>
    private sealed class LeakyTokenService(ITokenService inner) : ITokenService
    {
        public Task<TokenPrincipal?> ValidateAsync(string plaintext, CancellationToken cancellationToken) =>
            plaintext.StartsWith("leak-", StringComparison.Ordinal)
                ? throw new InvalidOperationException($"token lookup failed for '{plaintext}'")
                : inner.ValidateAsync(plaintext, cancellationToken);

        public Task<IssuedToken> IssueAsync(string userName, string label, CancellationToken cancellationToken) =>
            inner.IssueAsync(userName, label, cancellationToken);

        public Task<ApiToken> ImportAsync(
            string userName,
            string label,
            string plaintext,
            CancellationToken cancellationToken) =>
            inner.ImportAsync(userName, label, plaintext, cancellationToken);

        public Task<string> IssueVmTokenAsync(string vmName, CancellationToken cancellationToken) =>
            inner.IssueVmTokenAsync(vmName, cancellationToken);

        public Task<IReadOnlyList<ApiToken>> ListAsync(string userName, CancellationToken cancellationToken) =>
            inner.ListAsync(userName, cancellationToken);

        public Task<int> RevokeAllAsync(string userName, CancellationToken cancellationToken) =>
            inner.RevokeAllAsync(userName, cancellationToken);
    }

    [Fact]
    public async Task Our_own_error_messages_are_still_reported_in_full()
    {
        // SafeError only strips what came from a dependency: messages the service composes itself are
        // what a user needs to see, so they survive.
        using var app = new TestApp();
        using var bob = await app.CreateUserClientAsync("bob");
        app.Driver.Reachable = false;

        var job = await bob.WaitForJobAsync(await bob.StartCreateVmAsync("work-vm"));

        Assert.Equal(JobState.Failed, job.State);
        Assert.Contains("did not answer ssh", job.Error);
    }
}
