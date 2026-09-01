namespace Constructd.Core.Domain;

/// <summary>
/// One audit record. Every mutating API call and every idle-engine action writes one
/// (plan §4.4 "who did what when").
/// </summary>
/// <param name="Actor">Principal name, or <c>system</c> for service-initiated actions.</param>
/// <param name="Action">Stable verb, e.g. <c>vm.create</c>, <c>forward.add</c>.</param>
/// <param name="Target">What the action was about (VM name, user name, …).</param>
/// <param name="Detail">Free-form context. Never contains secrets.</param>
public sealed record AuditEntry(
    DateTimeOffset At,
    string Actor,
    string Action,
    string Target,
    AuditOutcome Outcome,
    string? Detail);
