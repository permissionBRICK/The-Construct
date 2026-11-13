using System.Net;
using System.Net.Http.Json;
using Constructd.Api.Composition;
using Constructd.Store.Client;
using Constructd.Store.Records;
using Constructd.Tests.Support;
using static Constructd.Tests.Store.StoreWrites;

namespace Constructd.Tests.Store;

/// <summary>The store's routes in constructd (<c>/api/v1/store</c>) and in the stand-alone host.</summary>
public sealed class StoreRoutesTests
{
    private static HttpRequestMessage Put(RecordWrite write, string? ifMatch)
    {
        var request = new HttpRequestMessage(write.Deleted ? HttpMethod.Delete : HttpMethod.Put, "/api/v1/store/records/" + Uri.EscapeDataString(write.Id))
        {
            Content = JsonContent.Create(write, options: StoreJson.Options),
        };
        if (ifMatch is not null) request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        return request;
    }

    private static readonly Uri ConstructdStore = new("http://localhost/api/v1/store/");

    private static async Task<TestDevice> DeviceOfAsync(TestApp app, string user)
    {
        using var signedIn = await app.CreateUserClientAsync(user);
        signedIn.BaseAddress = ConstructdStore;
        var device = new TestDevice(user + "-laptop", () => app.Server.CreateHandler(), ConstructdStore);
        Assert.Equal(HttpStatusCode.OK, (await device.EnrollAsync(signedIn)).StatusCode);
        return device;
    }

    [Fact]
    public async Task In_constructd_the_signed_in_user_registers_a_device_that_writes_as_itself()
    {
        using var app = new TestApp();
        using var laptop = await DeviceOfAsync(app, "alice");
        using var alice = laptop.Store();
        var device = StorePrincipal.Device("alice", laptop.Id);

        var created = await alice.SendAsync(Put(Write(device, "setting:theme", "setting", 0), "\"0\""));
        Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        var record = (await created.Content.ReadFromJsonAsync<StoredRecord>(StoreJson.Options))!;
        Assert.Equal("device:" + laptop.Id, record.Actor);
        Assert.Equal(Opaque, record.Ciphertext);

        // If-Match is required and must be the envelope's base.
        Assert.Equal((HttpStatusCode)428, (await alice.SendAsync(Put(Write(device, "setting:theme", "setting", record.Revision), null))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await alice.SendAsync(Put(Write(device, "setting:theme", "setting", record.Revision), "\"999\""))).StatusCode);

        // 412 carries the current record.
        var stale = await alice.SendAsync(Put(Write(device, "setting:theme", "setting", 0), "0"));
        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.Equal(record.Revision, (await stale.Content.ReadFromJsonAsync<WriteConflict>(StoreJson.Options))!.Current!.Revision);

        // A device does not write profiles (engines are the only mergers, S9) nor as somebody else.
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.SendAsync(Put(Write(device, "profile:a", "profile", 0), "0"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await alice.SendAsync(Put(Write(StorePrincipal.Device("alice", "bob"), "setting:x", "setting", 0), "0"))).StatusCode);

        // Accounts are separate: bob's device does not see alice's records.
        using var bobDevice = await DeviceOfAsync(app, "bob");
        using var bob = bobDevice.Store();
        Assert.Equal(HttpStatusCode.NotFound, (await bob.GetAsync("/api/v1/store/records/setting%3Atheme")).StatusCode);
        var feed = (await alice.GetFromJsonAsync<FeedPage>("/api/v1/store/records?since=0", StoreJson.Options))!;
        Assert.Single(feed.Records);

        // Not a participant: anonymous callers, and a user signed in without a device token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.CreateAnonymousClient().GetAsync("/api/v1/store/records")).StatusCode);
        using var signedIn = await app.CreateTokenClientAsync("alice");
        Assert.Equal(HttpStatusCode.Forbidden, (await signedIn.GetAsync("/api/v1/store/records")).StatusCode);

        // The engine writes in process as engine:<id>.
        var identity = app.Service<EngineStoreIdentity>();
        var engine = identity.Client(app.Service<RecordStore>(), "alice");
        var principal = StorePrincipal.Engine("alice", identity.Id);
        Assert.IsType<WriteOutcome.Accepted>(await engine.WriteAsync(Write(principal, "profile:a", "profile", 0), default));
        Assert.Equal("engine:" + identity.Id, (await alice.GetFromJsonAsync<StoredRecord>("/api/v1/store/records/profile%3Aa", StoreJson.Options))!.Actor);
    }

    [Fact]
    public async Task In_constructd_with_sqlite_the_store_has_its_own_file()
    {
        using var directory = new TempDirectory();
        using (var app = TestApp.WithSqlite(directory.File("constructd.db")))
        {
            using var laptop = await DeviceOfAsync(app, "alice");
            using var alice = laptop.Store();
            var device = StorePrincipal.Device("alice", laptop.Id);
            Assert.Equal(HttpStatusCode.OK, (await alice.SendAsync(Put(Write(device, "setting:a", "setting", 0), "0"))).StatusCode);
            var id = app.Service<EngineStoreIdentity>().Id;
            Assert.Equal(id, File.ReadAllText(directory.File(EngineStoreIdentity.FileName)));
        }

        Assert.True(File.Exists(directory.File(RecordStoreComposition.FileName)));
    }

    [Fact]
    public async Task A_vm_token_is_not_a_store_participant()
    {
        using var app = new TestApp();
        using var alice = await app.CreateUserClientAsync("alice");
        var job = await alice.CreateVmAsync("vm1");
        using var vm = app.CreateVmTokenClient(job.VmToken());
        Assert.Equal(HttpStatusCode.Forbidden, (await vm.GetAsync("/api/v1/store/records")).StatusCode);
    }

    [Fact]
    public async Task The_stand_alone_host_serves_the_same_routes()
    {
        using var directory = new TempDirectory();
        await using var store = new StandaloneStoreFixture(directory.Path,
            ("device-token", ParticipantKind.Device, "laptop"),
            ("engine-token", ParticipantKind.Engine, "e1"));

        Assert.Equal(HttpStatusCode.Unauthorized, (await store.Http(null).GetAsync("records")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await store.Http("wrong").GetAsync("records")).StatusCode);
        // A configured device is ignored: its token would not be bound to a key.
        Assert.Equal(HttpStatusCode.Unauthorized, (await store.Http("device-token").GetAsync("records")).StatusCode);

        var engine = store.Client("engine-token");
        var principal = StorePrincipal.Engine(StandaloneStoreFixture.Account, "e1");
        var created = Assert.IsType<WriteOutcome.Accepted>(await engine.WriteAsync(Write(principal, "profile:a", "profile", 0), default));
        Assert.IsType<WriteOutcome.Stale>(await engine.WriteAsync(Write(principal, "profile:a", "profile", 0), default));
        var deleted = Assert.IsType<WriteOutcome.Accepted>(await engine.WriteAsync(Write(principal, "profile:a", "profile", created.Record.Revision, deleted: true), default));
        Assert.True(deleted.Record.Deleted);
        var batch = Assert.IsType<BatchOutcome.Accepted>(await engine.BatchAsync(
            [Write(principal, "profile:a", "profile", deleted.Record.Revision), Write(principal, "profile:b", "profile", 0)], default));
        Assert.Equal(2, batch.Records.Count);
        Assert.Equal(1, await engine.ActiveEpochAsync(default));

        var refused = Assert.IsType<WriteOutcome.Refused>(await engine.WriteAsync(Write(principal, "setting:a", "setting", 0), default));
        Assert.Equal(403, refused.Status);

        // Export and import are for devices.
        using var laptop = new TestDevice("laptop", store.Handler, StandaloneStoreFixture.Base);
        using (var anonymous = laptop.Anonymous())
        {
            Assert.Equal(HttpStatusCode.OK, (await laptop.EnrollAsync(anonymous, StandaloneStoreFixture.Account, store.SetupCode)).StatusCode);
        }

        using var device = laptop.Store();
        Assert.Equal(HttpStatusCode.Forbidden, (await store.Http("engine-token").GetAsync("export")).StatusCode);
        var export = (await device.GetFromJsonAsync<StoreExport>("export", StoreJson.Options))!;
        Assert.Equal(2, export.Records.Count);
        Assert.Equal(HttpStatusCode.Conflict, (await device.PostAsJsonAsync("import", export, StoreJson.Options)).StatusCode);

        var feed = await engine.FeedAsync(0, TimeSpan.Zero, default);
        Assert.Equal(["profile:a", "profile:b"], feed.Records.Select(r => r.Id).Order());
        var history = (await device.GetFromJsonAsync<List<StoredRecord>>("records/profile%3Aa/history", StoreJson.Options))!;
        Assert.Equal(3, history.Count);
    }
}
