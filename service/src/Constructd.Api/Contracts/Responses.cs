using System.Text.Json;
using System.Text.Json.Nodes;
using Constructd.Core.Domain;

// Endpoint also exists in Microsoft.AspNetCore.Http (routing); the domain record is the one meant here.
using DomainEndpoint = Constructd.Core.Domain.Endpoint;

namespace Constructd.Api.Contracts;

// Response bodies. These are the API contract — domain records are never serialized directly, so a
// field like Vm.VmTokenHash cannot leak by accident.

/// <param name="Kind">Always <c>user</c>: VM-scoped tokens cannot reach this route.</param>
/// <param name="Known">False when the identity authenticated but is not enrolled on this host.</param>
public sealed record WhoAmIResponse(
    string Name,
    string Kind,
    string Scheme,
    bool Known,
    Role? Role,
    int? MaxVms,
    bool? AllowHostForwards);

public sealed record UserResponse(string Name, Role Role, int MaxVms, bool AllowHostForwards, DateTimeOffset Created)
{
    public static UserResponse From(User user) =>
        new(user.Name, user.Role, user.MaxVms, user.AllowHostForwards, user.Created);
}

/// <param name="Token">The plaintext secret — returned exactly once, never stored, never logged.</param>
public sealed record TokenIssuedResponse(string Id, string Label, string Token, DateTimeOffset Created);

public sealed record IdlePolicyResponse(int TimeoutMinutes, IdleAction Action, int MaxTimeoutMinutes, bool Clamped);

public sealed record ForwardResponse(
    string Id,
    string VmName,
    int VmPort,
    int? PublicPort,
    ForwardTarget Target,
    string Label,
    DateTimeOffset Created,
    string? Url)
{
    /// <summary>
    /// <paramref name="publicHost"/> is the LAN name forwards are advertised on. The URL is advisory
    /// (plan §4.9: "url on forwards is a field, not a format") and only exists for host targets;
    /// client targets are opened by the extension on the user's PC.
    /// </summary>
    public static ForwardResponse From(PortForward forward, string publicHost) =>
        new(
            forward.Id,
            forward.VmName,
            forward.VmPort,
            forward.PublicPort,
            forward.Target,
            forward.Label,
            forward.Created,
            forward.PublicPort is int port ? $"http://{publicHost}:{port}/" : null);
}

public sealed record VmResponse(
    string Name,
    string Owner,
    int Cpu,
    int RamGb,
    int DiskGb,
    DateTimeOffset Created,
    VmState State,
    int? SshForwardPort,
    /// <summary>True once a removal job has been accepted: the VM is fenced against mutations.</summary>
    bool Deleting,
    IdlePolicyResponse IdlePolicy,
    IReadOnlyList<ForwardResponse> Forwards);

public sealed record EndpointResponse(string SshHost, int SshPort)
{
    public static EndpointResponse From(DomainEndpoint endpoint) => new(endpoint.SshHost, endpoint.SshPort);
}

public sealed record VmStateResponse(VmState State);

/// <summary>Answer to every call that starts a job: <c>202 Accepted</c> plus the job id.</summary>
public sealed record JobAcceptedResponse(string JobId);

public sealed record JobProgressResponse(DateTimeOffset At, string Text)
{
    public static JobProgressResponse From(JobProgressLine line) => new(line.At, line.Text);
}

/// <summary>
/// Result of a creation job as the API presents it: the durable part (<see cref="Name"/>,
/// <see cref="Endpoint"/>) plus <see cref="VmToken"/>, which is filled in on the retrieval that
/// consumes the job's one-time secret and is <c>null</c> on every later one.
/// </summary>
public sealed record VmCreateResultResponse(string Name, EndpointResponse Endpoint, string? VmToken);

public sealed record JobResponse(
    string Id,
    string Kind,
    string? VmName,
    JobState State,
    DateTimeOffset Created,
    DateTimeOffset? Finished,
    IReadOnlyList<JobProgressResponse> Progress,
    object? Result,
    string? Error)
{
    /// <param name="vmToken">
    /// The consumed one-time secret, if this projection is the one that got it. It is merged into the
    /// result — the contract is <c>result: {name, endpoint, vmToken}</c> — and never stored anywhere.
    /// </param>
    public static JobResponse From(Job job, string? vmToken = null) =>
        new(
            job.Id,
            job.Kind,
            job.VmName,
            job.State,
            job.Created,
            job.Finished,
            [.. job.Progress.Select(JobProgressResponse.From)],
            ProjectResult(job, vmToken),
            job.Error);

    /// <summary>
    /// Projects the stored (secret-free) result for the wire. For a creation job the VM token is
    /// merged back in, so clients see one shape whether the job is still in this process's memory or
    /// was read back from the store after a restart (where <c>vmToken</c> is always null).
    /// </summary>
    private static object? ProjectResult(Job job, string? vmToken)
    {
        if (job.Kind != JobKinds.CreateVm || job.Result is null)
        {
            return job.Result;
        }

        if (job.Result is VmCreateResult create)
        {
            return new VmCreateResultResponse(create.Name, EndpointResponse.From(create.Endpoint), vmToken);
        }

        // Read back from a store: a JSON object without the token.
        if (job.Result is JsonElement { ValueKind: JsonValueKind.Object } element)
        {
            var node = JsonNode.Parse(element.GetRawText())!.AsObject();
            node["vmToken"] = vmToken;
            return node;
        }

        return job.Result;
    }
}

public sealed record AuditResponse(
    DateTimeOffset At,
    string Actor,
    string Action,
    string Target,
    AuditOutcome Outcome,
    string? Detail)
{
    public static AuditResponse From(AuditEntry entry) =>
        new(entry.At, entry.Actor, entry.Action, entry.Target, entry.Outcome, entry.Detail);
}
