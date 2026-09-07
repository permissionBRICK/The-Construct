using System.Net;
using Constructd.Api.Composition;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Fakes;
using Constructd.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
namespace Constructd.Tests.Media;
public sealed class MediaCompositionTests
{
    private sealed class Resolver : IMediaDnsResolver {public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct)=>Task.FromResult(new[]{IPAddress.Parse("93.184.216.34")});}
    private sealed class Connections : IMediaConnectionFactory
    {
        public List<IPAddress> Seen=[];
        public Task<(Stream,IPAddress)> ConnectAsync(IPAddress address,int port,CancellationToken ct)
        {Seen.Add(address); throw new MediaException("test-no-network");}
    }
    [Fact]
    public async Task Production_registration_uses_pinned_connection_callback()
    {
        var services=new ServiceCollection(); services.AddMediaPlatform(new ConstructdOptions {Fake=false});
        var connections=new Connections(); services.AddSingleton<IMediaConnectionFactory>(connections); services.AddSingleton<IMediaDnsResolver,Resolver>(); services.AddSingleton<IMediaFiles>(new InMemoryMediaFiles());
        await using var provider=services.BuildServiceProvider(); var transfer=provider.GetRequiredService<IMediaTransfer>(); Assert.IsType<HttpMediaTransfer>(transfer);
        var item=MediaStorageTests.Item() with {ExpectedSha256=new string('a',64)};
        await Assert.ThrowsAsync<MediaException>(()=>transfer.AcquireAsync(item,new Uri("http://public.example/"),50000,TimeSpan.FromSeconds(5),null,default));
        Assert.Equal("93.184.216.34",Assert.Single(connections.Seen).ToString());
    }

    [Fact]
    public void Production_root_cannot_overlap_primary_catalog()
    {
        var root=Path.Combine(Path.GetTempPath(),"catalog"); var options=new ConstructdOptions {Fake=false}; options.Iso.CacheDir=root; options.HostAdmin.Media.RootDir=Path.Combine(root,"media");
        Assert.Throws<MediaException>(()=>new ServiceCollection().AddMediaPlatform(options));
    }
    [Fact]
    public async Task Administrator_source_is_excluded_even_inside_media_root()
    {
        var root=Path.Combine(Path.GetTempPath(),"media-protected-"+Guid.NewGuid().ToString("n")); Directory.CreateDirectory(root);
        try
        {
            var path=Path.Combine(root,new string('a',32)+".iso"); await File.WriteAllTextAsync(path,"external source");
            var files=new MediaFileStore(root,[path]); Assert.Empty(await files.ListAsync(default));
            await Assert.ThrowsAsync<MediaException>(()=>files.DeleteAsync(path,default)); Assert.True(File.Exists(path));
        }
        finally {Directory.Delete(root,true);}
    }
}
