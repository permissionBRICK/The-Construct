using System.IO.Compression;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Windows.Updates;

namespace Constructd.Tests.Updates;

public sealed class LinuxPackageTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"linux-package-"+Guid.NewGuid().ToString("n"));
    public LinuxPackageTests()=>Directory.CreateDirectory(root);
    public void Dispose()=>Directory.Delete(root,true);
    [Fact]
    public async Task Linux_stages_reverifies_and_detects_tamper_while_windows_selects_its_own_asset()
    {
        using var fixture=new PackageTests(); var(m,zip)=fixture.Package(linux:true);
        var source=new FakeReleaseSource();
        var assets=new[]{("manifest.json",JsonSerializer.SerializeToUtf8Bytes(m,UpdateFiles.Json)),(m.LinuxAsset!,zip),(m.PayloadAsset,zip)}.Select(a=>
        {var url=new Uri("https://github.com/test/"+a.Item1);source.Assets[url]=a.Item2;return new ReleaseAsset(a.Item1,url,a.Item2.Length);}).ToArray();
        var release=new ReleaseDescriptor(m.ReleaseTag,m.Commit,m.BuiltAt,assets);source.Releases.Add(release);
        PackageStager Stager(bool windows)=>new(source,new InMemoryHostConfigStore(new MutableClock()),new(){DatabasePath=Path.Combine(root,"db")},new FakeReleaseInfo()){IsWindows=windows};
        Assert.Equal("self-contained",(await Stager(true).CheckAsync(null,default))!.Source);
        var native=new PackageStager(source,new InMemoryHostConfigStore(new MutableClock()),new(){DatabasePath=Path.Combine(root,"db")},new FakeReleaseInfo());
        Assert.Equal(OperatingSystem.IsWindows()?"self-contained":"linux",(await native.CheckAsync(null,default))!.Source);
        var stager=Stager(false);
        var staged=await stager.StageAsync(Guid.NewGuid().ToString("n"),release,null,default);
        Assert.Equal("linux",staged.Source);Assert.Equal(m.LinuxAsset,source.Downloads.Last().Name);
        Assert.True(await stager.VerifyStagedAsync(staged,default));
        await File.WriteAllTextAsync(Path.Combine(staged.StagedPath,"extracted/service/Constructd.Api"),"tampered");
        Assert.False(await stager.VerifyStagedAsync(staged,default));
    }
    [Fact]
    public async Task Old_release_checks_incompatible_and_stage_reports_no_linux_asset()
    {
        using var fixture=new PackageTests(); var(m,_)=fixture.Package();
        var source=new FakeReleaseSource();var bytes=JsonSerializer.SerializeToUtf8Bytes(m,UpdateFiles.Json);
        var url=new Uri("https://github.com/test/manifest.json");source.Assets[url]=bytes;
        var release=new ReleaseDescriptor(m.ReleaseTag,m.Commit,m.BuiltAt,[new("manifest.json",url,bytes.Length)]);source.Releases.Add(release);
        var stager=new PackageStager(source,new InMemoryHostConfigStore(new MutableClock()),new(){DatabasePath=Path.Combine(root,"db")},new FakeReleaseInfo()){IsWindows=false};
        Assert.Contains("no-linux-asset",(await stager.CheckAsync(null,default))!.Reasons);
        Assert.Equal("no-linux-asset",(await Assert.ThrowsAsync<UpdateException>(()=>stager.StageAsync(Guid.NewGuid().ToString("n"),release,null,default))).Code);
    }
    [Fact]
    public void Partial_or_misnamed_linux_metadata_is_refused()
    {
        using var fixture=new PackageTests();var(m,_)=fixture.Package(linux:true);
        ManifestRules.ValidateVariants(m);
        foreach(var invalid in new[]{m with{LinuxAsset="wrong.zip"},m with{LinuxUpdaterPath="updater/Update-ConstructHost.ps1"},
            m with{LinuxSha256=null},m with{LinuxSumsSha256="bad"},m with{LinuxSizeBytes=0},m with{LinuxUncompressedSizeBytes=null},m with{LinuxUpdaterSha256=null},m with{LinuxAsset=null}})
            Assert.Throws<UpdateException>(()=>ManifestRules.ValidateVariants(invalid));
    }
    [Fact]
    public void Linux_archive_requires_its_hash_list()
    {
        using var fixture=new PackageTests();var(m,bytes)=fixture.Package(linux:true);
        var path=Path.Combine(root,"package.zip");File.WriteAllBytes(path,bytes);
        using(var zip=ZipFile.Open(path,ZipArchiveMode.Update))zip.GetEntry("SHA256SUMS")!.Delete();
        using(var zip=ZipFile.OpenRead(path))m=m with{LinuxSha256=UpdateFiles.Sha256(path),LinuxSizeBytes=new FileInfo(path).Length,LinuxUncompressedSizeBytes=zip.Entries.Sum(e=>e.Length)};
        Assert.Equal("coverage-failed",Assert.Throws<UpdateException>(()=>PackageStager.ExtractAndVerify(root,m,"linux")).Code);
    }
    [Fact]
    public async Task Local_linux_packager_passes_the_production_extractor()
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory);
        while(repo is not null&&!File.Exists(Path.Combine(repo.FullName,"service/host/New-ConstructHostPackage.ps1")))repo=repo.Parent;
        Assert.NotNull(repo);
        var publish=Path.Combine(root,"publish");Directory.CreateDirectory(publish);
        await File.WriteAllTextAsync(Path.Combine(publish,"Constructd.Api.exe"),"windows");
        await File.WriteAllTextAsync(Path.Combine(publish,"Constructd.Api"),"linux");
        var output=Path.Combine(root,"output");
        await PackageTests.Run("pwsh","-NoProfile","-File",Path.Combine(repo.FullName,"service/host/New-ConstructHostPackage.ps1"),"-PublishDir",publish,"-LinuxPublishDir",publish,"-OutputDir",output,"-Commit",new string('a',40));
        var m=(await UpdateFiles.ReadAsync<ReleaseManifest>(Path.Combine(output,"manifest.json"),default))!;
        ManifestRules.ValidateVariants(m);
        File.Copy(Path.Combine(output,m.LinuxAsset!),Path.Combine(root,"package.zip"));
        var files=PackageStager.ExtractAndVerify(root,m,"linux");
        Assert.Contains(files,f=>f.Path=="service/Constructd.Api");
        Assert.Contains(files,f=>f.Path=="scripts/bin/provision.sh");
        Assert.Contains(files,f=>f.Path=="updater/update-construct-host.sh");
    }
}
