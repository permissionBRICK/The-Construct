using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Proxmox;
using Constructd.Windows.Forwards;
using Constructd.Windows.Iso;
using Constructd.Windows.Process;

namespace Constructd.Api.Composition;

/// <summary>
/// The Proxmox VE platform (<c>Constructd:Backend = proxmox</c>): the service runs on the node.
/// <list type="bullet">
/// <item><c>IHypervisorDriver</c> → <c>qm</c>/<c>pvesh</c> through <see cref="IProcessRunner"/>,</item>
/// <item><c>IIsoBuilder</c> → a per-VM cloud-init seed snippet instead of an ISO,</item>
/// <item><c>IPortForwardManager</c> → in-process TCP relays with their own connection count.</item>
/// </list>
/// Feature compositions register child VMs, media, QMP console, guest addresses and systemd updates.
/// Network isolation and the VMConnect browser gateway remain unsupported.
/// </summary>
public static class ProxmoxComposition
{
    public static IServiceCollection AddProxmoxPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateProxmoxOptions(options);

        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IHostAddressResolver, DnsHostAddressResolver>();

        services.AddSingleton<CloudInitSeedBuilder>();
        services.AddSingleton<IIsoBuilder>(sp => sp.GetRequiredService<CloudInitSeedBuilder>());
        services.AddSingleton<IProxmoxSeedFiles>(sp => sp.GetRequiredService<CloudInitSeedBuilder>());

        services.AddSingleton<ProxmoxDriver>();
        services.AddSingleton<IHypervisorDriver>(sp => sp.GetRequiredService<ProxmoxDriver>());
        services.AddSingleton<IVmCpuDriver>(sp => sp.GetRequiredService<ProxmoxDriver>());
        services.AddSingleton<IVmMemoryDriver>(sp => sp.GetRequiredService<ProxmoxDriver>());

        services.AddSingleton<TcpRelayPortForwardManager>();
        services.AddSingleton<IPortForwardManager>(sp => sp.GetRequiredService<TcpRelayPortForwardManager>());

        // No ISO catalog: the routes and `admin iso status` answer "nothing published".
        services.AddSingleton<IIsoFileSystem, IsoFileSystem>();
        services.AddSingleton<IIsoCatalog, UnsupportedIsoCatalog>();

        // A Proxmox node does not sleep under its guests; nothing to hold.
        services.AddSingleton<IHostPowerGuard, NullHostPowerGuard>();

        return services;
    }

    /// <summary>
    /// Fails at startup on what would otherwise fail twenty seconds into the first VM creation.
    /// </summary>
    private static void ValidateProxmoxOptions(ConstructdOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ScriptsDir))
        {
            throw new InvalidOperationException(
                "Constructd:ScriptsDir is not set. It must point at the Construct checkout the service " +
                "serves to guests (the one holding bin/ and keys/).");
        }

        var provision = Path.Combine(options.ScriptsDir, "bin", "provision.sh");
        if (!File.Exists(provision))
        {
            throw new InvalidOperationException(
                $"Constructd:ScriptsDir does not look like a Construct checkout: {provision} is missing.");
        }

        if (string.IsNullOrWhiteSpace(options.Iso.BootstrapPublicKeyPath) || !File.Exists(options.Iso.BootstrapPublicKeyPath))
        {
            throw new InvalidOperationException(
                "Constructd:Iso:BootstrapPublicKeyPath must name the bootstrap public key " +
                "(keys/bootstrap_ed25519.pub in the checkout) that every guest is seeded with.");
        }

        foreach (var (value, name) in new[]
                 {
                     (options.Proxmox.Storage, "Constructd:Proxmox:Storage"),
                     (options.Proxmox.MediaStorage, "Constructd:Proxmox:MediaStorage"),
                     (options.Proxmox.ImageVolume, "Constructd:Proxmox:ImageVolume"),
                     (options.Proxmox.SnippetStorage, "Constructd:Proxmox:SnippetStorage"),
                     (options.Proxmox.SnippetDir, "Constructd:Proxmox:SnippetDir"),
                     (options.Proxmox.Bridge, "Constructd:Proxmox:Bridge"),
                 })
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException($"{name} is not set.");
            }
        }

        if (!options.Proxmox.ImageVolume.Contains(":import/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Constructd:Proxmox:ImageVolume must be an import volume id (<storage>:import/<image>.qcow2), " +
                "the cloud image install-construct-host.sh cached on the node.");
        }
    }
}
