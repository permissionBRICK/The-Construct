using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Contracts;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Media;
namespace Constructd.Api.Endpoints;

public static class MediaEndpoints
{
    public static RouteGroupBuilder MapMediaEndpoints(this RouteGroupBuilder api)
    {
        var media = api.MapGroup("/media").RequireAuthorization(Policies.UserOrPrimaryToken);
        media.AddEndpointFilter(async(context,next)=>
        {
            try { return await next(context); }
            catch(NotSupportedException) { return Error("unsupported-capability",new {capability="media",level="unsupported"}); }
        });
        media.MapPost("/acquire",AcquireAsync).Audited("media.acquire").WithName("AcquireMedia");
        media.MapPost("/uploads",BeginAsync).Audited("media.upload.begin").WithName("BeginMediaUpload");
        media.MapPut("/uploads/{id}/chunks/{index:int}",ChunkAsync).Audited("media.upload.chunk","id").WithName("MediaUploadChunk");
        media.MapGet("/uploads/{id}",UploadAsync).WithName("GetMediaUpload");
        media.MapPost("/uploads/{id}/complete",CompleteAsync).Audited("media.upload.complete","id").WithName("CompleteMediaUpload");
        media.MapDelete("/uploads/{id}",AbortAsync).Audited("media.upload.abort","id").WithName("AbortMediaUpload");
        api.MapGet("/media",ListAsync).RequireAuthorization(Policies.UserOrPrimaryToken).WithName("ListMedia");
        media.MapGet("/{id}",GetAsync).WithName("GetMedia");
        media.MapGet("/{id}/references",ReferencesAsync).WithName("GetMediaReferences");
        media.MapDelete("/{id}",DeleteAsync).Audited("media.delete","id").WithName("DeleteMedia");
        media.MapPost("/cleanup",CleanupAsync).RequireAuthorization(Policies.Admin).Audited("media.cleanup").WithName("CleanupMedia");
        return api;
    }
    private static IResult Error(string code,object? extensions = null) => Results.Problem(statusCode: code switch {
        "not-found" => 404, "validation" or "chunk-size" or "url-refused" => 400,
        "checksum-mismatch" or "not-an-iso" => 422, "media-too-large" => 413, "maintenance" => 503, "job-start-failed" => 500, _ => 409 },
        title:code,type:"urn:construct:problem:"+code,extensions:Extensions(code,extensions));
    private static IResult Maintenance(HttpContext http,IMaintenanceGate gate)
    { http.Response.Headers.RetryAfter="30"; return Error("maintenance",new {phase=gate.State.ToString().ToLowerInvariant(),retryAfterSeconds=30}); }
    private static Dictionary<string,object?> Extensions(string code,object? value)
    {
        var result = new Dictionary<string,object?> { ["code"] = code };
        if(value is not null) foreach(var property in JsonSerializer.SerializeToElement(value).EnumerateObject()) result[property.Name] = property.Value.Clone();
        return result;
    }
    private static async Task<string?> OwnerAsync(HttpContext http,IVmRepository vms,CancellationToken ct) =>
        http.User.IsKnownUser() ? http.User.NameOrEmpty() : http.User.IsPrimaryToken() && await vms.GetAsync(http.User.VmTokenName()!,ct) is { Deleting:false, Kind:VmKind.Primary, TokenKind:VmTokenKind.Primary } vm ? vm.Owner : null;
    private static async Task<bool> OwnAsync(HttpContext http,string owner,IVmRepository vms,CancellationToken ct) => http.User.IsAdmin() || Ownership.SameName(owner,await OwnerAsync(http,vms,ct));
    private static string? Key(HttpContext http,string? key) => http.Request.Headers.TryGetValue("X-Construct-Operation-Key",out var value) ? value.ToString() : key;
    private static bool ValidKey(string? key) => key is null || key.Length is >=8 and <=128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-');
    private static bool ValidChecksum(string? sha) => sha is null || sha.Length == 64 && sha.All(Uri.IsHexDigit);
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static string Fingerprint(string route,object body)
    {
        var element = JsonSerializer.SerializeToElement(body,new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var stream = new MemoryStream(); using(var writer = new Utf8JsonWriter(stream))
        { writer.WriteStartObject(); foreach(var property in element.EnumerateObject().OrderBy(p => p.Name,StringComparer.Ordinal)) { writer.WritePropertyName(property.Name); if(property.Value.ValueKind == JsonValueKind.String) writer.WriteStringValue(property.Value.GetString()!.Normalize()); else property.Value.WriteTo(writer); } writer.WriteEndObject(); }
        return Hash(route + "\n" + Encoding.UTF8.GetString(stream.ToArray()));
    }
    private static OperationKeyRecord? Operation(string owner,string kind,string? key,string fingerprint,string id,string? job,IClock clock) =>
        key is null ? null : new(owner,kind,key,fingerprint,id,job,OperationKeyState.InFlight,null,null,null,clock.UtcNow);
    private static int[] Missing(MediaUpload u) => Enumerable.Range(0,checked((int)((u.SizeBytes+u.ChunkBytes-1)/u.ChunkBytes))).Except(u.Received).ToArray();
    private static object UploadResponse(MediaUpload u) => new { uploadId=u.Id,mediaId=u.MediaId,chunkSizeBytes=u.ChunkBytes,chunkCount=(u.SizeBytes+u.ChunkBytes-1)/u.ChunkBytes,expiresAt=u.ExpiresAt,received=u.Received };
    private static async Task<MediaItemResponse> ResponseAsync(MediaItem item,IMediaStore store,CancellationToken ct) => MediaItemResponse.From(item,(await store.ListReferencesAsync(item.Id,ct)).Count);
    private static async Task<bool> DedicatedAllowedAsync(string? dedicated,string owner,HttpContext http,IVmRepository vms,CancellationToken ct)
    {
        if(dedicated is null) return true;
        if(!VmNameValidator.IsValid(dedicated)) return false;
        var vm = await vms.GetAsync(dedicated,ct);
        return vm is null || vm.Kind == VmKind.Child && (http.User.IsAdmin() || Ownership.SameName(vm.Owner,owner));
    }
    private static ReservationRequest Reservation(MediaItem item,string operation,TimeSpan duration,IMediaFiles files) =>
        new(item.Owner,null,operation,[new(ReservationResource.Storage,item.ReservedBytes,item.Path,Path.GetPathRoot(files.Root))],duration);
    private static IResult AdmissionError(AdmissionResult result) => result.Outcome switch {
        AdmissionOutcome.KeyConflict => Error("operation-key-conflict",new { jobId=result.ExistingKey?.JobId,target=result.ExistingKey?.Target }),
        AdmissionOutcome.CapacityRefused => Error("capacity-exhausted",new { resource=result.Capacity?.Resource,scope=result.Capacity?.Scope,requested=result.Capacity?.Requested,allowed=result.Capacity?.AllowedAmount,available=result.Capacity?.Available,reason=result.Capacity?.Reason,epoch=result.Capacity?.Epoch }),
        _ => Error("operation-key-conflict") };
    private static async Task<IResult> BeginAsync(BeginMediaUploadRequest request,HttpContext http,IMediaStore store,IMediaFiles files,
        IVmRepository vms,IMediaGate gate,IMaintenanceGate maintenance,IAdmissionStore admission,IOperationKeyStore keys,MediaJobs work,IClock clock,CancellationToken ct)
    {
        using var activity = maintenance.TryEnter("media-upload",http.TraceIdentifier,null); if(activity is null) return Maintenance(http,maintenance);
        var owner = await OwnerAsync(http,vms,ct); if(owner is null) return Error("not-found");
        var limits = await work.ConfigurationAsync(ct) ?? HostAdminDefaults.Media; var key = Key(http,request.OperationKey);
        if(!ValidKey(key) || !ValidChecksum(request.ExpectedSha256) || !Enum.TryParse<MediaRole>(request.Role,true,out var role) || !Enum.IsDefined(role) || request.SizeBytes <= 0 || !await DedicatedAllowedAsync(request.DedicatedTo,owner,http,vms,ct)) return Error("validation");
        if(request.SizeBytes > limits.MaxBytes) return Error("media-too-large",new {maxBytes=limits.MaxBytes});
        var id = key is null ? Guid.NewGuid().ToString("n") : Hash(owner.ToLowerInvariant()+":"+key)[..32];
        var fingerprint = Fingerprint("/media/uploads",new {request.Name,request.Role,request.SizeBytes,request.ExpectedSha256,request.DedicatedTo});
        await using var admissionLock = await gate.AcquireAsync("$media-admission",http.TraceIdentifier,ct);
        if(key is not null && await keys.GetAsync(owner,"media-upload",key,ct) is { } previous)
        {
            if(previous.Fingerprint != fingerprint || previous.Target != id) return Error("operation-key-conflict");
            return await store.GetUploadAsync(previous.Target,ct) is { } existing ? Results.Ok(UploadResponse(existing)) : Error("upload-not-open");
        }
        if(await store.CountByOwnerAsync(owner,ct) >= limits.MaxItemsPerUser) return Error("media-limit",new {maxItemsPerUser=limits.MaxItemsPerUser});
        if(await store.GetAsync(id,ct) is not null) return Error("operation-key-conflict");
        await using var itemLock = await gate.AcquireAsync(id,http.TraceIdentifier,ct);
        if(await store.GetAsync(id,ct) is not null) return Error("operation-key-conflict");
        var item = new MediaItem(id,owner,MediaNameSanitizer.Clean(request.Name),role,MediaSource.Upload,null,files.PathFor(id),MediaState.Pending,request.SizeBytes,request.SizeBytes,null,request.ExpectedSha256?.ToLowerInvariant(),null,null,request.DedicatedTo,clock.UtcNow,null,null);
        var upload = new MediaUpload(id,id,owner,request.SizeBytes,limits.UploadChunkBytes,[],UploadState.Open,key,clock.UtcNow,clock.UtcNow.AddHours(limits.UploadTtlHours));
        var result = await admission.AdmitAsync(new(Operation(owner,"media-upload",key,fingerprint,id,null,clock),null,null,[item],[upload],[],Reservation(item,http.TraceIdentifier,TimeSpan.FromHours(limits.UploadTtlHours),files),null,null,null,null,false),ct);
        if(result.Outcome != AdmissionOutcome.Accepted) return AdmissionError(result);
        CodedProblems.Audit(http,"media.upload.begin",owner,target:id); http.SetAuditTarget(id);
        try { await files.CreateAsync(files.PathFor(id,true),request.SizeBytes,ct); await work.HoldAsync(item,ct); }
        catch { await work.AbortLockedAsync(upload,false,http.User.Actor(),CancellationToken.None); return Error("media-transfer-failed"); }
        if(key is not null) await keys.CompleteAsync(owner,"media-upload",key,JsonSerializer.Serialize(UploadResponse(upload)),ct);
        return Results.Created("/api/v1/media/uploads/"+id,UploadResponse(upload));
    }
    private static async Task<IResult> AcquireAsync(AcquireMediaRequest request,HttpContext http,IMediaStore store,IMediaFiles files,
        IVmRepository vms,IMediaGate gate,IMaintenanceGate maintenance,IAdmissionStore admission,IOperationKeyStore keys,IPersistedJobRunner runner,
        IMediaDnsResolver dns,IUrlAdmissionPolicy policy,MediaJobs work,IClock clock,CancellationToken ct)
    {
        var activity = maintenance.TryEnter("media-acquire",http.TraceIdentifier,null); if(activity is null) return Maintenance(http,maintenance);
        try
        {
            var owner = await OwnerAsync(http,vms,ct); if(owner is null) return Error("not-found");
            var limits = await work.ConfigurationAsync(ct) ?? HostAdminDefaults.Media; var key = Key(http,request.OperationKey);
            if(!ValidKey(key) || !ValidChecksum(request.ExpectedSha256) || !Enum.TryParse<MediaRole>(request.Role,true,out var role) || !Enum.IsDefined(role) || !await DedicatedAllowedAsync(request.DedicatedTo,owner,http,vms,ct)) return Error("validation");
            if(!Uri.TryCreate(request.Url,UriKind.Absolute,out var url) || url.UserInfo.Length != 0 || url.Scheme is not "http" and not "https" || url.Scheme == "http" && (!limits.AllowHttp || request.ExpectedSha256 is null)) return Error("url-refused",new {reason="scheme"});
            var id = key is null ? Guid.NewGuid().ToString("n") : Hash(owner.ToLowerInvariant()+":"+key)[..32];
            var fingerprint = Fingerprint("/media/acquire",new {request.Url,request.Name,request.Role,request.ExpectedSha256,request.DedicatedTo});
            if(key is not null && await keys.GetAsync(owner,"media-acquire",key,ct) is { } previous)
                return previous.Fingerprint==fingerprint && previous.Target==id ? Results.Ok(new {jobId=previous.JobId,mediaId=id,replayed=true}) : Error("operation-key-conflict");
            UrlAdmission check;
            try { check=policy.Check(url,await dns.ResolveAsync(url.IdnHost,ct),limits.AllowHttp,request.ExpectedSha256 is not null); }
            catch { return Error("url-refused",new {reason="address"}); }
            if(!check.Allowed) return Error("url-refused",new {reason=check.Reason,address=check.Address});
            await using var admissionLock=await gate.AcquireAsync("$media-admission",http.TraceIdentifier,ct);
            if(key is not null && await keys.GetAsync(owner,"media-acquire",key,ct) is { } prior)
                return prior.Fingerprint==fingerprint && prior.Target==id ? Results.Ok(new {jobId=prior.JobId,mediaId=id,replayed=true}) : Error("operation-key-conflict");
            if(await store.CountByOwnerAsync(owner,ct) >= limits.MaxItemsPerUser) return Error("media-limit",new {maxItemsPerUser=limits.MaxItemsPerUser});
            var job = new Job(Guid.NewGuid().ToString("n"),"media-acquire",null,owner,JobState.Queued,[],null,null,clock.UtcNow,null,http.User.Actor(),key);
            var item = new MediaItem(id,owner,MediaNameSanitizer.Clean(request.Name),role,MediaSource.Url,new UriBuilder(url) { Query="",Fragment="" }.Uri.AbsoluteUri,files.PathFor(id),MediaState.Transferring,null,limits.MaxBytes,null,request.ExpectedSha256?.ToLowerInvariant(),null,job.Id,request.DedicatedTo,clock.UtcNow,null,null);
            if(await store.GetAsync(id,ct) is not null) return Error("operation-key-conflict");
            await using(var itemLock=await gate.AcquireAsync(id,http.TraceIdentifier,ct))
            {
                if(await store.GetAsync(id,ct) is not null) return Error("operation-key-conflict");
                var result=await admission.AdmitAsync(new(Operation(owner,"media-acquire",key,fingerprint,id,job.Id,clock),null,null,[item],[],[],Reservation(item,job.Id,TimeSpan.FromMinutes(limits.AcquireTimeoutMinutes),files),null,job,null,null,false),ct);
                if(result.Outcome!=AdmissionOutcome.Accepted) return AdmissionError(result);
            }
            CodedProblems.Audit(http,"media.acquire",owner,target:id); http.SetAuditTarget(id);
            var initiator = http.User.Actor();
            try { await runner.StartPersistedAsync(job,activity,async(p,t) =>
                {
                    try { return await work.AcquireAsync(item,url,limits,initiator,p,t); }
                    finally { if(key is not null) await keys.CompleteAsync(owner,"media-acquire",key,JsonSerializer.Serialize(new {jobId=job.Id,mediaId=id}),CancellationToken.None); }
                },CancellationToken.None); activity=null; }
            catch(Exception ex)
            {
                await admission.MarkStartFailedAsync(job.Id,"job-start-failed",CancellationToken.None);
                await using(var failedLock=await gate.AcquireAsync(id,job.Id,CancellationToken.None)) await work.FailLockedAsync(item,"job-start-failed");
                if(key is not null) await keys.CompleteAsync(owner,"media-acquire",key,JsonSerializer.Serialize(new {jobId=job.Id,mediaId=id}),CancellationToken.None);
                return Error(ex is NotSupportedException ? "unsupported-capability" : "job-start-failed",new {jobId=job.Id,mediaId=id,capability="media",level="unsupported"});
            }
            return Results.Accepted("/api/v1/jobs/"+job.Id,new {jobId=job.Id,mediaId=id});
        }
        finally { activity?.Dispose(); }
    }
    private static async Task<IResult> ChunkAsync(string id,int index,HttpContext http,IMediaStore store,IMediaTransfer transfer,IVmRepository vms,IMediaGate gate,IMaintenanceGate maintenance,IClock clock,CancellationToken ct)
    {
        using var activity = maintenance.TryEnter("media-upload-chunk",http.TraceIdentifier,null); if(activity is null) return Maintenance(http,maintenance);
        var upload = await store.GetUploadAsync(id,ct); if(upload is null || !await OwnAsync(http,upload.Owner,vms,ct)) return Error("not-found");
        await using var handle = await gate.AcquireAsync(upload.MediaId,http.TraceIdentifier,ct); upload = (await store.GetUploadAsync(id,ct))!;
        if(upload.State == UploadState.Expired || upload.State == UploadState.Open && upload.ExpiresAt <= clock.UtcNow) return Error("upload-expired");
        if(upload.State != UploadState.Open) return Error("upload-not-open");
        if(http.Request.ContentType?.Split(';')[0] != "application/octet-stream" || http.Request.ContentLength is not long length) return Error("chunk-size");
        var offset=(long)index*upload.ChunkBytes;
        if(index<0 || offset>=upload.SizeBytes || length!=Math.Min(upload.ChunkBytes,upload.SizeBytes-offset)) return Error("chunk-size");
        if(http.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is {IsReadOnly:false} bodyLimit)
            bodyLimit.MaxRequestBodySize=upload.ChunkBytes;
        if(upload.Received.Contains(index) && !await store.TryTransitionUploadAsync(id,UploadState.Open,upload with {Received=upload.Received.Where(i=>i!=index).ToArray()},ct)) return Error("upload-not-open");
        try { await transfer.WriteChunkAsync(upload,index,http.Request.Body,length,ct); }
        catch(MediaException ex) { return Error(ex.Code); }
        catch(EndOfStreamException) { return Error("chunk-size"); }
        if(!await store.RecordChunkAsync(id,index,ct)) return Error("upload-not-open");
        CodedProblems.Audit(http,"media.upload.chunk",upload.Owner,target:upload.MediaId); return Results.NoContent();
    }
    private static async Task<IResult> UploadAsync(string id,HttpContext http,IMediaStore store,IVmRepository vms,CancellationToken ct) =>
        await store.GetUploadAsync(id,ct) is { } upload && await OwnAsync(http,upload.Owner,vms,ct) ? Results.Ok(new {state=upload.State,received=upload.Received,missing=Missing(upload),expiresAt=upload.ExpiresAt}) : Error("not-found");
    private static async Task<IResult> CompleteAsync(string id,HttpContext http,IMediaStore store,IVmRepository vms,IMediaGate gate,IMaintenanceGate maintenance,
        MediaJobs work,IAdmissionStore admission,IPersistedJobRunner runner,IClock clock,IHostApplicationLifetime lifetime,CancellationToken ct)
    {
        var activity = maintenance.TryEnter("media-verify",http.TraceIdentifier,null); if(activity is null) return Maintenance(http,maintenance);
        try
        {
            var upload = await store.GetUploadAsync(id,ct); if(upload is null || !await OwnAsync(http,upload.Owner,vms,ct)) return Error("not-found");
            Job? job=null;
            await using(var handle = await gate.AcquireAsync(upload.MediaId,http.TraceIdentifier,ct))
            {
                upload = (await store.GetUploadAsync(id,ct))!; var item = await store.GetAsync(upload.MediaId,ct);
                if(item is null) return Error("upload-not-open");
                if(upload.State == UploadState.Done) return Results.Ok(await ResponseAsync(item,store,ct));
                if(upload.State == UploadState.Completing) { if(item.JobId is null) http.Response.Headers.RetryAfter="5"; return item.JobId is null ? Error("upload-not-open") : Results.Accepted(null,new {jobId=item.JobId,mediaId=item.Id}); }
                if(upload.State == UploadState.Expired || upload.ExpiresAt <= clock.UtcNow) return Error("upload-expired");
                if(upload.State != UploadState.Open) return Error("upload-not-open");
                var missing = Missing(upload); if(missing.Length != 0) return Error("upload-incomplete",new {missing});
                if(upload.SizeBytes > 2L << 30)
                {
                    job = new(Guid.NewGuid().ToString("n"),"media-verify",null,upload.Owner,JobState.Queued,[],null,null,clock.UtcNow,null,http.User.Actor());
                    var admitted = await admission.AdmitAsync(new(null,null,null,[],[],[],null,null,job,null,null,false),ct);
                    if(admitted.Outcome != AdmissionOutcome.Accepted) return AdmissionError(admitted);
                }
                await store.TryTransitionUploadAsync(id,UploadState.Open,upload with {State=UploadState.Completing},ct);
                await store.TryTransitionAsync(item.Id,MediaState.Pending,item with {State=MediaState.Transferring,JobId=job?.Id},ct);
            }
            CodedProblems.Audit(http,"media.upload.complete",upload.Owner,target:upload.MediaId);
            if(job is not null)
            {
                var initiator=http.User.Actor();
                try { await runner.StartPersistedAsync(job,activity,async(p,t) => { var ready=await work.VerifyAsync(upload,initiator,p,t); return new(new {mediaId=ready.Id}); },CancellationToken.None); activity=null; }
                catch(Exception ex)
                {
                    await admission.MarkStartFailedAsync(job.Id,"job-start-failed",CancellationToken.None);
                    await using var failedLock=await gate.AcquireAsync(upload.MediaId,job.Id,CancellationToken.None);
                    var current=await store.GetUploadAsync(upload.Id,CancellationToken.None);
                    if(current is {State:UploadState.Completing}) await store.TryTransitionUploadAsync(upload.Id,UploadState.Completing,current with {State=UploadState.Aborted},CancellationToken.None);
                    if(await store.GetAsync(upload.MediaId,CancellationToken.None) is { } failed) await work.FailLockedAsync(failed,"job-start-failed");
                    return Error(ex is NotSupportedException ? "unsupported-capability" : "job-start-failed",new {jobId=job.Id,mediaId=upload.MediaId,capability="media",level="unsupported"});
                }
                return Results.Accepted(null,new {jobId=job.Id,mediaId=upload.MediaId});
            }
            try { var ready=await work.VerifyAsync(upload,http.User.Actor(),null,lifetime.ApplicationStopping); return Results.Created("/api/v1/media/"+ready.Id,await ResponseAsync(ready,store,ct)); }
            catch(MediaException ex) { return Error(ex.Code); }
        }
        finally { activity?.Dispose(); }
    }
    private static async Task<IResult> AbortAsync(string id,HttpContext http,IMediaStore store,IVmRepository vms,IMediaGate gate,IMaintenanceGate maintenance,MediaJobs work,CancellationToken ct)
    {
        using var activity=maintenance.TryEnter("media-upload-abort",http.TraceIdentifier,null); if(activity is null) return Maintenance(http,maintenance);
        var upload=await store.GetUploadAsync(id,ct); if(upload is null || !await OwnAsync(http,upload.Owner,vms,ct)) return Error("not-found");
        await using var handle=await gate.AcquireAsync(upload.MediaId,http.TraceIdentifier,ct); upload=(await store.GetUploadAsync(id,ct))!;
        try { await work.AbortLockedAsync(upload,false,http.User.Actor(),ct); CodedProblems.Audit(http,"media.upload.abort",upload.Owner,target:upload.MediaId); return Results.NoContent(); }
        catch(MediaException ex) { return Error(ex.Code); }
    }
    private static async Task<IResult> ListAsync(string? owner,HttpContext http,IMediaStore store,IVmRepository vms,CancellationToken ct)
    {
        if(!http.User.IsAdmin()) { owner=await OwnerAsync(http,vms,ct); if(owner is null) return Error("not-found"); }
        var result=new List<MediaItemResponse>(); foreach(var item in await store.ListAsync(owner,ct)) result.Add(await ResponseAsync(item,store,ct)); return Results.Ok(result);
    }
    private static async Task<IResult> GetAsync(string id,HttpContext http,IMediaStore store,IVmRepository vms,IUserStore users,CancellationToken ct)
    {
        var item=await store.GetAsync(id,ct); if(item is null) return Error("not-found");
        if(await OwnAsync(http,item.Owner,vms,ct)) return Results.Ok(await ResponseAsync(item,store,ct));
        foreach(var reference in await store.ListReferencesAsync(id,ct))
            if(await vms.GetAsync(reference.VmName,ct) is {Kind:VmKind.Child,Sharing:SharingScope.Host,Deleting:false} vm && await users.GetAsync(vm.Owner,ct) is {Enabled:true})
                return Results.Ok(new {item.Id,item.Name,item.Role,item.SizeBytes});
        return Error("not-found");
    }
    private static async Task<IResult> ReferencesAsync(string id,HttpContext http,IMediaStore store,IVmRepository vms,CancellationToken ct) =>
        await store.GetAsync(id,ct) is { } item && await OwnAsync(http,item.Owner,vms,ct) ? Results.Ok((await store.ListReferencesAsync(id,ct)).Select(r=>new {r.VmName,r.Slot,r.Created})) : Error("not-found");
    private static async Task<IResult> DeleteAsync(string id,HttpContext http,IMediaStore store,IVmRepository vms,IMediaGate gate,
        IMaintenanceGate maintenance,MediaJobs work,IAdmissionStore admission,IPersistedJobRunner runner,IClock clock,CancellationToken ct)
    {
        var activity=maintenance.TryEnter("media-delete",http.TraceIdentifier,null); if(activity is null) return Maintenance(http,maintenance);
        try
        {
            var item=await store.GetAsync(id,ct); if(item is null || !await OwnAsync(http,item.Owner,vms,ct)) return Error("not-found");
            if(item.State is MediaState.Pending or MediaState.Transferring) return Error("media-not-ready");
            Job job;
            await using(var handle=await gate.AcquireAsync(id,http.TraceIdentifier,ct))
            {
                item=await store.GetAsync(id,ct); if(item is null || !await OwnAsync(http,item.Owner,vms,ct)) return Error("not-found");
                var references=await store.ListReferencesAsync(id,ct); if(references.Count!=0) return Error("media-in-use",new {references=references.Select(r=>new {r.VmName,r.Slot})});
                CodedProblems.Audit(http,"media.delete",item.Owner,target:id);
                try { if(await work.DeleteLockedAsync(item,ct)) return Results.NoContent(); }
                catch(MediaException ex) { return Error(ex.Code); }
                job=new(Guid.NewGuid().ToString("n"),"media-cleanup",null,item.Owner,JobState.Queued,[],null,null,clock.UtcNow,null,http.User.Actor());
                var accepted=await admission.AdmitAsync(new(null,null,null,[],[],[],null,null,job,null,null,false),ct);
                if(accepted.Outcome!=AdmissionOutcome.Accepted) return AdmissionError(accepted);
            }
            var initiator=http.User.Actor();
            try { await runner.StartPersistedAsync(job,activity,(_,t)=>work.CleanupItemAsync(id,initiator,t),CancellationToken.None); activity=null; }
            catch(Exception ex)
            {
                await admission.MarkStartFailedAsync(job.Id,"job-start-failed",CancellationToken.None);
                return Error(ex is NotSupportedException ? "unsupported-capability" : "job-start-failed",new {jobId=job.Id,mediaId=id,capability="media",level="unsupported"});
            }
            return Results.Accepted(null,new {jobId=job.Id});
        }
        finally { activity?.Dispose(); }
    }
    private static async Task<IResult> CleanupAsync(HttpContext http,IMaintenanceGate maintenance,MediaJobs work,IJobEngine jobs,CancellationToken ct)
    {
        var activity=maintenance.TryEnter("media-cleanup",http.TraceIdentifier,null); if(activity is null) return Maintenance(http,maintenance);
        try
        {
            var initiator=http.User.Actor(); var job=await jobs.SubmitAsync("media-cleanup",null,http.User.NameOrEmpty(),async(p,t)=> {using(activity) return await work.CleanupAsync(initiator,p,t);},ct);
            CodedProblems.Audit(http,"media.cleanup",http.User.NameOrEmpty(),target:job.Id); return Results.Accepted(null,new {jobId=job.Id});
        }
        catch { activity.Dispose(); throw; }
    }
}
