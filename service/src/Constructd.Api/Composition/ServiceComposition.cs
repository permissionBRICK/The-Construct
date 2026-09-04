using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
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

        // Platform-agnostic and therefore checked in EVERY mode, fake included: a
        // PublicHostPattern that does not render to a host name would otherwise surface as a
        // URL nobody can open, weeks after it was configured (plan §4.12).
        if (PublicHostPatternRules.Validate(options.PublicHostPattern) is { } patternProblem)
        {
            throw new InvalidOperationException(patternProblem);
        }

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
        services.AddSingleton<IPortForwardManager, NetshPortForwardManager>();

        services.AddIsoStrategy(options);

        return services;
    }

    /// <summary>
    /// THE place that maps <c>Constructd:Iso:Mode</c> to an ISO build strategy. Adding a
    /// strategy is a new <see cref="IIsoBuilder"/> (and, if it can produce media, an
    /// <see cref="IIsoMediaBuilder"/>) plus one case here — no endpoint, job or CLI code changes,
    /// because everything else talks to the two seams and the catalog.
    ///
    /// Three registrations, and they are deliberately independent:
    /// <list type="bullet">
    /// <item>the <b>catalog</b> — versioned files, pointer, sidecars — shared by every strategy;</item>
    /// <item>the <b>producing</b> side (<see cref="IIsoMediaBuilder"/>), which is what
    /// <c>admin iso build</c> drives as the interactive administrator. It is registered whatever the
    /// mode is: in <c>Prebuilt</c> mode building the media is the whole point, and in <c>PerVm</c>
    /// mode an admin can still publish a catalog entry;</item>
    /// <item>the <b>consuming</b> side (<see cref="IIsoBuilder"/>), chosen by the mode. This is the
    /// only line that differs between a host that consumes pre-built media and one whose service
    /// identity can run WSL per VM.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddIsoStrategy(
        this IServiceCollection services,
        ConstructdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        services.AddSingleton<IIsoCatalog>(sp => new FileIsoCatalog(
            sp.GetRequiredService<IIsoFileSystem>(),
            sp.GetRequiredService<IClock>(),
            options.Iso.CacheDir,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<FileIsoCatalog>()));

        // WSL is what can build media on a Windows host today; the interactive administrator's WSL,
        // not the service's (it has none — that is the whole reason Prebuilt is the default).
        services.AddSingleton<WslIsoBuilder>();
        services.AddSingleton<IIsoMediaBuilder>(sp => sp.GetRequiredService<WslIsoBuilder>());

        switch (options.Iso.Mode)
        {
            case IsoBuildMode.Prebuilt:
                services.AddSingleton<IIsoBuilder>(sp => new PrebuiltIsoBuilder(
                    sp.GetRequiredService<IIsoCatalog>(),
                    sp.GetRequiredService<IIsoFileSystem>(),
                    options,
                    sp.GetRequiredService<ILoggerFactory>().CreateLogger<PrebuiltIsoBuilder>()));
                break;

            case IsoBuildMode.PerVm:
                services.AddSingleton<IIsoBuilder>(sp => sp.GetRequiredService<WslIsoBuilder>());
                break;

            default:
                // Unreachable: ValidatePlatformOptions refuses these at startup. Kept so that adding a
                // value to the enum without adding it here fails loudly rather than silently falling
                // back to a strategy nobody asked for.
                throw new InvalidOperationException(UnimplementedModeMessage(options.Iso.Mode));
        }

        return services;
    }

    /// <summary>Why a planned mode is not a working mode, and what to set instead.</summary>
    private static string UnimplementedModeMessage(IsoBuildMode mode) =>
        $"Constructd:Iso:Mode is '{mode}', which is a planned ISO build strategy (service/README.md, " +
        "\"ISO build strategies\") and is not implemented yet. Use 'Prebuilt' " +
        "(an administrator builds the media with 'constructd admin iso build') or 'PerVm' (the " +
        "service builds one ISO per VM through WSL, which needs a service identity that can run it).";

    /// <summary>
    /// Fails at startup on a misconfiguration that would otherwise surface as a failed VM creation
    /// twenty minutes in: the checkout the service invokes has to actually be a Construct checkout.
    /// </summary>
    private static void ValidatePlatformOptions(ConstructdOptions options)
    {
        if (options.Iso.Mode is not (IsoBuildMode.Prebuilt or IsoBuildMode.PerVm))
        {
            throw new InvalidOperationException(UnimplementedModeMessage(options.Iso.Mode));
        }

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
