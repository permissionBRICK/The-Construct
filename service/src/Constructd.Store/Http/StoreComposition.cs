using Constructd.Store.Records;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Store.Http;

public static class StoreComposition
{
    /// <summary>The record store over its own SQLite file (<c>:memory:</c> for a store that forgets).</summary>
    public static IServiceCollection AddRecordStore(this IServiceCollection services, string databasePath)
    {
        services.AddSingleton(sp => new RecordStore(databasePath, sp.GetService<TimeProvider>()));
        return services;
    }
}
