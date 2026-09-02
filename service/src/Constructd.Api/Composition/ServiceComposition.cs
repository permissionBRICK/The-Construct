using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Windows.Forwards;
using Constructd.Windows.HyperV;
using Constructd.Windows.Iso;
using Constructd.Windows.Process;

namespace Constructd.Api.Composition;

/// <summary>
/// The one place that decides which implementation of each Core interface is used. Two independent
/// axes:
///
/// <list type="bullet">
/// <item><b>Persistence</b> — SQLite (default) or in-memory. Cross-platform, so it is real here.</item>
/// <item><b>Platform</b> — the Hyper-V driver, the ISO builder and the port-forward manager. Those
/// are Windows-only and land in the follow-up batches; fake mode substitutes them.</item>
/// </list>
///
/// Adding the Windows implementations means writing them and extending
/// <see cref="AddPlatformImplementations"/> — no endpoint, policy or job code changes.
/// </summary>
public static class ServiceComposition
{
    public static IServiceCollection AddConstructdServices(
        this IServiceCollection services,
        ConstructdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddConstructdStores(options);

        if (options.Fake)
        {
            services.AddFakePlatform();
        }
        else
        {
            services.AddPlatformImplementations(options);
        }

        // Platform-agnostic, so these are the real implementations in every mode: the job engine runs
        // jobs in this process and persists them through whichever IJobStore was registered, and the
        // idle engine only talks to the driver, the forward manager and the repository.
        services.AddSingleton<InProcessJobEngine>(sp =>
        {
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<InProcessJobEngine>();

            return new InProcessJobEngine(
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<IJobStore>(),
                // A safe description and the job's own identifiers — never the exception object, whose
                // rendered form would carry the dependency's message, stack trace and Data with it.
                (job, error) => logger.LogError(
                    "Job {JobId} ({Kind}) for {Vm} failed: {Error}.",
                    job.Id, job.Kind, job.VmName ?? "-", error));
        });
        services.AddSingleton<IJobEngine>(sp => sp.GetRequiredService<InProcessJobEngine>());

        services.AddSingleton<IIdlePolicyEngine>(sp => new IdlePolicyEngine(
            sp.GetRequiredService<IVmRepository>(),
            sp.GetRequiredService<IPortForwardManager>(),
            sp.GetRequiredService<IHypervisorDriver>(),
            sp.GetRequiredService<IAuditLog>(),
            options.Idle));

        return services;
    }

    /// <summary>
    /// The persistence axis on its own: the clock and every store, with no platform implementation and
    /// no HTTP host. That is all the admin CLI (<c>constructd admin …</c>) needs, and it must work on a
    /// host where the hypervisor is unreachable — adding a user should not depend on Hyper-V.
    /// </summary>
    public static IServiceCollection AddConstructdStores(
        this IServiceCollection services,
        ConstructdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<IClock, SystemClock>();

        if (options.EffectivePersistence == PersistenceMode.Sqlite)
        {
            services.AddSqliteStores(options);
        }
        else
        {
            services.AddInMemoryStores();
        }

        return services;
    }

    /// <summary>The durable store: one SQLite file, hand-written SQL, schema created at startup.</summary>
    private static void AddSqliteStores(this IServiceCollection services, ConstructdOptions options)
    {
        services.AddSingleton(_ =>
        {
            var database = new SqliteDatabase(options.DatabasePath);
            database.EnsureCreated();
            return database;
        });

        services.AddSingleton<IUserStore, SqliteUserStore>();
        services.AddSingleton<IVmRepository, SqliteVmRepository>();
        services.AddSingleton<IAuditLog, SqliteAuditLog>();
        services.AddSingleton<IJobStore, SqliteJobStore>();
        services.AddSingleton<IForwardStore, SqliteForwardStore>();
        services.AddSingleton<ITokenService>(sp => new SqliteTokenService(
            sp.GetRequiredService<SqliteDatabase>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<IUserStore>(),
            sp.GetRequiredService<IVmRepository>()));
    }

    /// <summary>State for the lifetime of the process only — development and tests.</summary>
    private static void AddInMemoryStores(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryUserStore>();
        services.AddSingleton<IUserStore>(sp => sp.GetRequiredService<InMemoryUserStore>());

        services.AddSingleton<InMemoryVmRepository>();
        services.AddSingleton<IVmRepository>(sp => sp.GetRequiredService<InMemoryVmRepository>());

        services.AddSingleton<InMemoryAuditLog>();
        services.AddSingleton<IAuditLog>(sp => sp.GetRequiredService<InMemoryAuditLog>());

        services.AddSingleton<InMemoryJobStore>();
        services.AddSingleton<IJobStore>(sp => sp.GetRequiredService<InMemoryJobStore>());

        services.AddSingleton<InMemoryForwardStore>();
        services.AddSingleton<IForwardStore>(sp => sp.GetRequiredService<InMemoryForwardStore>());

        services.AddSingleton<InMemoryTokenService>();
        services.AddSingleton<ITokenService>(sp => sp.GetRequiredService<InMemoryTokenService>());
    }

    /// <summary>Hypervisor, ISO build and port forwards without Windows — fake mode only.</summary>
    private static void AddFakePlatform(this IServiceCollection services)
    {
        services.AddSingleton<FakeHypervisorDriver>();
        services.AddSingleton<IHypervisorDriver>(sp => sp.GetRequiredService<FakeHypervisorDriver>());

        services.AddSingleton<FakeIsoBuilder>();
        services.AddSingleton<IIsoBuilder>(sp => sp.GetRequiredService<FakeIsoBuilder>());

        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<ConstructdOptions>();
            return new InMemoryPortForwardManager(
                sp.GetRequiredService<IClock>(),
                sp.GetRequiredService<IVmRepository>(),
                sp.GetRequiredService<IForwardStore>(),
                options.SshForwardPorts,
                options.AppForwardPorts);
        });
        services.AddSingleton<IPortForwardManager>(sp => sp.GetRequiredService<InMemoryPortForwardManager>());
    }

    /// <summary>
    /// The Windows host implementations:
    /// <list type="bullet">
    /// <item><c>IHypervisorDriver</c> → <c>powershell.exe</c> running the repo's own
    /// <c>drivers/Load-ConstructDriver.ps1</c> contract,</item>
    /// <item><c>IIsoBuilder</c> → <c>wsl.exe</c> running <c>bin/build-autoinstall-iso.sh</c>,</item>
    /// <item><c>IPortForwardManager</c> → <c>netsh interface portproxy</c> plus TCP-table connection
    /// counting for the idle signal.</item>
    /// </list>
    ///
    /// All three go through <see cref="IProcessRunner"/>, so nothing here builds a command string.
    /// Off Windows the service refuses to start rather than pretending to work; persistence is
    /// unaffected either way, because the SQLite stores above are real in every mode.
    /// </summary>
    public static IServiceCollection AddPlatformImplementations(
        this IServiceCollection services,
        ConstructdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException(
                "constructd has no hypervisor platform here: the Hyper-V driver, the WSL ISO build and " +
                "the portproxy forward manager need Windows, and this process is running on " +
                $"{Environment.OSVersion.Platform}. Start the service with --fake (or " +
                "Constructd:Fake=true); persistence works in both modes.");
        }

        ValidatePlatformOptions(options);

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IIsoFileSystem, IsoFileSystem>();
        services.AddSingleton<IHostAddressResolver, DnsHostAddressResolver>();
        services.AddSingleton<ITcpTableReader, IpHlpApiTcpTableReader>();

        services.AddHttpClient<IIsoDownloader, HttpIsoDownloader>(client =>
            // The source ISO is gigabytes over whatever link the host has.
            client.Timeout = TimeSpan.FromHours(2));

        services.AddSingleton<IHypervisorDriver, HyperVDriver>();
        services.AddSingleton<IIsoBuilder, WslIsoBuilder>();
        services.AddSingleton<IPortForwardManager, NetshPortForwardManager>();

        return services;
    }

    /// <summary>
    /// Fails at startup on a misconfiguration that would otherwise surface as a failed VM creation
    /// twenty minutes in: the checkout the service invokes has to actually be a Construct checkout.
    /// </summary>
    private static void ValidatePlatformOptions(ConstructdOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ScriptsDir))
        {
            throw new InvalidOperationException(
                "Constructd:ScriptsDir is not set. It must point at the Construct checkout the service " +
                "invokes (the one holding drivers\\, lib\\ and bin\\).");
        }

        foreach (var required in new[]
                 {
                     Path.Combine(options.ScriptsDir, "drivers", "Load-ConstructDriver.ps1"),
                     Path.Combine(options.ScriptsDir, "lib", "AgentVm.Common.ps1"),
                     Path.Combine(options.ScriptsDir, "bin", "build-autoinstall-iso.sh"),
                 })
        {
            if (!File.Exists(required))
            {
                throw new InvalidOperationException(
                    $"Constructd:ScriptsDir does not look like a Construct checkout: {required} is missing.");
            }
        }
    }
}
