using Constructd.Api.Repair;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Services;
using Constructd.Fakes;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Constructd.Api.Composition;

/// <summary>
/// The repair agent (design 11.5): its sessions, the trigger on failed jobs, and the VM operation gate that
/// ends a VM's repair before a job takes the VM. The guest connection is the system <c>ssh</c>; fake mode has a
/// scripted agent (<see cref="FakeGuestSessionRunner"/>) that asks once and then fixes. Registered after the host administration core, whose gate
/// it wraps.
/// </summary>
public static class RepairComposition
{
    public static IServiceCollection AddRepairAgent(this IServiceCollection services, ConstructdOptions options)
    {
        if (options.Fake)
        {
            services.AddSingleton<FakeGuestSessionRunner>();
            services.AddSingleton<IGuestSessionRunner>(sp => sp.GetRequiredService<FakeGuestSessionRunner>());
        }
        else services.AddSingleton<IGuestSessionRunner>(_ => new SshGuestSessionRunner(new Constructd.Windows.Process.StreamingProcessRunner()));
        services.AddSingleton<InMemoryVmOperationGate>();
        services.AddSingleton<RepairSessions>();
        services.Replace(ServiceDescriptor.Singleton<IVmOperationGate>(sp =>
            new RepairAwareVmOperationGate(sp.GetRequiredService<InMemoryVmOperationGate>(), sp.GetRequiredService<RepairSessions>())));
        services.AddHostedService<RepairTrigger>();
        return services;
    }
}
