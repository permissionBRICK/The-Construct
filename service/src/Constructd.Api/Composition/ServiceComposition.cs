using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Constructd.Sqlite;

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

        services.AddSingleton<IClock, SystemClock>();

        if (options.EffectivePersistence == PersistenceMode.Sqlite)
        {
            services.AddSqliteStores(options);
        }
        else
        {
            services.AddInMemoryStores();
        }

        if (options.Fake)
        {
            services.AddFakePlatform();
        }
        else
        {
            services.AddPlatformImplementations();
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
    /// Where the Windows host implementations get registered:
    /// <list type="bullet">
    /// <item><c>IHypervisorDriver</c> → PowerShell/Hyper-V driver (B7),</item>
    /// <item><c>IIsoBuilder</c> → <c>wsl.exe bin/build-autoinstall-iso.sh</c> (B7),</item>
    /// <item><c>IPortForwardManager</c> → <c>netsh interface portproxy</c> + TCP-table connection
    /// counting (B7/B8).</item>
    /// </list>
    /// Until they exist the service refuses to start rather than pretending to work. Persistence is
    /// unaffected by this: the SQLite stores above are real in every mode.
    /// </summary>
    private static void AddPlatformImplementations(this IServiceCollection services) =>
        throw new InvalidOperationException(
            "constructd has no hypervisor platform yet: the Hyper-V driver, the ISO builder and the " +
            "portproxy forward manager land in follow-up packages. Start the service with --fake " +
            "(or Constructd:Fake=true) until then; persistence works in both modes.");
}
