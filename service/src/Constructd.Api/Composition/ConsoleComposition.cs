using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
namespace Constructd.Api.Composition;

public static class ConsoleComposition
{
    public static IServiceCollection AddConsolePlatform(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.Fake)
        {
            services.AddSingleton(_ => new FakeConsoleTransport { Capabilities = UnsupportedCapabilities.Console });
            services.AddSingleton<IConsoleTransport>(sp => sp.GetRequiredService<FakeConsoleTransport>());
        }
        else services.AddSingleton<IConsoleTransport, UnsupportedConsoleTransport>();
        services.AddSingleton<IConsoleSessionStore, InMemoryConsoleSessionStore>();
        return services;
    }
}
