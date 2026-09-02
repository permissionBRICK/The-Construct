using Constructd.Api.Composition;
using Constructd.Core.Configuration;

namespace Constructd.Api.Admin;

/// <summary>
/// Builds just enough of the service to run <c>constructd admin …</c>: the same configuration the
/// service itself reads, the same stores, and — on Windows — the platform implementations, which only
/// <c>forwards reconcile</c> needs.
///
/// No web host, no authentication, no job engine and no hosted services: the CLI must keep working on
/// a host where the API will not start, which is exactly when an operator reaches for it.
/// </summary>
public static class AdminCliHost
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);

        var environment = Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT")
                          ?? Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT")
                          ?? "Production";

        var configuration = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile($"appsettings.{environment}.json", optional: true)
            .AddEnvironmentVariables()
            .Build();

        var options = new ConstructdOptions();
        configuration.GetSection(ConstructdOptions.SectionName).Bind(options);

        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddSingleton<IConfiguration>(configuration);

        // Warnings and above only: the CLI's own output is the answer, and a chatty logger would
        // interleave with the JSON a script is parsing.
        services.AddLogging(logging => logging
            .AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace)
            .SetMinimumLevel(LogLevel.Warning));

        services.AddConstructdStores(options);

        if (!options.Fake && OperatingSystem.IsWindows())
        {
            try
            {
                services.AddPlatformImplementations(options);
            }
            catch (InvalidOperationException)
            {
                // A misconfigured ScriptsDir must not stop an admin from adding a user; the one verb
                // that needs the platform reports its absence itself.
            }
        }

        await using var provider = services.BuildServiceProvider();

        return await AdminCli.RunAsync(
            args,
            provider,
            Console.Out,
            Console.Error,
            cancellationToken).ConfigureAwait(false);
    }
}
