using Constructd.Api.Https;
using Constructd.Core.Abstractions;
using Constructd.Windows.Forwards;

namespace Constructd.Api.Composition;

/// <summary>HTTPS forwards (docs/https-forwards.md): the certificate store and the TLS front the forward managers hand HTTPS forwards to.</summary>
public static class HttpsComposition
{
    public static IServiceCollection AddHttpsForwards(this IServiceCollection services)
    {
        services.AddSingleton<HttpsCertificateStore>();
        services.AddSingleton(sp => new HttpsGateway(
            sp.GetRequiredService<IForwardStore>(),
            sp.GetRequiredService<IVmRepository>(),
            sp.GetRequiredService<IHypervisorDriver>(),
            sp.GetRequiredService<HttpsCertificateStore>(),
            sp.GetRequiredService<IHostConfigStore>(),
            sp.GetRequiredService<Constructd.Core.Configuration.ConstructdOptions>(),
            sp.GetRequiredService<IClock>(),
            sp,
            sp.GetRequiredService<ILogger<HttpsGateway>>(),
            sp.GetService<IHostAddressResolver>()));
        services.AddSingleton<IHttpsForwardHost>(sp => sp.GetRequiredService<HttpsGateway>());
        services.AddHostedService(sp => sp.GetRequiredService<HttpsGateway>());
        services.AddSingleton<VaultRelayEndpoint>();
        return services;
    }
}
