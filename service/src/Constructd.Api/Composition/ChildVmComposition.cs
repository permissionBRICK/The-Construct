using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
namespace Constructd.Api.Composition;

public static class ChildVmComposition
{
    // Owned by the ChildVm implementation pair; registrations stay in this file.
    public static IServiceCollection AddChildVmPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        services.AddSingleton<IChildVmDriver, UnsupportedChildVmDriver>();
        return services;
    }
}
