using System.Net.Http.Headers;
using Constructd.Api.Auth;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Constructd.Tests.Support;

/// <summary>
/// The API host under test: the real <c>Program</c>, running in fake mode, with in-memory stores.
/// Every test gets its own instance, so no state leaks between them.
/// </summary>
public sealed class TestApp(
    IDictionary<string, string?>? overrides = null,
    Action<IServiceCollection>? configureServices = null) : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings = Merge(overrides);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production, so appsettings.Development.json cannot influence the tests.
        builder.UseEnvironment("Production");

        // UseSetting (host configuration) rather than ConfigureAppConfiguration: the latter is applied
        // only when the host is built, which is after Program has bound its options.
        foreach (var (key, value) in _settings.Where(setting => setting.Value is not null))
        {
            builder.UseSetting(key, value);
        }

        // Everything the host logs is captured, so a test can assert that a secret never reaches a sink.
        builder.ConfigureLogging(logging => logging.AddProvider(Logs));

        // A clock the test controls, so timestamps (heartbeats, audit, jobs) are deterministic, plus
        // whatever the test wants to replace (a failing store, for instance).
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IClock>(Clock);
            configureServices?.Invoke(services);
        });
    }

    /// <summary>The clock the host runs on.</summary>
    public MutableClock Clock { get; } = new();

    /// <summary>Everything the host logged.</summary>
    public CapturedLogs Logs { get; } = new();

    public T Service<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public FakeHypervisorDriver Driver => Service<FakeHypervisorDriver>();

    public FakeIsoBuilder IsoBuilder => Service<FakeIsoBuilder>();

    public InMemoryPortForwardManager Forwards => Service<InMemoryPortForwardManager>();

    public IVmRepository Vms => Service<IVmRepository>();

    public IUserStore Users => Service<IUserStore>();

    /// <summary>Adds a user and returns a client authenticated with a freshly issued API token.</summary>
    public async Task<HttpClient> CreateUserClientAsync(
        string name,
        Role role = Role.User,
        int maxVms = 5,
        bool allowHostForwards = true)
    {
        await AddUserAsync(name, role, maxVms, allowHostForwards);
        return await CreateTokenClientAsync(name);
    }

    public async Task<User> AddUserAsync(
        string name,
        Role role = Role.User,
        int maxVms = 5,
        bool allowHostForwards = true)
    {
        var user = new User(name, role, maxVms, Service<IClock>().UtcNow, allowHostForwards);
        Assert.True(await Users.CreateAsync(user, CancellationToken.None));
        return user;
    }

    public async Task<HttpClient> CreateTokenClientAsync(string userName)
    {
        var issued = await Service<ITokenService>()
            .IssueAsync(userName, "test", CancellationToken.None);

        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            ConstructdSchemes.Bearer, issued.Plaintext);
        return client;
    }

    /// <summary>A client authenticated with a VM-scoped token.</summary>
    public HttpClient CreateVmTokenClient(string vmToken)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            ConstructdSchemes.VmToken, vmToken);
        return client;
    }

    /// <summary>A client using the fake Negotiate stand-in scheme (fake mode only).</summary>
    public HttpClient CreateTestIdentityClient(string name)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(ConstructdHeaders.TestIdentity, name);
        return client;
    }

    public HttpClient CreateAnonymousClient() => CreateClient();

    /// <summary>A host that keeps its state in a SQLite file (fake hypervisor, real persistence).</summary>
    public static TestApp WithSqlite(
        string databasePath,
        IDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Constructd:Persistence"] = "Sqlite",
            ["Constructd:DatabasePath"] = databasePath,
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }
        }

        return new TestApp(settings, configureServices);
    }

    private static Dictionary<string, string?> Merge(IDictionary<string, string?>? overrides)
    {
        var settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Constructd:Fake"] = "true",
            // Explicit, because appsettings.json names Sqlite as the production default.
            ["Constructd:Persistence"] = "Memory",
            ["Constructd:PublicHost"] = "buildbox.test",
            ["Constructd:MaxForwardsPerVm"] = "3",
            ["Constructd:VmReachableTimeoutMinutes"] = "1",
            ["Constructd:SshForwardPorts:Start"] = "2201",
            ["Constructd:SshForwardPorts:End"] = "2299",
            ["Constructd:AppForwardPorts:Start"] = "2300",
            ["Constructd:AppForwardPorts:End"] = "2999",
            // The scheduler is off in tests; the engine is invoked directly where it matters. Both
            // halves of it: the power reconcile rides on the same loop and would otherwise keep it
            // running (IdleSchedulerServiceTests covers that combination on its own).
            ["Constructd:Idle:SchedulerEnabled"] = "false",
            ["Constructd:Power:KeepHostAwake"] = "false",
            ["Constructd:Idle:DefaultTimeoutMinutes"] = "120",
            ["Constructd:Idle:DefaultAction"] = "Save",
            ["Constructd:Idle:MaxTimeoutMinutes"] = "0",
            ["Constructd:Idle:ForceEnabled"] = "false",
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }
        }

        return settings;
    }
}
