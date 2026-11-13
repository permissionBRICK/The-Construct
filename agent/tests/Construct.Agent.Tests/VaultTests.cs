using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Construct.Agent.Tests.Support;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Services;
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

    // ---- support ----

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
