using System.Net;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Sqlite;
using Constructd.Tests.Support;

namespace Constructd.Tests.Persistence;

/// <summary>
/// The durable stores. Every test runs against a real SQLite file and then reopens it, which is what
/// "survives a restart of the service" means in practice.
/// </summary>
public sealed class SqlitePersistenceTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"constructd-test-{Guid.NewGuid():n}.db");

    private SqliteDatabase Open()
    {
        var database = new SqliteDatabase(_path);
        database.EnsureCreated();
        return database;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_path)!, Path.GetFileName(_path) + "*"))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A leftover temp file is not worth failing a test over.
            }
        }
    }

    [Fact]
    public async Task Users_and_vms_survive_a_restart()
    {
        var vm = new Vm("work-vm", "DOMAIN\\christoph", 4, 8, 64, Now, VmState.Running, 2201, "hash",
            new IdlePolicy(45, IdleAction.Shutdown), Vm.NoForwards);

        {
            var database = Open();
            var users = new SqliteUserStore(database);
            var vms = new SqliteVmRepository(database);

            Assert.True(await users.CreateAsync(
                new User("DOMAIN\\christoph", Role.Admin, 3, Now, AllowHostForwards: false), CancellationToken.None));
            Assert.Equal(VmAddOutcome.Added, await vms.AddAsync(vm, maxVms: 3, CancellationToken.None));
        }

        // "Restart": brand new objects over the same file.
        var reopened = Open();
        var user = await new SqliteUserStore(reopened).GetAsync("domain\\CHRISTOPH", CancellationToken.None);
        var stored = await new SqliteVmRepository(reopened).GetAsync("work-vm", CancellationToken.None);

        Assert.NotNull(user);
        Assert.Equal(Role.Admin, user!.Role);
        Assert.Equal(3, user.MaxVms);
        Assert.False(user.AllowHostForwards);
        Assert.Equal(Now, user.Created);

        Assert.NotNull(stored);
        Assert.Equal(vm, stored);
    }

    [Fact]
    public async Task The_quota_is_enforced_by_the_insert_itself()
    {
        var database = Open();
        var vms = new SqliteVmRepository(database);

        Vm Make(string name) => new(name, "bob", 2, 4, 32, Now, VmState.Unknown, null, null,
            IdlePolicy.Disabled, Vm.NoForwards);

        Assert.Equal(VmAddOutcome.Added, await vms.AddAsync(Make("a"), maxVms: 2, CancellationToken.None));
        Assert.Equal(VmAddOutcome.Added, await vms.AddAsync(Make("b"), maxVms: 2, CancellationToken.None));
        Assert.Equal(VmAddOutcome.QuotaExceeded, await vms.AddAsync(Make("c"), maxVms: 2, CancellationToken.None));
        Assert.Equal(VmAddOutcome.NameTaken, await vms.AddAsync(Make("a"), maxVms: 9, CancellationToken.None));
    }

    [Fact]
    public async Task Only_the_token_hash_is_written_to_the_database()
    {
        var database = Open();
        var users = new SqliteUserStore(database);
        var vms = new SqliteVmRepository(database);
        var clock = new Constructd.Fakes.MutableClock(Now);
        var tokens = new SqliteTokenService(database, clock, users, vms);

        await users.CreateAsync(new User("bob", Role.User, 2, Now), CancellationToken.None);
        var issued = await tokens.IssueAsync("bob", "laptop", CancellationToken.None);

        await vms.AddAsync(
            new Vm("work-vm", "bob", 2, 4, 32, Now, VmState.Running, 2201, null, IdlePolicy.Disabled, Vm.NoForwards),
            maxVms: 2,
            CancellationToken.None);
        var vmToken = await tokens.IssueVmTokenAsync("work-vm", CancellationToken.None);

        // The plaintext of neither token appears anywhere in the file.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var bytes = await File.ReadAllBytesAsync(_path);
        var content = System.Text.Encoding.UTF8.GetString(bytes);

        Assert.DoesNotContain(issued.Plaintext, content, StringComparison.Ordinal);
        Assert.DoesNotContain(vmToken, content, StringComparison.Ordinal);
        Assert.Contains(TokenHasher.Hash(issued.Plaintext), content, StringComparison.Ordinal);

        // ...and both still validate against the stored hashes.
        var userPrincipal = await tokens.ValidateAsync(issued.Plaintext, CancellationToken.None);
        var vmPrincipal = await tokens.ValidateAsync(vmToken, CancellationToken.None);

        Assert.Equal(TokenKind.User, userPrincipal!.Kind);
        Assert.Equal("bob", userPrincipal.Name);
        Assert.Equal(TokenKind.Vm, vmPrincipal!.Kind);
        Assert.Equal("work-vm", vmPrincipal.VmName);
        Assert.Null(await tokens.ValidateAsync("not-a-token", CancellationToken.None));
    }

    [Fact]
    public async Task Audit_entries_are_kept_newest_first()
    {
        var database = Open();
        var audit = new SqliteAuditLog(database);

        for (var i = 0; i < 5; i++)
        {
            await audit.AppendAsync(
                new AuditEntry(Now.AddMinutes(i), "bob", $"action.{i}", "work-vm", AuditOutcome.Success, $"n={i}"),
                CancellationToken.None);
        }

        var page = await new SqliteAuditLog(Open()).QueryAsync(3, CancellationToken.None);

        Assert.Equal(3, page.Count);
        Assert.Equal("action.4", page[0].Action);
        Assert.Equal(AuditOutcome.Success, page[0].Outcome);
    }

    [Fact]
    public async Task Jobs_survive_a_restart_and_unfinished_ones_are_failed()
    {
        var job = new Job("job-1", JobKinds.CreateVm, "work-vm", "bob", JobState.Running,
            [new JobProgressLine(Now, "building autoinstall ISO")], null, null, Now, null);

        await new SqliteJobStore(Open()).UpsertAsync(job, CancellationToken.None);

        // "Restart": the recovery pass runs, then a client reads the job.
        var store = new SqliteJobStore(Open());
        Assert.Equal(1, await store.MarkInterruptedAsync(Now.AddMinutes(1), CancellationToken.None));

        var recovered = await store.GetAsync("job-1", CancellationToken.None);

        Assert.NotNull(recovered);
        Assert.Equal(JobState.Failed, recovered!.State);
        Assert.Contains("restart", recovered.Error);
        Assert.Equal("bob", recovered.Owner);
        Assert.Equal("building autoinstall ISO", Assert.Single(recovered.Progress).Text);

        // A finished job is left alone.
        Assert.Equal(0, await store.MarkInterruptedAsync(Now.AddMinutes(2), CancellationToken.None));
    }

    [Fact]
    public async Task Activity_reports_round_trip()
    {
        var database = Open();
        var vms = new SqliteVmRepository(database);
        await vms.AddAsync(
            new Vm("work-vm", "bob", 2, 4, 32, Now, VmState.Running, null, null, IdlePolicy.Disabled, Vm.NoForwards),
            maxVms: 2,
            CancellationToken.None);

        await vms.SaveActivityAsync(
            new ActivityReport("work-vm", true, ["claude running", "tmux output"], Now), CancellationToken.None);
        await vms.SaveActivityAsync(
            new ActivityReport("work-vm", false, [], Now.AddMinutes(5)), CancellationToken.None);

        var report = await new SqliteVmRepository(Open()).GetLatestActivityAsync("work-vm", CancellationToken.None);

        Assert.NotNull(report);
        Assert.False(report!.Busy);
        Assert.Empty(report.Reasons);
        Assert.Equal(Now.AddMinutes(5), report.ReportedAt);
    }

    /// <summary>The whole API, driven over HTTP, against the SQLite stores rather than the fakes.</summary>
    [Fact]
    public async Task The_api_works_end_to_end_on_sqlite_and_its_state_survives_a_restart()
    {
        string token;
        string vmName = "work-vm";

        using (var app = TestApp.WithSqlite(_path))
        {
            using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
            var issued = await (await admin.PostJsonAsync("/api/v1/users", new { name = "bob", role = "user", maxVms = 2 }))
                .ReadAsync<UserResponse>();
            Assert.Equal("bob", issued.Name);

            var tokenResponse = await (await admin.PostJsonAsync("/api/v1/users/bob/tokens", new { label = "laptop" }))
                .ReadAsync<TokenIssuedResponse>();
            token = tokenResponse.Token;

            using var bob = app.CreateAnonymousClient();
            bob.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var job = await bob.CreateVmAsync(vmName);
            Assert.Equal(JobState.Succeeded, job.State);
            await bob.PutJsonAsync($"/api/v1/vms/{vmName}/idle-policy", new { timeoutMinutes = 15, action = "shutdown" });
        }

        // A fresh host over the same database file: the user, their token and the VM are all still there.
        using (var restarted = TestApp.WithSqlite(_path))
        {
            using var bob = restarted.CreateAnonymousClient();
            bob.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            var whoami = await (await bob.GetAsync("/api/v1/whoami")).ReadAsync<WhoAmIResponse>();
            Assert.Equal("bob", whoami.Name);
            Assert.True(whoami.Known);

            var vm = await (await bob.GetAsync($"/api/v1/vms/{vmName}")).ReadAsync<VmResponse>();
            Assert.Equal("bob", vm.Owner);
            Assert.Equal(2201, vm.SshForwardPort);
            Assert.Equal(15, vm.IdlePolicy.TimeoutMinutes);
            Assert.Equal(IdleAction.Shutdown, vm.IdlePolicy.Action);

            using var admin = await restarted.CreateUserClientAsync("admin2", Role.Admin);
            var entries = await (await admin.GetAsync("/api/v1/audit")).ReadAsync<List<AuditResponse>>();
            Assert.Contains(entries, e => e is { Action: "vm.create", Target: "work-vm", Actor: "bob" });
        }
    }

    /// <remarks>
    /// Forward state is host-side durable state (plan §4.4/§4.6): it has to survive the service
    /// process, and the allocators have to come back knowing which ports are taken.
    /// </remarks>
    [Fact]
    public async Task Forwards_and_their_ports_survive_a_restart()
    {
        string hostForwardId;
        int hostPublicPort;

        using (var app = TestApp.WithSqlite(_path))
        {
            using var bob = await app.CreateUserClientAsync("bob");
            await bob.CreateVmAsync("work-vm");

            var host = await (await bob.PostJsonAsync("/api/v1/vms/work-vm/forwards",
                new { vmPort = 8080, label = "webhook", target = "host" })).ReadAsync<ForwardResponse>();
            await bob.PostJsonAsync("/api/v1/vms/work-vm/forwards",
                new { vmPort = 3000, label = "vite", target = "client" });

            hostForwardId = host.Id;
            hostPublicPort = host.PublicPort!.Value;
        }

        using (var restarted = TestApp.WithSqlite(_path))
        {
            using var bob = await restarted.CreateTokenClientAsync("bob");

            // The forwards are still there, with the same public port...
            var forwards = await (await bob.GetAsync("/api/v1/vms/work-vm/forwards"))
                .ReadAsync<List<ForwardResponse>>();
            Assert.Equal(2, forwards.Count);
            Assert.Equal(hostPublicPort, forwards.Single(f => f.Id == hostForwardId).PublicPort);

            // ...and startup reconciliation re-created the host's rules for them and for the ssh forward.
            Assert.Contains(restarted.Forwards.Materialized, kv => kv.Key == hostForwardId);
            Assert.Contains(restarted.Forwards.Materialized, kv => kv.Key == "ssh:work-vm");

            // A VM created after the restart cannot be handed the ssh port that is already in use...
            var next = await bob.CreateVmAsync("next-vm");
            Assert.Equal(2202, next.ResultElement("endpoint").GetProperty("sshPort").GetInt32());

            // ...and neither can a new host forward be handed the app port that is already in use.
            var another = await (await bob.PostJsonAsync("/api/v1/vms/next-vm/forwards",
                new { vmPort = 8080, label = "webhook", target = "host" })).ReadAsync<ForwardResponse>();
            Assert.NotEqual(hostPublicPort, another.PublicPort);
        }
    }

    /// <remarks>
    /// A crash right after the SSH port was allocated — before the creation job got to any of its later
    /// steps — must not leave a port that the restarted service thinks is free.
    /// </remarks>
    [Fact]
    public async Task An_ssh_allocation_is_durable_before_its_host_rule_exists()
    {
        using (var app = TestApp.WithSqlite(_path))
        {
            using var bob = await app.CreateUserClientAsync("bob");

            // Hold the creation job inside the driver, then allocate the forward the way the job would.
            app.Driver.HoldCreate = true;
            await bob.StartCreateVmAsync("work-vm");

            var port = await app.Service<IPortForwardManager>()
                .AllocateSshForwardAsync("work-vm", CancellationToken.None);
            Assert.Equal(2201, port);

            // It is on the VM record — i.e. in the database — even though the job never got further.
            var stored = await app.Vms.GetAsync("work-vm", CancellationToken.None);
            Assert.Equal(2201, stored!.SshForwardPort);
        }

        // "Crash and restart": reconciliation reserves 2201, so the next VM cannot be handed it.
        using (var restarted = TestApp.WithSqlite(_path))
        {
            using var bob = await restarted.CreateTokenClientAsync("bob");

            var next = await bob.CreateVmAsync("next-vm");
            Assert.Equal(2202, next.ResultElement("endpoint").GetProperty("sshPort").GetInt32());
            Assert.Contains(restarted.Forwards.Materialized, kv => kv.Key == "ssh:work-vm");
        }
    }

    /// <remarks>
    /// A dependency's exception message can contain a command line, and with it the VM's seed password.
    /// Nothing derived from it may end up in job state, the event stream, the audit trail or the file.
    /// </remarks>
    [Fact]
    public async Task A_dependency_failure_never_leaks_its_message_into_durable_state()
    {
        const string Sentinel = "SEED-PASSWORD-s3cr3t";

        using (var app = TestApp.WithSqlite(_path))
        {
            using var admin = await app.CreateUserClientAsync("admin", Role.Admin);

            app.IsoBuilder.Failure = new InvalidOperationException(
                $"wsl.exe build-autoinstall-iso.sh --pass {Sentinel} failed");

            var jobId = await admin.StartCreateVmAsync("work-vm");
            var job = await admin.WaitForJobAsync(jobId);

            Assert.Equal(JobState.Failed, job.State);
            Assert.Equal("InvalidOperationException", job.Error);
            Assert.DoesNotContain(Sentinel, string.Join("\n", job.Progress.Select(p => p.Text)));

            var stream = await (await admin.GetAsync($"/api/v1/jobs/{jobId}/events")).Content.ReadAsStringAsync();
            Assert.DoesNotContain(Sentinel, stream);

            // The same for a failing idle evaluation.
            app.IsoBuilder.Failure = null;
            await admin.CreateVmAsync("idle-vm");
            app.Driver.PowerFailure = new InvalidOperationException($"Save-VM failed: {Sentinel}");
            await admin.PutJsonAsync("/api/v1/vms/idle-vm/idle-policy", new { timeoutMinutes = 1, action = "save" });

            var engine = app.Service<IIdlePolicyEngine>();
            await engine.EvaluateAsync(app.Clock.UtcNow, CancellationToken.None);
            var outcomes = await engine.EvaluateAsync(app.Clock.UtcNow.AddHours(2), CancellationToken.None);
            Assert.Contains(outcomes, o => o.Error == "InvalidOperationException");

            var entries = await (await admin.GetAsync("/api/v1/audit?limit=1000"))
                .ReadAsync<List<AuditResponse>>();
            Assert.All(entries, e => Assert.DoesNotContain(Sentinel, e.Detail ?? string.Empty));
        }

        // ...and nowhere in the database file either.
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        var content = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_path));
        Assert.DoesNotContain(Sentinel, content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_vm_token_issued_before_a_restart_still_works_afterwards()
    {
        string vmToken;

        using (var app = TestApp.WithSqlite(_path))
        {
            using var bob = await app.CreateUserClientAsync("bob");
            vmToken = (await bob.CreateVmAsync("work-vm")).VmToken();
        }

        using (var restarted = TestApp.WithSqlite(_path))
        {
            using var guest = restarted.CreateVmTokenClient(vmToken);
            Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync("/api/v1/vms/work-vm/forwards")).StatusCode);
        }
    }
}
