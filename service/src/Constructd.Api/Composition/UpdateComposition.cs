using Constructd.Api.Hosting;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Windows.Updates;
namespace Constructd.Api.Composition;

public static class UpdateComposition
{
    public static IServiceCollection AddUpdatePlatform(this IServiceCollection services,ConstructdOptions options)
    {
        var data=Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
        if(options.Fake)
        {
            services.AddSingleton<FakeReleaseSource>();services.AddSingleton<IReleaseSource>(sp=>sp.GetRequiredService<FakeReleaseSource>());
            services.AddSingleton<FakeUpdateStager>();services.AddSingleton<IUpdateStager>(sp=>sp.GetRequiredService<FakeUpdateStager>());
            services.AddSingleton<FakeUpdaterLauncher>();services.AddSingleton<IUpdaterLauncher>(sp=>sp.GetRequiredService<FakeUpdaterLauncher>());
            services.AddSingleton<FakeHostLock>();services.AddSingleton<IHostLock>(sp=>sp.GetRequiredService<FakeHostLock>());
        }
        else
        {
            services.AddSingleton<IHostLock>(new FileHostLock(data));
            services.AddSingleton<IReleaseSource>(_=>new GitHubReleaseSource(new HttpClient(new HttpClientHandler{AllowAutoRedirect=false}){Timeout=TimeSpan.FromMinutes(30)}));
            services.AddSingleton<IUpdateStager>(sp=>sp.GetRequiredService<PackageStager>());
            services.AddSingleton<IUpdaterLauncher>(sp=>new ScheduledTaskUpdaterLauncher(sp.GetRequiredService<IProcessRunner>(),sp.GetRequiredService<IHostLock>(),data));
        }
        if(options.EffectivePersistence==PersistenceMode.Memory)
        {services.AddSingleton<InMemoryHostUpdateStore>();services.AddSingleton<IHostUpdateStore>(sp=>sp.GetRequiredService<InMemoryHostUpdateStore>());}
        else services.AddSingleton<IHostUpdateStore,SqliteHostUpdateStore>();
        if(options.EffectivePersistence==PersistenceMode.Memory) services.AddSingleton<IHostUpdateAdmission,InMemoryUpdateAdmission>();
        else services.AddSingleton<IHostUpdateAdmission>(sp=>(IHostUpdateAdmission)sp.GetRequiredService<IHostUpdateStore>());
        services.AddSingleton<IMaintenanceGate,InMemoryMaintenanceGate>();
        services.AddSingleton<PackageStager>();services.AddSingleton<HostUpdateJob>();
        services.AddSingleton<UpdateRecoveryService>();services.AddHostedService(sp=>sp.GetRequiredService<UpdateRecoveryService>());
        return services;
    }
}
