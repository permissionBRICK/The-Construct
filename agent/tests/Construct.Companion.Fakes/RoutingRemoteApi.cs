using System.Text.Json;
using Construct.Companion.Core.Abstractions;
namespace Construct.Companion.Fakes;
public sealed class RoutingRemoteApi : IRemoteApi
{
    public string Fingerprint { get; set; } = new('a', 64);
    public List<RemoteRequest> Requests { get; } = [];
    public Func<RemoteRequest, RemoteResponse> Handle { get; set; } = request =>
    {
        object body = request.Url.AbsolutePath switch
        {
            "/api/v1/health" => new { apiFeatures = new[] { "host-admin", "children", "media", "updates" } },
            "/api/v1/whoami" => new { name = "alice", role = "admin", known = true, enabled = true },
            "/api/v1/vms" or "/api/v1/users" or "/api/v1/jobs" or "/api/v1/audit" or "/api/v1/media" => Array.Empty<object>(),
            _ => new { }
        };
        return new(200, JsonSerializer.SerializeToElement(body));
    };
    public Task<RemoteResponse> SendAsync(RemoteRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); if (!request.VerifyPin(Fingerprint)) throw new InvalidOperationException("Certificate pin rejected.");
        lock (Requests) { Requests.Add(request); return Task.FromResult(Handle(request)); }
    }
}
