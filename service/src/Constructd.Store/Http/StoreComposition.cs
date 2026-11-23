using Constructd.Store.Identity;
using Constructd.Store.Records;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Constructd.Store.Http;

public static class StoreComposition
{
    /// <summary>
    /// The record store and its directory over one SQLite file (<c>:memory:</c> for a store that
    /// forgets), and who is calling (<see cref="DirectoryPrincipalResolver"/>) unless the host chose.
    /// </summary>
    public static IServiceCollection AddRecordStore(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton(sp => new RecordStore(databasePath, sp.GetService<TimeProvider>()));
        services.AddSingleton(sp => new StoreDirectory(databasePath, sp.GetService<TimeProvider>()));
        services.TryAddSingleton<IStorePrincipalResolver>(sp => new DirectoryPrincipalResolver(sp.GetRequiredService<StoreDirectory>(), sp.GetService<TimeProvider>()));
        return services;
    }
}
