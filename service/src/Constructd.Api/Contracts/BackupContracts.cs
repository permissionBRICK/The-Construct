namespace Constructd.Api.Contracts;

/// <summary><c>POST /vms/{name}/backups</c>: the guest opens an upload session.</summary>
public sealed record BackupCreateRequest(string? Trigger, long? EstimatedBytes);

/// <summary><c>POST /vms/{name}/backups/report</c>: a run that skipped or failed before or outside an upload.</summary>
public sealed record BackupReportRequest(string? Trigger, string? Result, string? Reason, string? Detail);
