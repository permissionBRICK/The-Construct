using Constructd.Core.Domain;

namespace Constructd.Core.Abstractions;

/// <summary>
/// Serves HTTPS forwards (docs/https-forwards.md). A forward manager still allocates, stores and releases
/// the public port of an HTTPS forward exactly like any host forward; it only hands the materialization
/// to this host instead of opening a raw relay or a netsh rule on that port, because the TLS has to be
/// terminated in this process with the host's certificate.
/// </summary>
public interface IHttpsForwardHost
{
    /// <summary>Starts terminating TLS on the forward's public port, if not already. Throws when the port cannot be bound.</summary>
    void Ensure(PortForward forward);

    /// <summary>Stops serving a forward's public port. A no-op for an id it does not serve.</summary>
    Task StopAsync(string forwardId);

    /// <summary>Live HTTPS connections to the VM, on its forward ports and the shared port: part of the idle signal.</summary>
    int ActiveConnections(string vmName);
}

/// <summary>Used where no HTTPS forward host exists (the in-memory fakes): an HTTPS forward is only recorded.</summary>
public sealed class NoHttpsForwardHost : IHttpsForwardHost
{
    public static NoHttpsForwardHost Instance { get; } = new();

    public void Ensure(PortForward forward)
    {
    }

    public Task StopAsync(string forwardId) => Task.CompletedTask;

    public int ActiveConnections(string vmName) => 0;
}
