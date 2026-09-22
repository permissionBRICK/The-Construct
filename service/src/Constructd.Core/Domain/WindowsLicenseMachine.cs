namespace Constructd.Core.Domain;

public sealed record WindowsLicenseMachine(string Id, string KeyId, string HostId, string Incarnation,
    ChildHardware Hardware, string State, string? VmName, string? AllocationId, string[] PreviousNames,
    bool HasConfirmationId = false, int Reuses = 0);

public sealed record WindowsActivationOperation(string Id, string AllocationId, string Mode, string Stage,
    string? Error = null);

// Secrets travel only over the protected host/guest channel, never in host-panel projections.
public sealed record WindowsActivationCommand(string Id, string AllocationId, string Action, string Key,
    string? ConfirmationId = null);
public sealed record WindowsActivationReport(string Id, string AllocationId, string Stage,
    string? InstallationId = null, string? ProductKeyId = null, string? ActivationId = null);
