using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.Desktop;
using Construct.Companion.Core.Ipc;
namespace Construct.Companion.Host.Ipc;

// A second process hands its command line to the running Companion over the §7 routes.
public sealed class ActivationClient(IFileSystem files, HttpClient client, IClock clock)
{
    public async Task SendAsync(string endpointPath, ActivationPlan plan, bool quit, CancellationToken cancellationToken = default)
    {
        // A secondary can beat the primary's bind/atomic endpoint write, and the file can be a
        // stale one from a crashed run: retry briefly, treating every failure as "not yet".
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (await TryOnceAsync(endpointPath, plan, quit, cancellationToken)) return;
            }
            catch (Exception e) when (e is HttpRequestException or JsonException or IOException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested) { }
            await clock.DelayAsync(TimeSpan.FromMilliseconds(250), cancellationToken);
        }
        throw new InvalidOperationException("The running Companion did not accept activation.");
    }
    private async Task<bool> TryOnceAsync(string endpointPath, ActivationPlan plan, bool quit, CancellationToken cancellationToken)
    {
        var bytes = files.ReadFile(endpointPath);
        if (bytes is null) return false;
        var endpoint = JsonSerializer.Deserialize<Endpoint>(bytes, IpcJson.Options);
        if (endpoint is not { V: 1, IpcApiVersion: 1, Port: > 0 and <= 65535 } || endpoint.Token is not { Length: 64 } token || !token.All(Uri.IsHexDigit)) return false;
        var address = new Uri($"http://127.0.0.1:{endpoint.Port}");
        var health = await client.GetFromJsonAsync<Health>(new Uri(address, "/v1/health"), IpcJson.Options, cancellationToken);
        if (health is not { Ok: true, IpcApiVersion: 1 } || health.Pid != endpoint.Pid || health.StartedAt != endpoint.StartedAt) return false;
        async Task Post(string path, object body)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(address, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.Token);
            request.Content = JsonContent.Create(body, options: IpcJson.Options);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Companion activation was refused.");
        }
        if (quit) { await Post("/v1/quit", new QuitRequest("user")); return true; }
        foreach (var view in plan.Views) await Post("/v1/ui/activate", view);
        if (plan.ForwardId is not null) await Post("/v1/instances/" + Uri.EscapeDataString(plan.ForwardInstance!) + "/messages", new { type = "command", id = "openForward", forward = plan.ForwardId });
        return true;
    }
}
