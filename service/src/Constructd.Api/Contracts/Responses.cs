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

/// <summary>
/// One forward on the wire. The ack fields are INLINE and the record stays FLAT on purpose:
/// <c>bin/construct-expose.sh</c> reads a forward with the same lenient, jq-optional parser it reads
/// a spool ack document with (<c>docs/expose.md</c>), and that parser's fallback path splits a JSON
/// array on <c>{…}</c> and greps flat <c>"key": value</c> pairs. A nested <c>ack</c> object would be
/// invisible to it on a VM without jq.
/// </summary>
/// <param name="Status">
/// <c>open</c> or <c>error</c> once a client acked; absent otherwise. An <c>error</c> is a FINAL
/// answer for the CLI — it stops waiting and prints <paramref name="Message"/>.
/// </param>
/// <param name="Url">
/// Advisory (plan §4.9: "url on forwards is a field, not a format"): the host forward's LAN URL, or
/// the link the client reported, or <c>null</c> while a client forward is still queued. It is null
/// for an <c>error</c> ack too, because the CLI checks <c>url</c> first and would print it instead
/// of reporting the failure.
/// </param>
public sealed record ForwardResponse(
    string Id,
    string VmName,
    int VmPort,
    int? PublicPort,
    ForwardTarget Target,
    string Label,
    DateTimeOffset Created,
    string? Url,
    string? Status = null,
    int? LocalPort = null,
    string? HostLabel = null,
    string? Message = null,
    DateTimeOffset? AckedAt = null)
{
    /// <summary>
    /// <paramref name="publicHost"/> is the LAN name host forwards are advertised on. A client
    /// forward is opened by the extension on the user's PC instead, so its link is built from the
    /// ack: <c>hostLabel</c> when the user's PC has a name other machines use, loopback otherwise.
    /// </summary>
    public static ForwardResponse From(PortForward forward, string publicHost)
    {
        ArgumentNullException.ThrowIfNull(forward);

        // Every host in a link goes through the one rule (ForwardHost, docs/expose.md): an IPv6
        // literal gets exactly one bracket pair, everything else is passed through unchanged.
        var url = forward.PublicPort is int port
            ? $"http://{ForwardHost.ForUrl(publicHost) ?? publicHost}:{port}/"
            : null;

        if (forward.Ack is not { } ack)
        {
            return new(
                forward.Id, forward.VmName, forward.VmPort, forward.PublicPort,
                forward.Target, forward.Label, forward.Created, url);
        }

        if (ack.Status == AckStatus.Open && ack.LocalPort is int local)
        {
            // The label is stored canonically (bare, ForwardHost.Normalize at the ack endpoint);
            // an unusable one — or one an older client stored bracketed — falls back to loopback
            // rather than producing a link nobody can open.
            var host = ForwardHost.ForUrl(ack.HostLabel) ?? "localhost";
            url = $"http://{host}:{local}/";
        }

        return new(
            forward.Id, forward.VmName, forward.VmPort, forward.PublicPort,
            forward.Target, forward.Label, forward.Created, url,
            Status: ack.Status.ToString().ToLowerInvariant(),
            LocalPort: ack.LocalPort,
            HostLabel: ack.HostLabel,
            Message: ack.Message,
            AckedAt: ack.At);
    }
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
