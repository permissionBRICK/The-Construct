using System.Security.Cryptography.X509Certificates;
using Constructd.Api.Admin;
using Constructd.Api.Auth;
using Constructd.Api.Composition;
using Constructd.Api.Endpoints;
using Constructd.Api.Hosting;
using Constructd.Api.Infrastructure;
using Constructd.Core.Configuration;

// ---- Admin CLI -----------------------------------------------------------------------------
// `constructd admin …` is the same executable in command-line mode: it works the stores directly,
// with no HTTP, which is how the first admin exists before anybody can authenticate. Nothing below
// runs in that mode — no listener, no jobs, no scheduler.
if (args.Length > 0 && string.Equals(args[0], AdminCli.Verb, StringComparison.OrdinalIgnoreCase))
{
    return await AdminCliHost.RunAsync(args[1..], CancellationToken.None);
}
// ---------------------------------------------------------------------------------------------

var builder = WebApplication.CreateBuilder(args);

// `constructd --fake` is shorthand for Constructd:Fake=true (local development without Hyper-V).
if (args.Any(arg => string.Equals(arg, "--fake", StringComparison.OrdinalIgnoreCase)))
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        [$"{ConstructdOptions.SectionName}:Fake"] = "true",
    });
}

var options = new ConstructdOptions();
builder.Configuration.GetSection(ConstructdOptions.SectionName).Bind(options);
builder.Services.AddSingleton(options);

// ---- Windows service hosting hook ---------------------------------------------------------
// Under the SCM this switches the lifetime to Windows-service mode; started from a console it is a
// no-op. Off Windows it is not called at all.
if (OperatingSystem.IsWindows())
{
    builder.Host.UseWindowsService();
}
// -------------------------------------------------------------------------------------------

if (!string.IsNullOrWhiteSpace(options.ListenUrl))
{
    builder.WebHost.UseUrls(options.ListenUrl);
}

ConfigureTls(builder, options);

builder.Services.ConfigureHttpJsonOptions(json =>
{
    var shared = ApiJson.Options;
    json.SerializerOptions.PropertyNamingPolicy = shared.PropertyNamingPolicy;
    json.SerializerOptions.PropertyNameCaseInsensitive = shared.PropertyNameCaseInsensitive;
    json.SerializerOptions.NumberHandling = shared.NumberHandling;
    foreach (var converter in shared.Converters)
    {
        json.SerializerOptions.Converters.Add(converter);
    }
});

builder.Services.AddProblemDetails();
builder.Services.AddConstructdServices(options);
builder.Services.AddConstructdAuthentication(options);
builder.Services.AddConstructdAuthorization();

// One loop, two responsibilities: the idle evaluation (§4.7) and the host power reconcile (§4.13)
// both ride on the same tick, so either one being wanted is reason enough to run it. Turning idle
// evaluation off must not silently take the host's power request with it.
if (options.Idle.SchedulerEnabled || options.Power.KeepHostAwake)
{
    builder.Services.AddHostedService<IdleSchedulerService>();
}

var app = builder.Build();

if (options.Fake)
{
    app.Logger.LogWarning(
        "Running in FAKE mode: in-memory stores, no Hyper-V, and the {Scheme} authentication scheme " +
        "trusts the {Header} header. Never use this on a real host.",
        ConstructdSchemes.TestIdentity,
        ConstructdHeaders.TestIdentity);
}

// Outermost on purpose, and instead of UseExceptionHandler: every request outcome — including a
// failure inside routing or inside an authentication handler, where the presented plaintext token is in
// scope — is audited and answered here, with only a safe description of the failure logged. The
// framework's own exception handler would log the exception object, whose rendered form carries its
// message, stack trace and Data with it.
app.UseMiddleware<RequestOutcomeMiddleware>();

app.UseStatusCodePages();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGroup("/api/v1")
    .MapIdentityEndpoints()
    .MapAdminEndpoints()
    .MapVmEndpoints()
    .MapForwardEndpoints()
    .MapIdleEndpoints()
    .MapJobEndpoints();

await Bootstrap.RunAsync(app.Services, CancellationToken.None);

await app.RunAsync();

return 0;

// ---- helpers --------------------------------------------------------------------------------

/// <summary>
/// TLS with the certificate created at service install; the client pins its thumbprint at enrollment
/// (plan §4.4). A thumbprint is resolved from the Windows certificate store, a path from a PFX.
/// </summary>
static void ConfigureTls(WebApplicationBuilder builder, ConstructdOptions options)
{
    if (string.IsNullOrWhiteSpace(options.CertPath) && string.IsNullOrWhiteSpace(options.CertThumbprint))
    {
        return;
    }

    builder.WebHost.ConfigureKestrel(kestrel => kestrel.ConfigureHttpsDefaults(https =>
    {
        if (!string.IsNullOrWhiteSpace(options.CertPath))
        {
            https.ServerCertificate = X509CertificateLoader.LoadPkcs12FromFile(
                options.CertPath,
                options.CertPassword);
            return;
        }

        using var store = new X509Store(StoreName.My, StoreLocation.LocalMachine);
        store.Open(OpenFlags.ReadOnly);
        var found = store.Certificates.Find(X509FindType.FindByThumbprint, options.CertThumbprint!, validOnly: false);

        https.ServerCertificate = found.Count > 0
            ? found[0]
            : throw new InvalidOperationException(
                $"No certificate with thumbprint '{options.CertThumbprint}' in LocalMachine\\My.");
    }));
}

/// <summary>Exposed so the integration tests can drive the real host through WebApplicationFactory.</summary>
public partial class Program;
