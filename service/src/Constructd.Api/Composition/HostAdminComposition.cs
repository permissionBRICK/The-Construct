using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
namespace Constructd.Api.Composition;

public static class HostAdminComposition
{
    public static IServiceCollection AddHostAdminCore(this IServiceCollection services, ConstructdOptions options)
    {
        services.AddSingleton<UnsupportedFeaturePlatform>();
        services.AddSingleton<IVmMetadataStore>(sp => (IVmMetadataStore)sp.GetRequiredService<IVmRepository>());
        services.AddSingleton<IVmDelegationRepository>(sp => sp.GetRequiredService<IVmRepository>() as IVmDelegationRepository ?? throw new InvalidOperationException("IVmRepository must also implement IVmDelegationRepository."));
        services.AddSingleton<IUserAllowanceStore>(sp => sp.GetRequiredService<IUserStore>() as IUserAllowanceStore ?? throw new InvalidOperationException("IUserStore must also implement IUserAllowanceStore."));
        services.AddSingleton<IVmTokenIssuer>(sp => options.EffectivePersistence == PersistenceMode.Sqlite
            ? new SqliteTokenService(sp.GetRequiredService<SqliteDatabase>(), sp.GetRequiredService<IClock>(), sp.GetRequiredService<IUserStore>(), sp.GetRequiredService<IVmRepository>())
            : sp.GetRequiredService<InMemoryTokenService>());
        if (options.EffectivePersistence == PersistenceMode.Sqlite)
            services.AddSingleton<IHostConfigStore, SqliteHostConfigStore>();
        else services.AddSingleton<IHostConfigStore, InMemoryHostConfigStore>();
        services.AddSingleton<IHostConfigMetadata>(sp => (IHostConfigMetadata)sp.GetRequiredService<IHostConfigStore>());
        services.AddSingleton<IJobQueryStore>(sp => sp.GetRequiredService<IJobStore>() as IJobQueryStore ?? throw new InvalidOperationException("Job store must implement IJobQueryStore."));
        services.AddSingleton<IUserTokenRevoker>(sp => sp.GetRequiredService<ITokenService>() as IUserTokenRevoker ?? throw new InvalidOperationException("Token store must implement IUserTokenRevoker."));
        services.AddSingleton<Constructd.Api.Endpoints.VmInventoryProjection>();
        services.AddSingleton<IVmOperationGate, InMemoryVmOperationGate>();
        services.AddSingleton<IMediaGate, InMemoryMediaGate>();
        services.AddSingleton<IOperationRegistry, InMemoryOperationRegistry>();
        services.AddSingleton<IDelegationPolicy, DelegationPolicy>();
        services.AddSingleton<ICapabilityAggregator, CapabilityAggregator>();
        if (options.Fake) services.AddSingleton<IReleaseInfo, FakeReleaseInfo>();
        else services.AddSingleton<IReleaseInfo, ReleaseInfo>();
        services.AddMediaPlatform(options);
        services.AddCapacityPlatform(options);
        services.AddChildVmPlatform(options);
        services.AddSingleton<Constructd.Api.Jobs.LifecycleStart>();
        services.AddSingleton<Constructd.Api.Jobs.ChildLifecycleJobs>();
        services.AddSingleton<Constructd.Api.Jobs.LifecycleJobAdmission>();
        services.AddSingleton<Constructd.Api.Jobs.CascadeJobs>();
        services.AddSingleton<IChildLeaseReconciler, Constructd.Api.Hosting.ChildLeaseReconciler>();
        if (options.EffectivePersistence == PersistenceMode.Memory) services.AddHostedService<Constructd.Api.Hosting.MemoryLeaseReconciliationService>();
        services.AddSingleton<Constructd.Api.Hosting.LeaseSchedulerService>();
        services.AddHostedService(sp => sp.GetRequiredService<Constructd.Api.Hosting.LeaseSchedulerService>());
        services.AddConsolePlatform(options);
        services.AddUpdatePlatform(options);
        services.AddNetworkPlatform(options);
        if (options.EffectivePersistence == PersistenceMode.Memory)
        {
            services.AddSingleton<IAdmissionStore, InMemoryAdmissionStore>();
        }
        else services.AddSingleton<IAdmissionStore, SqliteAdmissionStore>();
        services.AddSingleton<IPersistedJobRunner>(sp => (IPersistedJobRunner)sp.GetRequiredService<IJobEngine>());
        return services;
    }
}
