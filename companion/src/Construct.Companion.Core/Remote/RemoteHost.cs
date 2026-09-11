using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Core.State;

namespace Construct.Companion.Core.Remote;

public static class RemoteHost
{
    public static string NormalizeServiceUrl(string? value)
    {
        var raw = StateJson.Trim(value ?? ""); if (raw.Length == 0) throw new ArgumentException("No service URL given.");
        if (!Regex.IsMatch(raw, @"^[A-Za-z][A-Za-z0-9+.-]*://")) raw = "https://" + raw;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) || uri.Host.Length == 0 || uri.Scheme is not ("https" or "http")) throw new ArgumentException("Not a usable service URL; expected an HTTP or HTTPS host.");
        var host = uri.IdnHost.ToLowerInvariant().Trim('[', ']'); if (host.Contains(':')) host = "[" + host + "]";
        return $"{uri.Scheme}://{host}:{(uri.IsDefaultPort ? 7462 : uri.Port)}";
    }
    public static string HostSlug(string url)
    {
        var uri = new Uri(NormalizeServiceUrl(url)); return Regex.Replace(uri.Host.Trim('[', ']').ToLowerInvariant() + "_" + uri.Port, "[^a-z0-9._-]", "_");
    }
    public static string? PinPath(IFileSystem files, string url)
    {
        var root = files.GetRoot(FileSystemRoot.LocalAppData); if (string.IsNullOrEmpty(root)) root = files.GetRoot(FileSystemRoot.Temp);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "The-Construct", "remote", HostSlug(url) + ".pin");
    }
    public static string FormatFingerprint(string? value)
    {
        var hex = Regex.Replace(value ?? "", "[^0-9A-Fa-f]", "").ToUpperInvariant(); return hex.Length == 64 ? string.Join(":", Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2))) : "";
    }
    public static bool FingerprintsMatch(string? expected, string? actual) => FormatFingerprint(expected) is { Length: > 0 } pin && pin == FormatFingerprint(actual);
    public static bool IsLoopbackHost(string host)
    {
        host = StateJson.Trim(host.Trim('[', ']')).ToLowerInvariant(); if (host == "localhost") return true;
        if (!IPAddress.TryParse(host, out var address)) return false;
        return address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? address.GetAddressBytes()[0] == 127 : host is "::1" or "::ffff:127.0.0.1" || Regex.IsMatch(host, "^0*:(0*:)*0*1$");
    }
    public static void AssertTransportSafe(string url)
    {
        var uri = new Uri(NormalizeServiceUrl(url)); if (uri.Scheme != "https" && !IsLoopbackHost(uri.Host)) throw new InvalidOperationException("Refusing plain HTTP to a non-loopback Construct host; use HTTPS.");
    }
    public static string ApiPath(string route) { route = StateJson.Trim(route); if (!route.StartsWith('/')) route = "/" + route; return route.StartsWith("/api/", StringComparison.Ordinal) ? route : "/api/v1" + route; }
    public static string Encode(string value) => Uri.EscapeDataString(value).Replace("%21", "!", StringComparison.Ordinal).Replace("%27", "'", StringComparison.Ordinal).Replace("%28", "(", StringComparison.Ordinal).Replace("%29", ")", StringComparison.Ordinal).Replace("%2A", "*", StringComparison.Ordinal);
    public static string BuildQuery(JsonObject? query)
    {
        if (query is null) return "";
        var parts = query.Where(p => p.Value is not null && StateJson.Text(p.Value) != "").Select(p => Encode(p.Key) + "=" + Encode(StateJson.String(p.Value))).ToArray(); return parts.Length == 0 ? "" : "?" + string.Join("&", parts);
    }
    public static string MapError(int status, JsonNode? body, string? context = null)
    {
        var where = string.IsNullOrEmpty(context) ? "" : " (" + context + ")";
        var detail = body is JsonObject o ? string.Join(" — ", new[] { StateJson.Text(o["title"]), StateJson.Text(o["detail"]) }.Where(s => !string.IsNullOrEmpty(s))) : StateJson.Trim(StateJson.Text(body) ?? "");
        if (body is not JsonObject && detail.Length > 400) detail = detail[..400];
        var message = status switch { 0 => "Could not reach the Construct host service" + where, 401 => "The Construct host service rejected these credentials" + where, 403 => "The Construct host service refused this" + where + " — you are not enrolled, or it is not your VM", 404 => "The Construct host service has no such thing" + where, 409 => "The Construct host service cannot do that right now" + where, _ => "The Construct host service answered HTTP " + status + where };
        return message + (detail.Length == 0 ? "" : ": " + detail);
    }
    public static JsonObject? ReadEndpoint(JsonNode? body)
    {
        if (body is not JsonObject o || StateJson.Nonempty(o["sshHost"]) is not {} host) return null;
        var port = StateJson.CoerceNumber(o["sshPort"]);
        return new JsonObject { ["sshHost"] = host, ["sshPort"] = port is >= 1 and <= 65535 && port == Math.Truncate(port) ? (int)port : 22, ["publicHost"] = StateJson.Nonempty(o["publicHost"]) ?? host };
    }
    public static string MapVmState(string? state) => StateJson.Trim(state ?? "").ToLowerInvariant() switch { "running" => "running", "off" or "saved" or "paused" => "off", "absent" => "absent", _ => "unknown" };
    public static bool SameServiceUrl(string? a, string? b)
    {
        try { return string.Equals(NormalizeServiceUrl(a), NormalizeServiceUrl(b), StringComparison.OrdinalIgnoreCase); } catch (ArgumentException) { return false; }
    }
    public static string ReadPin(IFileSystem files, string url)
    {
        try { var path = PinPath(files, url); var bytes = path is null ? null : files.ReadFile(path); return bytes is null ? "" : FormatFingerprint(Encoding.UTF8.GetString(bytes)); }
        catch (IOException) { return ""; } catch (UnauthorizedAccessException) { return ""; }
    }
    public static string WritePin(IFileSystem files, string url, string fingerprint)
    {
        var pin = FormatFingerprint(fingerprint); if (pin.Length == 0) throw new ArgumentException("Not a SHA-256 certificate fingerprint (64 hex digits).");
        var path = PinPath(files, url) ?? throw new InvalidOperationException("No %LOCALAPPDATA% to store the certificate pin in."); files.CreateDirectory(Path.GetDirectoryName(path)!); files.WriteFileAtomic(path, Encoding.UTF8.GetBytes(pin)); return path;
    }
}

public sealed class RemoteApiException(int status, string message, string code = "", Exception? inner = null) : Exception(message, inner)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public JsonNode? Body { get; init; }
}

public sealed partial class RemoteHostClient
{
    private readonly IRemoteApi api;
    private readonly ITokenStore tokens;
    private readonly string pin;
    private readonly RemoteAuthentication authentication;
    public string BaseUrl { get; }
    public string Host => new Uri(BaseUrl).Host.Trim('[', ']');
    public RemoteHostClient(IRemoteApi api, IFileSystem files, ITokenStore tokens, string baseUrl, RemoteAuthentication authentication = RemoteAuthentication.Negotiate, string? pin = null)
    {
        BaseUrl = RemoteHost.NormalizeServiceUrl(baseUrl); RemoteHost.AssertTransportSafe(BaseUrl);
        this.api = api; this.tokens = tokens; this.authentication = authentication; this.pin = pin is null ? RemoteHost.ReadPin(files, BaseUrl) : RemoteHost.FormatFingerprint(pin);
    }
    public async Task<JsonNode?> RequestAsync(string method, string route, JsonNode? body = null, CancellationToken cancellationToken = default)
    {
        var path = RemoteHost.ApiPath(route); var secure = new Uri(BaseUrl).Scheme == "https";
        if (secure && pin.Length == 0) throw new RemoteApiException(0, "The host certificate has not been confirmed on this machine. Add Remote Host once.");
        var token = authentication == RemoteAuthentication.Token ? await tokens.ReadAsync(RemoteHost.HostSlug(BaseUrl), cancellationToken) : null;
        if (authentication == RemoteAuthentication.Token && token is null) throw new RemoteApiException(0, "The host API token is unavailable. Add Remote Host to re-enter it.");
        var secrets = RequestSecrets(token, body);
        RemoteResponse response;
        try
        {
            response = await api.SendAsync(new RemoteRequest(method, new Uri(BaseUrl + path), fingerprint => !secure || RemoteHost.FingerprintsMatch(pin, fingerprint),
                body is null ? null : JsonSerializer.SerializeToElement(body), authentication, token), cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) { throw new RemoteApiException(0, RemoteHost.MapError(0, null, method + " " + path), inner: SafeException(error, secrets)); }
        var result = response.Body is {} value ? JsonNode.Parse(value.GetRawText()) : null;
        if (response.StatusCode is >= 200 and < 300) return result;
        // Host errors may echo credentials; retain useful details after redaction.
        var errorBody = result is null ? null : JsonNode.Parse(Redact(result.ToJsonString(), secrets, json: true));
        throw new RemoteApiException(response.StatusCode, RemoteHost.MapError(response.StatusCode, errorBody, method + " " + path), StateJson.Text((errorBody as JsonObject)?["code"]) ?? "") { Body = errorBody };
    }
    private static string[] RequestSecrets(Secret? token, JsonNode? body)
    {
        var secrets = new List<string>(); if (token is not null) secrets.Add(token.Reveal());
        void Collect(JsonNode? node)
        {
            if (node is JsonObject obj) foreach (var (key, value) in obj)
            {
                if (Regex.IsMatch(key, "password|token|secret", RegexOptions.IgnoreCase) && StateJson.Text(value) is { Length: > 0 } secret) secrets.Add(secret);
                else Collect(value);
            }
            else if (node is JsonArray array) foreach (var value in array) Collect(value);
        }
        Collect(body); return secrets.Where(s => s.Length > 0).OrderByDescending(s => s.Length).ToArray();
    }
    private static string Redact(string text, string[] secrets, bool json = false)
    {
        foreach (var secret in secrets) text = text.Replace(json ? JsonSerializer.Serialize(secret)[1..^1] : secret, "[redacted]", StringComparison.Ordinal);
        return text;
    }
    private static Exception SafeException(Exception error, string[] secrets)
    {
        // Keep the original type and stack when safe; a transport that echoed a
        // credential gets a sanitized diagnostic chain instead of leaking it.
        if (Redact(error.ToString(), secrets) == error.ToString()) return error;
        return new Exception(Redact(error.GetType().Name + ": " + error.Message, secrets), error.InnerException is null ? null : SafeException(error.InnerException, secrets));
    }
}
