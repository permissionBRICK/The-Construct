using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Construct.Keys;

/// <summary>
/// Binds a token to the key it was issued for (8.1): every request that carries a device token also
/// carries <c>X-Construct-Proof: &lt;unix seconds&gt;.&lt;signature&gt;</c>, the device's signature over
/// the token and the time. A token without its key is refused; a token presented with another
/// device's key is refused. A captured proof is valid for <see cref="Window"/> on either side of its
/// time, with the token it was captured with; capturing one needs the TLS channel broken.
/// </summary>
public static class RequestProof
{
    public const string Header = "X-Construct-Proof";

    /// <summary>The token header at a store (devices and engine participants).</summary>
    public const string StoreTokenHeader = "X-Construct-Store-Token";

    /// <summary>The token header at an engine (devices).</summary>
    public const string DeviceTokenHeader = "X-Construct-Device-Token";

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(2);

    public static string Create(ParticipantKey key, string subject, DateTimeOffset now)
    {
        var time = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return time + "." + Base64Url.Encode(key.Sign(Message(time, subject)));
    }

    public static bool Verify(ParticipantPublicKey key, string subject, string? proof, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(proof) || proof.Length > 200) return false;
        var dot = proof.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0) return false;
        var time = proof[..dot];
        if (!long.TryParse(time, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return false;
        if (seconds < now.ToUnixTimeSeconds() - (long)Window.TotalSeconds || seconds > now.ToUnixTimeSeconds() + (long)Window.TotalSeconds) return false;
        try
        {
            return key.Verify(Message(time, subject), Base64Url.Decode(proof[(dot + 1)..]));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Message(string time, string subject) => $"construct-proof-v1\n{time}\n{subject}";
}

/// <summary>
/// Adds a device's token and a fresh proof to every request: the device side of
/// <see cref="RequestProof"/>, for a store (<see cref="RequestProof.StoreTokenHeader"/>) or an engine
/// (<see cref="RequestProof.DeviceTokenHeader"/>).
/// </summary>
public sealed class DeviceCredentialHandler(ParticipantKey key, string tokenHeader, string token, TimeProvider? time = null) : DelegatingHandler
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Stamp(request.Headers, key, tokenHeader, token, _time.GetUtcNow());
        return base.SendAsync(request, cancellationToken);
    }

    public static void Stamp(HttpRequestHeaders headers, ParticipantKey key, string tokenHeader, string token, DateTimeOffset now)
    {
        headers.Remove(tokenHeader);
        headers.Remove(RequestProof.Header);
        headers.TryAddWithoutValidation(tokenHeader, token);
        headers.TryAddWithoutValidation(RequestProof.Header, RequestProof.Create(key, token, now));
    }
}

/// <summary>Bearer-style secrets: 256 random bits as base64url; only their SHA-256 is stored.</summary>
public static class Tokens
{
    public static string New() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    public static byte[] Hash(string token) => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token));
}
