using Constructd.Api.Backups;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using Constructd.Sqlite;
namespace Constructd.Api.Composition;

/// <summary>Host-stored config backups (docs/plans/host-config-backups.md): store, payload directory, cleanup.</summary>
public static class BackupComposition
{
    public static IServiceCollection AddConfigBackups(this IServiceCollection services, ConstructdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.EffectivePersistence == PersistenceMode.Memory)
        {
            services.AddSingleton<InMemoryBackupStore>();
            services.AddSingleton<IBackupStore>(sp => sp.GetRequiredService<InMemoryBackupStore>());
        }
        else services.AddSingleton<IBackupStore, SqliteBackupStore>();

        // Fake mode without a configured directory writes nowhere near a real host's data.
        var root = options.Fake && string.IsNullOrWhiteSpace(options.Backups.RootDir)
            ? Path.Combine(Path.GetTempPath(), "constructd-fake-backups-" + Guid.NewGuid().ToString("n"))
            : options.Backups.ResolveRootDir(options.DatabasePath);
        if (!options.Fake) RefuseOverlap(root, options);
        services.AddSingleton<IFreeSpaceProbe, DriveFreeSpaceProbe>();
        services.AddSingleton<IBackupFiles>(sp => new BackupFileStore(root, sp.GetRequiredService<IFreeSpaceProbe>()));
        services.AddSingleton<BackupCoordinator>();
        services.AddSingleton<BackupCleanupService>();
        services.AddHostedService(sp => sp.GetRequiredService<BackupCleanupService>());
        return services;
    }

    /// <summary>The backup directory shares no tree with the other directories the service writes or deletes in.</summary>
    private static void RefuseOverlap(string root, ConstructdOptions options)
    {
        var data = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var others = new[]
        {
            options.Iso.CacheDir,
            options.HostAdmin.Media.RootDir ?? (options.IsProxmox ? "/var/lib/constructd/media/template/iso" : @"C:\ProgramData\Construct\service\media"),
            options.HostAdmin.Source.RootDir ?? Path.Combine(data, "source"),
            Path.Combine(data, "updates"),
        };
        foreach (var other in others.Where(o => !string.IsNullOrWhiteSpace(o)))
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(other));
            if (root.Equals(path, comparison) || root.StartsWith(path + Path.DirectorySeparatorChar, comparison) ||
                path.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                throw new InvalidOperationException($"Constructd:Backups:RootDir ({root}) must not overlap {path}.");
        }
    }
}
