using Construct.Companion.Core.Abstractions;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Ipc;
using Microsoft.Extensions.Hosting;
namespace Construct.Companion.Host.Composition;

internal sealed class CompanionEnrichmentService(CompanionInstances instances, MessageDispatcher dispatcher,
    IpcEvents events, IpcSettings settings, IpcLogs logs, IClock clock) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        string[] previous = [];
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var names = instances.Names;
                if (!names.SequenceEqual(previous))
                {
                    previous = names;
                    if (!names.Contains(settings.Read().ActiveInstance)) settings.Merge(new() { ["activeInstance"] = names.FirstOrDefault() });
                    events.Companion(new { type = "instances", instances = names });
                }
                await clock.DelayAsync(TimeSpan.FromSeconds(30), stoppingToken);
                foreach (var name in instances.Names)
                {
                    var entry = instances.Get(name);
                    if (!await entry.Serial.WaitAsync(0, stoppingToken)) continue;
                    try
                    {
                        if (entry.Runtime is null) continue;
                        if (entry.ConfigSync is {} area) await area.Runtime.TickAutoAsync(stoppingToken);
                        await dispatcher.RefreshAsync(entry, stoppingToken, probe:false, collectUsage:false);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception error) { logs.Failure("Instance refresh", error); }
                    finally { entry.Serial.Release(); }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
