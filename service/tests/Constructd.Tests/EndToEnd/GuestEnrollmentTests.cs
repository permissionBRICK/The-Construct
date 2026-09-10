using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Constructd.Api.Admin;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Constructd.Tests.EndToEnd;

public sealed class GuestEnrollmentTests
{
    [LinuxToolchainFact]
    public async Task Python_enrollment_authenticates_issued_and_rotated_adoption_tokens_over_https()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "bin", "adopt-host.py"))) root = root.Parent;
        Assert.NotNull(root);
        var temp = Directory.CreateTempSubdirectory("construct-enrollment-e2e-").FullName;
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(san.Build());
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            await using var app = TestApp.WithSqlite(Path.Combine(temp, "state.db"), new Dictionary<string, string?>
            {
                ["Constructd:ListenUrl"] = "https://127.0.0.1:0",
            });
            app.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(cert)));
            app.StartServer();
            await app.AddUserAsync("DOMAIN\\alice", Role.Admin);
            app.Driver.SetState("agent-vm", VmState.Running);
            var url = app.Service<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var incarnation = Guid.NewGuid().ToString("D");
            string? previousToken = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var output = new StringWriter(); var error = new StringWriter();
                var exit = await AdminCli.RunAsync(["vms", "adopt", "agent-vm", "--owner", "DOMAIN\\alice",
                    "--cpu", "4", "--ram-mb", "8192", "--disk-gb", "100", "--incarnation", incarnation, "--json"],
                    app.Services, output, error, CancellationToken.None);
                Assert.Equal(0, exit);
                using var json = JsonDocument.Parse(output.ToString());
                var token = json.RootElement.GetProperty("vmToken").GetString()!;
                var result = await Verify(token);
                Assert.True(result.Exit == 0, $"Enrollment attempt {attempt}: {result.Error}");
                Assert.Equal("Host identity verified", result.Output.Trim());
                // A retry rotates the credential; the old one must remain rejected.
                if (previousToken is not null)
                {
                    var stale = await Verify(previousToken);
                    Assert.Equal(1, stale.Exit);
                    Assert.Contains("HTTP 401", stale.Error);
                    Assert.DoesNotContain(previousToken, stale.Error);
                }
                previousToken = token;
            }
            Assert.Equal(VmState.Running, app.Driver.StateOf("agent-vm"));
            Assert.DoesNotContain(app.Driver.Calls, call => call.StartsWith("stop:") || call.StartsWith("create:") || call.StartsWith("remove:"));

            async Task<(int Exit, string Output, string Error)> Verify(string token)
            {
                using var process = new Process { StartInfo = new("python3")
                {
                    UseShellExecute = false, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                }};
                process.StartInfo.ArgumentList.Add(Path.Combine(root.FullName, "test", "helpers", "host-enrollment-verify.py"));
                process.StartInfo.Environment["NO_PROXY"] = "127.0.0.1";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                process.Start();
                var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
                try
                {
                    await process.StandardInput.WriteAsync(JsonSerializer.Serialize(new
                    {
                        name = "agent-vm", owner = "DOMAIN\\alice", serviceUrl = url,
                        certificate = cert.ExportCertificatePem(), vmToken = token,
                    }));
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(timeout.Token);
                    return (process.ExitCode, await stdout, await stderr);
                }
                finally { if (!process.HasExited) process.Kill(true); }
            }
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(temp, true);
        }
    }
}
