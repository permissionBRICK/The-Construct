using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
namespace Constructd.Api.Composition;

public static class UpdateComposition
{
    // Owned by the Update implementation pair; registrations stay in this file.
    public static IServiceCollection AddUpdatePlatform(this IServiceCollection services, ConstructdOptions options)
    {
        
        return services;
    }
}
