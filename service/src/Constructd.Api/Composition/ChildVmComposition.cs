using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Windows.HyperV;
using Constructd.Sqlite;
namespace Constructd.Api.Composition;

public static class ChildVmComposition
{
    public static IServiceCollection AddChildVmPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.Fake)
        {
            services.AddSingleton(sp => new FakeChildVmDriver(sp.GetRequiredService<FakeHypervisorDriver>()));
            services.AddSingleton<IChildVmDriver>(sp => sp.GetRequiredService<FakeChildVmDriver>());
        }
        else services.AddSingleton<IChildVmDriver, HyperVChildDriver>();
        if (options.EffectivePersistence == PersistenceMode.Memory)
        {
            services.AddSingleton<InMemoryOperationKeyStore>();
            services.AddSingleton<IOperationKeyStore>(sp => sp.GetRequiredService<InMemoryOperationKeyStore>());
        }
        else services.AddSingleton<IOperationKeyStore, SqliteOperationKeyStore>();
        services.AddSingleton<IChildVmCreationOwnership>(sp => (IChildVmCreationOwnership)sp.GetRequiredService<IChildVmDriver>());
        services.AddSingleton<IChildVmStorage>(sp => (IChildVmStorage)sp.GetRequiredService<IChildVmDriver>());
        services.AddSingleton<Constructd.Api.Jobs.ChildStartIntent>();
        services.AddSingleton<Constructd.Api.Jobs.ChildCreateJob>();
        services.AddSingleton<Constructd.Api.Jobs.ChildDeleteJob>();
        return services;
    }
}
