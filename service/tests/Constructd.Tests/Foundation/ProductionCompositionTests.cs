using Constructd.Api.Composition;
using Constructd.Api.Endpoints;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
namespace Constructd.Tests.Foundation;

public sealed class ProductionCompositionTests
{
    [Fact]
    public void EveryFoundationEndpointDependencyResolvesWithProductionPersistenceAndFeatureRegistrations()
    {
        var directory = Path.Combine(Path.GetTempPath(), "foundation-composition-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            var options = new ConstructdOptions { Fake = false, Persistence = PersistenceMode.Sqlite, DatabasePath = Path.Combine(directory, "test.db"), ScriptsDir = directory };
            var services = new ServiceCollection(); services.AddLogging(); services.AddSingleton(options);
            if (OperatingSystem.IsWindows()) services.AddConstructdServices(options);
            else
            {
                // The existing platform guard runs AFTER AddHostAdminCore. Exercise that exact
                // production registration path, then replace ONLY the old Windows platform leaves.
                var guard = Assert.Throws<InvalidOperationException>(() => services.AddConstructdServices(options));
                Assert.True(guard.Message.Contains("need Windows", StringComparison.Ordinal),
                    "Only the pre-existing Windows platform adapters are skipped on Linux; the production host-admin registrations must execute.");
                services.AddSingleton<IHypervisorDriver, FakeHypervisorDriver>();
                services.AddSingleton<IProcessRunner, RecordingProcessRunner>();
                services.AddSingleton<IPortForwardManager>(sp => new InMemoryPortForwardManager(sp.GetRequiredService<IClock>(),
                    sp.GetRequiredService<IVmRepository>(), sp.GetRequiredService<IForwardStore>(), options.SshForwardPorts, options.AppForwardPorts));
            }
            using var provider = services.BuildServiceProvider();
            Type[] required = [typeof(VmInventoryProjection), typeof(IVmDelegationRepository), typeof(IVmMetadataStore), typeof(IUserAllowanceStore), typeof(IVmTokenIssuer),
                typeof(IUserTokenRevoker), typeof(IJobQueryStore), typeof(IHostConfigStore), typeof(IHostConfigMetadata), typeof(IDelegationPolicy),
                typeof(ICapabilityAggregator), typeof(IChildVmDriver), typeof(IConsoleTransport), typeof(IConsoleSessionStore), typeof(IVmOperationGate),
                typeof(IMaintenanceGate), typeof(ICapacityLedger), typeof(IMediaStore), typeof(IReleaseInfo), typeof(IAdmissionStore),
                typeof(IPersistedJobRunner), typeof(IHypervisorInventory), typeof(IOperationKeyStore), typeof(IAccessExposure), typeof(IGuestAddressProvider),
                typeof(INetworkPolicyReconciler), typeof(IHostNetworkPolicy), typeof(IUrlAdmissionPolicy), typeof(IHostLock), typeof(IReleaseSource),
                typeof(IUpdateStager), typeof(IUpdaterLauncher), typeof(IHostUpdateStore)];
            foreach (var type in required) Assert.NotNull(provider.GetRequiredService(type));
            Assert.IsType<SqliteVmRepository>(provider.GetRequiredService<IVmRepository>());
            Assert.IsType<SqliteHostConfigStore>(provider.GetRequiredService<IHostConfigStore>());
            Assert.IsType<Constructd.Sqlite.SqliteCapacityLedger>(provider.GetRequiredService<ICapacityLedger>());
            Assert.IsType<UnsupportedChildVmDriver>(provider.GetRequiredService<IChildVmDriver>());
            Assert.IsType<UnsupportedConsoleTransport>(provider.GetRequiredService<IConsoleTransport>());
            Assert.IsType<ReleaseInfo>(provider.GetRequiredService<IReleaseInfo>());
        }
        finally { SqliteConnection.ClearAllPools(); Directory.Delete(directory, true); }
    }
}
