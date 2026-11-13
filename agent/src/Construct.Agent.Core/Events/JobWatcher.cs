using System.Collections.Concurrent;
using System.Net;
using System.Text.Json.Nodes;

namespace Construct.Agent.Events;

/// <summary>
/// Follows an engine job's event stream (<c>GET /api/v1/jobs/{id}/events</c>) and repeats it on the
/// agent's stream. The engine replays every progress line on each attach and ends with exactly one
/// <c>state</c> event, so after a dropped connection the watcher reattaches, skips the lines it has
/// already passed on, and publishes the terminal state once.
/// </summary>
public sealed class JobWatcher(AgentEvents events, Func<string, HttpClient?> engineClient, CancellationToken stopping)
{
    private readonly ConcurrentDictionary<(string Engine, string Job), Task> _watching = new();

    /// <summary>How long to wait before reattaching after a drop.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Starts following the job unless it is followed already; returns the task that ends with it.</summary>
    public Task Watch(string engine, string jobId) =>
        _watching.GetOrAdd((engine, jobId), key => Task.Run(async () =>
        {
            try { await FollowAsync(key.Engine, key.Job).ConfigureAwait(false); }
            finally { _watching.TryRemove(key, out _); }
        }));

    private async Task FollowAsync(string engine, string jobId)
    {
        var passed = 0;
        string? lastPhase = null;
        while (!stopping.IsCancellationRequested)
        {
            var client = engineClient(engine);
            if (client is null) return;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"api/v1/jobs/{Uri.EscapeDataString(jobId)}/events");
                request.Headers.Accept.ParseAdd("text/event-stream");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stopping).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
                {
                    events.Publish("job.lost", engine, new JsonObject { ["jobId"] = jobId, ["status"] = (int)response.StatusCode });
                    return;
                }

                response.EnsureSuccessStatusCode();
                using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(stopping).ConfigureAwait(false));
                var index = 0;
                string? name = null;
                var data = new System.Text.StringBuilder();
                while (await reader.ReadLineAsync(stopping).ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 0)
                    {
                        if (line.StartsWith("event:", StringComparison.Ordinal)) name = line[6..].Trim();
                        else if (line.StartsWith("data:", StringComparison.Ordinal)) data.Append(data.Length > 0 ? "\n" : "").Append(line[5..].TrimStart());
                        continue;
                    }

                    var payload = data.Length > 0 ? JsonNode.Parse(data.ToString()) : null;
                    switch (name)
                    {
                        case "progress":
                            if (++index > passed)
                            {
                                passed = index;
                                events.Publish("job.progress", engine, new JsonObject { ["jobId"] = jobId, ["line"] = payload });
                            }

                            break;
                        case "phase":
                            var phase = payload?.ToJsonString();
                            if (phase != lastPhase)
                            {
                                lastPhase = phase;
                                events.Publish("job.phase", engine, new JsonObject { ["jobId"] = jobId, ["phase"] = payload });
                            }

                            break;
                        case "state":
                            events.Publish("job.state", engine, new JsonObject { ["jobId"] = jobId, ["job"] = payload });
                            return;
                    }

                    name = null;
                    data.Clear();
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or System.Text.Json.JsonException or TaskCanceledException)
            {
                // Reattach below; the engine replays the lines and the state comes once.
            }

            try { await Task.Delay(RetryDelay, stopping).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
