using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Windows.Network;
namespace Constructd.Api.Composition;

public static class NetworkComposition
{
    public static IServiceCollection AddNetworkPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.Fake)
        {
            services.AddSingleton<FakeGuestAddressProvider>();
            services.AddSingleton<IGuestAddressProvider>(sp => sp.GetRequiredService<FakeGuestAddressProvider>());
        }
        else services.AddSingleton<IGuestAddressProvider, HyperVGuestAddressProvider>();
        if (options.EffectivePersistence == PersistenceMode.Sqlite) services.AddSingleton<INetworkRuleStore, SqliteNetworkRuleStore>();
        else services.AddSingleton<INetworkRuleStore, InMemoryNetworkRuleStore>();
        services.AddSingleton<GuestAddressResolver>();
        if (options.Fake)
        {
            services.AddSingleton<InMemoryAccessExposure>();
            services.AddSingleton<AccessExposure>(sp => sp.GetRequiredService<InMemoryAccessExposure>());
        }
        else services.AddSingleton<AccessExposure>();
        services.AddSingleton<IAccessExposure>(sp => sp.GetRequiredService<AccessExposure>());
        services.AddSingleton<IHostNetworkPolicy, HostNetworkPolicy>();
        services.AddSingleton<INetworkPolicyReconciler, NoIsolationNetworkPolicy>();
        return services;
    }
}
