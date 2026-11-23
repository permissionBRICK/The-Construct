using Construct.Keys;

namespace Constructd.Store.Identity;

/// <summary>
/// <c>POST /devices</c>: register a device. Exactly one of: <paramref name="SetupCode"/> with
/// <paramref name="Account"/> (the first device of a stand-alone store), <paramref name="EnrollToken"/>
/// (after a recovery), or neither (the signed-in user's first device in constructd).
/// <paramref name="Proof"/> is a <see cref="RequestProof"/> over <c>enroll\n&lt;publicKey&gt;</c>.
/// </summary>
public sealed record EnrollRequest(string PublicKey, string Name, string Proof, string? Account = null, string? SetupCode = null, string? EnrollToken = null);

public sealed record EnrollResponse(string Account, string DeviceId, string Token);

public sealed record EngineRegistration(string EngineId, string HostPublicKey);

public sealed record EngineRegistered(string Token);

/// <summary><c>PUT /recovery</c>: the verifier of a new recovery secret and <c>U</c> sealed to the new recovery key.</summary>
public sealed record RecoverySetup(byte[] Verifier, RootSeal Wrap);

/// <summary><c>POST /recovery</c>.</summary>
public sealed record RecoveryRequest(string Account, byte[] Secret);

public sealed record RecoveryResponse(RootSeal Wrap, string EnrollToken);

/// <summary><c>POST /pairing</c>: the new device's key and name.</summary>
public sealed record PairingRequest(string PublicKey, string Name);

public sealed record PairingCreated(string PairingId, DateTimeOffset ExpiresAt);

/// <summary><c>GET /pairing/{id}</c>: what the approver compares and seals to.</summary>
public sealed record PairingView(string PairingId, string PublicKey, string Name, DateTimeOffset ExpiresAt);

/// <summary><c>PUT /pairing/{id}</c>: the sealed grant and the approver's signature over it.</summary>
public sealed record PairingApproval(byte[] Grant, byte[] Signature);

/// <summary><c>GET /pairing/{id}/grant</c>, once.</summary>
public sealed record PairingRedeemed(string Account, string DeviceId, string Token, byte[] Grant, byte[] Signature, string ApproverId, string ApproverPublicKey);

/// <summary>The proof subjects of the identity routes (<see cref="RequestProof"/>).</summary>
public static class ProofSubjects
{
    public static string Enroll(string publicKey) => "enroll\n" + publicKey;

    public static string Redeem(string pairingId) => "pairing\n" + pairingId;

    /// <summary>A device enrolling with an engine (8.1): names the engine so a proof cannot be used at another one.</summary>
    public static string EngineEnroll(string engineId, string account, string deviceId) => $"engine-enroll\n{engineId}\n{account}\n{deviceId}";
}
