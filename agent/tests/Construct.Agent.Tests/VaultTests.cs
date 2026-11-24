using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Microsoft.Extensions.DependencyInjection;

namespace Construct.Agent.Tests;

/// <summary>
/// The vault's device side (plan 8.2, 8.4, 9.1, 10.3) through the agent's local API, against the real engine:
/// secrets as records the agent writes and opens with <c>K</c>, approvals and entry requests answered here, the
/// engine's mode switched by granting or withdrawing <c>K</c>.
/// </summary>
public sealed class VaultTests
{
    [Fact]
    public async Task The_agent_adds_lists_reveals_changes_and_deletes_secrets_and_never_lists_a_value()
    {
        using var engine = new EngineHost();
        var (laptop, engineId) = await SetUpAsync(engine, "available");
        await using var _ = laptop;

        var added = await laptop.PostAsync("api/vault/secrets", new { name = "github", description = "GitHub token", value = "ghp_Value_0001", username = "octo" });
        Assert.Equal("github", added["name"]!.GetValue<string>());
        Assert.True(added["hasUsername"]!.GetValue<bool>());
        Assert.False(added["autoApprove"]!.GetValue<bool>());
        Assert.True(added["readable"]!.GetValue<bool>());
        Assert.Equal("device", added["provenance"]!["kind"]!.GetValue<string>());
        await laptop.PostAsync("api/vault/secrets", new { name = "github", description = "again", value = "x" }, HttpStatusCode.Conflict);
        await laptop.PostAsync("api/vault/secrets", new { name = "../x", description = "bad", value = "x" }, HttpStatusCode.BadRequest);

        var revealed = await laptop.PostAsync("api/vault/secrets/github/reveal", null);
        Assert.Equal("ghp_Value_0001", revealed["value"]!.GetValue<string>());
        Assert.Equal("octo", revealed["username"]!.GetValue<string>());

        using (var patched = await laptop.Api.PatchAsJsonAsync("api/vault/secrets/github", new { value = "ghp_Value_0002" }))
            Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.Equal("ghp_Value_0002", (await laptop.PostAsync("api/vault/secrets/github/reveal", null))["value"]!.GetValue<string>());
        Assert.Equal("octo", (await laptop.PostAsync("api/vault/secrets/github/reveal", null))["username"]!.GetValue<string>());

        using (var on = await laptop.Api.PutAsJsonAsync("api/vault/secrets/github/auto-approve", new { on = true }))
            Assert.Equal(HttpStatusCode.NoContent, on.StatusCode);
        var listed = (await laptop.GetAsync("api/vault/secrets")).AsArray();
        Assert.True(Assert.Single(listed)!["autoApprove"]!.GetValue<bool>());

        // The engine reads the same record: its VMs see the name and the auto-approve flag.
        var vm = await CreateVmAsync(laptop, engine, engineId);
        var guestList = await SubmitAsync(engine, vm, new { v = 1, op = "list", names = Array.Empty<string>() });
        Assert.True(guestList.Response!["items"]![0]!["autoApprove"]!.GetValue<bool>());

        // Revocation (8.4): what a device could read is every secret of the account.
        var me = (await laptop.GetAsync("api/state"))["device"]!["id"]!.GetValue<string>();
        var readable = (await laptop.GetAsync($"api/devices/{me}/secrets")).AsArray();
        Assert.Equal("github", Assert.Single(readable)!["name"]!.GetValue<string>());
        await laptop.GetAsync("api/devices/unknown/secrets", HttpStatusCode.NotFound);

        using (var deleted = await laptop.Api.DeleteAsync("api/vault/secrets/github"))
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Empty((await laptop.GetAsync("api/vault/secrets")).AsArray());
        await laptop.PostAsync("api/vault/secrets/github/reveal", null, HttpStatusCode.NotFound);

        var history = (await laptop.GetAsync("api/vault/history")).AsArray();
        Assert.Equal(["deleted", "changed", "changed", "added"], history.Select(h => h!["action"]!.GetValue<string>()));
        Assert.All(history, h => Assert.Equal("device", h!["provenance"]!["kind"]!.GetValue<string>()));

        // No value in any listing, nor in the agent's state file.
        var everything = listed.ToJsonString() + history.ToJsonString() + readable.ToJsonString() + laptop.StateFileText();
        foreach (var value in new[] { "ghp_Value_0001", "ghp_Value_0002" }) Assert.DoesNotContain(value, everything);
    }

    [Fact]
    public async Task Adding_a_secret_asks_no_engine_for_leases_only_the_list_does()
    {
        // The move copies the old vault one secret after the other; each add once asked every engine (30 s each when one was away).
        var leases = new CountedPath("/api/v1/vault/leases");
        using var engine = new EngineHost(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(leases));
        var (laptop, _) = await SetUpAsync(engine, "locked");
        await using var __ = laptop;

        var added = await laptop.PostAsync("api/vault/secrets", new { name = "db", description = "Database", value = "db_pw_123456" });
        Assert.Equal(("db", "Database", true), (added["name"]!.GetValue<string>(), added["description"]!.GetValue<string>(), added["readable"]!.GetValue<bool>()));
        Assert.Empty(added["leases"]!.AsArray());
        Assert.Equal(0, leases.Count);
        await laptop.GetAsync("api/vault/secrets");
        Assert.True(leases.Count > 0);
    }

    /// <summary>Counts the requests to one path of the engine.</summary>
    private sealed class CountedPath(string path) : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(call => context =>
            {
                if (context.Request.Path.StartsWithSegments(path)) Interlocked.Increment(ref _count);
                return call(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task Approvals_and_entry_requests_are_answered_here_with_the_device_as_provenance()
    {
        using var engine = new EngineHost();
        var (laptop, engineId) = await SetUpAsync(engine, "available");
        await using var _ = laptop;
        await laptop.PostAsync("api/vault/secrets", new { name = "db", description = "Database", value = "db_pw_123456" });
        var vm = await CreateVmAsync(laptop, engine, engineId);

        // A VM asks to delete; the agent lists the approval with its engine and allows it.
        var asked = await SubmitAsync(engine, vm, new { v = 1, op = "delete", names = new[] { "db" }, reason = "cleanup" });
        var approvals = (await laptop.GetAsync("api/vault/approvals"))["approvals"]!.AsArray();
        var approval = Assert.Single(approvals)!;
        Assert.Equal(engineId, approval["engine"]!.GetValue<string>());
        Assert.Equal(asked.PendingId, approval["id"]!.GetValue<string>());
        Assert.Equal("delete", approval["op"]!.GetValue<string>());
        Assert.Equal(vm.Name, approval["vm"]!.GetValue<string>());
        await laptop.PostAsync($"api/vault/approvals/{engineId}/{asked.PendingId}", new { decision = "maybe" }, HttpStatusCode.BadRequest);
        await laptop.PostAsync($"api/vault/approvals/{engineId}/{asked.PendingId}", new { decision = "allow" }, HttpStatusCode.NoContent);
        Assert.Empty((await laptop.GetAsync("api/vault/secrets")).AsArray());
        var deleted = (await laptop.GetAsync("api/vault/history")).AsArray()[0]!;
        Assert.Equal("deleted", deleted["action"]!.GetValue<string>());
        Assert.Equal("vm-approved", deleted["provenance"]!["kind"]!.GetValue<string>());
        Assert.Equal(vm.Name, deleted["provenance"]!["vm"]!.GetValue<string>());
        Assert.Equal(asked.PendingId, deleted["provenance"]!["requestId"]!.GetValue<string>());
        var me = (await laptop.GetAsync("api/state"))["device"]!["id"]!.GetValue<string>();
        Assert.Equal("device:" + me, deleted["provenance"]!["device"]!.GetValue<string>());

        // construct secret ask: the user enters the value here.
        var ask = await SubmitAsync(engine, vm, new { v = 1, op = "ask", names = new[] { "jira" }, description = "Jira token", reason = "release" });
        var requests = (await laptop.GetAsync("api/vault/entry-requests"))["requests"]!.AsArray();
        var request = Assert.Single(requests)!;
        Assert.Equal(ask.Response!["requestId"]!.GetValue<string>(), request["id"]!.GetValue<string>());
        Assert.Equal("Jira token", request["description"]!.GetValue<string>());
        await laptop.PostAsync($"api/vault/entry-requests/{engineId}/{request["id"]}", new { value = "jira_V4lue", username = "bob" }, HttpStatusCode.NoContent);
        var entered = Assert.Single((await laptop.GetAsync("api/vault/secrets")).AsArray())!;
        Assert.Equal("jira", entered["name"]!.GetValue<string>());
        Assert.False(entered["autoApprove"]!.GetValue<bool>());
        Assert.Equal("entered-on-request", entered["provenance"]!["kind"]!.GetValue<string>());
        Assert.Equal("device:" + me, entered["provenance"]!["device"]!.GetValue<string>());
        Assert.Empty(entered["leases"]!.AsArray());
        Assert.Equal("jira_V4lue", (await laptop.PostAsync("api/vault/secrets/jira/reveal", null))["value"]!.GetValue<string>());

        // A second request for a name that exists by then overwrites nothing; a decline removes it.
        var again = await SubmitAsync(engine, vm, new { v = 1, op = "ask", names = new[] { "npm" }, description = "npm" });
        await laptop.PostAsync("api/vault/secrets", new { name = "npm", description = "set here", value = "npm_from_here" });
        var npmId = again.Response!["requestId"]!.GetValue<string>();
        await laptop.PostAsync($"api/vault/entry-requests/{engineId}/{npmId}", new { value = "npm_entered" }, HttpStatusCode.Conflict);
        Assert.Equal("npm_from_here", (await laptop.PostAsync("api/vault/secrets/npm/reveal", null))["value"]!.GetValue<string>());
        await laptop.PostAsync($"api/vault/entry-requests/{engineId}/{npmId}/decline", null, HttpStatusCode.NoContent);
        Assert.Empty((await laptop.GetAsync("api/vault/entry-requests"))["requests"]!.AsArray());
    }

    [Fact]
    public async Task An_approval_shows_the_uses_and_duration_asked_grants_the_duration_chosen_and_its_lease_can_be_ended()
    {
        using var engine = new EngineHost();
        var (laptop, engineId) = await SetUpAsync(engine, "available");
        await using var _ = laptop;
        await laptop.PostAsync("api/vault/secrets", new { name = "db", description = "Database", value = "db_pw_123456" });
        var vm = await CreateVmAsync(laptop, engine, engineId);

        var request = await SubmitAsync(engine, vm, new { v = 1, op = "request", names = new[] { "db" }, uses = 3, ttl = 7200 });
        var approval = Assert.Single((await laptop.GetAsync("api/vault/approvals"))["approvals"]!.AsArray())!;
        Assert.Equal(3, approval["uses"]!.GetValue<int>());
        Assert.Equal(7200, approval["ttlSeconds"]!.GetValue<int>());

        await laptop.PostAsync($"api/vault/approvals/{engineId}/{request.PendingId}", new { decision = "deny", ttlSeconds = 600 }, HttpStatusCode.BadRequest);
        await laptop.PostAsync($"api/vault/approvals/{engineId}/{request.PendingId}", new { decision = "allow", ttlSeconds = 10 }, HttpStatusCode.BadRequest);
        var before = DateTimeOffset.UtcNow;
        await laptop.PostAsync($"api/vault/approvals/{engineId}/{request.PendingId}", new { decision = "allow", ttlSeconds = 600 }, HttpStatusCode.NoContent);

        var lease = Assert.Single(Assert.Single((await laptop.GetAsync("api/vault/secrets")).AsArray())!["leases"]!.AsArray())!;
        Assert.Equal(3, lease["usesLeft"]!.GetValue<int>());
        Assert.InRange(lease["expiresAt"]!.GetValue<long>(), before.AddSeconds(590).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.AddSeconds(601).ToUnixTimeMilliseconds());
        var leaseId = lease["id"]!.GetValue<string>();
        Assert.NotEmpty(leaseId);

        using (var ended = await laptop.Api.DeleteAsync($"api/vault/leases/{engineId}/{leaseId}"))
            Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        Assert.Empty(Assert.Single((await laptop.GetAsync("api/vault/secrets")).AsArray())!["leases"]!.AsArray());
        using (var again = await laptop.Api.DeleteAsync($"api/vault/leases/{engineId}/{leaseId}"))
            Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task New_and_answered_vault_requests_reach_the_events_without_polling()
    {
        using var engine = new EngineHost();
        var (laptop, engineId) = await SetUpAsync(engine, "available");
        await using var _ = laptop;
        await laptop.PostAsync("api/vault/secrets", new { name = "db", description = "Database", value = "db_pw_123456" });
        var vm = await CreateVmAsync(laptop, engine, engineId);

        var request = await SubmitAsync(engine, vm, new { v = 1, op = "request", names = new[] { "db" } });
        await EventAsync(laptop, "vault.approval.added", request.PendingId!);
        await laptop.PostAsync($"api/vault/approvals/{engineId}/{request.PendingId}", new { decision = "deny" }, HttpStatusCode.NoContent);
        await EventAsync(laptop, "vault.approval.ended", request.PendingId!);

        var ask = await SubmitAsync(engine, vm, new { v = 1, op = "ask", names = new[] { "jira" }, description = "Jira token" });
        var askId = ask.Response!["requestId"]!.GetValue<string>();
        var added = await EventAsync(laptop, "vault.entry-request.added", askId);
        Assert.Equal(engineId, added.Engine);
        Assert.Equal(vm.Name, added.Data!["vm"]!.GetValue<string>());
        await laptop.PostAsync($"api/vault/entry-requests/{engineId}/{askId}/decline", null, HttpStatusCode.NoContent);
        await EventAsync(laptop, "vault.entry-request.ended", askId);
    }

    [Fact]
    public async Task An_answer_here_ends_the_request_on_every_surface_also_without_the_engines_events()
    {
        // Finding 47: a request allowed in the pop-out stayed in the window; an old host sends no events at all.
        using var engine = new EngineHost(services => services.AddTransient<Microsoft.AspNetCore.Hosting.IStartupFilter, NoEventStream>());
        var (laptop, engineId) = await SetUpAsync(engine, "available");
        await using var _ = laptop;
        await laptop.PostAsync("api/vault/secrets", new { name = "db", description = "Database", value = "db_pw_123456" });
        var vm = await CreateVmAsync(laptop, engine, engineId);

        var request = await SubmitAsync(engine, vm, new { v = 1, op = "request", names = new[] { "db" } });
        await laptop.PostAsync($"api/vault/approvals/{engineId}/{request.PendingId}", new { decision = "deny" }, HttpStatusCode.NoContent);
        Assert.Equal(engineId, (await EventAsync(laptop, "vault.approval.ended", request.PendingId!)).Engine);

        var ask = await SubmitAsync(engine, vm, new { v = 1, op = "ask", names = new[] { "jira" }, description = "Jira token" });
        var askId = ask.Response!["requestId"]!.GetValue<string>();
        await laptop.PostAsync($"api/vault/entry-requests/{engineId}/{askId}/decline", null, HttpStatusCode.NoContent);
        await EventAsync(laptop, "vault.entry-request.ended", askId);
    }

    /// <summary>An engine whose event stream does not answer (an old host has none).</summary>
    private sealed class NoEventStream : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(call => context =>
            {
                if (context.Request.Path.StartsWithSegments("/api/v1/events")) { context.Response.StatusCode = 404; return Task.CompletedTask; }
                return call(context);
            });
            next(app);
        };
    }

    private static async Task<Events.AgentEvent> EventAsync(TestAgent agent, string type, string id)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        while (true)
        {
            using (var subscription = agent.Host.Agent.Events.Subscribe(0))
            {
                if (subscription.Replay.FirstOrDefault(e => e.Type == type && e.Data?["id"]?.GetValue<string>() == id) is { } found) return found;
            }

            Assert.True(DateTime.UtcNow < until, $"no {type} event for {id}");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Switching_the_mode_grants_or_withdraws_K_and_an_allow_on_a_locked_engine_unlocks_the_VM()
    {
        using var engine = new EngineHost();
        var (laptop, engineId) = await SetUpAsync(engine, "available");
        await using var _ = laptop;
        await laptop.PostAsync("api/vault/secrets", new { name = "db", description = "Database", value = "db_pw_123456" });
        var vm = await CreateVmAsync(laptop, engine, engineId);

        var mode = await laptop.GetAsync($"api/vault/engines/{engineId}/mode");
        Assert.Equal("available", mode["mode"]!.GetValue<string>());
        Assert.True(mode["holdsKey"]!.GetValue<bool>());
        Assert.True(mode["records"]!.GetValue<bool>());

        using (var locked = await laptop.Api.PutAsJsonAsync($"api/vault/engines/{engineId}/mode", new { mode = "locked" }))
        {
            var body = JsonNode.Parse(await locked.Content.ReadAsStringAsync())!;
            Assert.Equal("locked", body["mode"]!.GetValue<string>());
            Assert.False(body["holdsKey"]!.GetValue<bool>());
        }

        // In locked mode, as today, a value is "locked" until a device unlocks the VM; a request waits for a device,
        // and its allow here unlocks the VM, so the lease it grants can be read.
        var service = engine.Services.GetRequiredService<VaultHostService>();
        Assert.Equal("locked", (await SubmitAsync(engine, vm, new { v = 1, op = "get", names = new[] { "db" } })).Response!["status"]!.GetValue<string>());
        var request = await SubmitAsync(engine, vm, new { v = 1, op = "request", names = new[] { "db" }, ttl = 600 });
        Assert.NotNull(request.PendingId);
        await laptop.PostAsync($"api/vault/approvals/{engineId}/{request.PendingId}", new { decision = "allow" }, HttpStatusCode.NoContent);
        Assert.Contains(vm.Name, (await laptop.GetAsync($"api/vault/engines/{engineId}/mode"))["unlockedVms"]!.AsArray().Select(v => v!.GetValue<string>()));
        Assert.Equal("ok", (await service.PollAsync(vm, request.PendingId!, TimeSpan.Zero, CancellationToken.None))!["status"]!.GetValue<string>());
        var get = await SubmitAsync(engine, vm, new { v = 1, op = "get", names = new[] { "db" } });
        Assert.Equal("ok", get.Response!["status"]!.GetValue<string>());

        using (var available = await laptop.Api.PutAsJsonAsync($"api/vault/engines/{engineId}/mode", new { mode = "available" }))
            Assert.Equal("available", JsonNode.Parse(await available.Content.ReadAsStringAsync())!["mode"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_allow_on_a_host_of_the_old_release_without_a_vault_mode_route_is_posted_as_before()
    {
        // The old release keeps the Companion's vault and has neither /engine nor /vault/mode.
        var oldHost = new OldHostRoutes();
        using var engine = new EngineHost(services => services.AddSingleton<Microsoft.AspNetCore.Hosting.IStartupFilter>(oldHost));
        var token = await engine.AddUserAsync("alice");
        await using var laptop = await TestAgent.StartAsync();
        var engineId = await laptop.AddEngineAsync(engine, token);
        var vm = await CreateVmAsync(laptop, engine, engineId);
        var service = engine.Services.GetRequiredService<VaultHostService>();
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(Constructd.Core.Logic.VaultCrypto.KeyBytes);
        await service.PutSettingsAsync("alice", "available", Convert.ToBase64String(key), Constructd.Core.Logic.VaultCrypto.KeyCheck(key), CancellationToken.None);
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await service.PutEntriesAsync("alice", [new VaultEntry("db", "Database", false,
            Constructd.Core.Logic.VaultCrypto.SealEntry(key, "db", stamp, "", "db_pw_123456"), stamp, "pc:test", false)], CancellationToken.None);

        var request = await SubmitAsync(engine, vm, new { v = 1, op = "request", names = new[] { "db" }, ttl = 600 });
        Assert.NotNull(request.PendingId);
        var approval = Assert.Single((await laptop.GetAsync("api/vault/approvals"))["approvals"]!.AsArray())!;
        Assert.Equal(request.PendingId, approval["id"]!.GetValue<string>());

        await laptop.PostAsync($"api/vault/approvals/{engineId}/{request.PendingId}", new { decision = "allow" }, HttpStatusCode.NoContent);
        Assert.True(oldHost.ModeAsked > 0);
        Assert.Equal("ok", (await service.PollAsync(vm, request.PendingId!, TimeSpan.Zero, CancellationToken.None))!["status"]!.GetValue<string>());
    }

    /// <summary>The routes a host of the old release lacks answer 404, as there.</summary>
    private sealed class OldHostRoutes : Microsoft.AspNetCore.Hosting.IStartupFilter
    {
        private int _modeAsked;

        public int ModeAsked => _modeAsked;

        public Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> Configure(Action<Microsoft.AspNetCore.Builder.IApplicationBuilder> next) => app =>
        {
            app.Use(call => context =>
            {
                var path = context.Request.Path;
                if (path.StartsWithSegments("/api/v1/vault/mode")) Interlocked.Increment(ref _modeAsked);
                if (path.StartsWithSegments("/api/v1/engine") || path.StartsWithSegments("/api/v1/vault/mode"))
                {
                    context.Response.StatusCode = 404;
                    return Task.CompletedTask;
                }

                return call(context);
            });
            next(app);
        };
    }

    [Fact]
    public async Task Files_a_scrub_found_are_listed_from_every_engine_and_answered_at_the_one_that_found_them()
    {
        using var engine = new EngineHost();
        var (laptop, engineId) = await SetUpAsync(engine, "available");
        await using var _ = laptop;
        await laptop.PostAsync("api/vault/secrets", new { name = "db", description = "Database", value = "db_pw_123456" });
        var vm = await CreateVmAsync(laptop, engine, engineId);
        Assert.Empty((await laptop.GetAsync("api/vault/files"))["files"]!.AsArray());

        // The VM's agent log is redacted without asking; the other two wait for the user, with the engine that found them.
        await FoundAsync(laptop, engine, engineId, vm, "/root/.claude/projects/-root/a.jsonl", "/root/.secrets/db.json", "/root/app/state.db");
        // A second engine of the account with a file of its own.
        using var other = new EngineHost();
        var otherId = await laptop.AddEngineAsync(other, await other.AddUserAsync("alice"));
        await other.Services.GetRequiredService<IVaultStore>().SaveAsync(new VaultFileDecision("f-other", "alice", "harbor", "/root/.npmrc",
            Convert.ToBase64String("/root/.npmrc"u8.ToArray()), [0], "text", 7, [new VaultSecretCopy("npm", "sealed", 1)], "scan-1", DateTimeOffset.UtcNow), CancellationToken.None);

        var view = await laptop.GetAsync("api/vault/files");
        Assert.Empty(view["unreachable"]!.AsArray());
        var files = view["files"]!.AsArray().Select(f => f!).ToArray();
        Assert.Equal([(engineId, "/root/.secrets/db.json"), (engineId, "/root/app/state.db"), (otherId, "/root/.npmrc")],
            files.Select(f => (f["engine"]!.GetValue<string>(), f["path"]!.GetValue<string>())));
        var (env, database) = (files[0], files[1]);
        Assert.Equal((vm.Name, "text", 42L), (env["vm"]!.GetValue<string>(), env["type"]!.GetValue<string>(), env["size"]!.GetValue<long>()));
        Assert.Equal(["db"], env["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("sqlite", database["type"]!.GetValue<string>());
        Assert.Equal("harbor", files[2]["vm"]!.GetValue<string>());
        Assert.Equal(["npm"], files[2]["names"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.False(string.IsNullOrEmpty(files[2]["engineName"]!.GetValue<string>()));

        // Keep, then the same file again: decided already. An action the engine does not know is refused here.
        var envId = env["id"]!.GetValue<string>();
        await laptop.PostAsync($"api/vault/files/{engineId}/{envId}", new { action = "shred" }, HttpStatusCode.BadRequest);
        await laptop.PostAsync($"api/vault/files/{engineId}/{envId}", new { action = "keep" }, HttpStatusCode.NoContent);
        Assert.Equal(engineId, (await EventAsync(laptop, "vault.file.ended", envId)).Engine);
        var again = await laptop.PostAsync($"api/vault/files/{engineId}/{envId}", new { action = "redact" }, HttpStatusCode.NotFound);
        Assert.Equal("not-found", again["code"]!.GetValue<string>());
        await laptop.PostAsync($"api/vault/files/{otherId}/{envId}", new { action = "keep" }, HttpStatusCode.NotFound);
        await laptop.PostAsync($"api/vault/files/no-such-engine/{envId}", new { action = "keep" }, HttpStatusCode.NotFound);

        // A delete goes to the VM as an apply job on its next scrub; the database takes its side files along.
        await laptop.PostAsync($"api/vault/files/{engineId}/{database["id"]!.GetValue<string>()}", new { action = "delete" }, HttpStatusCode.NoContent);
        var delete = (await ApplyFilesAsync(engine, vm)).Single(f => f.Path == "/root/app/state.db");
        Assert.Equal("delete", delete.Action);
        Assert.Equal(["/root/.npmrc"], (await laptop.GetAsync("api/vault/files"))["files"]!.AsArray().Select(f => f!["path"]!.GetValue<string>()));

        // An engine that does not answer is named, and its files are left out.
        other.Dispose();
        view = await laptop.GetAsync("api/vault/files");
        Assert.Empty(view["files"]!.AsArray());
        Assert.Equal([otherId], view["unreachable"]!.AsArray().Select(e => e!.GetValue<string>()));
    }

    [Fact]
    public async Task A_redact_on_a_locked_engine_unlocks_the_VM_so_it_is_delivered_and_waits_for_a_VM_that_is_off()
    {
        using var engine = new EngineHost();
        var (laptop, engineId) = await SetUpAsync(engine, "available");
        await using var _ = laptop;
        await laptop.PostAsync("api/vault/secrets", new { name = "db", description = "Database", value = "db_pw_123456" });
        var vm = await CreateVmAsync(laptop, engine, engineId);
        await FoundAsync(laptop, engine, engineId, vm, "/root/.secrets/db.json", "/root/notes.txt", "/root/old.txt");
        var ids = (await laptop.GetAsync("api/vault/files"))["files"]!.AsArray().ToDictionary(f => f!["path"]!.GetValue<string>(), f => f!["id"]!.GetValue<string>());
        using (var locked = await laptop.Api.PutAsJsonAsync($"api/vault/engines/{engineId}/mode", new { mode = "locked" }))
            Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        async Task<string[]> UnlockedAsync() =>
            (await laptop.GetAsync($"api/vault/engines/{engineId}/mode"))["unlockedVms"]!.AsArray().Select(v => v!.GetValue<string>()).ToArray();
        Assert.Empty(await UnlockedAsync());

        // A delete needs no key: the VM stays locked and gets the job.
        await laptop.PostAsync($"api/vault/files/{engineId}/{ids["/root/old.txt"]}", new { action = "delete" }, HttpStatusCode.NoContent);
        Assert.Empty(await UnlockedAsync());
        Assert.Equal("delete", (await ApplyFilesAsync(engine, vm)).Single(f => f.Path == "/root/old.txt").Action);

        // A redaction needs K on the VM: without the unlock it would wait for the next allow. The redact unlocks it.
        await laptop.PostAsync($"api/vault/files/{engineId}/{ids["/root/.secrets/db.json"]}", new { action = "redact" }, HttpStatusCode.NoContent);
        Assert.Contains(vm.Name, await UnlockedAsync());
        var redact = (await ApplyFilesAsync(engine, vm)).Single(f => f.Path == "/root/.secrets/db.json");
        Assert.Equal("redact", redact.Action);
        Assert.Contains("db_pw_123456", redact.Patterns);

        // A VM that is off cannot be unlocked: the redaction is recorded and waits for its next unlock.
        var service = engine.Services.GetRequiredService<VaultHostService>();
        var driver = engine.Services.GetRequiredService<FakeHypervisorDriver>();
        driver.SetState(vm.Name, VmState.Off);
        service.Drop(vm.Name);
        await laptop.PostAsync($"api/vault/files/{engineId}/{ids["/root/notes.txt"]}", new { action = "redact" }, HttpStatusCode.NoContent);
        Assert.Empty(await UnlockedAsync());
        Assert.Empty((await laptop.GetAsync("api/vault/files"))["files"]!.AsArray());
        Assert.DoesNotContain(await ApplyFilesAsync(engine, vm), f => f.Path == "/root/notes.txt");
        driver.SetState(vm.Name, VmState.Running);
        await laptop.PostAsync($"api/vault/engines/{engineId}/vms/{vm.Name}/unlock", null, HttpStatusCode.NoContent);
        Assert.Equal("redact", (await ApplyFilesAsync(engine, vm)).Single(f => f.Path == "/root/notes.txt").Action);
    }

    /// <summary>
    /// A lease of <c>db</c> on the VM, allowed and ended here, and the VM's scan of its disk: hits at <paramref name="paths"/>
    /// (a <c>.db</c> file as a database). An agent log among them is redacted without asking; every other one waits for the user.
    /// </summary>
    private static async Task FoundAsync(TestAgent laptop, EngineHost engine, string engineId, Vm vm, params string[] paths)
    {
        var request = await SubmitAsync(engine, vm, new { v = 1, op = "request", names = new[] { "db" } });
        await laptop.PostAsync($"api/vault/approvals/{engineId}/{request.PendingId}", new { decision = "allow" }, HttpStatusCode.NoContent);
        var lease = Assert.Single(Assert.Single((await laptop.GetAsync("api/vault/secrets")).AsArray(), s => s!["name"]!.GetValue<string>() == "db")!["leases"]!.AsArray())!;
        using (var ended = await laptop.Api.DeleteAsync($"api/vault/leases/{engineId}/{lease["id"]!.GetValue<string>()}"))
            Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);

        var service = engine.Services.GetRequiredService<VaultHostService>();
        var scan = Assert.Single((await service.ScrubsAsync(vm, CancellationToken.None))["jobs"]!.AsArray())!;
        Assert.Equal("scan", scan["step"]!.GetValue<string>());
        var hits = paths.Select(p => new { path = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(p)), indexes = new[] { 0 }, size = 42, type = p.EndsWith(".db", StringComparison.Ordinal) ? "sqlite" : "text" });
        await service.ScrubResultAsync(vm, scan["id"]!.GetValue<string>(), JsonNode.Parse(TestAgent.Json(new { step = "scan", complete = true, hits }))!.AsObject(), CancellationToken.None);
    }

    /// <summary>The files of the apply jobs the VM gets on its next scrub, with the patterns each is to redact.</summary>
    private static async Task<(string Path, string Action, string[] Patterns)[]> ApplyFilesAsync(EngineHost engine, Vm vm)
    {
        static string Text(JsonNode? node) => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(node!.GetValue<string>()));
        var jobs = (await engine.Services.GetRequiredService<VaultHostService>().ScrubsAsync(vm, CancellationToken.None))["jobs"]!.AsArray();
        return jobs.Where(j => j!["step"]!.GetValue<string>() == "apply").SelectMany(j => j!["files"]!.AsArray())
            .Select(f => (Text(f!["path"]), f!["action"]!.GetValue<string>(), f["patterns"]!.AsArray().Select(Text).ToArray())).ToArray();
    }

    // ---- support ----

    [Fact]
    public async Task A_lost_sign_in_is_listed_with_its_engine_until_it_is_dismissed()
    {
        using var engine = new EngineHost();
        var (laptop, engineId) = await SetUpAsync(engine, "available");
        await using var _ = laptop;
        var vm = await CreateVmAsync(laptop, engine, engineId);
        Assert.Empty((await laptop.GetAsync("api/agent-notices"))["notices"]!.AsArray());

        // The VM reported Codex signed in, then signed out (S36).
        var agents = engine.Services.GetRequiredService<Constructd.Api.Jobs.VmAgents>();
        await agents.SaveAsync(vm, [new("codex", "0.170.0", true)], DateTimeOffset.UtcNow, CancellationToken.None);
        await agents.SaveAsync(vm, [new("codex", "0.170.0", false)], DateTimeOffset.UtcNow, CancellationToken.None);

        var notice = Assert.Single((await laptop.GetAsync("api/agent-notices"))["notices"]!.AsArray())!;
        Assert.Equal((engineId, "dev", "codex", "Codex"), (notice["engine"]!.GetValue<string>(), notice["vm"]!.GetValue<string>(),
            notice["agent"]!.GetValue<string>(), notice["name"]!.GetValue<string>()));
        using (var dismissed = await laptop.Api.DeleteAsync($"api/engines/{engineId}/api/v1/vms/dev/agents/codex/notice"))
            Assert.Equal(HttpStatusCode.NoContent, dismissed.StatusCode);
        Assert.Empty((await laptop.GetAsync("api/agent-notices"))["notices"]!.AsArray());
    }

    [Fact]
    public async Task Displayed_vault_approvals_route_marks_approvals_and_entry_requests_and_validates_input()
    {
        using var engine = new EngineHost();
        var (laptop, engineId) = await SetUpAsync(engine, "available");
        await using var _ = laptop;
        await laptop.PostAsync("api/vault/secrets", new { name = "db", description = "Database", value = "db_pw_123456" });
        var vm = await CreateVmAsync(laptop, engine, engineId);

        var health = await laptop.GetAsync("api/health");
        Assert.Contains("vault-approvals-displayed", health["apiFeatures"]!.AsArray().Select(f => f!.GetValue<string>()));

        var asked = await SubmitAsync(engine, vm, new { v = 1, op = "request", names = new[] { "db" }, reason = "migrate" });
        var entry = await SubmitAsync(engine, vm, new { v = 1, op = "ask", names = new[] { "jira" }, description = "Jira token" });
        var entryId = entry.Response!["requestId"]!.GetValue<string>();

        // Initially nothing is displayed elsewhere.
        Assert.False((await laptop.GetAsync("api/vault/approvals"))["approvals"]![0]!["displayedElsewhere"]!.GetValue<bool>());
        Assert.False((await laptop.GetAsync("api/vault/entry-requests"))["requests"]![0]!["displayedElsewhere"]!.GetValue<bool>());

        // Empty heartbeat is accepted.
        await laptop.PostAsync("api/vault/approvals/displayed", new { items = Array.Empty<object>() }, HttpStatusCode.NoContent);
        Assert.True(laptop.Host.Agent.VaultDisplay.HasActiveReporter());

        // Reporting both the approval and the entry request marks them displayedElsewhere.
        await laptop.PostAsync("api/vault/approvals/displayed", new
        {
            items = new object[]
            {
                new { kind = "approval", engine = engineId, id = asked.PendingId },
                new { kind = "entry-request", engine = engineId, id = entryId },
            },
        }, HttpStatusCode.NoContent);

        Assert.True((await laptop.GetAsync("api/vault/approvals"))["approvals"]![0]!["displayedElsewhere"]!.GetValue<bool>());
        Assert.True((await laptop.GetAsync("api/vault/entry-requests"))["requests"]![0]!["displayedElsewhere"]!.GetValue<bool>());

        // Validation: malformed kind, invalid engine, >50 items are refused with 400.
        await laptop.PostAsync("api/vault/approvals/displayed", new { items = new[] { new { kind = "file", engine = engineId, id = "x" } } }, HttpStatusCode.BadRequest);
        await laptop.PostAsync("api/vault/approvals/displayed", new { items = new[] { new { kind = "approval", engine = "../x", id = "x" } } }, HttpStatusCode.BadRequest);
        var tooMany = Enumerable.Range(0, VaultDisplayTracker.MaxDisplayed + 1).Select(i => new { kind = "approval", engine = engineId, id = $"id-{i}" }).ToArray();
        await laptop.PostAsync("api/vault/approvals/displayed", new { items = tooMany }, HttpStatusCode.BadRequest);
    }

    private static async Task<(TestAgent Laptop, string EngineId)> SetUpAsync(EngineHost engine, string vaultMode)
    {
        var signIn = await engine.AddUserAsync("alice");
        var laptop = await TestAgent.StartAsync();
        var engineId = await laptop.AddEngineAsync(engine, signIn);
        await laptop.PostAsync("api/device/setup", new { engine = engineId, deviceName = "laptop", vaultMode });
        return (laptop, engineId);
    }

    private static async Task<Vm> CreateVmAsync(TestAgent laptop, EngineHost engine, string engineId)
    {
        using var created = await laptop.Api.PostAsJsonAsync($"api/engines/{engineId}/api/v1/vms", new { name = "dev", cpu = 2, ramGb = 4, diskGb = 40 });
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var jobId = JsonNode.Parse(await created.Content.ReadAsStringAsync())!["jobId"]!.GetValue<string>();
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while ((await laptop.GetAsync($"api/engines/{engineId}/api/v1/jobs/{jobId}"))["state"]!.GetValue<string>() is "queued" or "running")
        {
            Assert.True(DateTime.UtcNow < until, "The VM was not created in time.");
            await Task.Delay(100);
        }

        return (await engine.Services.GetRequiredService<IVmRepository>().GetAsync("dev", CancellationToken.None))!;
    }

    /// <summary>A guest request, as the VM's token would send it.</summary>
    private static Task<VaultSubmission> SubmitAsync(EngineHost engine, Vm vm, object request) =>
        engine.Services.GetRequiredService<VaultHostService>().SubmitAsync(vm, JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(request))!.AsObject(), CancellationToken.None);
}
