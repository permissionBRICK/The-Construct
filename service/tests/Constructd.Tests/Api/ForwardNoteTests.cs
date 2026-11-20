using System.Net;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Api;

/// <summary>
/// Every forward can say what it is for and where its service lives, so an agent that hits the per-VM cap can
/// tell the obsolete forwards from the ones still in use (docs/expose.md).
/// </summary>
public sealed class ForwardNoteTests
{
    private const string Forwards = "/api/v1/vms/work-vm/forwards";

    private static async Task<(TestApp App, HttpClient Owner, HttpClient Guest)> SetupAsync(TestApp? app = null)
    {
        app ??= new TestApp();
        var owner = await app.CreateUserClientAsync("bob");
        var job = await owner.CreateVmAsync("work-vm");
        return (app, owner, app.CreateVmTokenClient(job.VmToken()));
    }

    [Fact]
    public void A_vm_has_at_most_100_forwards_unless_the_host_says_otherwise() =>
        Assert.Equal(100, new ConstructdOptions().MaxForwardsPerVm);

    [Fact]
    public async Task The_shipped_configuration_caps_a_vm_at_100_forwards()
    {
        // The test host pins the cap to 3; without that, appsettings.json is what binds.
        await using var app = new TestApp(new Dictionary<string, string?> { ["Constructd:MaxForwardsPerVm"] = null });
        Assert.Equal(100, app.Service<ConstructdOptions>().MaxForwardsPerVm);
    }

    [Fact]
    public async Task An_acknowledged_forward_keeps_its_note()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app; using var _owner = owner; using var _guest = guest;
        var id = (await (await guest.PostJsonAsync(Forwards, new { vmPort = 5173, target = "client", purpose = "chart preview", source = "/root/repos/charts" }))
            .ReadAsync<ForwardResponse>()).Id;
        Assert.Equal(HttpStatusCode.OK, (await owner.PostJsonAsync($"{Forwards}/{id}/ack", new { status = "open", localPort = 5173 })).StatusCode);
        var listed = Assert.Single(await (await guest.GetAsync(Forwards)).ReadAsync<List<ForwardResponse>>());
        Assert.Equal("open", listed.Status);
        Assert.Equal(("chart preview", "/root/repos/charts"), (listed.Purpose, listed.Source));
    }

    [Fact]
    public async Task A_source_is_kept_exactly_and_one_with_control_characters_is_refused()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app; using var _owner = owner; using var _guest = guest;
        var kept = await (await guest.PostJsonAsync(Forwards, new { vmPort = 5173, target = "client", source = "/root/repos/chart  preview" }))
            .ReadAsync<ForwardResponse>();
        Assert.Equal("/root/repos/chart  preview", kept.Source);
        using var refused = await guest.PostJsonAsync(Forwards, new { vmPort = 5174, target = "client", source = "/root/repos/a\nb" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    /// <summary>A forward store whose note writes fail.</summary>
    private sealed class FailingNotes(InMemoryForwardStore inner) : IForwardStore
    {
        public Task<PortForward?> GetAsync(string id, CancellationToken ct) => inner.GetAsync(id, ct);
        public Task<IReadOnlyList<PortForward>> ListAsync(string? vmName, CancellationToken ct) => inner.ListAsync(vmName, ct);
        public Task<int> CountByVmAsync(string vmName, CancellationToken ct) => inner.CountByVmAsync(vmName, ct);
        public Task AddAsync(PortForward forward, CancellationToken ct) => inner.AddAsync(forward, ct);
        public Task<bool> SetAckAsync(string id, ForwardAck ack, CancellationToken ct) => inner.SetAckAsync(id, ack, ct);
        public Task<bool> SetDestinationAsync(string id, ForwardDestination destination, ForwardAck? ack, CancellationToken ct) => inner.SetDestinationAsync(id, destination, ack, ct);
        public Task<bool> SetNoteAsync(string id, string? purpose, string? source, CancellationToken ct) => throw new IOException("database is locked");
        public Task<bool> RemoveAsync(string id, CancellationToken ct) => inner.RemoveAsync(id, ct);
    }

    [Fact]
    public async Task A_note_that_cannot_be_stored_does_not_fail_the_forward()
    {
        var app = new TestApp(configureServices: services => services.AddSingleton<IForwardStore>(sp => new FailingNotes(sp.GetRequiredService<InMemoryForwardStore>())));
        var (_, owner, guest) = await SetupAsync(app);
        using var _app = app; using var _owner = owner; using var _guest = guest;
        // The forward exists once the insert is done; failing the request now would make a client retry into a second one.
        using var created = await guest.PostJsonAsync(Forwards, new { vmPort = 5173, target = "client", purpose = "chart preview" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Null((await created.ReadAsync<ForwardResponse>()).Purpose);
        Assert.Single(await (await owner.GetAsync(Forwards)).ReadAsync<List<ForwardResponse>>());
    }

    [Fact]
    public async Task A_forward_keeps_its_purpose_and_source_and_lists_them()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app; using var _owner = owner; using var _guest = guest;

        using var created = await guest.PostJsonAsync(Forwards, new
        {
            vmPort = 5173, label = "vite", target = "client",
            purpose = "  preview of the\tchart page  ", source = "/root/repos/charts",
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var forward = await created.ReadAsync<ForwardResponse>();
        Assert.Equal("preview of the chart page", forward.Purpose);
        Assert.Equal("/root/repos/charts", forward.Source);

        var listed = Assert.Single(await (await owner.GetAsync(Forwards)).ReadAsync<List<ForwardResponse>>());
        Assert.Equal(("preview of the chart page", "/root/repos/charts"), (listed.Purpose, listed.Source));
    }

    [Fact]
    public async Task A_forward_without_a_note_works_as_before_and_its_json_has_no_note_fields()
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app; using var _owner = owner; using var _guest = guest;

        using var created = await guest.PostJsonAsync(Forwards, new { vmPort = 3000, label = "api", target = "client" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var json = JsonDocument.Parse(await (await owner.GetAsync(Forwards)).Content.ReadAsStringAsync()).RootElement[0];
        Assert.False(json.TryGetProperty("purpose", out _));
        Assert.False(json.TryGetProperty("source", out _));
        Assert.Equal(3000, json.GetProperty("vmPort").GetInt32());
    }

    [Theory]
    [InlineData("purpose", 201)]
    [InlineData("source", 401)]
    public async Task An_overlong_note_is_refused(string field, int length)
    {
        var (app, owner, guest) = await SetupAsync();
        using var _app = app; using var _owner = owner; using var _guest = guest;

        var body = new Dictionary<string, object> { ["vmPort"] = 3000, ["target"] = "client", [field] = new string('x', length) };
        using var refused = await guest.PostJsonAsync(Forwards, body);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains(field, await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(await (await owner.GetAsync(Forwards)).ReadAsync<List<ForwardResponse>>());
    }

    [Fact]
    public async Task The_refusal_at_the_cap_says_how_to_make_room()
    {
        // The test host caps a VM at 3 forwards.
        var (app, owner, guest) = await SetupAsync();
        using var _app = app; using var _owner = owner; using var _guest = guest;
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Created, (await guest.PostJsonAsync(Forwards, new { vmPort = 3000 + i, target = "client" })).StatusCode);

        using var refused = await guest.PostJsonAsync(Forwards, new { vmPort = 3100, target = "client", purpose = "one too many" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        var problem = JsonDocument.Parse(await refused.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("forward-limit", problem.GetProperty("code").GetString());
        var detail = problem.GetProperty("detail").GetString()!;
        Assert.Contains("maximum of 3 forwards", detail, StringComparison.Ordinal);
        Assert.Contains("construct expose --list", detail, StringComparison.Ordinal);
        Assert.Contains("construct expose --close", detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Notes_are_stored_in_the_database()
    {
        var root = Path.Combine(Path.GetTempPath(), "forward-notes-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(root);
        try
        {
            var database = Path.Combine(root, "constructd.db");
            string id;
            {
                var (app, owner, guest) = await SetupAsync(TestApp.WithSqlite(database));
                using var _app = app; using var _owner = owner; using var _guest = guest;
                id = (await (await guest.PostJsonAsync(Forwards, new { vmPort = 8080, target = "client", purpose = "docs preview", source = "/root/repos/docs" }))
                    .ReadAsync<ForwardResponse>()).Id;
                Assert.Equal(HttpStatusCode.Created, (await guest.PostJsonAsync(Forwards, new { vmPort = 8081, target = "client" })).StatusCode);
            }
            await using var reopened = TestApp.WithSqlite(database);
            var store = reopened.Service<Constructd.Core.Abstractions.IForwardStore>();
            var forwards = await store.ListAsync("work-vm", default);
            var noted = Assert.Single(forwards, f => f.Id == id);
            Assert.Equal(("docs preview", "/root/repos/docs"), (noted.Purpose, noted.Source));
            var plain = Assert.Single(forwards, f => f.Id != id);
            Assert.Null(plain.Purpose);
            Assert.Null(plain.Source);
        }
        finally { Directory.Delete(root, true); }
    }
}
