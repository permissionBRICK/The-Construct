namespace Constructd.Api.Contracts;

// Request bodies. Every field is nullable so a missing field is a validation error rather than a
// silent default, and every enum arrives as a string that the endpoint parses explicitly — a typo
// then produces a 400 problem document instead of a deserializer exception.

public sealed record CreateUserRequest(string? Name, string? Role, int? MaxVms, bool? AllowHostForwards);

public sealed record CreateTokenRequest(string? Label);

public sealed record CreateVmRequest(string? Name, int? Cpu, int? RamGb, int? DiskGb, CreateVmOptions? Opts);

/// <param name="Nested">Expose virtualization extensions to the guest.</param>
/// <param name="AutomaticCheckpoints">Hyper-V automatic checkpoints (capability-gated).</param>
/// <param name="IdlePolicy">Initial idle policy; the service default is used when omitted.</param>
public sealed record CreateVmOptions(bool? Nested, bool? AutomaticCheckpoints, IdlePolicyRequest? IdlePolicy);

public sealed record PowerRequest(string? Action);

public sealed record CreateForwardRequest(int? VmPort, string? Label, string? Target);

public sealed record IdlePolicyRequest(int? TimeoutMinutes, string? Action);

public sealed record ActivityRequest(bool? Busy, IReadOnlyList<string>? Reasons);
