namespace Constructd.Core.Domain;

public sealed record WindowsKeyInfo(string Id, string Product, string Edition, string Kind, string PartialKey, int? Budget, int Used, string Notes, string HostId = "");
public sealed record WindowsGuestStatus(string VmName, string Incarnation, string Product, string Edition,
    string Stage = "installing", string Activation = "not-activated", string? PartialKey = null, bool GuestReported = false,
    string? KeyId = null, bool Released = false, double? Uptime = null, bool InstallEjected = false, bool AuxiliaryEjected = false,
    bool Attempted = false, bool Kms = false, string? Error = null, DateTimeOffset? DeliveredAt = null, string HostId = "", bool Evaluation = false,
    WindowsLicenseObservation? License = null, string? AllocationId = null, ChildHardware? Hardware = null,
    WindowsActivationOperation? Operation = null, string? MachineId = null);
public sealed record WindowsGuestReport(string Product, string Edition, bool FirstLogonDone, string Activation,
    string? PartialKey, bool Kms = false, bool Evaluation = false, WindowsLicenseObservation? License = null, WindowsActivationReport? Operation = null, string? AllocationId = null);

/// <summary>Windows' observation, independent of a Construct activation operation.</summary>
public sealed record WindowsLicenseObservation(DateTimeOffset ObservedAt, string? ActivationId = null,
    string? PartialKey = null, string? Channel = null, int? Status = null, uint? Reason = null,
    int? GraceMinutes = null, DateTimeOffset? EvaluationEnd = null);
