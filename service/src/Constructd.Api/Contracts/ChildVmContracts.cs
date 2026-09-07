using System.Text.Json.Serialization;
using Constructd.Core.Domain;
namespace Constructd.Api.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChildCreateRequest(string? Name, int Cpus, int RamMb, int DiskGb, string? Lifetime,
    ChildMediaRequest? Media, string? Preset = null, ChildFirmwareRequest? Firmware = null,
    ChildNetworkRequest? Network = null, bool Start = true, string? OperationKey = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChildMediaRequest(string InstallMediaId, string? AuxiliaryMediaId = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChildFirmwareRequest(int? Generation = null, bool? SecureBoot = null,
    SecureBootTemplate? SecureBootTemplate = null, bool? Tpm = null, IReadOnlyList<BootDevice>? BootOrder = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ChildNetworkRequest(bool Attach = true);
