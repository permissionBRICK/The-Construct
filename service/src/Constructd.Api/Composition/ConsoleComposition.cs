using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Windows.Console;
using Constructd.Fakes;
namespace Constructd.Api.Composition;

public static class ConsoleComposition
{
    public static IServiceCollection AddConsolePlatform(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.Fake)
        {
            services.AddSingleton(sp => new FakeConsoleTransport { IsRunning = name => sp.GetRequiredService<FakeHypervisorDriver>().StateOf(name) == Constructd.Core.Domain.VmState.Running });
            services.AddSingleton<IConsoleTransport>(sp => sp.GetRequiredService<FakeConsoleTransport>());
        }
        else services.AddSingleton<IConsoleTransport, HyperVConsoleTransport>();
        services.AddSingleton<IConsoleSessionStore, InMemoryConsoleSessionStore>();
        if (options.Fake) services.AddSingleton<IInteractiveConsole, Constructd.Core.Services.UnsupportedInteractiveConsole>();
        else services.AddSingleton<IInteractiveConsole, HyperVInteractiveConsole>();
        services.AddHostedService<Constructd.Api.Hosting.ConsoleCredentialCleanup>();
        return services;
    }
}
