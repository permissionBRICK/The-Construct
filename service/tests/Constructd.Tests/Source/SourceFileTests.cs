using Constructd.Api.Composition;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Windows.Source;
using Microsoft.Extensions.DependencyInjection;
namespace Constructd.Tests.Source;
public sealed class SourceFileTests
{
    [Theory]
    [InlineData("../escape")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    public void FileNamesAreConfined(string commit)
    {
        var files = new SourceFileStore(Path.GetTempPath());
        Assert.Throws<SourceException>(() => files.PathFor(commit, SourceFileKind.Zip));
    }
    [Theory]
    [InlineData("same")]
    [InlineData("inside")]
    [InlineData("outside")]
    public void SourceRootCannotOverlapOtherManagedRoots(string arrangement)
    {
        var root = Path.Combine(Path.GetTempPath(), "source-options-" + Guid.NewGuid().ToString("n"));
        var media = Path.Combine(root,"media");
        var options = new ConstructdOptions { Fake = true, DatabasePath = Path.Combine(root,"db") };
        options.HostAdmin.Media.RootDir = media;
        options.HostAdmin.Source.RootDir = arrangement switch { "same" => media, "inside" => Path.Combine(media,"source"), _ => root };
        Assert.Equal("source-root-overlap", Assert.Throws<SourceException>(() => new ServiceCollection().AddSourcePlatform(options)).Code);
        Assert.False(Directory.Exists(root));
    }
    [Fact]
    public void InvalidCapsFailBeforeDirectoryCreation()
    {
        var options = new ConstructdOptions { Fake = true }; options.HostAdmin.Source.MaxTotalBytes = 1;
        Assert.Equal("source-size-invalid", Assert.Throws<SourceException>(() => new ServiceCollection().AddSourcePlatform(options)).Code);
    }
}
