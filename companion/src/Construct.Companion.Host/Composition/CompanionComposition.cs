using Construct.Companion.Core.Abstractions;
using Construct.Companion.Host.ConfigSync;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Ipc;
using Construct.Companion.Host.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
namespace Construct.Companion.Host.Composition;

public static class CompanionComposition
{
    // The Windows app registers its platform seams before calling this method.
    public static IServiceCollection AddCompanionHost(this IServiceCollection services)
    {
        services.AddRuntime().AddConfigSync();
        services.TryAddSingleton(new CompanionProcessEnvironment((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator), Environment.GetEnvironmentVariable("SystemRoot"), OperatingSystem.IsWindows()));
        services.TryAddSingleton<IInstanceConnections, InstanceConnections>();
        services.TryAddSingleton<IpcEvents>(); services.TryAddSingleton<IpcSettings>(); services.TryAddSingleton<IpcLogs>();
        services.TryAddSingleton<CompanionInstances>(); services.TryAddSingleton<StateAggregation>();
        services.TryAddSingleton<HostAdministration>(); services.TryAddSingleton<MessageDispatcher>();
        services.TryAddSingleton<DispatchQueue>(); services.AddHostedService(p => p.GetRequiredService<DispatchQueue>());
        services.TryAddSingleton<IIpcBackend, CompanionBackend>();
        services.TryAddSingleton(p => new RuntimeSupervisor(p.GetRequiredService<CompanionInstances>(), p.GetRequiredService<CompanionInstances>().CreateRuntime, p.GetRequiredService<RuntimeMessageBus>(), p.GetRequiredService<CompanionInstances>().AcquireRetargetAsync, p.GetRequiredService<IClock>()));
        services.AddHostedService<CompanionRuntimeService>(); return services;
    }
}
internal sealed class CompanionRuntimeService(RuntimeMessageBus bus, StateAggregation aggregation, RuntimeSupervisor supervisor, HostAdministration hosts) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = bus.Subscribe();
        var relay = Relay();
        try { await supervisor.StartAsync(stoppingToken); await Task.WhenAll(relay, hosts.PollAsync(stoppingToken)); }
        finally { await supervisor.DisposeAsync(); }
        async Task Relay()
        { try { await foreach (var message in subscription.ReadAsync(stoppingToken)) aggregation.Forward(message); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { } }
    }
}
