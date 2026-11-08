using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Https;

/// <summary>Whether one VM may publish HTTPS forwards right now, and why not.</summary>
/// <param name="Reason">Null when available; otherwise a stable code (see <see cref="HttpsAvailability"/>).</param>
/// <param name="Message">What an owner or admin would change, null when available.</param>
/// <param name="MainAddress">The address a main forward gets on the shared port, when there is one.</param>
/// <param name="NamedAddress">
/// What a forward's own address on the shared port looks like, with <c>&lt;name&gt;</c> standing for its name
/// (<c>https://work-vm-&lt;name&gt;.vms.example/</c>), when forwards are served there by name.
/// </param>
public sealed record HttpsAvailabilityResult(bool Available, string? Reason, string? Message, string? MainAddress, string? NamedAddress = null);

/// <summary>
/// THE ONE RULE FOR "MAY THIS VM PUBLISH AN HTTPS FORWARD" (docs/https-forwards.md). Every condition has to
/// hold, so a guest is never offered an option that cannot give its user a working link:
/// <list type="number">
/// <item>a primary VM that is not being deleted;</item>
/// <item>the host has a certificate that has not expired and covers the VM's public name;</item>
/// <item>host forwards are enabled on the host;</item>
/// <item>the VM's owner has the separate HTTPS-forward permission (off by default).</item>
/// </list>
/// <c>construct expose --help</c> asks this through <c>GET /vms/{name}/https</c> and only then shows the
/// option; the forward route refuses with the same reason.
/// </summary>
public static class HttpsAvailability
{
    public static async Task<HttpsAvailabilityResult> CheckAsync(Vm vm, IServiceProvider services, CancellationToken ct)
    {
        var options = services.GetRequiredService<ConstructdOptions>();
        if (vm.Kind != VmKind.Primary || vm.Deleting)
            return No("primary-only", "HTTPS forwards are for primary VMs.");

        var certificate = services.GetRequiredService<HttpsCertificateStore>().Current;
        var clock = services.GetRequiredService<IClock>();
        var publicHost = options.PublicHostFor(vm.Name);
        if (certificate is null)
            return No("no-certificate", "This host has no HTTPS certificate.");
        if (certificate.Info.NotAfter <= clock.UtcNow)
            return No("certificate-expired", "The host's HTTPS certificate has expired.");
        if (!certificate.Covers(publicHost))
            return No("certificate-name", $"The host's HTTPS certificate does not cover {publicHost}.");

        var config = services.GetRequiredService<IHostConfigStore>();
        if ((await config.GetAsync<NetworkConfig>("network", ct).ConfigureAwait(false)) is { HostForwardsEnabled: false })
            return No("host-forwards-disabled", "Host forwards are disabled on this host.");

        var owner = await services.GetRequiredService<IUserStore>().GetAsync(vm.Owner, ct).ConfigureAwait(false);
        if (owner is not { Enabled: true, AllowHttpsForwards: true })
            return No("not-allowed", $"HTTPS forwards are not enabled for '{vm.Owner}'. A host admin allows them per user.");

        string? mainAddress = null;
        var https = await config.GetAsync<HttpsConfig>("https", ct).ConfigureAwait(false) ?? HostAdminDefaults.Https;
        string? namedAddress = null;
        if (https.SharedPort is int shared && !string.IsNullOrWhiteSpace(options.PublicHostPattern))
        {
            var port = shared == 443 ? "" : $":{shared}";
            mainAddress = $"https://{publicHost}{port}/";
            // Rendered with a real name so the pattern and the certificate are checked, then shown with a placeholder.
            const string probe = "name";
            if (HttpsForwardNames.Host(options.PublicHostPattern, options.PublicHost, vm.Name, probe) is { } named && certificate.Covers(named))
            {
                var label = HttpsForwardNames.Label(vm.Name, probe);
                namedAddress = $"https://{named.Replace(label, HttpsForwardNames.Label(vm.Name, "<name>"), StringComparison.OrdinalIgnoreCase)}{port}/";
            }
        }

        return new(true, null, null, mainAddress, namedAddress);
    }

    private static HttpsAvailabilityResult No(string reason, string message) => new(false, reason, message, null);
}
