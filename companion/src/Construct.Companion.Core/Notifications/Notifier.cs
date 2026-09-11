using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Forwards;
using Construct.Companion.Core.Runtime;

namespace Construct.Companion.Core.Notifications;

public sealed class Notifier(string instance, ISshTransport ssh, IRuntimeProcesses processes, IToastRaiser toasts, IClock clock) : IAsyncDisposable
{
    private readonly object gate = new();
    private ISupervisedProcess? watch;
    private string buffer = "";
    private bool disposed;
    public void Start()
    {
        lock (gate)
        {
            if (disposed || watch is not null) return;
            watch = processes.Start(ct => ssh.SpawnWatch(NotificationProtocol.WatchScript(), ct), new(TimeSpan.Zero, TimeSpan.FromSeconds(150)),
                state => { if (state.State == "starting") buffer = ""; }, ConsumeAsync);
        }
    }
    private async Task ConsumeAsync(string chunk, CancellationToken token)
    {
        var (lines, rest) = ForwardProtocol.SplitLines(buffer, chunk, 65536); buffer = rest;
        var selected = NotificationProtocol.SelectDeliverable(NotificationProtocol.ParseEntries(string.Join('\n', lines)), clock.UtcNow.ToUnixTimeMilliseconds());
        foreach (var entry in selected.Array("deliver").OfType<JsonObject>()) await DeliverAsync(entry, token).ConfigureAwait(false);
        var extra = selected["extra"]!.GetValue<int>();
        if (extra > 0) await DeliverAsync(new JsonObject { ["level"] = "info", ["title"] = "The Construct",
            ["body"] = $"{extra} more notification{(extra == 1 ? "" : "s")} from the VM (see the Construct log)", ["source"] = "" }, token).ConfigureAwait(false);
    }
    private async Task DeliverAsync(JsonObject entry, CancellationToken token)
    {
        if (await toasts.GetAvailabilityAsync(token).ConfigureAwait(false) != ToastAvailability.Available) return;
        await toasts.RaiseAsync(NotificationProtocol.Toast(entry, NotificationProtocol.ClickUri(instance)), token).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        ISupervisedProcess? child;
        lock (gate) { disposed = true; child = watch; watch = null; }
        if (child is not null) await child.DisposeAsync().ConfigureAwait(false);
    }
}
