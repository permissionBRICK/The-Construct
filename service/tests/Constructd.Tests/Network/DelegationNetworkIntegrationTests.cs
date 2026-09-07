using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Delegation;
using Constructd.Tests.Support;
using Microsoft.Data.Sqlite;
namespace Constructd.Tests.Network;

public sealed class DelegationNetworkIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharingAndCascadeRoutesRevokeForwardsAndNetworkIntents(bool sqlite)
    {
        var directory = Path.Combine(Path.GetTempPath(), "delegation-network-" + Guid.NewGuid().ToString("n"));
        try
        {
            await using var app = sqlite ? TestApp.WithSqlite(Path.Combine(directory, "test.db")) : new TestApp();
            using var owner = await LifecycleTests.Setup(app);
            using var consumer = await app.CreateUserClientAsync("bob");
            await app.Vms.AddAsync(new("consumer", "bob", 1, 1, 1, app.Clock.UtcNow, VmState.Running,
                null, null, IdlePolicy.Disabled, []), 5, default);
            var child = (await app.Vms.GetAsync("child", default))!;
            var addresses = app.Service<FakeGuestAddressProvider>();
            addresses.Adapters["child"] = [new(child.Incarnation!, "nic", "001122334455", false, "switch")];
            addresses.Adapters["parent"] = [new("parent-id", "nic", "001122334456", false, "switch")];
            addresses.Adapters["consumer"] = [new("consumer-id", "nic", "001122334457", false, "switch")];
            addresses.Reported["child"] = [new("10.2.3.4", GuestAddressFamily.Ipv4, GuestAddressSource.Kvp, app.Clock.UtcNow, false)];
            addresses.HostAddresses = [IPAddress.Parse("10.2.3.1")];
            addresses.Subnets = [new("10.2.3.1/24", "switch", "vEthernet (switch)")];
            var network = app.Service<INetworkRuleStore>();
            Assert.Equal("parent-child", Assert.Single(await network.ListAsync("child", default)).Kind);

            (await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" })).EnsureSuccessStatusCode();
            Assert.Contains(await network.ListAsync("child", default), r => r.Kind == "shared-consumer" && r.Peer == "consumer");
            var own = await (await owner.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).ReadAsync<ForwardResponse>();
            var shared = await (await consumer.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).ReadAsync<ForwardResponse>();
            Assert.Equal("10.2.3.4", shared.Destination!.ConnectAddress);
            Assert.Equal("consumer", shared.Destination.Via);
            (await consumer.PostJsonAsync($"/api/v1/vms/child/forwards/{shared.Id}/ack", new { status = "open", localPort = 8080 })).EnsureSuccessStatusCode();

            // Drive the real sharing endpoint: no manual reconciler callback or timer tick.
            (await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "private" })).EnsureSuccessStatusCode();
            Assert.Equal(own.Id, Assert.Single(await app.Forwards.ListAsync("child", default)).Id);
            Assert.Equal("parent-child", Assert.Single(await network.ListAsync("child", default)).Kind);
            Assert.Empty(await (await consumer.GetAsync("/api/v1/vms/consumer/forwards?via=consumer")).ReadAsync<List<ForwardResponse>>());
            Assert.Equal(HttpStatusCode.Forbidden, (await consumer.GetAsync("/api/v1/vms/child/forwards")).StatusCode);

            (await owner.PutAsJsonAsync("/api/v1/vms/child/sharing", new { scope = "host" })).EnsureSuccessStatusCode();
            (await consumer.PostJsonAsync("/api/v1/vms/child/forwards", new { vmPort = 80 })).EnsureSuccessStatusCode();
            using var preview = await owner.DeleteAsync("/api/v1/vms/parent");
            Assert.Equal(HttpStatusCode.Conflict, preview.StatusCode);
            var token = (await preview.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("cascadeToken").GetString();
            using var confirm = new HttpRequestMessage(HttpMethod.Delete, "/api/v1/vms/parent")
                { Content = JsonContent.Create(new { cascade = new { token } }) };
            Assert.Equal(JobState.Succeeded, (await LifecycleTests.Finish(app, await owner.SendAsync(confirm))).State);
            Assert.Null(await app.Vms.GetAsync("child", default));
            Assert.Null(await app.Vms.GetAsync("parent", default));
            Assert.NotNull(await app.Vms.GetAsync("consumer", default));
            Assert.Empty(await app.Forwards.ListAsync("child", default));
            Assert.Empty(await network.ListAsync(null, default));
            Assert.Empty(await app.Service<IMediaStore>().ListReferencesForVmAsync("child", default));
            Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false, default)).Reservations);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
