using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;

namespace Constructd.Api.Endpoints;

/// <summary>
/// Job status and the SSE progress stream (plan §4.4). A job is readable by the principal that
/// submitted it and by admins.
///
/// A creation job also carries a one-time secret — the VM-scoped token. It is never part of the
/// stored job; the first authorized retrieval of a succeeded creation job (<c>GET /jobs/{id}</c> or
/// the terminal SSE event) consumes it and gets it as <c>result.vmToken</c>. Every later retrieval,
/// reconnect or restart sees <c>vmToken: null</c>. "Consumes" means exactly that: the secret is taken
/// when the response is composed, so a response lost in transit loses the token, and a new one has to
/// be issued (<see cref="ITokenService.IssueVmTokenAsync"/>).
/// </summary>
public static class JobEndpoints
{
    public static RouteGroupBuilder MapJobEndpoints(this RouteGroupBuilder api)
    {
        var jobs = api.MapGroup("/jobs").RequireAuthorization(Policies.User);

        jobs.MapGet("/{id}", GetAsync).WithName("GetJob");
        jobs.MapGet("/{id}/events", EventsAsync).WithName("GetJobEvents");

        return api;
    }

    private static async Task<IResult> GetAsync(
        string id,
        HttpContext http,
        IJobEngine jobs,
        CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return Problems.NotFound($"No job '{id}'.");
        }

        if (!CanRead(http, job))
        {
            return Problems.Forbidden($"You may not read job '{id}'.");
        }

        var vmToken = await TakeSecretIfFinishedAsync(jobs, job, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(JobResponse.From(job, vmToken));
    }

    /// <summary>
    /// <c>text/event-stream</c>: one <c>progress</c> event per line — replaying the lines already
    /// recorded, so a client that attaches late (or reconnects) still sees the whole log — and then
    /// exactly one terminal <c>state</c> event, after which the stream ends.
    /// </summary>
    private static async Task<IResult> EventsAsync(
        string id,
        HttpContext http,
        IJobEngine jobs,
        CancellationToken cancellationToken)
    {
        var job = await jobs.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return Problems.NotFound($"No job '{id}'.");
        }

        if (!CanRead(http, job))
        {
            return Problems.Forbidden($"You may not read job '{id}'.");
        }

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Ask intermediaries not to buffer the stream.
        response.Headers["X-Accel-Buffering"] = "no";
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await foreach (var evt in jobs.SubscribeAsync(id, cancellationToken).ConfigureAwait(false))
            {
                string name;
                string payload;

                if (evt.Kind == JobEventKind.Progress)
                {
                    name = "progress";
                    payload = Serialize(JobProgressResponse.From(evt.Progress!));
                }
                else
                {
                    name = "state";
                    var vmToken = await TakeSecretIfFinishedAsync(jobs, evt.Job!, cancellationToken)
                        .ConfigureAwait(false);
                    payload = Serialize(JobResponse.From(evt.Job!, vmToken));
                }

                await response.WriteAsync($"event: {name}\ndata: {payload}\n\n", cancellationToken)
                    .ConfigureAwait(false);
                await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away; nothing to report.
        }

        return TypedResults.Empty;
    }

    /// <summary>
    /// Hands out the job's one-time secret, but only once the job has actually succeeded — a failed
    /// or still-running job has nothing to hand over.
    /// </summary>
    private static Task<string?> TakeSecretIfFinishedAsync(IJobEngine jobs, Job job, CancellationToken cancellationToken) =>
        job.State == JobState.Succeeded
            ? jobs.TakeOneTimeSecretAsync(job.Id, cancellationToken)
            : Task.FromResult<string?>(null);

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(value, ApiJson.Options);

    private static bool CanRead(HttpContext http, Job job) =>
        http.User.IsAdmin() || Ownership.SameName(http.User.NameOrEmpty(), job.Owner);
}
