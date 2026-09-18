using Constructd.Api.Auth;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
namespace Constructd.Api.Endpoints;

public static partial class MediaEndpoints
{
    public sealed record AcquireWindowsRequest(string Windows, string Edition = "pro", string Lang = "en");
    private static async Task<IResult> AcquireWindowsAsync(AcquireWindowsRequest request, HttpContext http, IVmRepository vms,
        IJobEngine jobs, WindowsMediaJobs work, IMaintenanceGate maintenance, CancellationToken ct)
    {
        var product = request.Windows switch { "11" => "win11", "server-2022" => "server2022", "server-2025" => "server2025", _ => request.Windows };
        try { _ = WindowsUnattendRenderer.Parse(product + "-" + request.Edition); }
        catch (ChildValidationException) { return Error("validation"); }
        var owner = await OwnerAsync(http, vms, ct); if (owner is null) return Error("not-found");
        var activity = maintenance.TryEnter("windows-media-acquire", http.TraceIdentifier, null); if (activity is null) return Maintenance(http, maintenance);
        try
        {
            var job = await jobs.SubmitAsync("windows-media-acquire", null, owner, async (p, t) =>
            { using (activity) { var item = await work.AcquireAsync(product, request.Edition, request.Lang, p, t); return new(new { mediaId = item.Id }); } }, ct);
            CodedProblems.Audit(http, "media.acquire-windows", owner, target: job.Id);
            return Results.Accepted(null, new { jobId = job.Id });
        }
        catch { activity.Dispose(); throw; }
    }
    private static async Task<IResult> PrepareWindowsAsync(string id, HttpContext http, IVmRepository vms, IMediaStore store,
        IMediaGate gate, IJobEngine jobs, WindowsMediaJobs work, IMaintenanceGate maintenance, CancellationToken ct)
    {
        var item = await store.GetAsync(id, ct);
        if (item is null || !(item.Shared || await OwnAsync(http, item.Owner, vms, ct))) return Error("not-found");
        if (item.State != MediaState.Ready || item.Role != MediaRole.Install) return Error("media-not-ready");
        var owner = await OwnerAsync(http, vms, ct); if (owner is null) return Error("not-found");
        var activity = maintenance.TryEnter("windows-media-prepare", http.TraceIdentifier, null); if (activity is null) return Maintenance(http, maintenance);
        try
        {
            var job = await jobs.SubmitAsync("windows-media-prepare", null, owner, async (p, t) =>
            {
                using (activity)
                {
                    await using var sourceLock = await gate.AcquireAsync(id, "windows-prepare", t);
                    var current = await store.GetAsync(id, t) ?? throw new MediaException("not-found");
                    var ready = await work.PrepareAsync(current, p, t); return new(new { mediaId = ready.Id });
                }
            }, ct);
            CodedProblems.Audit(http, "media.prepare-windows", owner, target: id);
            return Results.Accepted(null, new { jobId = job.Id });
        }
        catch { activity.Dispose(); throw; }
    }
}
