using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Constructd.Api.Jobs;
using Constructd.Api.Contracts;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Tests.Support;
using Constructd.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
namespace Constructd.Tests.Media;

public sealed class MediaApiTests
{
    private const string Root="/api/v1/media";
    private static byte[] Iso() {var bytes=new byte[40000]; new byte[] {1,67,68,48,48,49,1}.CopyTo(bytes,32768); return bytes;}
    private static async Task<string> Begin(HttpClient client,string? key=null,string role="install",string? sha=null)
    {
        var response=await client.PostAsJsonAsync(Root+"/uploads",new {name="sample.iso",role,sizeBytes=40000,operationKey=key,expectedSha256=sha});
        Assert.True(response.StatusCode == HttpStatusCode.Created,await response.Content.ReadAsStringAsync()); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString()!;
    }
    private static async Task<HttpResponseMessage> Chunk(HttpClient client,string id,int index,byte[] bytes)
    {using var body=new ByteArrayContent(bytes); body.Headers.ContentType=new("application/octet-stream"); return await client.PutAsync(Root+"/uploads/"+id+"/chunks/"+index,body);}
    private static async Task Configure(TestApp app)
    {
        app.Service<InMemoryCapacityLedger>().Mode=CapacityMode.Observe;
        await app.Service<IHostConfigStore>().SetAsync("media",HostAdminDefaults.Media with {UploadChunkBytes=20000},"admin",default);
    }
    [Fact]
    public async Task Upload_resume_replay_and_immutable_completion()
    {
        await using var app=new TestApp(); using var client=await app.CreateUserClientAsync("alice"); await Configure(app);
        var id=await Begin(client,"resume-key"); var bytes=Iso();
        Assert.Equal(HttpStatusCode.BadRequest,(await Chunk(client,id,0,new byte[1])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await Chunk(client,id,2,bytes[..20000])).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await Chunk(client,id,1,bytes[20000..])).StatusCode);
        app.Service<FakeMediaTransfer>().FailWrites=true;
        Assert.Equal(HttpStatusCode.InternalServerError,(await Chunk(client,id,1,bytes[20000..])).StatusCode);
        app.Service<FakeMediaTransfer>().FailWrites=false;
        Assert.Equal(2,(await client.GetFromJsonAsync<JsonElement>(Root+"/uploads/"+id)).GetProperty("missing").GetArrayLength());
        Assert.Equal(HttpStatusCode.NoContent,(await Chunk(client,id,1,bytes[20000..])).StatusCode);
        var status=await client.GetFromJsonAsync<JsonElement>(Root+"/uploads/"+id); Assert.Equal(0,status.GetProperty("missing")[0].GetInt32());
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsync(Root+"/uploads/"+id+"/complete",null)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,(await Chunk(client,id,0,bytes[..20000])).StatusCode);
        var replay=await client.PostAsJsonAsync(Root+"/uploads",new {name="sample.iso",role="install",sizeBytes=40000,operationKey="resume-key"});
        Assert.Equal(HttpStatusCode.OK,replay.StatusCode); Assert.Equal(id,(await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString());
        var done=await client.PostAsync(Root+"/uploads/"+id+"/complete",null); Assert.Equal(HttpStatusCode.Created,done.StatusCode);
        var media=await done.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal("ready",media.GetProperty("state").GetString()); Assert.False(media.TryGetProperty("path",out _));
        Assert.Equal(HttpStatusCode.OK,(await client.PostAsync(Root+"/uploads/"+id+"/complete",null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await Chunk(client,id,0,bytes[..20000])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await client.DeleteAsync(Root+"/uploads/"+id)).StatusCode);
        Assert.Single((await app.Service<ICapacityLedger>().SnapshotAsync(false,default)).Reservations);
        Assert.Equal(HttpStatusCode.NoContent,(await client.DeleteAsync(Root+"/"+id)).StatusCode);
        Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false,default)).Reservations);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Verification_failure_cleans_files_and_reservations(bool checksum)
    {
        await using var app=new TestApp(); using var client=await app.CreateUserClientAsync("alice"); await Configure(app);
        var id=await Begin(client,sha:checksum ? new string('a',64) : null); var bytes=checksum ? Iso() : new byte[40000];
        await Chunk(client,id,0,bytes[..20000]); await Chunk(client,id,1,bytes[20000..]);
        var response=await client.PostAsync(Root+"/uploads/"+id+"/complete",null); Assert.Equal(HttpStatusCode.UnprocessableEntity,response.StatusCode);
        Assert.Equal(checksum ? "checksum-mismatch" : "not-an-iso",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var item=await app.Service<IMediaStore>().GetAsync(id,default); Assert.Equal(MediaState.Failed,item!.State); Assert.Equal(0,item.ReservedBytes);
        Assert.Empty(await app.Service<IMediaFiles>().ListAsync(default)); Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false,default)).Reservations);
    }
    [Fact]
    public async Task Auxiliary_access_reduced_shared_metadata_and_reference_cleanup()
    {
        await using var app=new TestApp(); using var alice=await app.CreateUserClientAsync("alice"); using var bob=await app.CreateUserClientAsync("bob");
        using var admin=await app.CreateUserClientAsync("admin",Role.Admin); await Configure(app);
        var id=await Begin(alice,role:"auxiliary"); var bytes=Iso(); await Chunk(alice,id,0,bytes[..20000]); await Chunk(alice,id,1,bytes[20000..]); await alice.PostAsync(Root+"/uploads/"+id+"/complete",null);
        Assert.Equal(HttpStatusCode.NotFound,(await bob.GetAsync(Root+"/"+id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await bob.GetAsync(Root+"/uploads/"+id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,(await bob.DeleteAsync(Root+"/"+id)).StatusCode);
        var vm=new Vm("child","alice",2,1,20,app.Clock.UtcNow,VmState.Off,null,null,IdlePolicy.Disabled,[],Kind:VmKind.Child,Parent:"parent",Sharing:SharingScope.Host);
        await app.Vms.AddAsync(vm,10,default);
        Assert.True(await app.Service<IMediaStore>().TryAddReferenceAsync(new(id,"child",MediaSlot.Auxiliary,app.Clock.UtcNow),default));
        var shared=await bob.GetFromJsonAsync<JsonElement>(Root+"/"+id); Assert.Equal(new[] {"id","name","role","sizeBytes"},shared.EnumerateObject().Select(p=>p.Name).Order().ToArray());
        Assert.True((await alice.GetFromJsonAsync<JsonElement>(Root+"/"+id)).TryGetProperty("sha256",out _));
        Assert.Equal(HttpStatusCode.NotFound,(await bob.GetAsync(Root+"/"+id+"/references")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await alice.DeleteAsync(Root+"/"+id)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await admin.DeleteAsync(Root+"/"+id)).StatusCode);
        await app.Service<IMediaStore>().RemoveReferenceAsync(id,"child",MediaSlot.Auxiliary,default);
        Assert.Equal(HttpStatusCode.NoContent,(await alice.DeleteAsync(Root+"/"+id)).StatusCode);
    }
    [Fact]
    public async Task Abandoned_upload_expires_and_releases_accounting()
    {
        await using var app=new TestApp(); using var alice=await app.CreateUserClientAsync("alice"); await Configure(app);
        var id=await Begin(alice);
        app.Clock.Advance(TimeSpan.FromHours(25));
        await app.Service<MediaJobs>().CleanupAsync("system",null,default);
        Assert.Equal(UploadState.Expired,(await app.Service<IMediaStore>().GetUploadAsync(id,default))!.State);
        Assert.Null(await app.Service<IMediaStore>().GetAsync(id,default)); Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false,default)).Reservations);
    }
    [Fact]
    public async Task Limits_operation_conflict_maintenance_and_audit()
    {
        await using var app=new TestApp(); using var alice=await app.CreateUserClientAsync("alice");
        await Configure(app);
        await app.Service<IHostConfigStore>().SetAsync("media",HostAdminDefaults.Media with {MaxBytes=50000,MaxItemsPerUser=1,UploadChunkBytes=20000},"admin",default);
        var id=await Begin(alice,"limit-key");
        Assert.Equal(HttpStatusCode.Conflict,(await alice.PostAsJsonAsync(Root+"/uploads",new {name="different",role="install",sizeBytes=40000,operationKey="limit-key"})).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,(await alice.PostAsJsonAsync(Root+"/uploads",new {name="another",role="install",sizeBytes=40000})).StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge,(await alice.PostAsJsonAsync(Root+"/uploads",new {name="large",role="install",sizeBytes=60000})).StatusCode);
        app.Service<IMaintenanceGate>().Enter(MaintenanceState.Draining,null);
        Assert.Equal(HttpStatusCode.ServiceUnavailable,(await Chunk(alice,id,0,new byte[20000])).StatusCode);
        var audit=await app.Service<IAuditLog>().QueryAsync(20,default); Assert.Contains(audit,a=>a.Action=="media.upload.begin"); Assert.Contains(audit,a=>a.Action=="media.upload.chunk" && a.Outcome==AuditOutcome.Failure);
    }
    private sealed class Resolver : IMediaDnsResolver { public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct)=>Task.FromResult(new[]{IPAddress.Parse("93.184.216.34")}); }
    // Runs the already persisted row synchronously; no background processes and no replacement admission logic.
    private sealed class Runner(IJobStore store) : IPersistedJobRunner
    {
        public async Task StartPersistedAsync(Job queued,IDisposable gateHandle,Func<IProgress<string>,CancellationToken,Task<JobOutcome>> work,CancellationToken ct)
        {
            using(gateHandle)
            {
                try {var result=await work(new Progress<string>(),ct); await store.UpsertAsync(queued with {State=JobState.Succeeded,Result=result.Result},ct);}
                catch(MediaException ex) {await store.UpsertAsync(queued with {State=JobState.Failed,Error=ex.Code},ct);}
            }
        }
        public Task SetPhaseAsync(string jobId,string phase,CancellationToken ct)=>Task.CompletedTask;
    }
    [Fact]
    public async Task Url_job_replay_sanitization_and_reservation_trim()
    {
        await using var app=new TestApp(configureServices:services=> {services.AddSingleton<IMediaDnsResolver,Resolver>(); services.AddSingleton<IPersistedJobRunner,Runner>();});
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app); var url=new Uri("https://public.example/install.iso?credential=topsecret"); app.Service<FakeMediaTransfer>().Sources[url]=Iso();
        var request=new {url=url.AbsoluteUri,name="install.iso",role="install",operationKey="acquire-key"};
        var response=await alice.PostAsJsonAsync(Root+"/acquire",request); Assert.True(response.StatusCode == HttpStatusCode.Accepted,await response.Content.ReadAsStringAsync());
        var accepted=await response.Content.ReadFromJsonAsync<JsonElement>(); var id=accepted.GetProperty("mediaId").GetString()!;
        var item=(await app.Service<IMediaStore>().GetAsync(id,default))!; Assert.Equal(MediaState.Ready,item.State); Assert.DoesNotContain("?",item.SourceUrl); Assert.Equal(40000,item.ReservedBytes);
        var replay=await alice.PostAsJsonAsync(Root+"/acquire",request); Assert.Equal(HttpStatusCode.OK,replay.StatusCode); Assert.Equal(accepted.GetProperty("jobId").GetString(),(await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString());
        Assert.DoesNotContain("topsecret",JsonSerializer.Serialize(await app.Service<IAuditLog>().QueryAsync(100,default)));
        Assert.Equal(40000,Assert.Single((await app.Service<ICapacityLedger>().SnapshotAsync(false,default)).Reservations).Amount);
    }

    [Fact]
    public async Task Held_file_cleanup_retains_charge_and_is_retryable()
    {
        var files=new InMemoryMediaFiles();
        await using var app=new TestApp(configureServices:services=> {
            services.AddSingleton<IMediaFiles>(files);
            services.AddSingleton<IMediaTransfer>(new HttpMediaTransfer(files,new Resolver(),new Constructd.Core.Logic.UrlAdmissionRules(),new MediaConnectionFactory()));
        });
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app);
        var id=await Begin(alice); var bytes=Iso(); await Chunk(alice,id,0,bytes[..20000]); await Chunk(alice,id,1,bytes[20000..]);
        Assert.Equal(HttpStatusCode.Created,(await alice.PostAsync(Root+"/uploads/"+id+"/complete",null)).StatusCode);
        files.HeldOpen=true;
        var item=(await app.Service<IMediaStore>().GetAsync(id,default))!;
        await using(var handle=await app.Service<IMediaGate>().AcquireAsync(id,"test",default)) Assert.False(await app.Service<MediaJobs>().DeleteLockedAsync(item,default));
        var retained=(await app.Service<IMediaStore>().GetAsync(id,default))!;
        Assert.Equal(MediaState.Deleting,retained.State); Assert.Equal("cleanup-pending",retained.Error); Assert.Equal(40000,retained.ReservedBytes);
        Assert.Equal(40000,Assert.Single((await app.Service<ICapacityLedger>().SnapshotAsync(false,default)).Reservations).Amount);
        var held=Assert.IsType<MediaCleanupResult>((await app.Service<MediaJobs>().CleanupAsync("system",null,default)).Result);
        Assert.Empty(held.Removed); Assert.Contains(held.Retained,r=>r.Id==id && r.Reason=="held-open");
        files.HeldOpen=false; var collected=Assert.IsType<MediaCleanupResult>((await app.Service<MediaJobs>().CleanupAsync("system",null,default)).Result);
        Assert.Contains(id,collected.Removed); Assert.DoesNotContain(collected.Retained,r=>r.Id==id);
        Assert.Null(await app.Service<IMediaStore>().GetAsync(id,default)); Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false,default)).Reservations);
    }
    private sealed class BlockingTransfer(IMediaTransfer inner) : IMediaTransfer
    {
        public TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<string> HashAsync(string path,IProgress<string>? progress,CancellationToken ct) {Started.SetResult(); await Resume.Task.WaitAsync(ct); return new string('a',64);}
        public async Task<TransferResult> AcquireAsync(MediaItem item,Uri source,long maxBytes,TimeSpan timeout,IProgress<string>? progress,CancellationToken ct)
        {Started.SetResult(); await Resume.Task.WaitAsync(ct); return await inner.AcquireAsync(item,source,maxBytes,timeout,progress,ct);}
        public Task WriteChunkAsync(MediaUpload upload,int index,Stream body,long length,CancellationToken ct)=>inner.WriteChunkAsync(upload,index,body,length,ct);
        public Task<bool> LooksLikeIsoAsync(string path,CancellationToken ct)=>Task.FromResult(true);
        public Task<bool> TryDeleteAsync(string path,CancellationToken ct)=>inner.TryDeleteAsync(path,ct);
        public Task<IReadOnlyList<string>> ListFilesAsync(CancellationToken ct)=>inner.ListFilesAsync(ct);
    }
    [Fact]
    public async Task Abort_wins_over_hash_completion_and_cleanup_does_not_expire_completing_upload()
    {
        var files=new InMemoryMediaFiles(); var transfer=new BlockingTransfer(new HttpMediaTransfer(files,new Resolver(),new Constructd.Core.Logic.UrlAdmissionRules(),new MediaConnectionFactory()));
        await using var app=new TestApp(configureServices:services=> {services.AddSingleton<IMediaFiles>(files); services.AddSingleton<IMediaTransfer>(transfer);});
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app); var id=await Begin(alice);
        await Chunk(alice,id,0,Iso()[..20000]); await Chunk(alice,id,1,Iso()[20000..]);
        var completion=alice.PostAsync(Root+"/uploads/"+id+"/complete",null);
        await transfer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); app.Clock.Advance(TimeSpan.FromHours(25));
        await app.Service<MediaJobs>().CleanupAsync("system",null,default);
        Assert.Equal(UploadState.Completing,(await app.Service<IMediaStore>().GetUploadAsync(id,default))!.State);
        Assert.Equal(HttpStatusCode.NoContent,(await alice.DeleteAsync(Root+"/uploads/"+id)).StatusCode);
        transfer.Resume.SetResult(); Assert.Equal(HttpStatusCode.Conflict,(await completion).StatusCode);
        Assert.Null(await app.Service<IMediaStore>().GetAsync(id,default)); Assert.Empty(await files.ListAsync(default));
    }
    [Fact]
    public async Task Failed_acquisition_keeps_partial_accounting_until_cleanup_can_delete()
    {
        var files=new InMemoryMediaFiles();
        await using var app=new TestApp(configureServices:services=> {services.AddSingleton<IMediaFiles>(files);});
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app); var id=await Begin(alice);
        var item=(await app.Service<IMediaStore>().GetAsync(id,default))!;
        Assert.True(await app.Service<IMediaStore>().TryTransitionAsync(id,MediaState.Pending,item with {State=MediaState.Transferring},default));
        files.HeldOpen=true; await app.Service<MediaJobs>().RecoverAsync(default);
        var retained=(await app.Service<IMediaStore>().GetAsync(id,default))!;
        Assert.Equal(MediaState.Failed,retained.State); Assert.Contains("cleanup-pending",retained.Error); Assert.Equal(40000,retained.ReservedBytes);
        files.HeldOpen=false; app.Clock.Advance(TimeSpan.FromHours(25)); await app.Service<MediaJobs>().CleanupAsync("system",null,default);
        Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false,default)).Reservations);
    }
    [Fact]
    public async Task Item_limit_serializes_concurrent_begins_and_capacity_refusal_rolls_back()
    {
        await using var app=new TestApp(); using var alice=await app.CreateUserClientAsync("alice");
        var refused=await alice.PostAsJsonAsync(Root+"/uploads",new {name="refused",role="install",sizeBytes=40000,operationKey="refused-key"}); Assert.Equal(HttpStatusCode.Conflict,refused.StatusCode);
        Assert.Empty(await app.Service<IMediaStore>().ListAsync(null,default)); Assert.Null(await app.Service<IOperationKeyStore>().GetAsync("alice","media-upload","refused-key",default));
        await Configure(app); await app.Service<IHostConfigStore>().SetAsync("media",HostAdminDefaults.Media with {MaxItemsPerUser=1},"admin",default);
        var results=await Task.WhenAll(Enumerable.Range(0,2).Select(_=>alice.PostAsJsonAsync(Root+"/uploads",new {name="item",role="install",sizeBytes=40000})));
        Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Created); Assert.Single(results,r=>r.StatusCode==HttpStatusCode.Conflict); Assert.Single(await app.Service<IMediaStore>().ListAsync(null,default));
    }

    [Fact]
    public async Task Primary_token_uses_owner_media_and_legacy_token_is_refused()
    {
        await using var app=new TestApp(); using var alice=await app.CreateUserClientAsync("alice"); await Configure(app);
        var id=await Begin(alice); var token="test-primary-secret";
        var vm=new Vm("primary","alice",2,1,20,app.Clock.UtcNow,VmState.Off,null,Constructd.Core.Logic.TokenHasher.Hash(token),IdlePolicy.Disabled,[],TokenKind:VmTokenKind.Primary);
        await app.Vms.AddAsync(vm,10,default); using var primary=app.CreateVmTokenClient(token);
        Assert.Equal(HttpStatusCode.OK,(await primary.GetAsync(Root+"/"+id)).StatusCode);
        var created=await Begin(primary); Assert.Equal("alice",(await app.Service<IMediaStore>().GetAsync(created,default))!.Owner);
        await app.Service<IVmMetadataStore>().SetTokenAsync("primary",Constructd.Core.Logic.TokenHasher.Hash(token),VmTokenKind.Legacy,default);
        Assert.Equal(HttpStatusCode.Forbidden,(await primary.GetAsync(Root+"/"+id)).StatusCode);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Large_completion_uses_persisted_verify_job_and_replays_done(bool supported)
    {
        BlockingTransfer? transfer=null;
        await using var app=new TestApp(configureServices:services=> {
            if(supported) services.AddSingleton<IPersistedJobRunner,Runner>();
            services.AddSingleton<IMediaTransfer>(sp=> {transfer=new BlockingTransfer(sp.GetRequiredService<FakeMediaTransfer>()); transfer.Resume.SetResult(); return transfer;});
        });
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app);
        var response=await alice.PostAsJsonAsync(Root+"/uploads",new {name="large.iso",role="install",sizeBytes=(2L<<30)+1,expectedSha256=new string('a',64)});
        Assert.Equal(HttpStatusCode.Created,response.StatusCode); var id=(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("uploadId").GetString()!;
        var store=app.Service<IMediaStore>(); var upload=(await store.GetUploadAsync(id,default))!;
        // Protocol state fixture; the sparse file avoids allocating a multi-GiB test payload.
        Assert.True(await store.TryTransitionUploadAsync(id,UploadState.Open,upload with {Received=Enumerable.Range(0,(int)((upload.SizeBytes+upload.ChunkBytes-1)/upload.ChunkBytes)).ToArray()},default));
        var done=await alice.PostAsync(Root+"/uploads/"+id+"/complete",null);
        if(!supported)
        {
            Assert.Equal(HttpStatusCode.Conflict,done.StatusCode); Assert.Equal("unsupported-capability",(await done.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Equal(MediaState.Failed,(await store.GetAsync(id,default))!.State); Assert.Equal(UploadState.Aborted,(await store.GetUploadAsync(id,default))!.State);
            Assert.Equal(0,(await store.GetAsync(id,default))!.ReservedBytes); return;
        }
        Assert.Equal(HttpStatusCode.Accepted,done.StatusCode);
        Assert.Equal(MediaState.Ready,(await store.GetAsync(id,default))!.State); Assert.Equal(UploadState.Done,(await store.GetUploadAsync(id,default))!.State);
        Assert.Equal(HttpStatusCode.OK,(await alice.PostAsync(Root+"/uploads/"+id+"/complete",null)).StatusCode);
    }

    [Fact]
    public async Task Disconnect_during_inline_hash_does_not_abort_completed_upload()
    {
        var files=new InMemoryMediaFiles(); var transfer=new BlockingTransfer(new HttpMediaTransfer(files,new Resolver(),new Constructd.Core.Logic.UrlAdmissionRules(),new MediaConnectionFactory()));
        await using var app=new TestApp(configureServices:services=> {services.AddSingleton<IMediaFiles>(files); services.AddSingleton<IMediaTransfer>(transfer);});
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app); var id=await Begin(alice);
        await Chunk(alice,id,0,Iso()[..20000]); await Chunk(alice,id,1,Iso()[20000..]);
        using var disconnect=new CancellationTokenSource(); var completion=alice.PostAsync(Root+"/uploads/"+id+"/complete",null,disconnect.Token);
        await transfer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); disconnect.Cancel();
        var retry=await alice.PostAsync(Root+"/uploads/"+id+"/complete",null); Assert.Equal(HttpStatusCode.Conflict,retry.StatusCode); Assert.NotNull(retry.Headers.RetryAfter);
        transfer.Resume.SetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(async()=>await completion);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while((await app.Service<IMediaStore>().GetUploadAsync(id,deadline.Token))!.State!=UploadState.Done) await Task.Delay(10,deadline.Token);
        Assert.Equal(MediaState.Ready,(await app.Service<IMediaStore>().GetAsync(id,default))!.State);
        Assert.Equal(HttpStatusCode.OK,(await alice.PostAsync(Root+"/uploads/"+id+"/complete",null)).StatusCode);
    }
    [Fact]
    public async Task Same_key_across_upload_and_acquire_is_a_conflict()
    {
        await using var app=new TestApp(configureServices:services=>services.AddSingleton<IMediaDnsResolver,Resolver>());
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app); await Begin(alice,"shared-key");
        var response=await alice.PostAsJsonAsync(Root+"/acquire",new {url="https://public.example/media.iso",role="install",operationKey="shared-key"});
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode); Assert.Equal("operation-key-conflict",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Single(await app.Service<IMediaStore>().ListAsync(null,default));
    }
    [Fact]
    public async Task Busy_acquire_does_not_block_delete_or_cleanup()
    {
        using var inner=new FakeMediaTransfer(); var transfer=new BlockingTransfer(inner);
        await using var app=new TestApp(configureServices:services=> {services.AddSingleton<IMediaTransfer>(transfer); services.AddSingleton<IMediaFiles>(new MediaFileStore(inner.Root));});
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app);
        var item=MediaStorageTests.Item(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("alice:busy-key")))[..32]); item=item with {Path=app.Service<IMediaFiles>().PathFor(item.Id),State=MediaState.Transferring,JobId="busy-job"};
        await app.Service<IMediaStore>().AddAsync(item,default); var url=new Uri("https://public.example/media.iso"); inner.Sources[url]=Iso();
        var acquire=app.Service<MediaJobs>().AcquireAsync(item,url,HostAdminDefaults.Media,"alice",new Progress<string>(),default);
        await transfer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var conflict=await alice.PostAsJsonAsync(Root+"/uploads",new {name="collision.iso",role="install",sizeBytes=40000,operationKey="busy-key"}).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(HttpStatusCode.Conflict,conflict.StatusCode);
            var response=await alice.DeleteAsync(Root+"/"+item.Id).WaitAsync(TimeSpan.FromSeconds(2)); Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
            Assert.Equal("media-not-ready",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            var result=Assert.IsType<MediaCleanupResult>((await app.Service<MediaJobs>().CleanupAsync("system",null,default).WaitAsync(TimeSpan.FromSeconds(3))).Result);
            Assert.Contains(result.Retained,r=>r.Id==item.Id && r.Reason=="busy");
        }
        finally {transfer.Resume.TrySetResult(); await acquire;}
    }
    [Fact]
    public async Task Unsupported_runner_fails_media_immediately_without_losing_accounting()
    {
        await using var app=new TestApp(configureServices:services=>services.AddSingleton<IMediaDnsResolver,Resolver>());
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app);
        var response=await alice.PostAsJsonAsync(Root+"/acquire",new {url="https://public.example/media.iso",role="install",operationKey="no-runner"});
        Assert.Equal(HttpStatusCode.Conflict,response.StatusCode); Assert.Equal("unsupported-capability",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var item=Assert.Single(await app.Service<IMediaStore>().ListAsync(null,default)); Assert.Equal(MediaState.Failed,item.State); Assert.Equal(0,item.ReservedBytes);
        Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false,default)).Reservations);
    }
    [Fact]
    public async Task Unsupported_sqlite_admission_returns_coded_refusal()
    {
        var root=Path.Combine(Path.GetTempPath(),"media-admission-"+Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            await using var app=TestApp.WithSqlite(Path.Combine(root,"db")); using var alice=await app.CreateUserClientAsync("alice");
            var response=await alice.PostAsJsonAsync(Root+"/uploads",new {name="upload.iso",role="install",sizeBytes=40000});
            Assert.Equal(HttpStatusCode.Conflict,response.StatusCode); Assert.Equal("unsupported-capability",(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
            Assert.Empty(await app.Service<IMediaStore>().ListAsync(null,default));
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true);}
    }

    [Fact]
    public async Task Cancelling_acquire_cleans_partial_and_preserves_job_cancellation_signal()
    {
        var files=new InMemoryMediaFiles(); var transfer=new BlockingTransfer(new HttpMediaTransfer(files,new Resolver(),new Constructd.Core.Logic.UrlAdmissionRules(),new MediaConnectionFactory()));
        await using var app=new TestApp(configureServices:services=> {services.AddSingleton<IMediaFiles>(files); services.AddSingleton<IMediaTransfer>(transfer);});
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app); var id=await Begin(alice);
        var item=(await app.Service<IMediaStore>().GetAsync(id,default))!; item=item with {State=MediaState.Transferring,JobId="cancelled-job"};
        await app.Service<IMediaStore>().TryTransitionAsync(id,MediaState.Pending,item,default);
        using var cancel=new CancellationTokenSource();
        var acquire=app.Service<MediaJobs>().AcquireAsync(item,new Uri("https://public.example/media.iso"),HostAdminDefaults.Media,"alice",new Progress<string>(),cancel.Token);
        await transfer.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>acquire);
        var failed=(await app.Service<IMediaStore>().GetAsync(id,default))!; Assert.Equal(MediaState.Failed,failed.State); Assert.Equal("cancelled",failed.Error); Assert.Equal(0,failed.ReservedBytes);
        Assert.Empty(await files.ListAsync(default)); Assert.Empty((await app.Service<ICapacityLedger>().SnapshotAsync(false,default)).Reservations);
    }

    [Fact]
    public async Task Primary_delete_retry_is_scoped_to_one_item_and_records_initiator()
    {
        var files=new InMemoryMediaFiles();
        await using var app=new TestApp(configureServices:services=> {
            services.AddSingleton<IMediaFiles>(files); services.AddSingleton<IPersistedJobRunner,Runner>();
            services.AddSingleton<IMediaTransfer>(new HttpMediaTransfer(files,new Resolver(),new Constructd.Core.Logic.UrlAdmissionRules(),new MediaConnectionFactory()));
        });
        using var alice=await app.CreateUserClientAsync("alice"); await Configure(app); var id=await Begin(alice);
        await Chunk(alice,id,0,Iso()[..20000]); await Chunk(alice,id,1,Iso()[20000..]); await alice.PostAsync(Root+"/uploads/"+id+"/complete",null);
        var other=MediaStorageTests.Item(); other=other with {Owner="bob",State=MediaState.Ready,ReadyAt=app.Clock.UtcNow.AddHours(-2),Path=files.PathFor(other.Id)};
        await app.Service<IMediaStore>().AddAsync(other,default); await files.CreateAsync(other.Path,40000,default);
        await app.Service<IHostConfigStore>().SetAsync("media",HostAdminDefaults.Media with {UnreferencedTtlHours=1},"admin",default);
        var token="delete-retry-test-token";
        await app.Vms.AddAsync(new Vm("primary","alice",2,1,20,app.Clock.UtcNow,VmState.Off,null,Constructd.Core.Logic.TokenHasher.Hash(token),IdlePolicy.Disabled,[],TokenKind:VmTokenKind.Primary),10,default);
        using var primary=app.CreateVmTokenClient(token); files.HeldOpen=true;
        var response=await primary.DeleteAsync(Root+"/"+id); Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var jobId=(await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("jobId").GetString()!;
        var job=(await app.Service<IJobStore>().GetAsync(jobId,default))!; Assert.Equal("vm:primary",job.Initiator); Assert.Equal("alice",job.Owner);
        Assert.Equal(id,Assert.Single(Assert.IsType<MediaCleanupResult>(job.Result).Retained).Id);
        Assert.Equal(MediaState.Ready,(await app.Service<IMediaStore>().GetAsync(other.Id,default))!.State);
    }
}
