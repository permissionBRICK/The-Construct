using Constructd.Api.Hosting;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Windows.Media;
namespace Constructd.Api.Composition;
public static class MediaComposition
{
    public static IServiceCollection AddMediaPlatform(this IServiceCollection services,ConstructdOptions options)
    {
        if(options.EffectivePersistence == PersistenceMode.Memory)
        {
            services.AddSingleton<InMemoryMediaStore>();
            services.AddSingleton<IMediaStore>(sp=>sp.GetRequiredService<InMemoryMediaStore>());
        }
        else services.AddSingleton<IMediaStore,SqliteMediaStore>();
        services.AddSingleton<IUrlAdmissionPolicy,Constructd.Core.Logic.UrlAdmissionRules>();
        services.AddSingleton<IMediaDnsResolver,MediaDnsResolver>();
        services.AddSingleton<IMediaConnectionFactory,MediaConnectionFactory>();
        if(options.Fake)
        {
            services.AddSingleton<FakeMediaTransfer>();
            services.AddSingleton<IMediaTransfer>(sp=>sp.GetRequiredService<FakeMediaTransfer>());
            services.AddSingleton<IMediaFiles>(sp=>new MediaFileStore(sp.GetRequiredService<FakeMediaTransfer>().Root));
        }
        else
        {
            var root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.HostAdmin.Media.RootDir ?? @"C:\ProgramData\Construct\service\media"));
            var catalog=Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Iso.CacheDir));
            var comparison=OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if(root.Equals(catalog,comparison) || root.StartsWith(catalog+Path.DirectorySeparatorChar,comparison) || catalog.StartsWith(root+Path.DirectorySeparatorChar,comparison))
                throw new MediaException("media-root-overlap");
            services.AddSingleton<IMediaFiles>(new MediaFileStore(root,[options.Iso.SourcePath]));
            // Deliberately explicit: production never supplies the test-only handler factory.
            services.AddSingleton<IMediaTransfer>(sp=>new HttpMediaTransfer(sp.GetRequiredService<IMediaFiles>(),sp.GetRequiredService<IMediaDnsResolver>(),
                sp.GetRequiredService<IUrlAdmissionPolicy>(),sp.GetRequiredService<IMediaConnectionFactory>()));
        }
        services.AddSingleton<MediaJobs>();
        services.AddHostedService<MediaCleanupService>();
        return services;
    }
}
