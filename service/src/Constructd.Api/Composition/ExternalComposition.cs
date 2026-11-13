using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Proxmox;
using Constructd.Windows.Forwards;
using Constructd.Windows.Iso;

namespace Constructd.Api.Composition;

/// <summary>
/// The external-endpoint platform (<c>Constructd:Backend = external</c>, design 7.2): VMs that exist
/// already and are reachable over SSH (<see cref="ExternalEndpointDriver"/>). It runs on any OS:
/// <list type="bullet">
/// <item><c>IHypervisorDriver</c> → <see cref="ExternalEndpointDriver"/> (register, unregister, probe SSH; no power),</item>
/// <item><c>IIsoBuilder</c> → no install media,</item>
/// <item><c>IPortForwardManager</c> → the in-process TCP relays the Proxmox platform uses, which reach the
/// VM at the endpoint the driver reports.</item>
/// </list>
/// Children, consoles, media, host updates and capacity inventory are unsupported here.
/// </summary>
public static class ExternalComposition
{
    public static IServiceCollection AddExternalPlatform(this IServiceCollection services, ConstructdOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (ExternalEndpointDriver.Validate(options.External) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        services.AddSingleton<IHostAddressResolver, DnsHostAddressResolver>();

        services.AddSingleton<ExternalEndpointDriver>();
        services.AddSingleton<IHypervisorDriver>(sp => sp.GetRequiredService<ExternalEndpointDriver>());
        services.AddSingleton<IVmCpuDriver>(sp => sp.GetRequiredService<ExternalEndpointDriver>());
        services.AddSingleton<IVmMemoryDriver>(sp => sp.GetRequiredService<ExternalEndpointDriver>());

        services.AddSingleton<IIsoBuilder, NoInstallMedia>();
        services.AddSingleton<IIsoFileSystem, IsoFileSystem>();
        services.AddSingleton<IIsoCatalog, UnsupportedIsoCatalog>();

        services.AddSingleton<TcpRelayPortForwardManager>();
        services.AddSingleton<IPortForwardManager>(sp => sp.GetRequiredService<TcpRelayPortForwardManager>());

        services.AddSingleton<IHostPowerGuard, NullHostPowerGuard>();
        return services;
    }
}
