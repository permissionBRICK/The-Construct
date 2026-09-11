using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Windows.Updates;
namespace Constructd.Tests.Updates;

public sealed class PackageTests : IDisposable
{
    private readonly string _root=Path.Combine(Path.GetTempPath(),"package-test-"+Guid.NewGuid().ToString("n"));
    public PackageTests()=>Directory.CreateDirectory(_root);
    public void Dispose()=>Directory.Delete(_root,true);
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    internal (ReleaseManifest Manifest,byte[] Zip) Package(string? extra=null,int attributes=0,bool listed=true)
    {
        var files=new Dictionary<string,byte[]>{["service/Constructd.Api.exe"]=RandomNumberGenerator.GetBytes(4096),
            ["scripts/drivers/driver.ps1"]=Encoding.UTF8.GetBytes("driver"),["updater/Update-ConstructHost.ps1"]=Encoding.UTF8.GetBytes("updater")};
        if(extra is not null && listed) files[extra]=Encoding.UTF8.GetBytes("extra");
        var sums=Encoding.UTF8.GetBytes(string.Concat(files.Select(f=>$"{Hash(f.Value)}  {f.Key}\n")));
        using var output=new MemoryStream();
        using(var archive=new ZipArchive(output,ZipArchiveMode.Create,true))
        {
            foreach(var file in files.Append(new("SHA256SUMS",sums)))
            {var entry=archive.CreateEntry(file.Key,CompressionLevel.NoCompression);entry.ExternalAttributes=file.Key==extra ? attributes : 0;using var stream=entry.Open();stream.Write(file.Value);}
            if(extra is not null&&!listed){var entry=archive.CreateEntry(extra,CompressionLevel.NoCompression);using var stream=entry.Open();stream.WriteByte(1);}
        }
        var zip=output.ToArray();var commit=new string('a',40);
        var m=new ReleaseManifest(1,commit,"refs/heads/main","test",DateTimeOffset.UtcNow,"permissionBRICK/The-Construct","host-"+commit,
            "construct-host-aaaaaaa-win-x64.zip",Hash(zip),Hash(sums),"updater/Update-ConstructHost.ps1",Hash(files["updater/Update-ConstructHost.ps1"]),new(600,0,[]),new(1,1,[],[]),new("2026-08-01",0));
        return(m,zip);
    }
    [Fact] public async Task Production_stage_without_signing_is_pinned_and_every_file_is_reverified()
    {
        var (manifest,zip)=Package();
        var bytes=JsonSerializer.SerializeToUtf8Bytes(manifest,UpdateFiles.Json);
        var source=new FakeReleaseSource();var assets=new[]{("manifest.json",bytes),(manifest.PayloadAsset,zip)}.Select(a=>{
            var uri=new Uri("https://github.com/permissionBRICK/The-Construct/releases/download/"+manifest.ReleaseTag+"/"+a.Item1);source.Assets[uri]=a.Item2;return new ReleaseAsset(a.Item1,uri,a.Item2.Length);}).ToArray();
        var release=new ReleaseDescriptor(manifest.ReleaseTag,manifest.Commit,DateTimeOffset.UtcNow,assets);source.Releases.Add(release);
        var config=new InMemoryHostConfigStore(new MutableClock());
        var stager=new PackageStager(source,config,new(){DatabasePath=Path.Combine(_root,"db"),Fake=false},new FakeReleaseInfo());
        var check=await stager.CheckAsync(null,default);Assert.NotNull(check);Assert.Empty(check.Reasons);
        var staged=await stager.StageAsync(Guid.NewGuid().ToString("n"),release,null,default);
        Assert.True(await stager.VerifyStagedAsync(staged,default));
        await File.WriteAllTextAsync(Path.Combine(staged.StagedPath,"extracted","service","Constructd.Api.exe"),"tampered");
        Assert.False(await stager.VerifyStagedAsync(staged,default));
        source.Assets[assets[0].Url]=Encoding.UTF8.GetBytes("tampered");
        var error=await Assert.ThrowsAsync<UpdateException>(()=>stager.CheckAsync(null,default));Assert.Equal("incompatible",error.Code);
    }
    [Theory]
    [InlineData("service/../../outside",0,true,"extraction-refused")]
    [InlineData("service/link",unchecked((int)0xA0000000),true,"extraction-refused")]
    [InlineData("service/reparse",1024,true,"extraction-refused")]
    [InlineData("service/unlisted.dll",0,false,"coverage-failed")]
    public void Unsafe_or_uncovered_archives_fail(string entry,int attrs,bool listed,string code)
    {
        var(m,zip)=Package(entry,attrs,listed);File.WriteAllBytes(Path.Combine(_root,"package.zip"),zip);
        Assert.Equal(code,Assert.Throws<UpdateException>(()=>PackageStager.ExtractAndVerify(_root,m)).Code);
    }
    [Fact] public void Payload_corruption_is_rejected_without_signatures()
    {
        var (manifest,zip)=Package();zip[^1]^=1;
        File.WriteAllBytes(Path.Combine(_root,"package.zip"),zip);
        Assert.Equal("payload-hash-mismatch",Assert.Throws<UpdateException>(()=>PackageStager.ExtractAndVerify(_root,manifest)).Code);
    }
    [Fact] public void Legacy_signing_fields_do_not_block_reading_stored_update_settings()
    {
        var legacy="""{"repository":"permissionBRICK/The-Construct","channel":"main","drainTimeoutMinutes":60,"healthTimeoutSeconds":120,"requireSignature":true,"manifestPublicKey":"unused"}""";
        Assert.Equal(HostAdminDefaults.Updates,JsonSerializer.Deserialize<Constructd.Core.Domain.UpdatesConfig>(legacy,UpdateFiles.Json));
    }
    [Fact] public async Task Host_lock_is_exclusive_and_reusable()
    {
        var a=new FileHostLock(_root);var b=new FileHostLock(_root);
        await using(var held=await a.TryAcquireAsync("admin.lock",TimeSpan.Zero,default))
        {Assert.NotNull(held);Assert.True(b.IsHeldByAnotherProcess("admin.lock"));Assert.Null(await b.TryAcquireAsync("admin.lock",TimeSpan.Zero,default));}
        await using var next=await b.TryAcquireAsync("admin.lock",TimeSpan.Zero,default);Assert.NotNull(next);
    }
    [Fact] public async Task Scheduled_task_argv_is_pinned_and_health_secret_is_only_in_handoff()
    {
        var runner=new RecordingRunner();var launcher=new ScheduledTaskUpdaterLauncher(runner,new FileHostLock(_root),_root);
        var handoff=new UpdateHandoff(new string('a',32),new string('b',40),Path.Combine(_root,new string('s', 150),"staged path"),"C:\\Construct\\service\\publish","C:\\Construct",_root,"constructd",new string('c',40),"https://127.0.0.1:7462/api/v1/health",new string('d',40),"Constructd.Api.exe","test-health-secret",DateTimeOffset.UtcNow);
        await launcher.LaunchAsync(handoff,default);
        Assert.Equal(2,runner.Calls.Count);var create=runner.Calls[0];Assert.Equal("schtasks.exe",create.File);
        Assert.Equal(new[]{"/Create","/TN","Construct-HostUpdate","/XML",Path.Combine(_root,"updates","updater-task.xml"),"/F"},create.Args);
        var xml = await File.ReadAllTextAsync(create.Args[4]);
        Assert.Contains("S-1-5-18", xml); Assert.DoesNotContain(handoff.HealthToken, xml);
        Assert.DoesNotContain("/TR", create.Args);
        Assert.DoesNotContain(System.Xml.Linq.XDocument.Parse(xml).Descendants(), e => e.Name.LocalName == "LogonType");
        Assert.Equal(new[]{"/Run","/TN","Construct-HostUpdate"},runner.Calls[1].Args);
        await launcher.ResumeAsync(handoff,default);
        var task = System.Xml.Linq.XDocument.Load(create.Args[4]);
        Assert.True(task.Descendants().Single(e => e.Name.LocalName == "Arguments").Value.Length > 261);
        Assert.EndsWith(" -Resume", task.Descendants().Single(e => e.Name.LocalName == "Arguments").Value);
        // Paths longer than the default Windows layout are represented without truncation.
        Assert.Contains(Path.Combine(handoff.StagedPath,"extracted","updater","Update-ConstructHost.ps1"),
            task.Descendants().Single(e => e.Name.LocalName == "Arguments").Value);
    }
    [Fact] public async Task Automatic_closed_fence_cannot_hide_a_replacement_recorded_before_lock_acquisition()
    {
        var launcher=new ScheduledTaskUpdaterLauncher(new RecordingRunner(),new FileHostLock(_root),_root);
        await UpdateFiles.WriteAsync(Path.Combine(_root,"updates","last-update.json"),
            new RecoveryRecord("update","new","old","replace",DateTimeOffset.UtcNow,"recoveryFailed",null,"backup",true,true,"stage",0,[]),default);
        Assert.False(await launcher.TryWriteFenceAsync(new("update",FenceDisposition.Closed,"system",DateTimeOffset.UtcNow),default));
        Assert.False(File.Exists(Path.Combine(_root,"updates","fence.json")));
    }
    [Fact] public async Task Local_packager_without_signing_passes_the_production_extractor()
    {
        var repo=new DirectoryInfo(AppContext.BaseDirectory);
        while(repo is not null && !File.Exists(Path.Combine(repo.FullName,"service/host/New-ConstructHostPackage.ps1"))) repo=repo.Parent;
        Assert.NotNull(repo);
        var publish=Path.Combine(_root,"publish");Directory.CreateDirectory(publish);
        await File.WriteAllBytesAsync(Path.Combine(publish,"Constructd.Api.exe"),RandomNumberGenerator.GetBytes(4096));
        var output=Path.Combine(_root,"output");
        await Run("pwsh","-NoProfile","-File",Path.Combine(repo.FullName,"service/host/New-ConstructHostPackage.ps1"),"-PublishDir",publish,"-OutputDir",output,"-Commit",new string('a',40));
        var bytes=await File.ReadAllBytesAsync(Path.Combine(output,"manifest.json"));
        var manifest=JsonSerializer.Deserialize<ReleaseManifest>(bytes,UpdateFiles.Json)!;
        File.Copy(Path.Combine(output,manifest.PayloadAsset),Path.Combine(_root,"package.zip"));
        var files=PackageStager.ExtractAndVerify(_root,manifest);
        Assert.Contains(files,f=>f.Path=="scripts/service/host/Update-ConstructHost.ps1");
        Assert.Contains(files,f=>f.Path=="service/Constructd.Api.exe");
    }
    private static async Task Run(string executable,params string[] args)
    {
        var start=new System.Diagnostics.ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args)start.ArgumentList.Add(arg);
        using var process=System.Diagnostics.Process.Start(start)!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try {await process.WaitForExitAsync(timeout.Token);}
        finally {if(!process.HasExited) {process.Kill(true);await process.WaitForExitAsync();}}
        Assert.True(process.ExitCode==0,$"{executable} failed: {await stdout} {await stderr}");
    }
    private sealed class RecordingRunner:IProcessRunner
    {
        public List<(string File,IReadOnlyList<string> Args)> Calls {get;}=[];
        public Task<ProcessResult> RunAsync(string fileName,IReadOnlyList<string> arguments,string? standardInput,TimeSpan timeout,IProgress<string>? standardOutputLines,CancellationToken cancellationToken)
        {Calls.Add((fileName,arguments));return Task.FromResult(new ProcessResult(0,"","",false));}
    }
}
