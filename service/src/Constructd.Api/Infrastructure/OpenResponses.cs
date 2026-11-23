namespace Constructd.Api.Infrastructure;

/// <summary>
/// Responses that stay open: event streams and long-polls. When the engine stops, Kestrel waits for open
/// requests and cuts them only at the host's shutdown timeout, so these end as soon as the stop begins.
/// </summary>
public static class OpenResponses
{
    /// <summary>The engine's stop, for a wait that should end early with whatever it has.</summary>
    public static CancellationToken Stopping(HttpContext http) =>
        http.RequestServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;

    /// <summary>Cancelled when the client goes away or the engine stops.</summary>
    public static CancellationTokenSource UntilStopping(HttpContext http) =>
        CancellationTokenSource.CreateLinkedTokenSource(http.RequestAborted, Stopping(http));
}
