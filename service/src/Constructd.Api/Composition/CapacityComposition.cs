using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
namespace Constructd.Api.Composition;

public static class CapacityComposition
{
    // Owned by the Capacity implementation pair; registrations stay in this file.
    public static IServiceCollection AddCapacityPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        
        return services;
    }
}
