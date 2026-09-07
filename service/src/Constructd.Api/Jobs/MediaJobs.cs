using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
namespace Constructd.Api.Jobs;

/// <summary>All deletion and publishing holds the shared media gate; capacity is always innermost.</summary>
public sealed class MediaJobs(IMediaStore store, IMediaTransfer transfer, IMediaFiles files, IMediaGate gate,
    ICapacityLedger capacity, IClock clock, IAuditLog audit, IHostConfigStore config, IVmRepository vms, IJobStore jobs)
{
    public Task<MediaConfig?> ConfigurationAsync(CancellationToken ct) => config.GetAsync<MediaConfig>("media", ct);
    public async Task<string[]> ReservationIdsAsync(MediaItem item, CancellationToken ct) =>
        (await capacity.SnapshotAsync(false, ct)).Reservations.Where(r => r.Resource == ReservationResource.Storage && r.Artifact == item.Path && string.Equals(r.ScopeOwner,item.Owner,StringComparison.OrdinalIgnoreCase)).Select(r => r.Id).ToArray();
    public async Task HoldAsync(MediaItem item, CancellationToken ct) => await capacity.ConfirmAsync(await ReservationIdsAsync(item,ct), VmState.Off,ct);
    public async Task<JobOutcome> AcquireAsync(MediaItem item, Uri url, MediaConfig limits, string initiator, IProgress<string> progress, CancellationToken ct)
    {
        await using var handle = await gate.AcquireAsync(item.Id,item.JobId!,ct);
        try
        {
            if ((await store.GetAsync(item.Id,ct))?.State != MediaState.Transferring) throw new MediaException("media-not-ready");
            await HoldAsync(item,ct);
            // A retry may encounter a crash-left partial. Do not reconnect until absence is proven.
            if(!await files.DeleteAsync(files.PathFor(item.Id,true),ct)) throw new MediaException("cleanup-pending");
            var result = await transfer.AcquireAsync(item,url,limits.MaxBytes,TimeSpan.FromMinutes(limits.AcquireTimeoutMinutes),progress,ct);
            var ready = item with { State = MediaState.Ready, SizeBytes = result.SizeBytes, ReservedBytes = result.SizeBytes,
                Sha256 = result.Sha256, SourceUrl = new UriBuilder(result.FinalUrl) { Query = "", Fragment = "" }.Uri.AbsoluteUri, ReadyAt = clock.UtcNow };
            foreach(var id in await ReservationIdsAsync(item,ct)) await capacity.TrimAsync(id,result.SizeBytes,ct);
            if (!await store.TryTransitionAsync(item.Id,MediaState.Transferring,ready,ct)) throw new MediaException("media-not-ready");
            await AuditAsync(item,initiator,"media.acquire",null); return new(new { mediaId = item.Id });
        }
        catch(Exception ex)
        {
            var code = SafeCode(ex,ct); await FailLockedAsync(item,code); await AuditAsync(item,initiator,"media.acquire",code);
            ct.ThrowIfCancellationRequested(); throw new MediaException(code);
        }
    }
    public async Task<MediaItem> VerifyAsync(MediaUpload upload, string initiator, IProgress<string>? progress, CancellationToken ct)
    {
        var item = await store.GetAsync(upload.MediaId,ct) ?? throw new MediaException("upload-not-open");
        string? sha = null; string? error = null;
        try
        {
            sha = await transfer.HashAsync(files.PathFor(item.Id,true),progress,ct);
            if (item.ExpectedSha256 is not null && !string.Equals(sha,item.ExpectedSha256,StringComparison.OrdinalIgnoreCase)) throw new MediaException("checksum-mismatch");
            if (item.ExpectedSha256 is null && !await transfer.LooksLikeIsoAsync(files.PathFor(item.Id,true),ct)) throw new MediaException("not-an-iso");
        }
        catch(Exception ex) { error = SafeCode(ex,ct); }
        await using var handle = await gate.AcquireAsync(item.Id,upload.Id,CancellationToken.None);
        var current = await store.GetUploadAsync(upload.Id,CancellationToken.None);
        if(current?.State != UploadState.Completing) throw new MediaException("upload-not-open");
        if(error is not null)
        {
            await store.TryTransitionUploadAsync(upload.Id,UploadState.Completing,current with { State = UploadState.Aborted },CancellationToken.None);
            await FailLockedAsync(item,error); await AuditAsync(item,initiator,"media.upload.verify",error); ct.ThrowIfCancellationRequested(); throw new MediaException(error);
        }
        try
        {
            await files.PublishAsync(files.PathFor(item.Id,true),item.Path,ct);
            var ready = item with { State = MediaState.Ready, Sha256 = sha, ReadyAt = clock.UtcNow, ReservedBytes = upload.SizeBytes };
            foreach(var id in await ReservationIdsAsync(item,ct)) await capacity.TrimAsync(id,upload.SizeBytes,ct);
            if(!await store.CompleteUploadAsync(upload.Id,ready,ct)) throw new MediaException("upload-not-open");
            await AuditAsync(item,initiator,"media.upload.verify",null); return ready;
        }
        catch(Exception ex)
        {
            var code = SafeCode(ex,ct); await store.TryTransitionUploadAsync(upload.Id,UploadState.Completing,current with { State = UploadState.Aborted },CancellationToken.None);
            await FailLockedAsync(item,code); throw new MediaException(code);
        }
    }
    public async Task FailLockedAsync(MediaItem item,string code)
    {
        var current = await store.GetAsync(item.Id,CancellationToken.None); if(current is null || current.State == MediaState.Ready) return;
        var gone = await DeleteFilesAsync(current);
        if(gone) await capacity.ReleaseAsync(await ReservationIdsAsync(current,CancellationToken.None),VmState.Absent,"media files confirmed absent",CancellationToken.None);
        await store.TryTransitionAsync(item.Id,current.State,current with { State = MediaState.Failed, Error = gone ? code : code + "; cleanup-pending", ReservedBytes = gone ? 0 : current.ReservedBytes },CancellationToken.None);
    }
    private async Task<bool> DeleteFilesAsync(MediaItem item)
    {
        // A corrupt/stale row must never point cleanup at an administrator's external source.
        if (!string.Equals(item.Path,files.PathFor(item.Id),OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) return false;
        var gone = true;
        foreach(var path in new[] {files.PathFor(item.Id,true),item.Path})
            try { gone &= await files.DeleteAsync(path,CancellationToken.None); } catch { gone = false; }
        return gone;
    }
    /// <summary>Caller holds the media gate. False retains a visible tombstone and its entire liability.</summary>
    public async Task<bool> DeleteLockedAsync(MediaItem item,CancellationToken ct)
    {
        if ((await store.ListReferencesAsync(item.Id,ct)).Count != 0) throw new MediaException("media-in-use");
        if(item.State is not MediaState.Ready and not MediaState.Failed and not MediaState.Deleting) throw new MediaException("media-not-ready");
        var deleting = item with { State = MediaState.Deleting };
        if(!await store.TryTransitionAsync(item.Id,item.State,deleting,ct)) throw new MediaException("media-in-use");
        if(!await DeleteFilesAsync(item))
        { await store.TryTransitionAsync(item.Id,MediaState.Deleting,deleting with { Error = "cleanup-pending" },CancellationToken.None); return false; }
        await capacity.ReleaseAsync(await ReservationIdsAsync(item,CancellationToken.None),VmState.Absent,"media files confirmed absent",CancellationToken.None);
        await store.RemoveAsync(item.Id,CancellationToken.None); return true;
    }
    public async Task AbortLockedAsync(MediaUpload upload,bool expired,string initiator,CancellationToken ct)
    {
        if(upload.State is not UploadState.Open and not UploadState.Completing) throw new MediaException("upload-not-open");
        if(!await store.TryTransitionUploadAsync(upload.Id,upload.State,upload with { State = expired ? UploadState.Expired : UploadState.Aborted },ct)) throw new MediaException("upload-not-open");
        if(await store.GetAsync(upload.MediaId,ct) is not { } item) return;
        await FailLockedAsync(item,expired ? "upload-expired" : "cancelled");
        if ((await store.GetAsync(item.Id,CancellationToken.None)) is { ReservedBytes: 0 }) await store.RemoveAsync(item.Id,CancellationToken.None);
        await AuditAsync(item,initiator,expired ? "media.upload.expire" : "media.upload.abort",null);
    }
    /// <summary>Runs during host startup before new media requests are served.</summary>
    public async Task RecoverAsync(CancellationToken ct)
    {
        var existing = (await files.ListAsync(ct)).Select(f=>f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach(var item in await store.ListAsync(null,ct))
        {
            await using var handle=await gate.AcquireAsync(item.Id,"media-recovery",ct);
            var upload=await store.GetUploadAsync(item.Id,ct);
            if(upload is {State:UploadState.Completing} || item.State == MediaState.Transferring ||
                upload is {State:UploadState.Open} && item.State == MediaState.Pending && !existing.Contains(files.PathFor(item.Id,true)))
            {
                if(upload is not null) await store.TryTransitionUploadAsync(upload.Id,upload.State,upload with {State=UploadState.Aborted},ct);
                await FailLockedAsync(item,"interrupted"); await AuditAsync(item,"system","media.recover","interrupted");
            }
        }
    }
    private async Task<IAsyncDisposable?> TryCleanupGateAsync(string id,CancellationToken ct)
    {
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try { return await gate.AcquireAsync(id,"media-cleanup",timeout.Token); }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested) { return null; }
    }
    public async Task<JobOutcome> CleanupItemAsync(string id,string initiator,CancellationToken ct)
    {
        await using var handle=await TryCleanupGateAsync(id,ct);
        if(handle is null) return new(new MediaCleanupResult([], [new(id,"busy")]));
        var item=await store.GetAsync(id,ct);
        if(item is null) return new(new MediaCleanupResult([id], []));
        if((await store.ListReferencesAsync(id,ct)).Count!=0) return new(new MediaCleanupResult([], [new(id,"referenced")]));
        if(item.State!=MediaState.Deleting) return new(new MediaCleanupResult([], [new(id,"not-eligible")]));
        var gone=await DeleteLockedAsync(item,ct);
        await AuditAsync(item,initiator,"media.cleanup",gone ? null : "held-open");
        return new(new MediaCleanupResult(gone ? [id] : [],gone ? [] : [new(id,"held-open")]));
    }
    public async Task<JobOutcome> CleanupAsync(string initiator,IProgress<string>? progress,CancellationToken ct)
    {
        var limits=await ConfigurationAsync(ct) ?? HostAdminDefaults.Media;
        var removed=new HashSet<string>(StringComparer.Ordinal);
        var retained=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var candidate in await store.ListExpiredUploadsAsync(clock.UtcNow,ct))
        {
            await using var handle=await TryCleanupGateAsync(candidate.MediaId,ct);
            if(handle is null) { retained[candidate.MediaId]="busy"; continue; }
            if(await store.GetUploadAsync(candidate.Id,ct) is {State:UploadState.Open} upload && upload.ExpiresAt<=clock.UtcNow)
            {
                await AbortLockedAsync(upload,true,initiator,ct);
                if(await store.GetAsync(upload.MediaId,ct) is null) removed.Add(upload.MediaId);
                else retained[upload.MediaId]="held-open";
            }
        }
        foreach(var candidate in await store.ListAsync(null,ct))
        {
            if(retained.ContainsKey(candidate.Id)) continue;
            await using var handle=await TryCleanupGateAsync(candidate.Id,ct);
            if(handle is null) { retained[candidate.Id]="busy"; continue; }
            var item=await store.GetAsync(candidate.Id,ct); if(item is null) continue;
            // Bootstrap marks interrupted queued/running jobs failed before hosted services start.
            if(item.State==MediaState.Transferring && item.JobId is not null && (await jobs.GetAsync(item.JobId,ct))?.State is JobState.Failed or JobState.Cancelled)
            {
                if(await store.GetUploadAsync(item.Id,ct) is {State:UploadState.Completing} upload)
                    await store.TryTransitionUploadAsync(upload.Id,UploadState.Completing,upload with {State=UploadState.Aborted},ct);
                await FailLockedAsync(item,"interrupted"); item=(await store.GetAsync(item.Id,ct))!;
            }
            if((await store.ListReferencesAsync(item.Id,ct)).Count!=0) { retained[item.Id]="referenced"; continue; }
            if(item.State!=MediaState.Deleting && item.DedicatedTo is { } vm && await vms.GetAsync(vm,ct) is not null)
            { retained[item.Id]="dedicated"; continue; }
            var eligible=item.State==MediaState.Deleting || item.State==MediaState.Failed && item.Created.AddHours(limits.UploadTtlHours)<=clock.UtcNow ||
                item.State==MediaState.Ready && item.DedicatedTo is null && limits.UnreferencedTtlHours is int ttl && (item.LastReferencedAt ?? item.ReadyAt ?? item.Created).AddHours(ttl)<=clock.UtcNow;
            if(!eligible) { retained[item.Id]="not-eligible"; continue; }
            var gone=await DeleteLockedAsync(item,ct);
            if(gone) removed.Add(item.Id); else retained[item.Id]="held-open";
            await AuditAsync(item,initiator,"media.cleanup",gone ? null : "held-open");
        }
        foreach(var group in (await files.ListAsync(ct)).GroupBy(f=>Path.GetFileNameWithoutExtension(f.Path)))
        {
            var id=group.Key;
            if(retained.ContainsKey(id) || removed.Contains(id)) continue;
            await using var handle=await TryCleanupGateAsync(id,ct);
            if(handle is null) { retained[id]="busy"; continue; }
            if(await store.GetAsync(id,ct) is not null) continue;
            if(group.Any(f=>f.Modified.AddHours(1)>clock.UtcNow)) { retained[id]="not-eligible"; continue; }
            var gone=true;
            foreach(var file in group)
                try { gone &= await files.DeleteAsync(file.Path,ct); } catch { gone=false; }
            if(gone) removed.Add(id); else retained[id]="held-open";
            await audit.AppendAsync(new(clock.UtcNow,initiator,"media.cleanup",id,gone ? AuditOutcome.Success : AuditOutcome.Failure,
                $"op=media-cleanup, initiator={initiator}, target={id}, orphan=true, result={(gone ? "removed" : "held-open")}"),CancellationToken.None);
        }
        progress?.Report($"removed {removed.Count} media artifacts; retained {retained.Count}");
        return new(new MediaCleanupResult(removed.Order().ToArray(),retained.OrderBy(p=>p.Key).Select(p=>new MediaCleanupRetained(p.Key,p.Value)).ToArray()));
    }
    private Task AuditAsync(MediaItem item,string initiator,string action,string? error) => audit.AppendAsync(new(clock.UtcNow,initiator,action,item.Id,error is null ? AuditOutcome.Success : AuditOutcome.Failure,
        $"op={item.JobId ?? item.Id}, owner={item.Owner}, initiator={initiator}, target={item.Id}" + (error is null ? "" : $", error={error}")),CancellationToken.None);
    private static string SafeCode(Exception ex,CancellationToken ct) => ex is MediaException safe ? safe.Code : ct.IsCancellationRequested ? "cancelled" : "media-transfer-failed";
}
