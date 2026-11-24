namespace Construct.Agent.Engines;

/// <summary>
/// One pinned handler per engine (<see cref="EngineHttp.Pinned"/>), shared by every client the agent makes to it, so its
/// calls reuse pooled connections instead of paying DNS, TCP and TLS each time. A handler is kept per engine id and
/// sign-in (an engine still signed in as the Windows user has a second one that answers its challenge) and replaced
/// when the engine's address or pin changes; <see cref="Drop"/> forgets an engine's. A handler replaced or forgotten is
/// disposed after <see cref="Grace"/>, so requests already running on it end as they would have. Each client gets it
/// wrapped in a handler of its own that leaves it open when the client is disposed; credentials stay per client, around
/// that wrapper.
/// </summary>
internal sealed class EngineHandlers : IDisposable
{
    /// <summary>How long a replaced handler stays open for the requests already on it (they time out after 30 s).</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Dictionary<(string Id, bool SignIn), Pooled> _pool = [];

    private sealed class Pooled(string url, string pin, HttpMessageHandler handler)
    {
        public string Url { get; } = url;

        public string Pin { get; } = pin;

        public HttpMessageHandler Handler { get; } = handler;

        /// <summary>Connections opened (tests read it: calls share them).</summary>
        public int Connects;
    }

    /// <summary>The engine's shared handler, wrapped for one client; <paramref name="signIn"/> answers a sign-in challenge as the Windows user.</summary>
    public HttpMessageHandler For(string id, string url, string pin, IWindowsSignIn? signIn)
    {
        HttpMessageHandler? replaced = null;
        Pooled pooled;
        lock (_gate)
        {
            var key = (id, signIn is not null);
            if (!_pool.TryGetValue(key, out pooled!) || pooled.Url != url || !string.Equals(pooled.Pin, pin, StringComparison.OrdinalIgnoreCase))
            {
                replaced = pooled?.Handler;
                var handler = EngineHttp.Pinned(pin);
                pooled = new Pooled(url, pin, signIn?.Apply(handler) ?? handler);
                var counted = pooled;
                handler.ConnectCallback = (context, ct) =>
                {
                    Interlocked.Increment(ref counted.Connects);
                    return HappyEyeballs.ConnectAsync(context, ct);
                };
                _pool[key] = pooled;
            }
        }

        if (replaced is not null) Retire(replaced);
        return new Kept(pooled.Handler);
    }

    /// <summary>Forgets an engine's handlers (it was removed, added again, or enrolled in place of a sign-in).</summary>
    public void Drop(string id)
    {
        List<HttpMessageHandler> dropped;
        lock (_gate)
        {
            dropped = _pool.Where(p => p.Key.Id == id).Select(p => p.Value.Handler).ToList();
            foreach (var key in _pool.Keys.Where(k => k.Id == id).ToArray()) _pool.Remove(key);
        }

        foreach (var handler in dropped) Retire(handler);
    }

    private static void Retire(HttpMessageHandler handler) =>
        _ = Task.Delay(Grace).ContinueWith(_ => handler.Dispose(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>The engine's shared handler without sign-in and the connections it opened; null when it has none.</summary>
    internal (HttpMessageHandler Handler, int Connects)? Of(string id)
    {
        lock (_gate) return _pool.TryGetValue((id, false), out var pooled) ? (pooled.Handler, Volatile.Read(ref pooled.Connects)) : null;
    }

    public void Dispose()
    {
        List<HttpMessageHandler> all;
        lock (_gate)
        {
            all = _pool.Values.Select(p => p.Handler).ToList();
            _pool.Clear();
        }

        foreach (var handler in all) handler.Dispose();
    }

    /// <summary>Passes a client's requests to the shared handler and leaves it open when the client is disposed.</summary>
    private sealed class Kept(HttpMessageHandler shared) : DelegatingHandler(shared)
    {
        protected override void Dispose(bool disposing)
        {
            // The pool owns the shared handler.
        }
    }
}
