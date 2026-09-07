using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Constructd.Api.Hosting;
using Constructd.Api.Jobs;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit.Abstractions;

namespace Constructd.Tests.EndToEnd;

public sealed class HostAdminEndToEndTests(ITestOutputHelper output)
{
    [LinuxToolchainFact]
    public async Task FullStoryOverHttpsWithSqliteAndRealClients()
    {
        var root = FindRoot();
        var temp = Directory.CreateTempSubdirectory("construct-host-admin-e2e-").FullName;
        try
        {
            using var key = RSA.Create(2048);
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddIpAddress(IPAddress.Loopback); san.AddDnsName("localhost");
            request.CertificateExtensions.Add(san.Build());
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
            var ca = Path.Combine(temp, "ca.pem"); await File.WriteAllTextAsync(ca, cert.ExportCertificatePem());
            var adminToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var requests = new RequestAuditCapture();
            await using var app = TestApp.WithSqlite(Path.Combine(temp, "state.db"), new Dictionary<string, string?>
            {
                ["Constructd:BootstrapAdmin"] = "e2e-admin",
                ["Constructd:BootstrapAdminToken"] = adminToken,
                ["Constructd:PublicHost"] = "127.0.0.1",
                ["Constructd:ListenUrl"] = "https://127.0.0.1:0",
            }, services =>
            {
                services.AddSingleton<IStartupFilter>(requests);
                // Keep production transfer/hash/upload code. Only this test accepts the loopback
                // fixture origin; production URL admission and connection pinning stay unchanged.
                services.AddSingleton<IUrlAdmissionPolicy, LoopbackMediaPolicy>();
                services.AddSingleton<IMediaTransfer>(sp => new HttpMediaTransfer(
                    sp.GetRequiredService<IMediaFiles>(), sp.GetRequiredService<IMediaDnsResolver>(),
                    sp.GetRequiredService<IUrlAdmissionPolicy>(), sp.GetRequiredService<IMediaConnectionFactory>(),
                    (_, _) => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false }));
                // Reconcile explicitly against each coherent fixture snapshot. A timer reading
                // the static fake snapshot could overwrite states changed by live HTTP requests.
                foreach (var service in services.Where(s => s.ServiceType == typeof(IHostedService) &&
                    s.ImplementationType == typeof(CapacityReconciliationService)).ToArray()) services.Remove(service);
            });
            app.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(cert)));
            app.StartServer();
            var url = app.Service<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var process = new Process { StartInfo = new("node")
            {
                WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true,
                RedirectStandardError = true, UseShellExecute = false,
            }};
            process.StartInfo.ArgumentList.Add(Path.Combine(root, "test/helpers/host-admin-e2e.js"));
            process.StartInfo.Environment["E2E_BASE"] = url;
            process.StartInfo.Environment["E2E_TEMP"] = temp;
            process.StartInfo.Environment["E2E_ADMIN_TOKEN"] = adminToken;
            process.StartInfo.Environment["NODE_EXTRA_CA_CERTS"] = ca;
            process.StartInfo.Environment["E2E_CA"] = ca;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var updateLockHeld = false;
            process.Start();
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                while (await process.StandardOutput.ReadLineAsync(timeout.Token) is { } line)
                {
                    if (!line.StartsWith("CONTROL ", StringComparison.Ordinal)) { output.WriteLine(line); continue; }
                    using var command = JsonDocument.Parse(line[8..]);
                    var body = command.RootElement;
                    switch (body.GetProperty("action").GetString())
                    {
                        case "expire":
                            app.Clock.Advance(TimeSpan.FromSeconds(body.GetProperty("seconds").GetInt32()));
                            var jobs = await app.Service<LeaseSchedulerService>().TickAsync(timeout.Token);
                            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jobs }));
                            break;
                        case "capacity":
                            await RefreshInventory(app, body.GetProperty("totalGb").GetInt32(), timeout.Token);
                            await process.StandardInput.WriteLineAsync("{}");
                            break;
                        case "drain":
                            // The updater owns this semaphore during drain/handoff. Holding it
                            // also prevents recovery from clearing our intentionally synthetic gate.
                            await app.Service<HostUpdateJob>().Acceptance.WaitAsync(timeout.Token);
                            updateLockHeld = true;
                            var drained = await app.Service<IMaintenanceGate>().DrainAsync(TimeSpan.FromSeconds(5), timeout.Token);
                            Assert.True(drained.Drained);
                            await process.StandardInput.WriteLineAsync("{}");
                            break;
                        case "freeze":
                            app.Service<IMaintenanceGate>().Enter(MaintenanceState.Maintenance, "e2e-update");
                            await process.StandardInput.WriteLineAsync("{}");
                            break;
                        case "reopen":
                            app.Service<IMaintenanceGate>().Reopen();
                            if (updateLockHeld) { app.Service<HostUpdateJob>().Acceptance.Release(); updateLockHeld = false; }
                            await process.StandardInput.WriteLineAsync("{}");
                            break;
                        case "shutdownUnavailable":
                            app.Service<FakeChildVmDriver>().ShutdownOutcome = GracefulShutdownOutcome.Unavailable;
                            await process.StandardInput.WriteLineAsync("{}");
                            break;
                        case "shutdownAvailable":
                            app.Service<FakeChildVmDriver>().ShutdownOutcome = GracefulShutdownOutcome.Completed;
                            await process.StandardInput.WriteLineAsync("{}");
                            break;
                        default: throw new InvalidOperationException("Unknown e2e control.");
                    }
                }
                await process.WaitForExitAsync(timeout.Token);
                // The client prints only assertion labels, never HTTP bodies or credentials.
                Assert.True(process.ExitCode == 0, "Client story failed: " + await errors);
                var audit = await app.Service<IAuditLog>().QueryAsync(10000, timeout.Token);
                var actual = audit.Where(e => e.Detail?.StartsWith("status=", StringComparison.Ordinal) == true)
                    .GroupBy(e => (e.Action, Status: int.Parse(e.Detail!.Substring(7, 3))))
                    .ToDictionary(g => g.Key, g => g.Count());
                var auditable = requests.Mutations.Where(r => r.Action != "vm.activity" || r.Status >= 400).ToArray();
                foreach (var group in auditable.GroupBy(r => r))
                    Assert.True(actual.GetValueOrDefault(group.Key) == group.Count(),
                        $"Audit count differs for {group.Key.Action} HTTP {group.Key.Status}.");
                Assert.Equal(auditable.Length, actual.Values.Sum());
                output.WriteLine($"PASS exactly one HTTP audit record for each of {auditable.Length} auditable mutations, including refusals");
                var secrets = await File.ReadAllLinesAsync(Path.Combine(temp, "secrets"), timeout.Token);
                Assert.True(secrets.All(secret => !app.Logs.AllText().Contains(secret, StringComparison.Ordinal)), "Service log leaked a sentinel.");
                Assert.True(File.Exists(Path.Combine(temp, "state.db")), "SQLite persistence was not created.");
                output.WriteLine("PASS service log secret hygiene and SQLite persistence");
            }
            finally
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                app.Service<IMaintenanceGate>().Reopen();
                if (updateLockHeld) app.Service<HostUpdateJob>().Acceptance.Release();
            }
        }
        finally { Directory.Delete(temp, recursive: true); }
    }

    private static async Task RefreshInventory(TestApp app, int totalGb, CancellationToken ct)
    {
        var vms = new List<HypervisorVmInfo>();
        foreach (var vm in await app.Vms.ListAsync(null, ct))
        {
            var state = app.Driver.StateOf(vm.Name);
            var id = await app.Service<IChildVmDriver>().GetVmIdAsync(vm.Name, ct) ?? "primary-" + vm.Name;
            vms.Add(new(vm.Name, id, state, state.ToString(), 2, vm.Cpu, vm.RamBytes,
                state == VmState.Running ? vm.RamBytes : 0, false, null,
                [new(@"C:\VMs\" + vm.Name + ".vhdx", (long)vm.DiskGb << 30, 0, null, @"C:\", true)],
                0, @"C:\", true));
        }
        var artifacts = new List<CapacityArtifactInfo>();
        foreach (var media in await app.Service<IMediaStore>().ListAsync(null, ct))
            if (File.Exists(media.Path)) artifacts.Add(new(media.Path, media.Path, "/", new FileInfo(media.Path).Length, ArtifactPresence.Present));
        var now = app.Clock.UtcNow;
        var used = vms.Sum(v => v.MemoryAssignedBytes);
        app.Service<FakeHypervisorInventory>().Snapshot = new(1, now,
            new(8, (long)totalGb << 30, ((long)totalGb << 30) - used,
                [new(@"C:\", 1L << 40, 1L << 40), new("/", 1L << 40, 1L << 40)], now), vms, true, [], artifacts);
        await app.Service<ICapacityLedger>().ReconcileAsync(ct);
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "bin/construct"))) return dir.FullName;
        throw new InvalidOperationException("Run this test from a Construct checkout.");
    }

    private sealed class RequestAuditCapture : IStartupFilter
    {
        public ConcurrentQueue<(string Action, int Status)> Mutations { get; } = new();
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => builder =>
        {
            builder.Use(async (context, proceed) =>
            {
                await proceed();
                if (HttpMethods.IsPost(context.Request.Method) || HttpMethods.IsPut(context.Request.Method) || HttpMethods.IsDelete(context.Request.Method))
                {
                    var metadata = context.GetEndpoint()?.Metadata.GetMetadata<AuditActionMetadata>();
                    // Record missing metadata too: an assertion thrown after the response
                    // starts would live on the server task rather than fail this test.
                    Mutations.Enqueue((metadata?.Action ?? "$missing-audit-metadata", context.Response.StatusCode));
                }
            });
            next(builder);
        };
    }

    private sealed class LoopbackMediaPolicy : IUrlAdmissionPolicy
    {
        public UrlAdmission Check(Uri url, IReadOnlyList<IPAddress> resolved, bool allowHttp, bool hasChecksum) =>
            url.Scheme == "http" && url.Host == "127.0.0.1" && url.UserInfo.Length == 0 && allowHttp && hasChecksum &&
            resolved.All(IPAddress.IsLoopback)
                ? new(true, null, null, url, resolved.Select(a => a.ToString()).ToArray())
                : new UrlAdmissionRules().Check(url, resolved, allowHttp, hasChecksum);
    }
}
