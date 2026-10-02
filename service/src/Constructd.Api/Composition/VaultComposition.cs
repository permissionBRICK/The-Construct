using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Windows.Media;

namespace Constructd.Api.Composition;

/// <summary>
/// The hosted key vault (docs/plans/key-vault-hosted.md): its store, the host master key that wraps
/// always-available vault keys, the service and its scheduler.
/// </summary>
public static class VaultComposition
{
    public static IServiceCollection AddVaultPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.EffectivePersistence == PersistenceMode.Sqlite)
        {
            services.AddSingleton<IVaultStore, SqliteVaultStore>();
            // The license store's protected key directory, with its own vault-master.key.
            var root = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath))!;
            var keys = options.Fake || OperatingSystem.IsWindows() ? Path.Combine(root, "keys") : "/etc/constructd/keys";
            services.AddSingleton<IVaultKeyProtector>(_ => new HostVaultKeyProtector(new WindowsKeyCipher(keys, HostVaultKeyProtector.FileName)));
        }
        else
        {
            services.AddSingleton<IVaultStore, InMemoryVaultStore>();
            // In-memory persistence forgets every wrapped key on restart anyway.
            services.AddSingleton<IVaultKeyProtector, EphemeralVaultKeyProtector>();
        }

        services.AddSingleton<VaultHostService>();
        services.AddSingleton<IVaultUnlocks>(sp => sp.GetRequiredService<VaultHostService>());
        services.AddHostedService<Hosting.VaultSchedulerService>();
        return services;
    }
}
