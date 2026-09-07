using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
namespace Constructd.Api.Composition;

public static class MediaComposition
{
    public static IServiceCollection AddMediaPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.Fake)
        {
            services.AddSingleton<InMemoryMediaStore>();
            services.AddSingleton<IMediaStore>(sp => sp.GetRequiredService<InMemoryMediaStore>());
        }
        else services.AddSingleton<IMediaStore>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        if (options.Fake)
        {
            services.AddSingleton<FakeMediaTransfer>();
            services.AddSingleton<IMediaTransfer>(sp => sp.GetRequiredService<FakeMediaTransfer>());
        }
        else services.AddSingleton<IMediaTransfer>(sp => sp.GetRequiredService<UnsupportedFeaturePlatform>());
        services.AddSingleton<IUrlAdmissionPolicy, Constructd.Core.Logic.UrlAdmissionRules>();
        return services;
    }
}
