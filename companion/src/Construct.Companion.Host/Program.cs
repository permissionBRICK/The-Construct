using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Construct.Companion.Core.Abstractions;
using System.Text;
namespace Construct.Companion.Host;
public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!args.Contains("--fake") || args.Any(a => a is not ("--fake" or "--remote-only"))) { Console.Error.WriteLine("Use --fake for the portable test host. The desktop app supplies Windows adapters."); return 2; }
        await using var app = IpcServer.Build(services => services.AddCompanionFakes(integrationRegistry: true, remoteOnly: args.Contains("--remote-only")).AddCompanionHost());
        await app.StartAsync();
        // Fake-mode discovery only: this token protects an in-memory test host.
        var path = Path.Combine(app.Services.GetRequiredService<IpcSettings>().Directory, "endpoint.json");
        Console.WriteLine(Encoding.UTF8.GetString(app.Services.GetRequiredService<IFileSystem>().ReadFile(path)!));
        await app.WaitForShutdownAsync(); return 0;
    }
}
