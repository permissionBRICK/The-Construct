using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Fakes;
using Constructd.Sqlite;
using Constructd.Windows.Media;
namespace Constructd.Tests.Media;

public sealed class MediaStorageTests
{
    public static MediaItem Item(string? id = null) => new(id ?? Guid.NewGuid().ToString("n"), "alice", "install.iso", MediaRole.Install, MediaSource.Upload, null, "unused.iso", MediaState.Pending, 40000, 40000, null, null, null, null, null, DateTimeOffset.UtcNow, null, null);
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Registry_round_trip_references_and_upload_cas(bool sqlite)
    {
        var root = Path.Combine(Path.GetTempPath(), "media-test-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            var db = new SqliteDatabase(Path.Combine(root, "db")); db.EnsureCreated();
            IMediaStore store = sqlite ? new SqliteMediaStore(db) : new InMemoryMediaStore();
            var item = Item(); await store.AddAsync(item, default);
            Assert.Equal(item, await store.GetAsync(item.Id, default)); Assert.Equal(1, await store.CountByOwnerAsync("ALICE", default));
            var reference = new MediaReference(item.Id, "child", MediaSlot.Install, item.Created);
            Assert.False(await store.TryAddReferenceAsync(reference, default));
            Assert.True(await store.TryTransitionAsync(item.Id, MediaState.Pending, item with { State = MediaState.Ready }, default));
            Assert.True(await store.TryAddReferenceAsync(reference, default));
            Assert.True(await store.TryAddReferenceAsync(reference with { VmName = "CHILD" }, default));
            Assert.Single(await store.ListReferencesAsync(item.Id, default));
            Assert.False(await store.TryTransitionAsync(item.Id, MediaState.Ready, item with { State = MediaState.Deleting }, default));
            Assert.False(await store.RemoveAsync(item.Id, default));
            Assert.True(await store.RemoveReferenceAsync(item.Id, "CHILD", MediaSlot.Install, default));
            Assert.True(await store.TryTransitionAsync(item.Id, MediaState.Ready, item with { State = MediaState.Deleting }, default));
            Assert.False(await store.TryAddReferenceAsync(reference, default));
            var upload = new MediaUpload("upload", item.Id, "alice", 40000, 20000, [], UploadState.Open, null, item.Created, item.Created.AddHours(24));
            await store.AddUploadAsync(upload, default); Assert.True(await store.RecordChunkAsync("upload", 1, default));
            Assert.Equal([1], (await store.GetUploadAsync("upload", default))!.Received);
            Assert.False(await store.RecordChunkAsync("upload", 2, default));
            Assert.True(await store.TryTransitionUploadAsync("upload", UploadState.Open, upload with { State = UploadState.Completing }, default));
            Assert.False(await store.RecordChunkAsync("upload", 0, default));
            Assert.False(await store.CompleteUploadAsync(upload.Id,item with {State=MediaState.Ready},default));
            Assert.True(await store.TryTransitionAsync(item.Id,MediaState.Deleting,item with {State=MediaState.Transferring},default));
            Assert.True(await store.CompleteUploadAsync(upload.Id,item with {State=MediaState.Ready},default));
            Assert.Equal(UploadState.Done,(await store.GetUploadAsync(upload.Id,default))!.State);
            Assert.Equal(MediaState.Ready,(await store.GetAsync(item.Id,default))!.State);
            Assert.False(await store.RecordChunkAsync(upload.Id,0,default));
            Assert.True(await store.RemoveAsync(item.Id, default));
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Media_files_refuse_external_source_and_symlinks()
    {
        var root = Path.Combine(Path.GetTempPath(), "media-test-" + Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            var files = new MediaFileStore(Path.Combine(root,"media") + Path.DirectorySeparatorChar);
            await Assert.ThrowsAsync<MediaException>(() => files.CreateAsync(Path.Combine(root, "source.iso"), 1, default));
            var path = files.PathFor(new string('a',32), true); await files.CreateAsync(path, 40000, default);
            await files.WriteAsync(path, 32768, new byte[] {1,67,68,48,48,49,1}, default);
            await files.PublishAsync(path, files.PathFor(new string('a',32)), default);
            Assert.Single(await files.ListAsync(default));
            File.CreateSymbolicLink(path, Path.Combine(root,"source.iso"));
            await Assert.ThrowsAsync<MediaException>(() => files.DeleteAsync(path, default));
        }
        finally { Directory.Delete(root,true); }
    }

    [Fact]
    public async Task Sqlite_concurrent_chunks_retain_both_indexes()
    {
        var root=Path.Combine(Path.GetTempPath(),"media-test-"+Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            var db=new SqliteDatabase(Path.Combine(root,"db")); db.EnsureCreated(); var store=new SqliteMediaStore(db); var item=Item(); await store.AddAsync(item,default);
            var upload=new MediaUpload(item.Id,item.Id,item.Owner,40000,20000,[],UploadState.Open,null,item.Created,item.Created.AddHours(24)); await store.AddUploadAsync(upload,default);
            using var start=new ManualResetEventSlim();
            var first=Task.Run(async()=> {start.Wait(); return await store.RecordChunkAsync(upload.Id,0,default);});
            var second=Task.Run(async()=> {start.Wait(); return await store.RecordChunkAsync(upload.Id,1,default);}); start.Set();
            Assert.All(await Task.WhenAll(first,second),Assert.True); Assert.Equal([0,1],(await store.GetUploadAsync(upload.Id,default))!.Received);
        }
        finally {Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(root,true);}
    }
}
