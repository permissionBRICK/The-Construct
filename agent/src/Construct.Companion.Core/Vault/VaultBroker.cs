using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Forwards;
namespace Construct.Companion.Core.Vault;

// The per-VM end of the key vault: one SSH watch claims `construct secret` requests from the guest
// spool, each answer goes back through vault-respond.sh with the response on stdin. Requests are
// answered concurrently, so one dialog waiting for the user never blocks a lease that is already granted.
public sealed class VaultBroker(string instance, ISshTransport ssh, IRuntimeProcesses processes, VaultService vault, string spool = VaultProtocol.SpoolDirectory) : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly CancellationTokenSource stop = new();
    private readonly List<Task> inflight = [];
    private ISupervisedProcess? watch;
    private IDisposable? attachment;
    private string buffer = "";
    private bool disposed;
    public void Start()
    {
        lock (gate)
        {
            if (disposed || watch is not null) return;
            attachment = vault.Attach(instance, ssh);
            watch = processes.Start(ct => ssh.SpawnWatch(VaultProtocol.WatchScript(spool), ct), new(TimeSpan.Zero, TimeSpan.FromSeconds(150)),
                state => { if (state.State == "starting") buffer = ""; }, ConsumeAsync);
        }
    }
    private Task ConsumeAsync(string chunk, CancellationToken token)
    {
        var (lines, rest) = ForwardProtocol.SplitLines(buffer, chunk, VaultProtocol.MaxRequestBytes); buffer = rest;
        foreach (var line in lines)
        {
            var (request, id, error) = VaultProtocol.ParseRequest(line);
            if (id is null) continue;
            lock (gate)
            {
                if (disposed) return Task.CompletedTask;
                inflight.RemoveAll(t => t.IsCompleted);
                inflight.Add(Task.Run(() => AnswerAsync(request, id, error, stop.Token)));
            }
        }
        return Task.CompletedTask;
    }
    private async Task AnswerAsync(VaultRequest? request, string id, string? error, CancellationToken token)
    {
        try
        {
            var response = request is null ? VaultProtocol.Response(id, "invalid", error ?? "Malformed request.") : await vault.HandleAsync(instance, request, token).ConfigureAwait(false);
            await ssh.RunRemoteScriptAsync(VaultProtocol.RespondScript(id, spool), TimeSpan.FromSeconds(30), token, new Secret(response.ToJsonString())).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { /* the CLI times out and says so; there is nothing to retry against a VM that went away */ }
    }
    public async ValueTask DisposeAsync()
    {
        ISupervisedProcess? child; Task[] pending;
        lock (gate) { if (disposed) return; disposed = true; child = watch; watch = null; pending = inflight.ToArray(); }
        await stop.CancelAsync().ConfigureAwait(false);
        if (child is not null) await child.DisposeAsync().ConfigureAwait(false);
        try { await Task.WhenAll(pending).ConfigureAwait(false); } catch (Exception) { }
        attachment?.Dispose(); stop.Dispose();
    }
}
