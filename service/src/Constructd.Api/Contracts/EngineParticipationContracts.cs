using System.Text.Json.Nodes;
using Construct.Keys;
using Constructd.Api.Infrastructure;

namespace Constructd.Api.Contracts;

/// <summary><c>GET /engine</c>: this engine's participant id and host key, which devices seal grants to (8.7).</summary>
public sealed record EngineIdentityResponse(string EngineId, string HostPublicKey);

/// <summary>
/// <c>POST /devices/enroll</c>: a device of <paramref name="Account"/> asks for a device token. The
/// <paramref name="Proof"/> is the device key's proof over
/// <c>engine-enroll\n&lt;engine id&gt;\n&lt;account&gt;\n&lt;device id&gt;</c>.
/// </summary>
public sealed record DeviceEnrollRequest(string Account, string DeviceId, [property: ConstructSecret] string Proof);

public sealed record DeviceEnrollResponse([property: ConstructSecret] string Token);

/// <summary><c>PUT /engine/grants</c>: keys sealed to this engine's host key, <c>D</c> and per vault mode <c>K</c>.</summary>
public sealed record EngineGrantsRequest([property: ConstructSecret] IReadOnlyList<EngineGrant> Grants);

/// <summary><c>GET /sync/records/{id}</c>: a record of the caller's account, opened with this engine's <c>D</c>.</summary>
public sealed record SyncRecordResponse(string Id, string Kind, string Actor, long Revision, JsonNode? Value);

/// <summary><c>PUT /device/backup-key</c>: the account's backup recipient <c>B.pub</c> (SubjectPublicKeyInfo PEM, RSA of at least 3072 bits).</summary>
public sealed record AccountBackupKeyRequest(string PublicKey);

/// <summary><c>POST /vms/{name}/backup-recipients</c>: the fingerprint of the key now enrolled on the VM.</summary>
public sealed record BackupRecipientResponse(string Fingerprint);

/// <summary><c>PUT /device/ssh-key</c>: the calling device's SSH public key (one OpenSSH line), for its user's VMs.</summary>
public sealed record DeviceSshKeyRequest(string PublicKey);
