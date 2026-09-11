using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;
using Construct.Companion.Host.ConfigSync;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Host.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace Construct.Companion.Host.Composition;

public static class CompanionComposition
{
    // The Windows app (or AddCompanionFakes) registers its platform seams before this; TryAdd keeps them.
    // runtimeJobs=false is the diagnostic host of --selftest: routes only, no probes, no tunnels.
    public static IServiceCollection AddCompanionHost(this IServiceCollection services, bool runtimeJobs = true)
    {
        services.AddRuntime().AddConfigSync();
        // per-instance connections
        services.TryAddSingleton<IInstanceConnections, InstanceConnections>();
        services.TryAddSingleton<CompanionInstances>();
        services.TryAddSingleton<CachedUpdateSource>();
        services.TryAddSingleton(p => new RuntimeSupervisor(p.GetRequiredService<CompanionInstances>(), p.GetRequiredService<CompanionInstances>().CreateRuntime,
            p.GetRequiredService<RuntimeMessageBus>(), p.GetRequiredService<IClock>(), p.GetRequiredService<CompanionInstances>().AcquireRetargetAsync));
        // dispatch and state
        services.TryAddSingleton<IpcEvents>();
        services.TryAddSingleton<IpcSettings>();
        services.TryAddSingleton<IpcLogs>();
        services.TryAddSingleton<StateAggregation>();
        services.TryAddSingleton<RemoteVmWizard>();
        services.TryAddSingleton<HostConversionWorkflow>();
        services.TryAddSingleton<HostAdministration>();
        services.TryAddSingleton<MessageDispatcher>();
        services.TryAddSingleton<CompanionBackend>();
        services.TryAddSingleton<IMessageSink, DispatcherMessageSink>();
        services.TryAddSingleton<DispatchQueue>();
        services.AddHostedService(p => p.GetRequiredService<DispatchQueue>());
        // background jobs
        if (runtimeJobs)
        {
            services.AddHostedService<CompanionRuntimeService>();
            services.AddHostedService<CompanionEnrichmentService>();
        }
        return services;
    }
}
