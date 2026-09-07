using Constructd.Api.Hosting;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Windows.HyperV;
namespace Constructd.Api.Composition;

public static class CapacityComposition
{
    public static IServiceCollection AddCapacityPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.Fake)
        {
            services.AddSingleton<FakeHypervisorInventory>();
            services.AddSingleton<IHypervisorInventory>(sp => sp.GetRequiredService<FakeHypervisorInventory>());
        }
        else services.AddSingleton<IHypervisorInventory, HyperVInventory>();
        if (options.EffectivePersistence == PersistenceMode.Sqlite)
        {
            services.AddSingleton<SqliteCapacityLedger>(sp => new(sp.GetRequiredService<SqliteDatabase>(), sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<IHypervisorInventory>(), options) { Operations = sp.GetRequiredService<IOperationRegistry>() });
            services.AddSingleton<ICapacityReconciliationStore>(sp => sp.GetRequiredService<SqliteCapacityLedger>());
            services.AddSingleton<CapacityReconciler>();
            services.AddSingleton<ICapacityLedger>(sp =>
            {
                var ledger = sp.GetRequiredService<SqliteCapacityLedger>();
                ledger.Reconcile = sp.GetRequiredService<CapacityReconciler>().ReconcileAsync;
                return ledger;
            });
            services.AddHostedService<CapacityReconciliationService>();
        }
        else
        {
            services.AddSingleton<InMemoryCapacityLedger>();
            services.AddSingleton<ICapacityLedger>(sp => sp.GetRequiredService<InMemoryCapacityLedger>());
        }
        services.AddSingleton<IDelegationPolicy>(sp => new CapacityDelegationPolicy(
            new DelegationPolicy(sp.GetRequiredService<IUserStore>(), sp.GetRequiredService<IVmRepository>(), sp.GetRequiredService<IVmDelegationRepository>(),
                sp.GetRequiredService<IHostConfigStore>(), sp.GetRequiredService<ICapabilityAggregator>()),
            sp.GetRequiredService<ICapacityLedger>(), sp.GetRequiredService<IVmRepository>()));
        return services;
    }
}
