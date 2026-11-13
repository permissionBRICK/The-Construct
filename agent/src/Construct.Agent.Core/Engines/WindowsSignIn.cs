using System.Net;

namespace Construct.Agent.Engines;

/// <summary>
/// Signing in at an engine as the Windows user this agent runs as (8.1: Windows sign-in names the user,
/// the device token names the device). Only on a handler that already checks the engine's pin, so the
/// sign-in reaches the confirmed engine or nowhere.
/// </summary>
public interface IWindowsSignIn
{
    /// <summary>The scheme an engine challenges with when it offers this sign-in (<c>Negotiate</c>).</summary>
    string Scheme { get; }

    /// <summary>The pinned handler, made to answer the engine's challenge as the signed-in user.</summary>
    HttpMessageHandler Apply(SocketsHttpHandler pinned);
}

/// <summary>Kerberos or NTLM with the signed-in user's own credentials; Windows only.</summary>
public sealed class NegotiateSignIn : IWindowsSignIn
{
    public string Scheme => "Negotiate";

    public HttpMessageHandler Apply(SocketsHttpHandler pinned)
    {
        pinned.Credentials = CredentialCache.DefaultCredentials;
        return pinned;
    }
}
