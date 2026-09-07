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
        services.AddSingleton<IVmDelegationRepository>(sp => sp.GetRequiredService<IVmRepository>() as IVmDelegationRepository ?? throw new InvalidOperationException("IVmRepository must also implement IVmDelegationRepository."));
        services.AddSingleton<IUserAllowanceStore>(sp => sp.GetRequiredService<IUserStore>() as IUserAllowanceStore ?? throw new InvalidOperationException("IUserStore must also implement IUserAllowanceStore."));
        services.AddSingleton<IVmTokenIssuer>(sp => sp.GetRequiredService<ITokenService>() as IVmTokenIssuer ?? throw new InvalidOperationException("ITokenService must also implement IVmTokenIssuer."));
        if (options.EffectivePersistence == PersistenceMode.Sqlite)
            services.AddSingleton<IHostConfigStore, SqliteHostConfigStore>();
        else services.AddSingleton<IHostConfigStore, InMemoryHostConfigStore>();
        services.AddSingleton<IHostConfigMetadata>(sp => (IHostConfigMetadata)sp.GetRequiredService<IHostConfigStore>());
        services.AddSingleton<IVmOperationGate, InMemoryVmOperationGate>();
        services.AddSingleton<IMediaGate, InMemoryMediaGate>();
        services.AddSingleton<IOperationRegistry, InMemoryOperationRegistry>();
        services.AddSingleton<IDelegationPolicy, DelegationPolicy>();
        services.AddSingleton<ICapabilityAggregator, CapabilityAggregator>();
        services.AddSingleton<IReleaseInfo, ReleaseInfo>();
        services.AddMediaPlatform(options);
        services.AddCapacityPlatform(options);
        services.AddChildVmPlatform(options);
        services.AddConsolePlatform(options);
        services.AddUpdatePlatform(options);
        services.AddNetworkPlatform(options);
        return services;
    }
}
