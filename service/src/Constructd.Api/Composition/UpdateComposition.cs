using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
namespace Constructd.Api.Composition;

public static class UpdateComposition
{
    public static IServiceCollection AddUpdatePlatform(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.Fake)
        {
            services.AddSingleton<FakeReleaseSource>();
            services.AddSingleton<IReleaseSource>(sp => sp.GetRequiredService<FakeReleaseSource>());
        }
        else services.AddSingleton<IReleaseSource>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        if (options.Fake)
        {
            services.AddSingleton<FakeUpdateStager>();
            services.AddSingleton<IUpdateStager>(sp => sp.GetRequiredService<FakeUpdateStager>());
        }
        else services.AddSingleton<IUpdateStager>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        if (options.Fake)
        {
            services.AddSingleton<FakeUpdaterLauncher>();
            services.AddSingleton<IUpdaterLauncher>(sp => sp.GetRequiredService<FakeUpdaterLauncher>());
        }
        else services.AddSingleton<IUpdaterLauncher>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        if (options.Fake)
        {
            services.AddSingleton<InMemoryHostUpdateStore>();
            services.AddSingleton<IHostUpdateStore>(sp => sp.GetRequiredService<InMemoryHostUpdateStore>());
        }
        else services.AddSingleton<IHostUpdateStore>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        if (options.Fake)
        {
            services.AddSingleton<FakeHostLock>();
            services.AddSingleton<IHostLock>(sp => sp.GetRequiredService<FakeHostLock>());
        }
        else services.AddSingleton<IHostLock>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        services.AddSingleton<IMaintenanceGate, InMemoryMaintenanceGate>();
        return services;
    }
}
