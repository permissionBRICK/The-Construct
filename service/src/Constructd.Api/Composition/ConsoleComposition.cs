using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
namespace Constructd.Api.Composition;

public static class ConsoleComposition
{
    // Owned by the Console implementation pair; registrations stay in this file.
    public static IServiceCollection AddConsolePlatform(this IServiceCollection services, ConstructdOptions options)
    {
        services.AddSingleton<IConsoleTransport, UnsupportedConsoleTransport>();
        return services;
    }
}
