using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Sqlite;
using Constructd.Tests.Support;
namespace Constructd.Tests.Network;

public sealed class NetworkPersistenceTests
{
    [Fact]
    public async Task Destination_ack_intended_rules_and_history_survive_a_restart_without_changing_primary_rows()
    {
        var directory = Path.Combine(Path.GetTempPath(), "construct-network-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var db = Path.Combine(directory, "state.db");
        var destination = new ForwardDestination("child", "parent", "10.2.3.4", 80, "vm:parent", ForwardRelationship.Parent, false);
        try
        {
            using (var app = TestApp.WithSqlite(db))
            {
                using var owner = await app.CreateUserClientAsync("alice"); await owner.CreateVmAsync("parent");
                var parent = (await app.Vms.GetAsync("parent", default))!;
                await app.Vms.AddAsync(parent with { Name = "child", Kind = VmKind.Child, Parent = "parent", SshForwardPort = null, VmTokenHash = null }, 10, default);
                var store = app.Service<IForwardStore>();
                await store.AddAsync(new("child-forward", "child", 3000, null, ForwardTarget.Client, "", app.Clock.UtcNow, Destination: destination), default);
                await store.AddAsync(new("primary-forward", "parent", 3000, null, ForwardTarget.Client, "", app.Clock.UtcNow), default);
                await store.SetAckAsync("child-forward", new(AckStatus.Open, 3000, null, "", app.Clock.UtcNow), default);
                var network = app.Service<INetworkRuleStore>();
                await network.ReplaceAsync("child", [new("rule", "child", "parent", "parent-child", "intended", app.Clock.UtcNow, app.Clock.UtcNow)], default);
                await network.RememberAddressAsync("child", "10.2.3.4", default);
            }
            using (var app = TestApp.WithSqlite(db))
            {
                var store = app.Service<IForwardStore>();
                var row = (await store.GetAsync("child-forward", default))!;
                Assert.Equal(destination, row.Destination); Assert.Equal(AckStatus.Open, row.Ack!.Status);
                Assert.Null((await store.GetAsync("primary-forward", default))!.Destination);
                Assert.True(await store.SetDestinationAsync(row.Id, destination with { ConnectAddress = null }, null, default));
                Assert.Null((await store.GetAsync(row.Id, default))!.Ack);
                await store.RemoveAsync(row.Id, default);
                Assert.False(await store.SetDestinationAsync(row.Id, destination, null, default));
                Assert.Single(await app.Service<INetworkRuleStore>().ListAsync("child", default));
                Assert.Equal(["10.2.3.4"], await app.Service<INetworkRuleStore>().PreviousAddressesAsync("child", default));
            }
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
