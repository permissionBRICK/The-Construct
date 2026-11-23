using Constructd.Api.Contracts;
using Constructd.Api.Auth;
using Constructd.Api.Https;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Endpoints;

/// <summary>
/// The host's HTTPS certificate (docs/https-forwards.md): its state, and setting or removing it. Host admins
/// only. The same store is written by <c>constructd admin https import</c> on the host itself, which is what a
/// renewal script uses.
/// </summary>
public static class HttpsEndpoints
{
    /// <summary>A PEM chain with its key, or a PFX (base64) with an optional password.</summary>
    public sealed record CertificateRequest(string? CertificatePem, [property: ConstructSecret] string? PrivateKeyPem, [property: ConstructSecret] string? Pfx,
        [property: ConstructSecret] string? Password);

    public static RouteGroupBuilder MapHttpsEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/host/https", StatusAsync).RequireAuthorization(Policies.Admin).WithName("HttpsStatus").Produces<HttpsStatusResponse>();
        api.MapPut("/host/https/certificate", ImportAsync).RequireAuthorization(Policies.Admin).Audited("host.https.certificate").WithName("SetHttpsCertificate").Produces<HttpsStatusResponse>();
        api.MapDelete("/host/https/certificate", RemoveAsync).RequireAuthorization(Policies.Admin).Audited("host.https.certificate.remove").WithName("RemoveHttpsCertificate").Produces<HttpsStatusResponse>();
        return api;
    }

    private static async Task<IResult> StatusAsync(HttpsCertificateStore certificates, HttpsGateway gateway, IHostConfigStore config,
        ConstructdOptions options, IClock clock, CancellationToken ct) =>
        Results.Ok(await DescribeAsync(certificates, config, options, clock, gateway, ct));

    private static async Task<IResult> ImportAsync(CertificateRequest? request, HttpContext http, HttpsCertificateStore certificates,
        HttpsGateway gateway, IHostConfigStore config, ConstructdOptions options, IClock clock, CancellationToken ct)
    {
        if (request is null) return CodedProblems.Validation("certificate", "A body with certificatePem and privateKeyPem, or pfx, is required.");
        HttpsCertificateInfo info;
        try
        {
            if (!string.IsNullOrWhiteSpace(request.Pfx))
            {
                byte[] pfx;
                try
                {
                    pfx = Convert.FromBase64String(request.Pfx.Trim());
                }
                catch (FormatException)
                {
                    return CodedProblems.Validation("pfx", "The PFX must be base64.");
                }

                info = certificates.ImportPfx(pfx, request.Password);
            }
            else
            {
                info = certificates.Import(request.CertificatePem ?? string.Empty, request.PrivateKeyPem ?? string.Empty);
            }
        }
        catch (HttpsCertificateException ex)
        {
            http.SetAuditDetail("refused");
            return CodedProblems.Create(400, "invalid-certificate", ex.Message);
        }

        http.SetAuditDetail($"sha256={info.Sha256}, notAfter={info.NotAfter:u}");
        await gateway.ReconcileSharedAsync(ct);
        return Results.Ok(await DescribeAsync(certificates, config, options, clock, gateway, ct));
    }

    private static async Task<IResult> RemoveAsync(HttpContext http, HttpsCertificateStore certificates, HttpsGateway gateway,
        IHostConfigStore config, ConstructdOptions options, IClock clock, CancellationToken ct)
    {
        http.SetAuditDetail(certificates.Remove() ? "removed" : "none installed");
        await gateway.ReconcileSharedAsync(ct);
        return Results.Ok(await DescribeAsync(certificates, config, options, clock, gateway, ct));
    }

    /// <summary>The state an admin needs: the certificate, which advertised names it covers, the listeners and what to fix.</summary>
    internal static async Task<HttpsStatusResponse> DescribeAsync(HttpsCertificateStore certificates, IHostConfigStore config, ConstructdOptions options,
        IClock clock, HttpsGateway? gateway, CancellationToken ct)
    {
        var current = certificates.Current;
        var https = await config.GetAsync<HttpsConfig>("https", ct) ?? HostAdminDefaults.Https;
        var vault = await config.GetAsync<VaultConfig>("vault", ct);
        var (vaultUrl, _) = VaultWebAddress.Resolve(vault?.WebUrl, options);
        var vaultHost = Uri.TryCreate(vaultUrl, UriKind.Absolute, out var vaultUri) && vaultUri.Scheme == Uri.UriSchemeHttps ? vaultUri.IdnHost : null;
        var pattern = string.IsNullOrWhiteSpace(options.PublicHostPattern) ? null : options.PublicHostPattern.Trim();
        var example = options.PublicHostFor("example-vm");

        var warnings = new List<string>();
        if (certificates.LoadError is { } loadError) warnings.Add($"The installed certificate cannot be loaded: {loadError}");
        if (current is not null)
        {
            var days = (current.Info.NotAfter - clock.UtcNow).TotalDays;
            if (days < 0) warnings.Add("The certificate has expired.");
            else if (days < 14) warnings.Add($"The certificate expires in {Math.Floor(days)} days.");
            if (!current.Covers(example))
                warnings.Add($"The certificate does not cover the names VMs are advertised under (for example {example}); browsers will refuse HTTPS forwards.");
            if (https.SharedPort is not null && vaultHost is not null && !current.Covers(vaultHost))
                warnings.Add($"The certificate does not cover the vault web address's host {vaultHost}.");
        }

        if (https.SharedPort is not null && pattern is null)
            warnings.Add("Main forwards are only served on the shared port when Constructd:PublicHostPattern gives every VM its own name.");

        return new HttpsStatusResponse(
            current?.Info,
            certificates.Directory,
            https,
            new HttpsNamesResponse(
                options.PublicHost,
                current?.Covers(options.PublicHost),
                pattern,
                example,
                current?.Covers(example),
                vaultHost,
                vaultHost is null ? null : current?.Covers(vaultHost)),
            gateway?.Listeners() ?? [],
            warnings);
    }
}
