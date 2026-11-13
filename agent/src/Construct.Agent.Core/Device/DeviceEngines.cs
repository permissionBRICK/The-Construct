using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Construct.Agent.Device;

/// <summary>
/// The engine calls the device features make, with this device's credential (the proxy's pinned
/// connection and stamp). Tests replace it with a fake engine.
/// </summary>
public interface IDeviceEngines
{
    /// <summary>The engines this device knows.</summary>
    IReadOnlyList<EngineView> List();

    /// <summary>Sends to <c>/api/v1/{path}</c> of <paramref name="engine"/>; a non-success answer throws <see cref="AgentFailure"/>.</summary>
    Task<JsonNode?> SendAsync(string engine, HttpMethod method, string path, JsonNode? body, CancellationToken ct);
}

public static class DeviceEnginesExtensions
{
    public static Task<JsonNode?> GetAsync(this IDeviceEngines engines, string engine, string path, CancellationToken ct) =>
        engines.SendAsync(engine, HttpMethod.Get, path, null, ct);

    public static Task<JsonNode?> PostAsync(this IDeviceEngines engines, string engine, string path, JsonNode? body, CancellationToken ct) =>
        engines.SendAsync(engine, HttpMethod.Post, path, body ?? new JsonObject(), ct);
}

/// <summary>The real engines, through <see cref="DeviceAgent"/>'s pinned clients.</summary>
public sealed class AgentEngines(DeviceAgent agent) : IDeviceEngines
{
    public IReadOnlyList<EngineView> List() => agent.Engines();

    public async Task<JsonNode?> SendAsync(string engine, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        var http = agent.ProxyClient(engine) ?? throw new AgentFailure(404, "no-engine", "No such engine.");
        using var request = new HttpRequestMessage(method, "api/v1/" + path);
        if (body is not null) request.Content = JsonContent.Create(body);
        agent.Stamp(engine, request.Headers);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new AgentFailure(502, "engine-unreachable", "The engine did not answer.");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var code = "engine-refused";
                try { code = JsonNode.Parse(text)?["code"]?.GetValue<string>() ?? code; }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException) { }
                var status = (int)response.StatusCode switch { 401 => 403, >= 400 and < 500 and var s => s, _ => 502 };
                throw new AgentFailure(status, code, $"The engine answered {(int)response.StatusCode}.");
            }

            try { return text.Length == 0 ? null : JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException) { return null; }
        }
    }
}
