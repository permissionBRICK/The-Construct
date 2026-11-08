using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Runtime;
using Microsoft.Extensions.Hosting;
namespace Construct.Companion.Host.Composition;

// Runs the per-instance runtimes and relays their bus into the aggregated IPC event stream; also the
// host-admin poll, the lifecycle-run marker poll and the config-backup status poll (BackupWarnings).
internal sealed class CompanionRuntimeService(RuntimeMessageBus bus, StateAggregation aggregation, RuntimeSupervisor supervisor, HostAdministration hosts, LifecycleRuns runs, BackupWarnings backups) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = bus.Subscribe();
        var relay = Relay();
        try { await supervisor.StartAsync(stoppingToken); await Task.WhenAll(relay, hosts.PollAsync(stoppingToken), runs.PollAsync(stoppingToken), backups.PollAsync(stoppingToken)); }
        finally { await supervisor.DisposeAsync(); }
        async Task Relay()
        { try { await foreach (var message in subscription.ReadAsync(stoppingToken)) aggregation.Forward(message); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { } }
    }
}
