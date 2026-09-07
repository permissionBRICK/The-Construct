using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
namespace Constructd.Api.Composition;

public static class MediaComposition
{
    // Owned by the Media implementation pair; registrations stay in this file.
    public static IServiceCollection AddMediaPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        
        return services;
    }
}
