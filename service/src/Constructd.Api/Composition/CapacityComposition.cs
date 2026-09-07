using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
namespace Constructd.Api.Composition;

public static class CapacityComposition
{
    public static IServiceCollection AddCapacityPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.Fake)
        {
            services.AddSingleton<InMemoryCapacityLedger>();
            services.AddSingleton<ICapacityLedger>(sp => sp.GetRequiredService<InMemoryCapacityLedger>());
        }
        else services.AddSingleton<ICapacityLedger, UnsupportedCapacityLedger>();
        if (options.Fake)
        {
            services.AddSingleton<FakeHypervisorInventory>();
            services.AddSingleton<IHypervisorInventory>(sp => sp.GetRequiredService<FakeHypervisorInventory>());
        }
        else services.AddSingleton<IHypervisorInventory>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        return services;
    }
}
