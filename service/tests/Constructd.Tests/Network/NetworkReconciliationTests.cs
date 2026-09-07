using System.Text.Json;
using Constructd.Api.Hosting;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Network;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
namespace Constructd.Tests.Network;

public sealed class NetworkReconciliationTests
{
    [Fact]
    public async Task Whole_tick_uses_one_process_snapshot_for_multiple_forwards_and_network_policy()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice");
        await owner.CreateVmAsync("parent"); var parent = (await app.Vms.GetAsync("parent", default))!;
        var id = Guid.NewGuid().ToString();
        foreach (var name in new[] { "child", "other-child", "primary-two", "primary-three" })
            await app.Vms.AddAsync(parent with { Name = name, Kind = name.Contains("child") ? VmKind.Child : VmKind.Primary,
                Parent = "parent", SshForwardPort = null, VmTokenHash = null, Incarnation = id }, 10, default);
        var facts = new HyperVGuestAddressProvider.Snapshot(
            [new("child", [new("10.2.3.4", GuestAddressFamily.Ipv4, GuestAddressSource.Kvp, app.Clock.UtcNow, false, "nic")], [new(id, "nic", "aa", false, "switch")]),
             new("parent", [], [new(parent.Incarnation ?? "parent-id", "nic", "bb", false, "switch")])],
            [], [new("10.2.3.1/24", "switch", "eth")], ["10.2.3.1"]);
        var runner = new RecordingProcessRunner { Default = new(0, JsonSerializer.Serialize(new { ok = true, value = facts }, ApiJson.Options), "", false) };
        var provider = new HyperVGuestAddressProvider(runner, new ConstructdOptions { ScriptsDir = @"C:\Construct" }, app.Vms, app.Clock);
        var exposure = new AccessExposure(app.Vms, app.Users, app.Forwards, app.Service<IForwardStore>(),
            new GuestAddressResolver(provider, app.Vms, app.Service<INetworkRuleStore>()), app.Service<IHostNetworkPolicy>(),
            app.Service<INetworkRuleStore>(), app.Clock, app.Service<IAuditLog>());
        for (var n = 0; n < 3; n++) await app.Service<IForwardStore>().AddAsync(new("forward" + n, "child", 80 + n, null, ForwardTarget.Client, "", app.Clock.UtcNow,
            Destination: new("child", "parent", "10.2.3.4", 80 + n, "alice", ForwardRelationship.Owner, false)), default);
        var service = new ForwardReconciliationService(app.Forwards, new(), NullLogger<ForwardReconciliationService>.Instance,
            exposure: exposure, network: app.Service<INetworkPolicyReconciler>(), addresses: provider, vms: app.Vms);
        await service.TickAsync(default);
        Assert.Single(runner.Calls);
        using var request = JsonDocument.Parse(runner.Calls[0].StandardInput!);
        Assert.Equal(5, request.RootElement.GetProperty("vms").GetArrayLength());
        Assert.Equal(3, (await app.Forwards.ListAsync("child", default)).Count);
        await service.TickAsync(default);
        Assert.Equal(2, runner.Calls.Count); // Fresh next pass, independent of number of destinations or peers.
    }
    [Fact]
    public async Task Child_failure_cannot_skip_primary_repair_or_other_bookkeeping()
    {
        using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); await owner.CreateVmAsync("parent");
        // Clearing only fake materialized state makes primary reconciliation observable.
        app.Forwards.Materialized.Clear();
        var store = new ThrowingForwardStore();
        var exposure = new AccessExposure(app.Vms, app.Users, app.Forwards, store, app.Service<GuestAddressResolver>(),
            app.Service<IHostNetworkPolicy>(), app.Service<INetworkRuleStore>(), app.Clock, app.Service<IAuditLog>());
        var policy = new RecordingPolicy();
        using var logs = new CapturedLogs(); using var factory = LoggerFactory.Create(b => b.AddProvider(logs));
        var service = new ForwardReconciliationService(app.Forwards, new(), factory.CreateLogger<ForwardReconciliationService>(), exposure: exposure, network: policy);
        await service.TickAsync(default);
        Assert.NotEmpty(app.Forwards.Materialized); Assert.Equal(1, policy.Passes);
        Assert.Contains("Child forward", logs.AllText());
        policy.Fail = true; app.Forwards.Materialized.Clear();
        await service.TickAsync(default);
        Assert.NotEmpty(app.Forwards.Materialized); Assert.Contains("Network policy", logs.AllText());
    }
    private sealed class ThrowingForwardStore : IForwardStore
    {
        public Task<IReadOnlyList<PortForward>> ListAsync(string? name, CancellationToken ct) => throw new NotSupportedException();
        public Task<PortForward?> GetAsync(string id, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> CountByVmAsync(string name, CancellationToken ct) => throw new NotSupportedException();
        public Task AddAsync(PortForward f, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SetAckAsync(string id, ForwardAck ack, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> RemoveAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class RecordingPolicy : INetworkPolicyReconciler
    {
        public int Passes { get; private set; }
        public bool Fail { get; set; }
        public string IsolationLevel => "none";
        public Task<int> ReconcileAsync(CancellationToken ct) { Passes++; return Fail ? throw new IOException() : Task.FromResult(0); }
        public Task OnVmCreatedAsync(Vm vm, CancellationToken ct) => Task.CompletedTask;
        public Task OnVmDeletedAsync(string vm, CancellationToken ct) => Task.CompletedTask;
        public Task OnSharingChangedAsync(Vm vm, SharingScope previous, CancellationToken ct) => Task.CompletedTask;
        public Task OnAddressChangedAsync(string vm, IReadOnlyList<GuestAddress> a, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<NetworkRule>> ListRulesAsync(string vm, CancellationToken ct) => Task.FromResult<IReadOnlyList<NetworkRule>>([]);
    }
}
