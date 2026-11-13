using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Windows.Process;

namespace Constructd.Api.Composition;

/// <summary>
/// Provisioning from the engine (design 7.3): the SSH runner, the desired and applied state per VM,
/// and the <c>provision</c> job. The SSH runner is the system <c>ssh</c> in every mode; tests
/// replace <see cref="ISshRunner"/>.
/// </summary>
public static class ProvisionComposition
{
    public static IServiceCollection AddProvisioning(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.EffectivePersistence == PersistenceMode.Sqlite)
        {
            services.AddSingleton<SqliteProvisionStateStore>();
            services.AddSingleton<IVmDesiredStateSource>(sp => sp.GetRequiredService<SqliteProvisionStateStore>());
            services.AddSingleton<IVmAppliedStateStore>(sp => sp.GetRequiredService<SqliteProvisionStateStore>());
        }
        else
        {
            services.AddSingleton<InMemoryProvisionStateStore>();
            services.AddSingleton<IVmDesiredStateSource>(sp => sp.GetRequiredService<InMemoryProvisionStateStore>());
            services.AddSingleton<IVmAppliedStateStore>(sp => sp.GetRequiredService<InMemoryProvisionStateStore>());
        }

        // Its own process runner: fake mode registers a recording one for the platform calls.
        services.AddSingleton<ISshRunner>(_ => new SystemSshRunner(new ProcessRunner()));
        services.AddSingleton<IProvisionTargets, ProvisionTargets>();
        services.AddSingleton<RecoveryMarks>();
        services.AddSingleton<ReinstallKeys>();
        services.AddSingleton<ProvisionJob>();
        services.AddSingleton<ReinstallJob>();
        services.AddSingleton<ProvisionJobs>();
        return services;
    }
}
