namespace Constructd.Core.Domain;

public sealed record WindowsKeyInfo(string Id, string Product, string Edition, string Kind, string PartialKey, int? Budget, int Used, string Notes);
public sealed record WindowsGuestStatus(string VmName, string Incarnation, string Product, string Edition,
    string Stage = "installing", string Activation = "not-activated", string? PartialKey = null, bool GuestReported = false,
    string? KeyId = null, bool Released = false, double? Uptime = null, bool InstallEjected = false, bool AuxiliaryEjected = false,
    bool Attempted = false, bool Kms = false, string? Error = null, DateTimeOffset? DeliveredAt = null);
public sealed record WindowsGuestReport(string Product, string Edition, bool FirstLogonDone, string Activation,
    string? PartialKey, bool Kms = false);
