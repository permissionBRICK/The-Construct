using System.Net;
using System.Text;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Windows.Media;
namespace Constructd.Tests.Media;

public sealed class MediaTransferTests
{
    private sealed class Resolver(params string[] addresses) : IMediaDnsResolver
    {
        public int Calls;
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken ct) { Calls++; return Task.FromResult(addresses.Select(IPAddress.Parse).ToArray()); }
    }
    private sealed class Wire(byte[] response, string peer = "93.184.216.34") : IMediaConnectionFactory
    {
        public List<IPAddress> Addresses = [];
        public Task<(Stream, IPAddress)> ConnectAsync(IPAddress address, int port, CancellationToken ct)
        { Addresses.Add(address); return Task.FromResult<(Stream, IPAddress)>((new Duplex(response), IPAddress.Parse(peer))); }
    }
    private sealed class Duplex(byte[] response) : Stream
    {
        private readonly MemoryStream input = new(response);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] b,int o,int c) => input.Read(b,o,c);
        public override ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default) => input.ReadAsync(b,ct);
        public override void Write(byte[] b,int o,int c) { } public override ValueTask WriteAsync(ReadOnlyMemory<byte> b,CancellationToken ct=default) => ValueTask.CompletedTask;
        public override void Flush() { } public override long Seek(long o,SeekOrigin s) => throw new NotSupportedException(); public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if(disposing) input.Dispose(); base.Dispose(disposing); }
    }
    [Theory]
    [InlineData("127.0.0.1")] [InlineData("169.254.169.254")] [InlineData("::1")] [InlineData("10.1.1.1")]
    [InlineData("172.16.0.1")] [InlineData("192.168.1.1")] [InlineData("100.64.0.1")] [InlineData("198.18.0.1")]
    [InlineData("192.0.0.1")] [InlineData("0.1.2.3")] [InlineData("224.0.0.1")] [InlineData("255.255.255.255")]
    [InlineData("fe80::1")] [InlineData("fc00::1")] [InlineData("ff02::1")] [InlineData("::ffff:127.0.0.1")]
    [InlineData("::127.0.0.1")] [InlineData("64:ff9b::a00:1")] [InlineData("::")]
    [InlineData("2002:7f00:1::")][InlineData("2001::1")][InlineData("64:ff9b:1::a00:1")][InlineData("100::1")]
    [InlineData("192.0.2.1")][InlineData("198.51.100.1")][InlineData("203.0.113.1")]
    public void Ssrf_matrix(string address) => Assert.False(new UrlAdmissionRules().Check(new Uri("https://public.example/"), [IPAddress.Parse("93.184.216.34"),IPAddress.Parse(address)],false,false).Allowed);
    [Theory]
    [InlineData("127.0.0.1")] [InlineData("169.254.169.254")] [InlineData("[::1]")]
    public async Task Redirect_to_private_is_refused_before_connection(string target)
    {
        var dns = new Resolver("93.184.216.34"); var wire = new Wire(Encoding.ASCII.GetBytes($"HTTP/1.1 302 Found\r\nLocation: http://{target}/secret\r\nContent-Length: 0\r\n\r\n"));
        var files = new InMemoryMediaFiles(); var transfer = new HttpMediaTransfer(files,dns,new UrlAdmissionRules(),wire);
        var item = MediaStorageTests.Item() with { ExpectedSha256 = new string('a',64) }; item = item with { Path = files.PathFor(item.Id) };
        Assert.Equal("url-refused",(await Assert.ThrowsAsync<MediaException>(() => transfer.AcquireAsync(item,new Uri("http://public.example/"),50000,TimeSpan.FromSeconds(5),null,default))).Code);
        Assert.Single(wire.Addresses);
    }
    [Fact]
    public async Task Rebinding_peer_is_rejected_and_dns_is_not_repeated()
    {
        var dns = new Resolver("93.184.216.34"); var wire = new Wire([],"127.0.0.1"); var files = new InMemoryMediaFiles();
        var transfer = new HttpMediaTransfer(files,dns,new UrlAdmissionRules(),wire); var item = MediaStorageTests.Item() with { ExpectedSha256 = new string('a',64) };
        await Assert.ThrowsAsync<MediaException>(() => transfer.AcquireAsync(item,new Uri("http://public.example/"),50000,TimeSpan.FromSeconds(5),null,default));
        Assert.Equal(1,dns.Calls); Assert.Equal("93.184.216.34",Assert.Single(wire.Addresses).ToString());
    }
    [Theory]
    [InlineData(false, 50000, false, null)] [InlineData(true, 50000, false, null)]
    [InlineData(false, 100, false, "media-too-large")] [InlineData(true, 100, false, "media-too-large")]
    [InlineData(true, 50000, true, "checksum-mismatch")]
    public async Task Bounded_stream_and_unknown_length(bool unknown,long max,bool mismatch,string? error)
    {
        var bytes = new byte[40000]; new byte[] {1,67,68,48,48,49,1}.CopyTo(bytes,32768);
        var sha = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        var header = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nConnection: close\r\n" + (unknown ? "" : $"Content-Length: {bytes.Length}\r\n") + "\r\n");
        var wire = new Wire(header.Concat(bytes).ToArray()); var files = new InMemoryMediaFiles();
        var transfer = new HttpMediaTransfer(files,new Resolver("93.184.216.34"),new UrlAdmissionRules(),wire);
        var item = MediaStorageTests.Item() with { ExpectedSha256 = mismatch ? new string('a',64) : sha }; item = item with { Path = files.PathFor(item.Id) };
        if(error is null) { var result = await transfer.AcquireAsync(item,new Uri("http://public.example/secret?token=hidden"),max,TimeSpan.FromSeconds(5),null,default); Assert.Equal(40000,result.SizeBytes); Assert.Equal("",result.FinalUrl.Query); Assert.Equal(sha,result.Sha256); }
        else Assert.Equal(error,(await Assert.ThrowsAsync<MediaException>(() => transfer.AcquireAsync(item,new Uri("http://public.example/"),max,TimeSpan.FromSeconds(5),null,default))).Code);
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage,HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct) => Task.FromResult(respond(request));
    }
    private sealed class RedirectResolver(string target) : IMediaDnsResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host,CancellationToken ct) => Task.FromResult(new[] {IPAddress.Parse(host == "private.example" ? target : "93.184.216.34")});
    }
    [Theory]
    [InlineData("127.0.0.1")] [InlineData("::1")]
    public async Task Hostname_redirect_private_resolution_never_connects(string address)
    {
        var wire = new Wire(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://private.example/\r\nContent-Length: 0\r\n\r\n"));
        var transfer=new HttpMediaTransfer(new InMemoryMediaFiles(),new RedirectResolver(address),new UrlAdmissionRules(),wire);
        var item=MediaStorageTests.Item() with {ExpectedSha256=new string('a',64)};
        Assert.Equal("url-refused",(await Assert.ThrowsAsync<MediaException>(()=>transfer.AcquireAsync(item,new Uri("http://public.example/"),50000,TimeSpan.FromSeconds(5),null,default))).Code);
        Assert.Single(wire.Addresses);
    }
    [Theory]
    [InlineData("http://public.example/down")] [InlineData("https://user:password@public.example/hidden")]
    public async Task Redirect_downgrade_and_userinfo_are_refused(string location)
    {
        var requests=0;
        var transfer=new HttpMediaTransfer(new InMemoryMediaFiles(),new Resolver("93.184.216.34"),new UrlAdmissionRules(),new Wire([]),
            (_,_)=>new ResponseHandler(_=> { requests++; var response=new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location=new Uri(location); return response; }));
        Assert.Equal("url-refused",(await Assert.ThrowsAsync<MediaException>(()=>transfer.AcquireAsync(MediaStorageTests.Item(),new Uri("https://public.example/"),50000,TimeSpan.FromSeconds(5),null,default))).Code);
        Assert.Equal(1,requests);
    }
    [Theory]
    [InlineData(5,true)] [InlineData(6,false)]
    public async Task Redirect_hop_limit(int redirects,bool success)
    {
        var requests=0; var bytes=new byte[40000]; new byte[] {1,67,68,48,48,49,1}.CopyTo(bytes,32768);
        var files=new InMemoryMediaFiles();
        var transfer=new HttpMediaTransfer(files,new Resolver("93.184.216.34"),new UrlAdmissionRules(),new Wire([]),
            (_,_)=>new ResponseHandler(_=> { requests++; if(requests>redirects) return new(HttpStatusCode.OK) {Content=new ByteArrayContent(bytes)};
                var response=new HttpResponseMessage(HttpStatusCode.Redirect); response.Headers.Location=new Uri("https://public.example/"+requests); return response; }));
        var item=MediaStorageTests.Item(); item=item with {Path=files.PathFor(item.Id)};
        if(success) Assert.Equal(40000,(await transfer.AcquireAsync(item,new Uri("https://public.example/"),50000,TimeSpan.FromSeconds(5),null,default)).SizeBytes);
        else Assert.Equal("url-refused",(await Assert.ThrowsAsync<MediaException>(()=>transfer.AcquireAsync(item,new Uri("https://public.example/"),50000,TimeSpan.FromSeconds(5),null,default))).Code);
        Assert.Equal(6,requests);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Non_iso_requires_matching_checksum(bool checksum)
    {
        var bytes=new byte[100]; var files=new InMemoryMediaFiles();
        var transfer=new HttpMediaTransfer(files,new Resolver("93.184.216.34"),new UrlAdmissionRules(),new Wire([]),
            (_,_)=>new ResponseHandler(_=>new(HttpStatusCode.OK) {Content=new ByteArrayContent(bytes)}));
        var item=MediaStorageTests.Item(); item=item with {Path=files.PathFor(item.Id),ExpectedSha256=checksum ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) : null};
        if(checksum) Assert.Equal(100,(await transfer.AcquireAsync(item,new Uri("https://public.example/"),50000,TimeSpan.FromSeconds(5),null,default)).SizeBytes);
        else Assert.Equal("not-an-iso",(await Assert.ThrowsAsync<MediaException>(()=>transfer.AcquireAsync(item,new Uri("https://public.example/"),50000,TimeSpan.FromSeconds(5),null,default))).Code);
    }
}
