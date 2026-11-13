using Constructd.Api.Profiles;

namespace Constructd.Api.Composition;

/// <summary>Profile sync through the engine (docs/plans/construct-platform-design.md 9.3): the replicas and their loop.</summary>
public static class ProfileSyncComposition
{
    public static IServiceCollection AddProfileSync(this IServiceCollection services)
    {
        services.AddSingleton<ProfileSync>();
        services.AddHostedService<ProfileSyncService>();
        return services;
    }
}
