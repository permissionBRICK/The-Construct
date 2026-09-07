using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
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
        else services.AddSingleton<IGuestAddressProvider>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        if (options.Fake)
        {
            services.AddSingleton<InMemoryAccessExposure>();
            services.AddSingleton<IAccessExposure>(sp => sp.GetRequiredService<InMemoryAccessExposure>());
        }
        else services.AddSingleton<IAccessExposure>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        services.AddSingleton<IHostNetworkPolicy, HostNetworkPolicy>();
        services.AddSingleton<INetworkPolicyReconciler, NoIsolationNetworkPolicy>();
        return services;
    }
}
