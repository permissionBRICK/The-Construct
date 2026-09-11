using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Runtime;
using Microsoft.Extensions.Hosting;
namespace Construct.Companion.Host.Composition;

// Runs the per-instance runtimes and relays their bus into the aggregated IPC event stream.
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
