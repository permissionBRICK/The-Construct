using Microsoft.Extensions.Hosting;
namespace Construct.Companion.Host.Ipc;

// Accepted messages belong to the process, not the HTTP connection that submitted them.
public sealed class DispatchQueue(IHostApplicationLifetime lifetime) : IHostedService
{
    private readonly object gate = new();
    private readonly Dictionary<string, Task> tails = new(StringComparer.Ordinal);
    public void Enqueue(string key, Func<CancellationToken, Task> operation, Action<Exception> failure)
    {
        lock (gate)
        {
            var previous = tails.GetValueOrDefault(key, Task.CompletedTask);
            tails[key] = Task.Run(async () =>
            {
                await previous;
                try { lifetime.ApplicationStopping.ThrowIfCancellationRequested(); await operation(lifetime.ApplicationStopping); }
                catch (OperationCanceledException) when (lifetime.ApplicationStopping.IsCancellationRequested) { }
                catch (Exception e) { failure(e); }
            });
        }
    }
    public Task DrainAsync() { lock (gate) return Task.WhenAll(tails.Values); }
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => DrainAsync().WaitAsync(cancellationToken);
}
