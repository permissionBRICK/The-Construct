using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;

namespace Constructd.Api.Composition;

/// <summary>
/// One-time startup work: seed the first admin (there is no self-registration, so a fresh host would
/// otherwise be unreachable) and reconcile the host's port forwards against the store, which is what
/// makes forwards survive a reboot of the service host (plan §4.4).
/// </summary>
public static class Bootstrap
{
    public static async Task RunAsync(IServiceProvider services, CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var provider = scope.ServiceProvider;
        var options = provider.GetRequiredService<ConstructdOptions>();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(Bootstrap));

        await SeedAdminAsync(provider, options, logger, cancellationToken).ConfigureAwait(false);

        // A job that was still running when the process ended cannot be resumed; mark it failed so
        // clients see a terminal state instead of a job that never finishes.
        var clock = provider.GetRequiredService<IClock>();
        var interrupted = await provider.GetRequiredService<IJobStore>()
            .MarkInterruptedAsync(clock.UtcNow, cancellationToken).ConfigureAwait(false);
        if (interrupted > 0)
        {
            logger.LogWarning("Marked {Count} job(s) as failed: they were interrupted by a restart.", interrupted);
        }

        var forwards = provider.GetRequiredService<IPortForwardManager>();
        var repaired = await forwards.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        if (repaired > 0)
        {
            logger.LogInformation("Reconciled {Count} port forward(s) against the host.", repaired);
        }
    }

    private static async Task SeedAdminAsync(
        IServiceProvider provider,
        ConstructdOptions options,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.BootstrapAdmin))
        {
            return;
        }

        var users = provider.GetRequiredService<IUserStore>();
        var existing = await users.ListAsync(cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0)
        {
            return;
        }

        var clock = provider.GetRequiredService<IClock>();
        var admin = new User(
            options.BootstrapAdmin.Trim(),
            Role.Admin,
            options.BootstrapAdminMaxVms,
            clock.UtcNow);

        if (!await users.CreateAsync(admin, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        logger.LogInformation("Seeded bootstrap admin {User}.", admin.Name);

        if (!string.IsNullOrWhiteSpace(options.BootstrapAdminToken))
        {
            var tokens = provider.GetRequiredService<ITokenService>();
            await tokens.ImportAsync(admin.Name, "bootstrap", options.BootstrapAdminToken, cancellationToken)
                .ConfigureAwait(false);

            // The plaintext itself is never logged.
            logger.LogWarning(
                "Registered the configured bootstrap token for {User}. Issue a real token and remove " +
                "Constructd:BootstrapAdminToken from the configuration.",
                admin.Name);
        }

        var audit = provider.GetRequiredService<IAuditLog>();
        await audit.AppendAsync(
            new AuditEntry(clock.UtcNow, "system", "user.bootstrap", admin.Name, AuditOutcome.Success, "seeded admin"),
            cancellationToken).ConfigureAwait(false);
    }
}
