using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Remote;
using Construct.Companion.Core.Vault;
using Construct.Companion.Fakes;
using Microsoft.Extensions.DependencyInjection;
using static Construct.Companion.Tests.Runtime.ProcessSupervisorTests;
using Harness = Construct.Companion.Tests.Ipc.HttpTests.Harness;
using VaultHostsTests = Construct.Companion.Tests.Vault.VaultHostsTests;
namespace Construct.Companion.Tests.Ipc;

// GET/POST /v1/vault/approvals: T3 Code Desktop lists and answers pending key vault approvals through the
// Companion's authenticated local API. Nothing else of the vault is reachable there (HttpTests).
public sealed class VaultApprovalsHttpTests
{
    private const string Value = "SENTINEL-value-8d2f", Username = "SENTINEL-user-41c7";
    private static readonly string[] Fields = ["id", "instance", "vm", "kind", "host", "requestId", "hostRequestId", "op", "title", "message", "action", "deny", "names", "createdAt", "deadline"];

    // A dialog that stays open until it is closed from outside.
    private static void OpenUntilClosed(FakePrompts prompts, Action closed) =>
        prompts.ApprovalHandler = async (_, token) => { try { await Task.Delay(Timeout.Infinite, token); return false; } finally { closed(); } };
    private static async Task<JsonArray> List(Harness host) => (await host.Client.GetFromJsonAsync<JsonObject>("/v1/vault/approvals"))!["approvals"]!.AsArray();
    private static Task<HttpResponseMessage> Decide(Harness host, string id, object body) => host.Client.PostAsJsonAsync("/v1/vault/approvals/" + id, body);
    private static async Task Problem(HttpResponseMessage response, int status, string code)
    {
        Assert.Equal(status, (int)response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(code, (await response.Content.ReadFromJsonAsync<JsonObject>())!["code"]!.GetValue<string>());
    }

    [Fact]
    public async Task ALocalVmsRequestIsListedWithoutValuesAndAnsweredOverIpc()
    {
        await using var host = await Harness.Start();
        var vault = host.App.Services.GetRequiredService<VaultService>();
        vault.Save(new("github-token", "GitHub token for CI", Username, new Secret(Value))); vault.EnsureKey();
        var key = vault.ExportKey()!.Reveal();
        var closed = 0; OpenUntilClosed(host.Get<FakePrompts, IPrompts>(), () => Interlocked.Increment(ref closed));
        var transport = host.Get<FakeInstanceConnections, IInstanceConnections>().Transports["agent-vm"];
        await Eventually(() => transport.Watches.Any(w => w.Script == VaultProtocol.WatchScript() && !w.Process.Stopped));
        var watch = transport.Watches.Last(w => w.Script == VaultProtocol.WatchScript()).Process;
        string Ask(string op, string id)
        {
            watch.Emit(new JsonObject { ["v"] = 1, ["id"] = id, ["op"] = op, ["names"] = new JsonArray("github-token"), ["reason"] = "release", ["source"] = "root@agent-vm",
                ["deadline"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds() }.ToJsonString() + "\n");
            return id;
        }
        JsonObject? Response(string id) { lock (transport.Scripts) { var i = transport.Scripts.IndexOf(VaultProtocol.RespondScript(id)); return i < 0 ? null : JsonNode.Parse(transport.StandardInputs[i]!.Reveal())!.AsObject(); } }

        var requestId = Ask("get", "1700000000000-1-71");
        JsonArray listed = [];
        await Eventually(() => (listed = List(host).GetAwaiter().GetResult()).Count == 1);
        var raw = await host.Client.GetStringAsync("/v1/vault/approvals");
        foreach (var sentinel in new[] { Value, Username, key, Convert.ToBase64String(Encoding.UTF8.GetBytes(Value)) }) Assert.DoesNotContain(sentinel, raw);
        var item = listed[0]!.AsObject();
        Assert.Equal(Fields.Order(), item.Select(p => p.Key).Order());
        Assert.Equal(("agent-vm", "agent-vm", "local", requestId, "get"), (item["instance"]!.GetValue<string>(), item["vm"]!.GetValue<string>(), item["kind"]!.GetValue<string>(),
            item["requestId"]!.GetValue<string>(), item["op"]!.GetValue<string>()));
        Assert.Null(item["host"]); Assert.Null(item["hostRequestId"]);
        Assert.Equal(("Key vault — one-time access", "Allow once", "Deny"), (item["title"]!.GetValue<string>(), item["action"]!.GetValue<string>(), item["deny"]!.GetValue<string>()));
        Assert.Contains("“release”", item["message"]!.GetValue<string>()); Assert.Equal("github-token", Assert.Single(item["names"]!.AsArray())!.GetValue<string>());
        Assert.True(item["createdAt"]!.GetValue<long>() > 0); Assert.True(item["deadline"]!.GetValue<long>() > item["createdAt"]!.GetValue<long>());

        var id = item["id"]!.GetValue<string>();
        using (var approved = await Decide(host, id, new { decision = "approve" })) Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        await Eventually(() => Response(requestId) is not null);
        Assert.Equal("ok", Response(requestId)!["status"]!.GetValue<string>());
        Assert.Equal(1, closed); Assert.Empty(await List(host));
        using (var again = await Decide(host, id, new { decision = "deny" })) await Problem(again, 409, "already-decided");

        // A denial: the agent hears "denied", the secret stays.
        var deleteId = Ask("delete", "1700000000000-2-71");
        await Eventually(() => List(host).GetAwaiter().GetResult().Count == 1);
        var pending = (await List(host))[0]!["id"]!.GetValue<string>();
        using (var denied = await Decide(host, pending, new { decision = "deny" })) Assert.Equal(HttpStatusCode.NoContent, denied.StatusCode);
        await Eventually(() => Response(deleteId) is not null);
        Assert.Equal("denied", Response(deleteId)!["status"]!.GetValue<string>()); Assert.Equal(Value, vault.Reveal("github-token")!.Reveal());
        Assert.Equal(2, closed);
    }

    [Fact]
    public async Task DecisionsAreValidatedAndNeedTheBearer()
    {
        await using var host = await Harness.Start(runtimeJobs: false);
        var vault = host.App.Services.GetRequiredService<VaultService>();
        vault.Save(new("github-token", "GitHub token for CI", "", new Secret(Value)));
        OpenUntilClosed(host.Get<FakePrompts, IPrompts>(), () => { });
        var request = VaultProtocol.ParseRequest("""{"v":1,"id":"1700000000000-3-71","op":"get","names":["github-token"]}""").Request!;
        var get = vault.HandleAsync("agent-vm", request);
        await Eventually(() => vault.PendingApprovals().Count == 1);
        var id = vault.PendingApprovals()[0].Id;

        using (var unknown = await Decide(host, "no-such-approval", new { decision = "approve" })) await Problem(unknown, 404, "not-found");
        using (var odd = await Decide(host, "bad!id", new { decision = "approve" })) await Problem(odd, 404, "not-found");
        foreach (var body in new object[] { new { decision = "maybe" }, new { decision = 1 }, new { } })
            using (var bad = await Decide(host, id, body)) await Problem(bad, 400, "invalidDecision");
        using (var array = await Decide(host, id, new[] { "approve" })) await Problem(array, 400, "invalidRequest");
        using (var broken = await host.Client.PostAsync("/v1/vault/approvals/" + id, new StringContent("{", Encoding.UTF8, "application/json"))) await Problem(broken, 400, "invalidRequest");
        host.Client.DefaultRequestHeaders.Authorization = null;
        await host.Problem("GET", "/v1/vault/approvals", 401, "unauthorized");
        using (var anonymous = await Decide(host, id, new { decision = "approve" })) await Problem(anonymous, 401, "unauthorized");
        host.Authenticate();
        Assert.Equal(id, (await List(host))[0]!["id"]!.GetValue<string>()); // none of the refusals answered it
        Assert.False(get.IsCompleted);
        await host.Problem("GET", "/v1/vault/approvals/" + id, 405, "routeNotFound");
        await host.Problem("DELETE", "/v1/vault/approvals/" + id, 405, "routeNotFound");
        using (var answered = await Decide(host, id, new { decision = "deny" })) Assert.Equal(HttpStatusCode.NoContent, answered.StatusCode);
        Assert.Equal("denied", (await get)["status"]!.GetValue<string>());
    }

    [Fact]
    public async Task AHostApprovalIsAnsweredOnTheHost()
    {
        var fake = new VaultHostsTests.FakeHost(); var pin = new string('b', 64);
        var api = new FakeRemoteApi { Fingerprint = pin, Handler = fake.Handle };
        var client = new RemoteHostClient(api, new FakeFileSystem(new FakeClock()), new FakeTokenStore(), "https://host.example:7462", RemoteAuthentication.Negotiate, pin);
        await using var host = await Harness.Start(s => s.AddSingleton<IVaultHostDirectory>(new VaultHostsTests.TestDirectory(client)), runtimeJobs: false);
        var vault = host.App.Services.GetRequiredService<VaultService>(); vault.EnsureKey();
        var hosts = host.App.Services.GetRequiredService<VaultHosts>();
        var closed = 0; OpenUntilClosed(host.Get<FakePrompts, IPrompts>(), () => Interlocked.Increment(ref closed));
        hosts.Online(new("dev", VaultHostsTests.Slug, "dev"), true);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeMilliseconds();
        JsonObject Approval(string id) => new()
        {
            ["id"] = id, ["vm"] = "dev", ["op"] = "request", ["title"] = "Key vault — access request", ["message"] = "The VM “dev” asks for access to:\n• github-token — CI",
            ["action"] = "Approve", ["names"] = new JsonArray("github-token"), ["createdAt"] = deadline - 600000, ["deadline"] = deadline
        };
        fake.Edit(x => x.Approvals.Add(Approval("h1")));
        await hosts.PollAsync(false, default);
        await Eventually(() => vault.PendingApprovals().Count == 1);
        var raw = await host.Client.GetStringAsync("/v1/vault/approvals");
        Assert.DoesNotContain(vault.ExportKey()!.Reveal(), raw);
        var item = JsonNode.Parse(raw)!["approvals"]![0]!.AsObject();
        Assert.Equal(("dev", "dev", "host", VaultHostsTests.Slug, "h1"), (item["instance"]!.GetValue<string>(), item["vm"]!.GetValue<string>(), item["kind"]!.GetValue<string>(),
            item["host"]!.GetValue<string>(), item["hostRequestId"]!.GetValue<string>()));
        Assert.Null(item["requestId"]); Assert.Equal(deadline - 600000, item["createdAt"]!.GetValue<long>()); Assert.Equal(deadline, item["deadline"]!.GetValue<long>());

        using (var denied = await Decide(host, item["id"]!.GetValue<string>(), new { decision = "deny" })) Assert.Equal(HttpStatusCode.NoContent, denied.StatusCode);
        await hosts.DrainAsync();
        Assert.Equal("deny", fake.Decisions["h1"]); Assert.Single(fake.Calls("POST", "/vault/approvals/h1")); Assert.Equal(1, closed);

        // Answered on the phone a moment earlier: the host's 409 is the reply.
        fake.Edit(x => x.Approvals.Add(Approval("h2")));
        await hosts.PollAsync(false, default);
        await Eventually(() => vault.PendingApprovals().Count == 1);
        fake.Edit(x => x.Decisions["h2"] = "approve");
        using (var late = await Decide(host, vault.PendingApprovals()[0].Id, new { decision = "deny" })) await Problem(late, 409, "already-decided");
        await hosts.DrainAsync();
        Assert.Equal("approve", fake.Decisions["h2"]); Assert.Equal(2, closed);
    }
}
