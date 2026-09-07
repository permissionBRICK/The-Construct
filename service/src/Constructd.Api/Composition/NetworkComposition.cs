using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
namespace Constructd.Api.Composition;

public static class NetworkComposition
{
    // Owned by the Network implementation pair; registrations stay in this file.
    public static IServiceCollection AddNetworkPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        
        return services;
    }
}
