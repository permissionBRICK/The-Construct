using System.Text.Json;
using Constructd.Api.Admin;
using Constructd.Api.Composition;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Constructd.Tests.Https;

/// <summary>
/// <c>constructd admin https …</c> with the same service set the CLI host builds (stores only, no
/// host-administration services), over a real SQLite file: import, status, a refusal and remove.
/// </summary>
public sealed class HttpsAdminCliTests : IDisposable
{
    private readonly string _directory = TestCertificates.TempDirectory();

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private async Task<(int Exit, string Output, string Error)> RunAsync(params string[] args)
    {
        Directory.CreateDirectory(_directory);
        var options = new ConstructdOptions
        {
            Persistence = PersistenceMode.Sqlite,
            DatabasePath = Path.Combine(_directory, "constructd.db"),
            PublicHost = "buildbox.vms.test",
        };
        var services = new ServiceCollection();
        services.AddSingleton(options);
        services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
        services.AddConstructdStores(options);
        await using var provider = services.BuildServiceProvider();
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await AdminCli.RunAsync(args, provider, output, error, CancellationToken.None);
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task Import_status_and_remove_work_with_the_cli_hosts_services()
    {
        var issued = TestCertificates.SelfSigned(["*.vms.test"]);
        Directory.CreateDirectory(_directory);
        var chain = Path.Combine(_directory, "fullchain.pem");
        var key = Path.Combine(_directory, "privkey.pem");
        await File.WriteAllTextAsync(chain, issued.ChainPem);
        await File.WriteAllTextAsync(key, issued.KeyPem);

        var empty = await RunAsync("https", "status");
        Assert.Equal(AdminExitCode.Ok, empty.Exit);
        Assert.Contains("No HTTPS certificate installed", empty.Output);

        var imported = await RunAsync("https", "import", "--cert", chain, "--key", key);
        Assert.Equal(AdminExitCode.Ok, imported.Exit);
        Assert.Contains(issued.Sha256, imported.Output);

        var status = await RunAsync("https", "status", "--json");
        Assert.Equal(AdminExitCode.Ok, status.Exit);
        var json = JsonDocument.Parse(status.Output).RootElement;
        Assert.Equal(issued.Sha256, json.GetProperty("certificate").GetProperty("sha256").GetString());
        Assert.True(json.GetProperty("names").GetProperty("publicHostCovered").GetBoolean());
        Assert.DoesNotContain("PRIVATE KEY", status.Output);

        await File.WriteAllTextAsync(key, TestCertificates.SelfSigned(["other.test"]).KeyPem);
        var refused = await RunAsync("https", "import", "--cert", chain, "--key", key);
        Assert.Equal(AdminExitCode.Failed, refused.Exit);
        Assert.Contains("does not belong", refused.Error);

        Assert.Equal(AdminExitCode.Usage, (await RunAsync("https", "import", "--cert", chain)).Exit);
        var removed = await RunAsync("https", "remove");
        Assert.Equal(AdminExitCode.Ok, removed.Exit);
        Assert.Contains("removed", removed.Output);
    }
}
