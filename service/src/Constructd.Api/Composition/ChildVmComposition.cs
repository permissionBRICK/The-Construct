using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
namespace Constructd.Api.Composition;

public static class ChildVmComposition
{
    public static IServiceCollection AddChildVmPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.Fake)
        {
            services.AddSingleton(sp => new FakeChildVmDriver(sp.GetRequiredService<FakeHypervisorDriver>())
            { Capabilities = UnsupportedCapabilities.Backend(sp.GetRequiredService<IHypervisorDriver>().Capabilities) with { Backend = "fake" } });
            services.AddSingleton<IChildVmDriver>(sp => sp.GetRequiredService<FakeChildVmDriver>());
        }
        else services.AddSingleton<IChildVmDriver, UnsupportedChildVmDriver>();
        if (options.Fake)
        {
            services.AddSingleton<InMemoryOperationKeyStore>();
            services.AddSingleton<IOperationKeyStore>(sp => sp.GetRequiredService<InMemoryOperationKeyStore>());
        }
        else services.AddSingleton<IOperationKeyStore>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        return services;
    }
}
