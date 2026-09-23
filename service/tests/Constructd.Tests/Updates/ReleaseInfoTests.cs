using System.Text.Json;
using Constructd.Api.Composition;
using Constructd.Core.Abstractions;
using Constructd.Windows.Updates;
using Microsoft.Extensions.DependencyInjection;
namespace Constructd.Tests.Updates;

public sealed class ReleaseInfoTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"release-info-test-"+Guid.NewGuid().ToString("n"));
    public ReleaseInfoTests()=>Directory.CreateDirectory(_root);
    public void Dispose()=>Directory.Delete(_root,true);

    [Fact]
    public void Installed_refreshes_when_install_record_changes()
    {
        var path=Path.Combine(_root,"install.json");
        var info=new ReleaseInfo(installRecordPath:path);
        var baseline=info.Installed;
        var other=baseline.Commit==new string('0',40) ? new string('1',40) : new string('0',40);
        Write(other,"other",DateTimeOffset.Parse("2026-01-01T00:00:00Z"),DateTime.UtcNow.AddMinutes(-4));
        Assert.Equal("installer",info.Installed.Source);

        var t1=DateTimeOffset.Parse("2026-02-01T00:00:00Z");
        Write(baseline.Commit,"package-1",t1,DateTime.UtcNow.AddMinutes(-3));
        Assert.Equal(t1,info.Installed.InstalledAt);
        Assert.Equal("package-1",info.Installed.PackageVersion);
        Assert.Equal("release",info.Installed.Source);

        var t2=DateTimeOffset.Parse("2026-03-01T00:00:00Z");
        Write(baseline.Commit,"package-2",t2,DateTime.UtcNow.AddMinutes(-2));
        Assert.Equal(t2,info.Installed.InstalledAt);
        Assert.Equal("package-2",info.Installed.PackageVersion);
        Assert.Equal("release",info.Installed.Source);
    }

    [Fact]
    public void Release_info_resolves_from_di_without_registered_optional_parameters()
    {
        using var services=new ServiceCollection().AddSingleton<IReleaseInfo,ReleaseInfo>().BuildServiceProvider();
        Assert.IsType<ReleaseInfo>(services.GetRequiredService<IReleaseInfo>());
    }

    private void Write(string commit,string packageVersion,DateTimeOffset installedAt,DateTime lastWriteTimeUtc)
    {
        var record=new InstallRecord(commit,packageVersion,installedAt,null,null,[]);
        File.WriteAllText(Path.Combine(_root,"install.json"),JsonSerializer.Serialize(record,UpdateFiles.Json));
        File.SetLastWriteTimeUtc(Path.Combine(_root,"install.json"),lastWriteTimeUtc);
    }
}
