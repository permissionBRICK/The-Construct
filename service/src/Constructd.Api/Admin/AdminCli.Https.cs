using Constructd.Api.Endpoints;
using Constructd.Api.Https;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;

namespace Constructd.Api.Admin;

/// <summary>
/// <c>constructd admin https …</c>: the host's HTTPS certificate (docs/https-forwards.md), set on the host
/// itself, with no API token. This is what a renewal script calls (for example as an SSH forced command); the
/// running service picks a new certificate up within seconds, with no restart.
/// </summary>
public static partial class AdminCli
{
    private static async Task<int> HttpsAsync(IReadOnlyList<string> args, IServiceProvider services, AdminOutput writer, CancellationToken ct)
    {
        var options = services.GetRequiredService<ConstructdOptions>();
        var clock = services.GetService<IClock>() ?? new SystemClock();
        var store = new HttpsCertificateStore(options, clock,
            services.GetService<ILogger<HttpsCertificateStore>>() ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpsCertificateStore>.Instance);
        var verb = args.ElementAtOrDefault(1)?.ToLowerInvariant();
        switch (verb)
        {
            case "status":
                if (Extra(args, 2) is { } unexpected) return writer.Usage($"https status takes no options; got '{unexpected}'");
                return await HttpsStatusAsync(store, services, options, clock, writer, ct);
            case "import":
            {
                var parsed = new OptionSet(args.Skip(2), valueOptions: ["--cert", "--key", "--pfx", "--password-file"]);
                if (parsed.Unknown is { } unknown) return writer.Usage($"unknown option '{unknown}'");
                var pfx = parsed.Value("--pfx");
                var cert = parsed.Value("--cert");
                var key = parsed.Value("--key");
                if (pfx is null == (cert is null || key is null) || pfx is not null && (cert is not null || key is not null))
                    return writer.Usage("https import needs --cert <chain.pem> --key <key.pem>, or --pfx <file> [--password-file <file>]");
                HttpsCertificateInfo info;
                try
                {
                    info = pfx is not null
                        ? store.ImportPfx(await File.ReadAllBytesAsync(pfx, ct),
                            parsed.Value("--password-file") is { } passwordFile ? (await File.ReadAllTextAsync(passwordFile, ct)).TrimEnd('\r', '\n') : null)
                        : store.Import(await File.ReadAllTextAsync(cert!, ct), await File.ReadAllTextAsync(key!, ct));
                }
                catch (HttpsCertificateException ex)
                {
                    return writer.Error(AdminExitCode.Failed, ex.Message);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return writer.Error(AdminExitCode.Failed, SafeError.Describe(ex));
                }

                await AuditAsync(services, "host.https.certificate", "host", $"sha256={info.Sha256}, notAfter={info.NotAfter:u}", ct);
                return writer.Result(info, $"Installed {info.Subject} ({info.Sha256}), valid until {info.NotAfter:u}.");
            }

            case "remove":
                if (Extra(args, 2) is { } extra) return writer.Usage($"https remove takes no options; got '{extra}'");
                var removed = store.Remove();
                if (removed) await AuditAsync(services, "host.https.certificate.remove", "host", "removed", ct);
                return writer.Result(new { removed }, removed ? "HTTPS certificate removed." : "No HTTPS certificate was installed.");
            default:
                return writer.Usage("https needs status, import or remove");
        }
    }

    /// <summary>
    /// The host settings store. The CLI host registers only the stores, not the host-administration
    /// services, so the SQLite one is built here; without a database the defaults are reported.
    /// </summary>
    private static IHostConfigStore HostConfig(IServiceProvider services, IClock clock) =>
        services.GetService<IHostConfigStore>()
        ?? (services.GetService<Constructd.Sqlite.SqliteDatabase>() is { } database
            ? new Constructd.Sqlite.SqliteHostConfigStore(database, clock)
            : new Constructd.Fakes.InMemoryHostConfigStore(clock));

    private static async Task<int> HttpsStatusAsync(HttpsCertificateStore store, IServiceProvider services, ConstructdOptions options,
        IClock clock, AdminOutput writer, CancellationToken ct)
    {
        var config = HostConfig(services, clock);
        var status = await HttpsEndpoints.DescribeAsync(store, config, options, clock, gateway: null, ct);
        var info = store.Current?.Info;
        var https = await config.GetAsync<Core.Domain.HttpsConfig>("https", ct) ?? HostAdminDefaults.Https;
        var text = info is null
            ? $"No HTTPS certificate installed ({store.Directory})."
            : $"Certificate : {info.Subject}{Environment.NewLine}" +
              $"Names       : {string.Join(", ", info.DnsNames)}{Environment.NewLine}" +
              $"Valid until : {info.NotAfter:u}{Environment.NewLine}" +
              $"SHA-256     : {info.Sha256}";
        text += $"{Environment.NewLine}Shared port : {(https.SharedPort?.ToString() ?? "off")}, start on request: {(https.StartOnRequest ? "on" : "off")}";
        return writer.Result(status, text);
    }
}
