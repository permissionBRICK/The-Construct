using Constructd.Api.Source;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Windows.Source;
namespace Constructd.Api.Composition;

public static class SourceComposition
{
    public static IServiceCollection AddSourcePlatform(this IServiceCollection services, ConstructdOptions options)
    {
        var limits = options.HostAdmin.Source;
        if (limits.MaxItemBytes <= 0 || limits.MaxTotalBytes < limits.MaxItemBytes) throw new SourceException("source-size-invalid");
        var data = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
        if (options.Fake && limits.RootDir is null)
        { services.AddSingleton<FakeSourceFiles>(); services.AddSingleton<ISourceFiles>(sp => sp.GetRequiredService<FakeSourceFiles>()); }
        else
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(limits.RootDir ?? Path.Combine(data, "source")));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (var other in new[] { options.Iso.CacheDir, options.HostAdmin.Media.RootDir ?? @"C:\ProgramData\Construct\service\media", Path.Combine(data, "updates") })
            {
                var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other));
                if (root.Equals(path, comparison) || root.StartsWith(path + Path.DirectorySeparatorChar, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                    throw new SourceException("source-root-overlap");
            }
            var files = new SourceFileStore(root);
            _ = files.PathFor(new string('0', 40), SourceFileKind.Part); Directory.CreateDirectory(root);
            services.AddSingleton<ISourceFiles>(files);
        }
        if (options.EffectivePersistence == PersistenceMode.Memory) services.AddSingleton<ISourceStore, InMemorySourceStore>();
        else services.AddSingleton<ISourceStore, SqliteSourceStore>();
        services.AddSingleton<SourceGate>(); services.AddSingleton<SourceCatalog>();
        services.AddSingleton<ISourceCache, SourceCache>();
        return services;
    }
}
