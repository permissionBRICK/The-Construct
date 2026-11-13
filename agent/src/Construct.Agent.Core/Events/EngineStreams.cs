using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace Construct.Agent.Events;

/// <summary>
/// Follows each engine's change stream (<c>GET /api/v1/events</c>) and repeats it on the agent's
/// stream: VM, job, backup and update changes, whoever caused them. An event carries what changed
/// and its id, as the engine sent it; the UI re-reads the thing.
///
/// A job's end reaches the UI exactly once. After a dropped connection the agent resumes from the
/// last event it passed on, so the engine repeats nothing and leaves no hole. On the first connection,
/// and when the engine has lost that point (it restarted, or the agent fell too far behind), the UI is
/// told to reload that engine (<c>engine.reset</c>) and every job the agent saw start and not end is
/// read once: an ended one is announced then, and its end is not announced again if the stream repeats it.
/// </summary>
public sealed class EngineStreams(AgentEvents events, Func<string, HttpClient?> engineClient, Func<IEnumerable<string>> engines, CancellationToken stopping)
{
    private const int EndedKept = 1000;

    private readonly ConcurrentDictionary<string, Task> _following = new(StringComparer.Ordinal);
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>How long to wait before connecting again after a drop or a refusal.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>A connection silent this long is given up (the engine sends a keepalive every 15 s).</summary>
    public TimeSpan Silence { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Follows every engine not followed yet, and wakes the ones waiting to retry (a credential may have arrived).</summary>
    public void Sync()
    {
        // One follower per engine: two Syncs at once (two changes on different threads) must not both start one.
        lock (_following)
        {
            foreach (var engine in engines())
            {
                if (_following.ContainsKey(engine)) continue;
                _following[engine] = Task.Run(async () =>
                {
                    try { await FollowAsync(engine).ConfigureAwait(false); }
                    finally { lock (_following) _following.TryRemove(engine, out _); }
                });
            }
        }

        Interlocked.Exchange(ref _wake, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    }

    /// <summary>The task following <paramref name="engine"/>, if any (tests wait on it).</summary>
    public Task? Following(string engine) => _following.GetValueOrDefault(engine);

    private async Task FollowAsync(string engine)
    {
        var state = new StreamState();
        while (!stopping.IsCancellationRequested)
        {
            var wake = _wake.Task;
            var refused = false;
            using var client = engineClient(engine);
            if (client is null) return;
            try
            {
                refused = await ReadAsync(engine, client, state).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or OperationCanceledException or InvalidOperationException)
            {
                // Connect again below, from the last event passed on.
            }

            // A refusal waits longer: it ends when a credential arrives (Sync wakes it), not by itself.
            try { await Task.WhenAny(Task.Delay(refused ? RetryDelay * 10 : RetryDelay, stopping), wake).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Reads one connection until it ends; true when the engine refused the credential.</summary>
    private async Task<bool> ReadAsync(string engine, HttpClient client, StreamState state)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/v1/events" + (state.Cursor is { } cursor ? "?since=" + cursor : ""));
        request.Headers.Accept.ParseAdd("text/event-stream");
        using var silence = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        silence.CancelAfter(Silence);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, silence.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return true;
        response.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(silence.Token).ConfigureAwait(false));
        string? name = null;
        var data = new System.Text.StringBuilder();
        while (await reader.ReadLineAsync(silence.Token).ConfigureAwait(false) is { } line)
        {
            silence.CancelAfter(Silence);
            if (line.Length > 0)
            {
                if (line.StartsWith("event:", StringComparison.Ordinal)) name = line[6..].Trim();
                else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(data.Length > 0 ? "\n" : "").Append(line[5..].TrimStart());
                continue;
            }

            if (name is not null && data.Length > 0 && JsonNode.Parse(data.ToString()) is JsonObject payload)
                await HandleAsync(engine, client, state, name, payload).ConfigureAwait(false);
            name = null;
            data.Clear();
        }

        return false;
    }

    private async Task HandleAsync(string engine, HttpClient client, StreamState state, string type, JsonObject payload)
    {
        var seq = payload["seq"]?.GetValue<long>() ?? throw new System.Text.Json.JsonException("An engine event without seq.");
        var id = payload["id"]?.GetValue<string>();
        if (type == "reset")
        {
            // From here on nothing is missed; what happened before is for the UI to read again.
            events.Publish("engine.reset", engine);
            // Throws on a failure, so the cursor stays and the next connection resets and reads again.
            foreach (var job in state.Open.ToArray()) await RecheckAsync(engine, client, state, job).ConfigureAwait(false);
            state.Cursor = seq;
            return;
        }

        state.Cursor = seq;
        if (id is null) return;
        switch (type)
        {
            case "job.ended":
                End(engine, state, id, "job.ended", payload["vm"]?.GetValue<string>());
                return;
            case "job.started" or "job.progress":
                if (state.Ended.Contains(id)) return;
                state.Open.Add(id);
                break;
        }

        events.Publish(type, engine, Data(id, payload["vm"]?.GetValue<string>()));
    }

    /// <summary>Reads a job the agent saw start, after events about it may have been lost.</summary>
    private async Task RecheckAsync(string engine, HttpClient client, StreamState state, string job)
    {
        using var response = await client.GetAsync($"api/v1/jobs/{Uri.EscapeDataString(job)}", stopping).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            End(engine, state, job, "job.lost", null);
            return;
        }

        response.EnsureSuccessStatusCode();
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(stopping).ConfigureAwait(false));
        if (body?["state"]?.GetValue<string>() is "succeeded" or "failed" or "cancelled")
            End(engine, state, job, "job.ended", body["vmName"]?.GetValue<string>());
    }

    /// <summary>Announces a job's end unless it was announced already.</summary>
    private void End(string engine, StreamState state, string job, string type, string? vm)
    {
        if (!state.Ended.Add(job)) return;
        state.EndedOrder.Enqueue(job);
        if (state.EndedOrder.Count > EndedKept) state.Ended.Remove(state.EndedOrder.Dequeue());
        state.Open.Remove(job);
        events.Publish(type, engine, Data(job, vm));
    }

    private static JsonObject Data(string id, string? vm)
    {
        var data = new JsonObject { ["id"] = id };
        if (vm is not null) data["vm"] = vm;
        return data;
    }

    /// <summary>One engine's stream, kept across reconnects; touched only by its own follower.</summary>
    private sealed class StreamState
    {
        public long? Cursor { get; set; }

        public HashSet<string> Open { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Ended { get; } = new(StringComparer.Ordinal);

        public Queue<string> EndedOrder { get; } = new();
    }
}
