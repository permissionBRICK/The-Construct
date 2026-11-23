using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Construct.Keys;

/// <summary>An engine a newly paired device enrols with (8.3): its address and TLS pin.</summary>
public sealed record EngineEndpoint(string Url, string? Pin);

/// <summary>What the approver hands a new device (8.3), sealed whole to the new device's key.</summary>
public sealed record PairingGrant(string Account, long Epoch, byte[] Root, IReadOnlyList<EngineEndpoint> Engines);

/// <summary>
/// The pairing grant (8.3): sealed to the new device's key under a context naming the pairing, and
/// signed by the approver over <c>pairingId || devicePub || grant</c>. The new device checks the
/// signature against the approver's registered key before it opens the grant.
/// </summary>
public static class Pairing
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Seal(PairingGrant grant, ParticipantPublicKey device, string pairingId) =>
        SealedBox.Seal(device, JsonSerializer.SerializeToUtf8Bytes(grant, Json), Context(pairingId));

    public static PairingGrant Open(ParticipantKey device, byte[] sealedGrant, string pairingId) =>
        JsonSerializer.Deserialize<PairingGrant>(device.Open(sealedGrant, Context(pairingId)), Json)
        ?? throw new CryptographicException("The grant is empty.");

    public static byte[] Sign(ParticipantKey approver, string pairingId, ParticipantPublicKey device, byte[] sealedGrant) =>
        approver.Sign(Signed(pairingId, device, sealedGrant));

    public static bool Verify(ParticipantPublicKey approver, string pairingId, ParticipantPublicKey device, byte[] sealedGrant, byte[] signature) =>
        approver.Verify(Signed(pairingId, device, sealedGrant), signature);

    private static byte[] Signed(string pairingId, ParticipantPublicKey device, byte[] sealedGrant) =>
        Encoding.UTF8.GetBytes($"construct-pairing-v1\n{pairingId}\n{device}\n{Convert.ToBase64String(sealedGrant)}");

    private static string Context(string pairingId) => $"pairing-grant/{pairingId}";
}

/// <summary>
/// Keys a device grants an engine (8.7): <c>D</c> always, <c>K</c> when the vault is
/// <c>available</c> on that engine. Each is sealed to the engine's host key under a context naming
/// the engine, account, key and epoch. <c>U</c> and <c>B</c>'s private half are never granted.
/// </summary>
public static class EngineGrants
{
    public const string Records = "D";
    public const string Vault = "K";

    public static bool IsGrantable(string name) => name is Records or Vault;

    /// <summary>The keys an engine gets under a vault mode (<c>available</c> or <c>locked</c>).</summary>
    public static IReadOnlyList<EngineGrant> For(AccountKeys keys, ParticipantPublicKey host, string engineId, string account, string vaultMode)
    {
        var grants = new List<EngineGrant> { Grant(host, engineId, account, Records, keys.Epoch, keys.Records) };
        if (vaultMode == "available") grants.Add(Grant(host, engineId, account, Vault, keys.Epoch, keys.Vault));
        return grants;
    }

    public static EngineGrant Grant(ParticipantPublicKey host, string engineId, string account, string name, long epoch, byte[] key)
    {
        if (!IsGrantable(name)) throw new ArgumentException($"Key '{name}' is never granted to an engine.", nameof(name));
        return new EngineGrant(name, epoch, SealedBox.Seal(host, key, Context(engineId, account, name, epoch)));
    }

    public static byte[] Open(ParticipantKey host, string engineId, string account, EngineGrant grant) =>
        host.Open(grant.Sealed, Context(engineId, account, grant.Name, grant.Epoch));

    private static string Context(string engineId, string account, string name, long epoch) => $"engine-grant/{engineId}/{account}/{name}/{epoch}";
}

/// <summary>One key granted to an engine, sealed to its host key.</summary>
public sealed record EngineGrant(string Name, long Epoch, byte[] Sealed);
