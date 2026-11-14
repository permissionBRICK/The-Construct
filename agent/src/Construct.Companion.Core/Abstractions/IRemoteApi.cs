namespace Construct.Companion.Core.Abstractions;

// Sends HTTP over an injected client using token or current-user Negotiate auth.
// VerifyPin must accept the peer fingerprint before credentials or body are sent.
public interface IRemoteApi
{
    Task<RemoteResponse> SendAsync(RemoteRequest request, CancellationToken cancellationToken = default);
}
public enum RemoteAuthentication { None, Token, Negotiate }
/// <param name="Pin">The expected certificate fingerprint when the caller pins the host; a pinned
/// request may reuse a connection that was validated against the same pin. Unpinned requests
/// (enrollment, probing) always validate on a fresh connection.</param>
public sealed record RemoteRequest(string Method, Uri Url, Func<string, bool> VerifyPin,
    System.Text.Json.JsonElement? Body = null, RemoteAuthentication Authentication = RemoteAuthentication.None,
    Secret? Token = null, string? Pin = null)
{
    public override string ToString() => "RemoteRequest";
}
public sealed record RemoteResponse(int StatusCode, System.Text.Json.JsonElement? Body = null)
{
    public override string ToString() => $"RemoteResponse {{ StatusCode = {StatusCode} }}";
}
