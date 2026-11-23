using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Constructd.Tests.Provisioning;

/// <summary>A fake-mode engine with a scripted guest, a cached source and one VM.</summary>
internal sealed class ProvisionHarness : IAsyncDisposable
{
    public const string Commit = "c0ffee0000000000000000000000000000000001";
    public const string Vm = "devbox";

    public TestApp App { get; }
    public FakeSshRunner Ssh { get; }
    public HttpClient Client { get; private set; } = null!;
    /// <summary>The VM token the create job handed out (null without a VM).</summary>
    public string? VmToken { get; private set; }

    private ProvisionHarness(TestApp app, FakeSshRunner ssh) { App = app; Ssh = ssh; }

    public static async Task<ProvisionHarness> StartAsync(FakeSshRunner? ssh = null, string? databasePath = null, bool createVm = true,
        IDictionary<string, string?>? settings = null, ISshRunner? runner = null, Action<IServiceCollection>? configure = null)
    {
        ssh ??= new FakeSshRunner();
        void Configure(IServiceCollection services)
        {
            services.AddSingleton(runner ?? ssh);
            configure?.Invoke(services);
        }
        var app = databasePath is null ? new TestApp(settings, Configure) : TestApp.WithSqlite(databasePath, settings, Configure);
        var harness = new ProvisionHarness(app, ssh);
        AddSource(app);
        if (createVm)
        {
            harness.Client = await app.CreateUserClientAsync("alice");
            harness.VmToken = (await harness.Client.CreateVmAsync(Vm)).VmTokenOrNull();
        }
        else
        {
            harness.Client = await app.CreateTokenClientAsync("alice");
        }

        return harness;
    }

    public static void AddSource(TestApp app)
    {
        var bytes = SourceZip;
        var uri = new Uri("https://github.com/test/repo/" + Commit);
        var releases = app.Service<FakeReleaseSource>();
        releases.SourceAssets[Commit] = new(Commit, "host-" + Commit, uri, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        releases.Assets[uri] = bytes;
    }

    private static readonly byte[] SourceZip = Zip();

    public static string PayloadHash => Convert.ToHexStringLower(SHA256.HashData(SourceZip));

    private static byte[] Zip()
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            Add(zip, "repo-main/bin/provision.sh", 0x81ed, "#!/bin/bash\n");
            Add(zip, "repo-main/README.md", 0, "readme\n");
            zip.CreateEntry("repo-main/docs/").ExternalAttributes = 0x41ed << 16;
            Add(zip, "repo-main/.construct-revision", 0x81a4, "$Format:%H$\n");
        }

        return bytes.ToArray();

        static void Add(ZipArchive zip, string name, int mode, string text)
        {
            var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
            entry.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = mode << 16;
            using var s = entry.Open();
            s.Write(Encoding.UTF8.GetBytes(text));
        }
    }

    public static object Desired(string projects = "default", string? t3 = "true") => new
    {
        projects = projects.Split(','),
        settings = new { aiTools = "claude-code", seedUser = "agent", t3Code = t3, serviceUrl = "https://host.test:8443", externalHost = "host.test", externalSshPort = 2201 },
    };

    public Task<HttpResponseMessage> PostAsync(string requestId, object? desired = null, object? secrets = null, bool? defer = null, string commit = Commit) =>
        Client.PostJsonAsync($"/api/v1/vms/{Vm}/jobs", new { kind = "provision", requestId, commit, desired, secrets, deferProjectCommands = defer });

    public async Task<string> StartAsync(string requestId, object? desired = null, object? secrets = null, bool? defer = null)
    {
        var response = await PostAsync(requestId, desired ?? Desired(), secrets, defer);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return (await response.ReadAsync<VmJobAcceptedResponse>()).JobId;
    }

    public async Task<JobResponse> RunAsync(string requestId, object? desired = null, object? secrets = null, bool? defer = null) =>
        await Client.WaitForJobAsync(await StartAsync(requestId, desired, secrets, defer));

    public static ProvisionJobResult Result(JobResponse job) =>
        JsonSerializer.Deserialize<ProvisionJobResult>(JsonSerializer.Serialize(job.Result), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public async ValueTask DisposeAsync()
    {
        Ssh.Release.TrySetResult();
        await App.DisposeAsync();
    }
}
