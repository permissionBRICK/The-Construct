using System.Text;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Lifecycle;
using Construct.Companion.Host.Composition;
using Construct.Companion.Host.Ipc;
namespace Construct.Companion.Host.Dispatch;

// The host-wide lifecycle run every panel shows, whichever instance it is subscribed to: the launch this
// Companion is preparing, or the run whose script holds the marker (a console, another surface, the VS Code
// panel). Published to every instance scope when it changes, so a page that switched instances while its
// own launch was preparing still sees it end.
public sealed class LifecycleRuns(CompanionInstances instances, IpcEvents events, IStateFileSystem files, IProcessLiveness processes, IClock clock)
{
    private readonly object gate = new();
    private JsonObject? preparing;
    private string published = "null";
    public JsonObject? Current()
    {
        JsonObject? running = null;
        if (instances.Host.LocalAppData is { } root)
        {
            try { running = LifecycleRun.Parse(files.ReadFile(LifecycleRun.MarkerPath(root)) is { } bytes ? Encoding.UTF8.GetString(bytes) : null, processes.ProcessIsDefinitelyDead); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        lock (gate) return running ?? preparing?.DeepClone().AsObject();
    }
    // Claims the preparing phase of one launch (its checks, prompts and the launch itself); null with the reason
    // while another run holds the host. Disposed at launch: from then on the run's own marker shows it, so a
    // console closed without a result unlocks every panel at once. Disposing twice, or late, ends only this claim.
    public IDisposable? TryBegin(string? instance, string action, out string reason)
    {
        JsonObject claim;
        lock (gate)
        {
            if (Current() is { } busy) { reason = LifecycleRun.BusyReason(busy); return null; }
            preparing = claim = LifecycleRun.Describe(instance, action, "preparing");
        }
        reason = ""; Publish(); return new Claim(this, claim);
    }
    private void End(JsonObject claim) { lock (gate) { if (!ReferenceEquals(preparing, claim)) return; preparing = null; } Publish(); }
    private sealed class Claim(LifecycleRuns runs, JsonObject claim) : IDisposable { public void Dispose() => runs.End(claim); }
    public void Publish()
    {
        lock (gate)
        {
            var run = Current(); var text = run?.ToJsonString() ?? "null";
            if (text == published) return;
            published = text;
            foreach (var name in instances.Names) events.Message(name, new { type = "lifecycleRun", run });
        }
    }
    // A run that ends in its console (or whose console is closed) only shows in the marker.
    public async Task PollAsync(CancellationToken ct)
    {
        try { while (true) { Publish(); await clock.DelayAsync(TimeSpan.FromSeconds(2), ct); } }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
