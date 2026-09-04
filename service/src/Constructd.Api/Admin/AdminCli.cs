using System.Globalization;
using System.Text.Json;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Admin;

/// <summary>Process exit codes of <c>constructd admin …</c>, so a script can branch on the outcome.</summary>
public static class AdminExitCode
{
    public const int Ok = 0;

    /// <summary>The command was understood but failed (a store error, netsh, the wrong OS).</summary>
    public const int Failed = 1;

    /// <summary>The command line itself was wrong.</summary>
    public const int Usage = 2;

    /// <summary>The user or VM named does not exist.</summary>
    public const int NotFound = 3;

    /// <summary>It already exists, or still owns VMs.</summary>
    public const int Conflict = 4;
}

/// <summary>
/// The service executable's second personality: <c>constructd admin …</c> works the stores directly,
/// with no HTTP and no authentication.
///
/// That is deliberate and it is why it exists. The first admin has to be created before anybody can
/// authenticate, and an operator standing at the host needs a way in when the API will not start or
/// when Negotiate is misconfigured. The gate is the host itself: the CLI reads the service's own
/// configuration and opens its database, so being able to run it means already having the privileges
/// on the machine that owning the service implies.
///
/// Every verb also speaks <c>--json</c>, because the installer script consumes it.
/// </summary>
public static class AdminCli
{
    /// <summary>The first argument that switches the executable into CLI mode.</summary>
    public const string Verb = "admin";

    private const string Usage = """
        Usage: constructd admin <command> [options]

          users add <name> --role Admin|User --max-vms <n> [--no-host-forwards]
          users remove <name>
          users list
          tokens issue <user> --label <label>
          tokens revoke-all <user>
          forwards reconcile
          host status
          iso build [--force]
          iso status
          iso prune

        Options:
          --json    machine-readable output on stdout (errors on stderr)

        Exit codes: 0 ok, 1 failed, 2 usage, 3 not found, 4 conflict.
        """;

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(services);

        var json = args.Any(arg => string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase));
        var positional = args.Where(arg => !string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase)).ToList();
        var writer = new AdminOutput(output, error, json);

        if (positional.Count == 0 || positional[0] is "--help" or "-h" or "help")
        {
            output.WriteLine(Usage);
            return positional.Count == 0 ? AdminExitCode.Usage : AdminExitCode.Ok;
        }

        try
        {
            return (positional[0].ToLowerInvariant(), positional.ElementAtOrDefault(1)?.ToLowerInvariant()) switch
            {
                ("users", "add") => await AddUserAsync(positional, services, writer, cancellationToken).ConfigureAwait(false),
                ("users", "remove") => await RemoveUserAsync(positional, services, writer, cancellationToken).ConfigureAwait(false),
                ("users", "list") => await ListUsersAsync(positional, services, writer, cancellationToken).ConfigureAwait(false),
                ("tokens", "issue") => await IssueTokenAsync(positional, services, writer, cancellationToken).ConfigureAwait(false),
                ("tokens", "revoke-all") => await RevokeTokensAsync(positional, services, writer, cancellationToken).ConfigureAwait(false),
                ("forwards", "reconcile") => await ReconcileAsync(positional, services, writer, cancellationToken).ConfigureAwait(false),
                ("host", "status") => HostStatus(positional, services, writer),
                ("iso", "build") => await IsoBuildAsync(positional, services, writer, output, cancellationToken).ConfigureAwait(false),
                ("iso", "status") => IsoStatus(positional, services, writer),
                ("iso", "prune") => IsoPrune(positional, services, writer),
                _ => writer.Usage($"unknown command '{string.Join(' ', positional.Take(2))}'"),
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Same rule as everywhere else in this service: only a safe description leaves the process.
            return writer.Error(AdminExitCode.Failed, SafeError.Describe(ex));
        }
    }

    private static async Task<int> AddUserAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        AdminOutput writer,
        CancellationToken cancellationToken)
    {
        if (args.ElementAtOrDefault(2) is not { Length: > 0 } name || name.StartsWith('-'))
        {
            return writer.Usage("users add needs a user name");
        }

        var options = new OptionSet(
            args.Skip(3),
            valueOptions: ["--role", "--max-vms"],
            flags: ["--no-host-forwards"]);

        var role = Role.User;
        if (options.Value("--role") is { } roleText)
        {
            // Matched against the names, not parsed: Enum.TryParse also accepts "3" — and any other
            // number, defined or not — so a typo could otherwise produce a role nothing in the service
            // knows how to authorize.
            var match = Enum.GetNames<Role>()
                .FirstOrDefault(candidate => string.Equals(candidate, roleText.Trim(), StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                return writer.Usage($"--role must be one of: {string.Join(", ", Enum.GetNames<Role>())}");
            }

            role = Enum.Parse<Role>(match);
        }

        var maxVms = 0;
        if (options.Value("--max-vms") is { } maxText &&
            (!int.TryParse(maxText, NumberStyles.None, CultureInfo.InvariantCulture, out maxVms) || maxVms > 10_000))
        {
            return writer.Usage("--max-vms must be a number between 0 and 10000");
        }

        var allowHostForwards = !options.Flag("--no-host-forwards");

        if (options.Unknown is { } unknown)
        {
            return writer.Usage($"unknown option '{unknown}'");
        }

        var clock = services.GetRequiredService<IClock>();
        var users = services.GetRequiredService<IUserStore>();
        var user = new User(name.Trim(), role, maxVms, clock.UtcNow, allowHostForwards);

        if (!await users.CreateAsync(user, cancellationToken).ConfigureAwait(false))
        {
            return writer.Error(AdminExitCode.Conflict, $"user '{user.Name}' already exists");
        }

        await AuditAsync(services, "user.create", user.Name, $"role={user.Role}, maxVms={user.MaxVms}", cancellationToken)
            .ConfigureAwait(false);

        return writer.Result(
            Describe(user),
            $"Added {user.Name}: role {user.Role}, quota {user.MaxVms} VM(s), " +
            $"host forwards {(user.AllowHostForwards ? "allowed" : "denied")}.");
    }

    private static async Task<int> RemoveUserAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        AdminOutput writer,
        CancellationToken cancellationToken)
    {
        if (args.ElementAtOrDefault(2) is not { Length: > 0 } name || name.StartsWith('-'))
        {
            return writer.Usage("users remove needs a user name");
        }

        if (Extra(args, 3) is { } unexpected)
        {
            return writer.Usage($"users remove takes no options; got '{unexpected}'");
        }

        var users = services.GetRequiredService<IUserStore>();
        var vms = services.GetRequiredService<IVmRepository>();

        if (await users.GetAsync(name, cancellationToken).ConfigureAwait(false) is null)
        {
            return writer.Error(AdminExitCode.NotFound, $"no such user: {name}");
        }

        // The same rule the API enforces: a user with VMs cannot be removed, because their VMs would
        // be left with an owner that does not exist.
        var owned = await vms.ListAsync(name, cancellationToken).ConfigureAwait(false);
        if (owned.Count > 0)
        {
            return writer.Error(
                AdminExitCode.Conflict,
                $"{name} still owns {owned.Count} VM(s); remove those first");
        }

        var tokens = services.GetRequiredService<ITokenService>();
        var revoked = await tokens.RevokeAllAsync(name, cancellationToken).ConfigureAwait(false);
        await users.DeleteAsync(name, cancellationToken).ConfigureAwait(false);

        await AuditAsync(services, "user.delete", name, $"tokens revoked={revoked}", cancellationToken)
            .ConfigureAwait(false);

        return writer.Result(
            new { name, revokedTokens = revoked },
            $"Removed {name} ({revoked} token(s) revoked).");
    }

    private static async Task<int> ListUsersAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        AdminOutput writer,
        CancellationToken cancellationToken)
    {
        if (Extra(args, 2) is { } unexpected)
        {
            return writer.Usage($"users list takes no arguments; got '{unexpected}'");
        }

        var users = await services.GetRequiredService<IUserStore>().ListAsync(cancellationToken).ConfigureAwait(false);

        return writer.Result(
            users.Select(Describe).ToList(),
            users.Count == 0
                ? "No users yet."
                : string.Join(
                    Environment.NewLine,
                    users.Select(user =>
                        $"{user.Name}\t{user.Role}\tmaxVms={user.MaxVms}\thostForwards={user.AllowHostForwards}")));
    }

    private static async Task<int> IssueTokenAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        AdminOutput writer,
        CancellationToken cancellationToken)
    {
        if (args.ElementAtOrDefault(2) is not { Length: > 0 } name || name.StartsWith('-'))
        {
            return writer.Usage("tokens issue needs a user name");
        }

        var options = new OptionSet(args.Skip(3), valueOptions: ["--label"]);
        var label = options.Value("--label") ?? "admin-cli";

        if (options.Unknown is { } unknown)
        {
            return writer.Usage($"unknown option '{unknown}'");
        }

        var users = services.GetRequiredService<IUserStore>();
        if (await users.GetAsync(name, cancellationToken).ConfigureAwait(false) is null)
        {
            return writer.Error(AdminExitCode.NotFound, $"no such user: {name}");
        }

        var issued = await services.GetRequiredService<ITokenService>()
            .IssueAsync(name, label, cancellationToken).ConfigureAwait(false);

        // The label, never the secret.
        await AuditAsync(services, "token.issue", name, $"label={label}", cancellationToken).ConfigureAwait(false);

        // The plaintext exists exactly here and never again: only its hash is stored, and it is not
        // logged or audited. Printing it is the whole purpose of the command.
        return writer.Result(
            new { user = issued.Token.UserName, label = issued.Token.Label, token = issued.Plaintext },
            $"Token for {issued.Token.UserName} ({issued.Token.Label}) — shown once, store it now:" +
            Environment.NewLine + issued.Plaintext);
    }

    private static async Task<int> RevokeTokensAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        AdminOutput writer,
        CancellationToken cancellationToken)
    {
        if (args.ElementAtOrDefault(2) is not { Length: > 0 } name || name.StartsWith('-'))
        {
            return writer.Usage("tokens revoke-all needs a user name");
        }

        if (Extra(args, 3) is { } unexpected)
        {
            return writer.Usage($"tokens revoke-all takes no options; got '{unexpected}'");
        }

        var users = services.GetRequiredService<IUserStore>();
        if (await users.GetAsync(name, cancellationToken).ConfigureAwait(false) is null)
        {
            return writer.Error(AdminExitCode.NotFound, $"no such user: {name}");
        }

        var revoked = await services.GetRequiredService<ITokenService>()
            .RevokeAllAsync(name, cancellationToken).ConfigureAwait(false);

        await AuditAsync(services, "token.revoke-all", name, $"count={revoked}", cancellationToken)
            .ConfigureAwait(false);

        return writer.Result(
            new { user = name, revoked },
            $"Revoked {revoked} token(s) of {name}.");
    }

    private static async Task<int> ReconcileAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        AdminOutput writer,
        CancellationToken cancellationToken)
    {
        if (Extra(args, 2) is { } unexpected)
        {
            return writer.Usage($"forwards reconcile takes no arguments; got '{unexpected}'");
        }

        var forwards = services.GetService<IPortForwardManager>();
        if (forwards is null)
        {
            return writer.Error(
                AdminExitCode.Failed,
                "port forwards can only be reconciled on the Windows host that materializes them");
        }

        var repaired = await forwards.ReconcileAsync(cancellationToken).ConfigureAwait(false);

        return writer.Result(
            new { repaired },
            repaired == 0 ? "Host port forwards already match the store." : $"Repaired {repaired} host rule(s).");
    }

    /// <summary>
    /// Builds the autoinstall media and publishes it into the catalog.
    ///
    /// A thin driver over "a builder + the catalog", and it is thin on purpose: which
    /// strategy builds (WSL today; Native, InGuest or HypervisorHost later) is composition's decision,
    /// and none of it is visible here. Run by the interactive administrator — the installer's ISO step
    /// is this command — because on Windows the service identity (LocalSystem) cannot run WSL.
    ///
    /// The seed password is generated here and discarded: nobody logs in with it (the client
    /// provisions over the bootstrap key), it is never printed, stored or audited.
    /// </summary>
    private static async Task<int> IsoBuildAsync(
        IReadOnlyList<string> args,
        IServiceProvider services,
        AdminOutput writer,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var parsed = new OptionSet(args.Skip(2), valueOptions: [], flags: ["--force"]);
        if (parsed.Unknown is { } unknown)
        {
            return writer.Usage($"unknown option '{unknown}'");
        }

        var force = parsed.Flag("--force");

        var catalog = services.GetService<IIsoCatalog>();
        var builder = services.GetService<IIsoMediaBuilder>();
        var options = services.GetService<ConstructdOptions>();
        if (catalog is null || builder is null || options is null)
        {
            return writer.Error(AdminExitCode.Failed, NoIsoSupport);
        }

        var current = catalog.GetCurrent();
        if (!force && current is { Sidecar: not null })
        {
            // Idempotent: re-running the installer must not spend twenty minutes rebuilding media
            // that is already there. --force is how a new Ubuntu release or a rotated key gets in.
            return writer.Result(
                Describe(current, options),
                $"The autoinstall ISO is already built ({current.FileName}, " +
                $"built {current.Sidecar.BuiltAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)})." +
                Environment.NewLine +
                "Pass --force to build a new one." + Environment.NewLine +
                $"ISO: {current.Path}");
        }

        // Progress is the only sign of life during a multi-gigabyte repack, so it is streamed — but
        // never into stdout that a script is parsing as JSON.
        var progress = writer.Json ? null : new TextWriterProgress(output);

        var mediaPath = catalog.NextMediaPath();
        var result = await builder.BuildMediaAsync(
            new IsoMediaRequest(
                mediaPath,
                options.Iso.SeedUser,
                TokenHasher.GenerateSecret(),
                options.Iso.BootstrapPublicKeyPath,
                options.Iso.HostnameSource),
            progress,
            cancellationToken).ConfigureAwait(false);

        var clock = services.GetRequiredService<IClock>();
        var entry = catalog.Publish(
            result.IsoPath,
            new IsoSidecar(
                clock.UtcNow,
                result.SourceIsoPath,
                result.SourceSha256,
                options.Iso.SeedUser,
                result.BootstrapKeyFingerprint,
                options.Iso.HostnameSource,
                result.BuildScriptSha256));

        await AuditAsync(
            services,
            "iso.build",
            entry.FileName,
            $"source={result.SourceIsoPath}, hostnameSource={options.Iso.HostnameSource}, " +
            $"bootstrapKey={result.BootstrapKeyFingerprint}",
            cancellationToken).ConfigureAwait(false);

        return writer.Result(
            Describe(entry, options),
            $"Built and published {entry.FileName}." + Environment.NewLine +
            $"ISO: {entry.Path}");
    }

    /// <summary>
    /// How this host advertises its VMs: the LAN name endpoints and forwards are published on, and
    /// the optional per-VM host-name pattern (plan §4.12) with an example rendering, so an admin can
    /// see at a glance whether the wildcard DNS record is actually in play.
    /// </summary>
    private static int HostStatus(IReadOnlyList<string> args, IServiceProvider services, AdminOutput writer)
    {
        if (Extra(args, 2) is { } unexpected)
        {
            return writer.Usage($"host status takes no options; got '{unexpected}'");
        }

        var options = services.GetService<ConstructdOptions>();
        if (options is null)
        {
            return writer.Error(AdminExitCode.Failed, "This build has no service configuration to report.");
        }

        var pattern = (options.PublicHostPattern ?? string.Empty).Trim();
        var configured = pattern.Length > 0;
        // The rendering an admin can check against DNS. ExampleVm is a legal instance name, so this
        // is exactly what a VM of that name would be advertised as.
        var example = options.PublicHostFor(ExampleVm);

        var lines = new List<string>
        {
            $"PublicHost        : {options.PublicHost}",
            $"PublicHostPattern : {(configured ? pattern : "(unset -- every VM is advertised on PublicHost)")}",
            $"Example           : VM '{ExampleVm}' is advertised as {example}",
        };

        if (configured)
        {
            lines.Add($"                    point a wildcard DNS record at this host for {pattern.Replace(PublicHostPatternRules.Placeholder, "*", StringComparison.Ordinal)}");
        }

        lines.Add($"SSH forwards      : {options.SshForwardPorts.Start}-{options.SshForwardPorts.End}");
        lines.Add($"App forwards      : {options.AppForwardPorts.Start}-{options.AppForwardPorts.End}");

        return writer.Result(
            new
            {
                publicHost = options.PublicHost,
                publicHostPattern = configured ? pattern : null,
                example = new { vmName = ExampleVm, publicHost = example },
                sshForwardPorts = new { start = options.SshForwardPorts.Start, end = options.SshForwardPorts.End },
                appForwardPorts = new { start = options.AppForwardPorts.Start, end = options.AppForwardPorts.End },
            },
            string.Join(Environment.NewLine, lines));
    }

    /// <summary>The VM name `host status` renders the pattern with. A valid instance name.</summary>
    private const string ExampleVm = "work-vm";

    /// <summary>
    /// What media this host has, and whether a VM could be created right now. Exit code 3 when
    /// nothing usable is published, so the installer and a health check can branch on it.
    /// </summary>
    private static int IsoStatus(IReadOnlyList<string> args, IServiceProvider services, AdminOutput writer)
    {
        if (Extra(args, 2) is { } unexpected)
        {
            return writer.Usage($"iso status takes no options; got '{unexpected}'");
        }

        var catalog = services.GetService<IIsoCatalog>();
        var options = services.GetService<ConstructdOptions>();
        if (catalog is null || options is null)
        {
            return writer.Error(AdminExitCode.Failed, NoIsoSupport);
        }

        var current = catalog.GetCurrent();
        var all = catalog.List();

        if (current is null || current.Sidecar is null)
        {
            var why = current is null
                ? "No autoinstall ISO is published on this host."
                : $"The current ISO ({current.FileName}) has no readable sidecar, so it is not usable.";

            return writer.Error(
                AdminExitCode.NotFound,
                $"{why} VM creation will fail until it is built: {IsoBuildCommand}");
        }

        var lines = new List<string>
        {
            $"Mode          : {options.Iso.Mode}",
            $"Current ISO   : {current.Path}",
            $"Built         : {current.Sidecar.BuiltAt.UtcDateTime.ToString("u", CultureInfo.InvariantCulture)}",
            $"Source ISO    : {current.Sidecar.SourceIso}",
            $"Source SHA256 : {current.Sidecar.SourceSha256}",
            $"Seed user     : {current.Sidecar.SeedUser}",
            $"Bootstrap key : {current.Sidecar.BootstrapKeyFingerprint}",
            $"Guest identity: {current.Sidecar.HostnameSource}",
            $"Size          : {current.SizeBytes} bytes",
        };

        var others = all.Where(entry => !entry.IsCurrent).ToList();
        if (others.Count > 0)
        {
            lines.Add($"Older ISOs    : {others.Count} ({string.Join(", ", others.Select(entry => entry.FileName))})");
            lines.Add("                remove them with: constructd admin iso prune");
        }

        return writer.Result(
            new
            {
                mode = options.Iso.Mode.ToString(),
                current = Describe(current, options),
                others = others.Select(entry => Describe(entry, options)).ToList(),
            },
            string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// Deletes the media nothing points at. An ISO a VM still has attached is held open by Hyper-V:
    /// that is reported and skipped, not treated as a failure.
    /// </summary>
    private static int IsoPrune(IReadOnlyList<string> args, IServiceProvider services, AdminOutput writer)
    {
        if (Extra(args, 2) is { } unexpected)
        {
            return writer.Usage($"iso prune takes no options; got '{unexpected}'");
        }

        var catalog = services.GetService<IIsoCatalog>();
        if (catalog is null)
        {
            return writer.Error(AdminExitCode.Failed, NoIsoSupport);
        }

        var pruned = catalog.Prune();

        var lines = new List<string>
        {
            pruned.Removed.Count == 0
                ? "No ISO was removed."
                : $"Removed {pruned.Removed.Count} ISO(s): {string.Join(", ", pruned.Removed)}",
        };
        lines.AddRange(pruned.Skipped.Select(skip => $"Kept {skip.FileName}: {skip.Reason}."));

        return writer.Result(
            new
            {
                removed = pruned.Removed,
                skipped = pruned.Skipped.Select(skip => new { file = skip.FileName, reason = skip.Reason }).ToList(),
            },
            string.Join(Environment.NewLine, lines));
    }

    /// <summary>The command that produces the media; printed wherever its absence is reported.</summary>
    private const string IsoBuildCommand = "constructd admin iso build";

    private const string NoIsoSupport =
        "install media can only be built or inspected on the Windows host that holds it";

    private static object Describe(IsoCatalogEntry entry, ConstructdOptions options) => new
    {
        file = entry.FileName,
        path = entry.Path,
        current = entry.IsCurrent,
        sizeBytes = entry.SizeBytes,
        builtAt = entry.Sidecar?.BuiltAt,
        sourceIso = entry.Sidecar?.SourceIso,
        sourceSha256 = entry.Sidecar?.SourceSha256,
        seedUser = entry.Sidecar?.SeedUser,
        bootstrapKeyFingerprint = entry.Sidecar?.BootstrapKeyFingerprint,
        hostnameSource = entry.Sidecar?.HostnameSource ?? options.Iso.HostnameSource,
        scriptSha256 = entry.Sidecar?.ScriptSha256,
    };

    /// <summary>
    /// The first argument past what a command takes, if any. A verb that quietly ignores what it was
    /// not expecting is how somebody ends up believing they set a quota they did not set.
    /// </summary>
    private static string? Extra(IReadOnlyList<string> args, int expectedCount) =>
        args.Count > expectedCount ? args[expectedCount] : null;

    private static object Describe(User user) => new
    {
        name = user.Name,
        role = user.Role.ToString(),
        maxVms = user.MaxVms,
        allowHostForwards = user.AllowHostForwards,
        created = user.Created,
    };

    /// <summary>
    /// The CLI writes the same audit trail the API does — an admin action taken at the console is an
    /// admin action, and "who did what" cannot have a hole in it that is reachable from a shell.
    /// </summary>
    private static async Task AuditAsync(
        IServiceProvider services,
        string action,
        string target,
        string detail,
        CancellationToken cancellationToken)
    {
        var audit = services.GetService<IAuditLog>();
        if (audit is null)
        {
            return;
        }

        var clock = services.GetRequiredService<IClock>();
        await audit.AppendAsync(
            new AuditEntry(clock.UtcNow, "admin-cli", action, target, AuditOutcome.Success, detail),
            cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Progress lines straight to the console — what a long build shows while it runs.</summary>
internal sealed class TextWriterProgress(TextWriter output) : IProgress<string>
{
    public void Report(string value) => output.WriteLine(value);
}

/// <summary>Text or JSON, chosen once by <c>--json</c> and applied to results and errors alike.</summary>
internal sealed class AdminOutput(TextWriter output, TextWriter error, bool json)
{
    /// <summary>Whether stdout is being parsed — nothing else may be written to it.</summary>
    public bool Json => json;

    public int Result(object payload, string text)
    {
        if (json)
        {
            output.WriteLine(JsonSerializer.Serialize(payload, ApiJson.Options));
        }
        else
        {
            output.WriteLine(text);
        }

        return AdminExitCode.Ok;
    }

    public int Error(int exitCode, string message)
    {
        if (json)
        {
            error.WriteLine(JsonSerializer.Serialize(new { ok = false, error = message }, ApiJson.Options));
        }
        else
        {
            error.WriteLine($"constructd admin: {message}");
        }

        return exitCode;
    }

    public int Usage(string message) => Error(AdminExitCode.Usage, message);
}

/// <summary>
/// The handful of <c>--option value</c> / <c>--flag</c> pairs the admin verbs take. Deliberately tiny:
/// a command-line parser package for six commands would be more machinery than the commands.
/// </summary>
internal sealed class OptionSet
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="valueOptions">Options that take the next argument as their value.</param>
    /// <param name="flags">Options that stand alone.</param>
    public OptionSet(IEnumerable<string> arguments, string[] valueOptions, string[]? flags = null)
    {
        var known = new HashSet<string>(valueOptions, StringComparer.OrdinalIgnoreCase);
        var knownFlags = new HashSet<string>(flags ?? [], StringComparer.OrdinalIgnoreCase);
        string? pending = null;

        foreach (var argument in arguments)
        {
            if (pending is not null)
            {
                _values[pending] = argument;
                pending = null;
                continue;
            }

            if (knownFlags.Contains(argument))
            {
                _flags.Add(argument);
                continue;
            }

            if (known.Contains(argument))
            {
                pending = argument;
                continue;
            }

            // An option nobody declared, or a stray positional: both mean the caller mistyped
            // something, and silently ignoring it is how a quota ends up not being what was asked for.
            Unknown ??= argument;
        }

        // "--role" with nothing after it.
        if (pending is not null)
        {
            Unknown ??= pending;
        }
    }

    /// <summary>The first argument this command does not understand, if any.</summary>
    public string? Unknown { get; }

    public string? Value(string name) =>
        _values.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    public bool Flag(string name) => _flags.Contains(name);
}
