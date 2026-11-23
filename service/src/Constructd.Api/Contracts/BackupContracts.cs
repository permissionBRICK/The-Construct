namespace Constructd.Api.Contracts;

/// <summary>
/// <c>POST /vms/{name}/backups</c>: the guest opens an upload session. The host decides what it counts as:
/// the trigger of its open request, or the VM's slot; <c>trigger</c> is only checked for form.
/// </summary>
/// <param name="FileCount">How many files the export holds, when the guest says; listed with the backup.</param>
public sealed record BackupCreateRequest(string? Trigger, long? EstimatedBytes, long? FileCount = null);

/// <summary><c>POST /vms/{name}/backups/request</c>: what the user asks for; no body is a manual backup.</summary>
/// <param name="Trigger"><c>manual</c> (the default) or <c>pre-reinstall</c>.</param>
public sealed record BackupRequestRequest(string? Trigger);

/// <summary><c>POST /vms/{name}/backups/report</c>: a run that skipped or failed before or outside an upload.</summary>
public sealed record BackupReportRequest(string? Trigger, string? Result, string? Reason, string? Detail);
