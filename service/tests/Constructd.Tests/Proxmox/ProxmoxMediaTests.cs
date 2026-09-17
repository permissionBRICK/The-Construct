using Constructd.Core.Configuration;
using Constructd.Core.Logic;
using Constructd.Proxmox;

namespace Constructd.Tests.Proxmox;

public sealed class ProxmoxMediaTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "pve-media-" + Guid.NewGuid().ToString("n"));
    private const string Iso = "0123456789abcdef0123456789abcdef.iso";
    private ProxmoxMediaVolumes Mapper()
    {
        Directory.CreateDirectory(root);
        var options = new ConstructdOptions();
        options.HostAdmin.Media.RootDir = root;
        options.Proxmox.MediaStorage = "test-media";
        return new(options);
    }
    [Fact]
    public void Roundtrip_uses_configured_storage_and_requires_ready_file()
    {
        var mapper = Mapper(); var path = Path.Combine(root, Iso);
        Assert.Equal("media-not-ready", Assert.Throws<ChildValidationException>(() => mapper.ToVolume(path)).Code);
        File.WriteAllText(path, "ISO");
        Assert.Equal("test-media:iso/" + Iso, mapper.ToVolume(path));
        Assert.Equal(path, mapper.FromVolume("test-media:iso/" + Iso));
    }
    [Theory]
    [InlineData("../0123456789abcdef0123456789abcdef.iso")]
    [InlineData("sub/0123456789abcdef0123456789abcdef.iso")]
    [InlineData("0123456789abcdef0123456789abcdef.part")]
    [InlineData("file.iso")]
    public void Paths_outside_flat_generated_iso_namespace_are_refused(string path)
    {
        var mapper = Mapper();
        Assert.Throws<ChildValidationException>(() => mapper.ToVolume(Path.Combine(root, path)));
        Assert.Throws<ChildValidationException>(() => mapper.FromVolume("test-media:iso/" + path));
        Assert.Throws<ChildValidationException>(() => mapper.FromVolume("other:iso/" + Iso));
    }
    [Fact]
    public void Links_including_dangling_links_are_refused()
    {
        if (OperatingSystem.IsWindows()) return;
        var mapper = Mapper(); var path = Path.Combine(root, Iso);
        File.CreateSymbolicLink(path, Path.Combine(root, "missing"));
        Assert.Throws<ChildValidationException>(() => mapper.ToVolume(path));
        Assert.Throws<ChildValidationException>(() => mapper.FromVolume("test-media:iso/" + Iso));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
