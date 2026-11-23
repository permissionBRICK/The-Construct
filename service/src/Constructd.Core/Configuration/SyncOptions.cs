namespace Constructd.Core.Configuration;

/// <summary>
/// Profile sync (docs/plans/construct-platform-design.md 9). Every account this engine takes part in
/// syncs against the engine's own store unless <see cref="Stores"/> names a store elsewhere for it.
/// </summary>
public sealed class SyncOptions
{
    public List<RemoteStoreOptions> Stores { get; set; } = [];
}

/// <summary>
/// A store elsewhere (another engine's, or a stand-alone one) for one account: its base address and the
/// participant token it issued this engine. The store knows the token as this engine's id
/// (<c>GET /engine</c>).
/// </summary>
public sealed class RemoteStoreOptions
{
    public string Account { get; set; } = "";

    /// <summary>The store's base address; its routes are under <c>/api/v1/store/</c>.</summary>
    public string Url { get; set; } = "";

    public string Token { get; set; } = "";
}
