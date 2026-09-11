using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Constructd.Api.Source;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Services;
using Constructd.Fakes;
namespace Constructd.Tests.Source;

internal sealed class SourceFixture : IDisposable
{
    public const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public MutableClock Clock { get; } = new(DateTimeOffset.UtcNow);
    public InMemoryVmRepository Vms { get; } = new();
    public InMemorySourceStore Store { get; }
    public FakeSourceFiles Files { get; } = new();
    public FakeReleaseSource Releases { get; } = new();
    public SourceGate Gates { get; } = new();
    public SourceCatalog Catalog { get; } = new();
    public ConstructdOptions Options { get; } = new() { Fake = true };
    public InMemoryAuditLog Audit { get; } = new();
    public SourceCache Cache { get; }
    public SourceFixture()
    {
        Store = new(Vms);
        Cache = new(Store, Files, Releases, Gates, Catalog, Options, new InMemoryHostConfigStore(Clock), Clock, Audit);
    }
    public byte[] Add(string commit = Commit, byte[]? bytes = null)
    {
        bytes ??= Zip(); var uri = new Uri("https://github.com/test/repo/" + commit);
        Releases.SourceAssets[commit] = new(commit, "host-" + commit, uri, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)));
        Releases.Assets[uri] = bytes; return bytes;
    }
    public SourceItem Item(SourceState state, string commit = Commit) => new(commit, state, 123, new string('b',64), "host-" + commit, null, "job", Clock.UtcNow, null, Clock.UtcNow);
    public static byte[] Zip(string name = "repo-main/bin/provision.sh", int mode = 0x81ed, string? second = null, int size = 128, CompressionLevel compression = CompressionLevel.NoCompression)
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry(name, compression); entry.ExternalAttributes = mode << 16;
            using (var s = entry.Open()) s.Write(Encoding.UTF8.GetBytes(new string('x', size)));
            if (second is not null) zip.CreateEntry(second);
        }
        return bytes.ToArray();
    }
    public void Dispose() => Files.Dispose();
}
