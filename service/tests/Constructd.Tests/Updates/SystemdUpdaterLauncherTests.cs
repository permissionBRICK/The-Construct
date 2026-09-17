using System.Diagnostics;
using Constructd.Core.Abstractions;
using Constructd.Core.Logic;
using Constructd.Proxmox.Updates;
using Constructd.Windows.Updates;

namespace Constructd.Tests.Updates;

public sealed class SystemdUpdaterLauncherTests : IDisposable
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"systemd-update-"+Guid.NewGuid().ToString("n"));
    public SystemdUpdaterLauncherTests()=>Directory.CreateDirectory(root);
    public void Dispose()=>Directory.Delete(root,true);
    private UpdateHandoff Handoff()=>new(new string('a',32),new string('b',40),Path.Combine(root,"staged path"),Path.Combine(root,"publish"),Path.Combine(root,"scripts"),root,
        "constructd",new string('c',40),"https://127.0.0.1:7462/api/v1/health",new string('d',40),Path.Combine(root,"publish/Constructd.Api"),"secret-only-in-handoff",DateTimeOffset.UtcNow);
    [Fact]
    public async Task Launch_and_resume_use_exact_argv_and_private_handoff()
    {
        var runner=new Runner();var launcher=new SystemdUpdaterLauncher(runner,new FileHostLock(root),root);var h=Handoff();
        await launcher.LaunchAsync(h,default);
        var expected=new[]{"--unit","construct-host-update-"+h.UpdateId,"--collect","--quiet","--property=KillMode=process","/bin/bash",
            Path.Combine(h.StagedPath,"extracted/updater/update-construct-host.sh"),"--handoff",Path.Combine(root,"updates/handoff.json")};
        Assert.Equal(expected,Assert.Single(runner.Calls));
        Assert.Equal(h,await launcher.ReadHandoffAsync(default));
        if(!OperatingSystem.IsWindows())Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(expected[^1]));
        await launcher.ResumeAsync(h,default);Assert.Equal(expected.Append("--resume"),runner.Calls[1]);
        Assert.All(runner.Calls,args=>Assert.DoesNotContain(h.HealthToken,args));
        Assert.Equal(h,await launcher.ReadOwnHandoffAsync(h.Commit,default));
        Assert.Equal(h,await launcher.ReadOwnHandoffAsync(h.PreviousCommit,default));
        Assert.Null(await launcher.ReadOwnHandoffAsync("unknown",default));
    }
    [Fact]
    public async Task Failed_launch_leaves_recovery_files_and_closed_replacement_cannot_be_prepared()
    {
        var runner=new Runner{Fail=true};var launcher=new SystemdUpdaterLauncher(runner,new FileHostLock(root),root);var h=Handoff();
        Assert.Equal("updater-launch-failed",(await Assert.ThrowsAsync<UpdateException>(()=>launcher.LaunchAsync(h,default))).Code);
        Assert.Equal(h,await launcher.ReadHandoffAsync(default));Assert.Single(runner.Calls);
        var fence=new UpdateFence(h.UpdateId,FenceDisposition.Closed,"test",DateTimeOffset.UtcNow);
        Assert.True(await launcher.TryWriteFenceAsync(fence,default));Assert.Equal(fence,await launcher.ReadFenceAsync(default));
        var record=new RecoveryRecord(h.UpdateId,h.Commit,h.PreviousCommit,"replace",DateTimeOffset.UtcNow,null,null,"backup",true,true,h.StagedPath,0,[]);
        await UpdateFiles.WriteAsync(Path.Combine(root,"updates/last-update.json"),record,default);
        var read=(await launcher.ReadRecoveryRecordAsync(default))!;
        Assert.Equal(record with{ManualSteps=read.ManualSteps},read);
        Assert.Equal("update-not-interrupted",(await Assert.ThrowsAsync<UpdateException>(()=>launcher.PrepareAsync(h,default))).Code);
        Assert.False(await launcher.TryWriteFenceAsync(fence,default));
        Assert.Equal(fence,await launcher.ReadFenceAsync(default));
    }
    [Theory]
    [InlineData("quote\"")][InlineData("quote'")][InlineData("line\n")]
    public async Task Unsafe_paths_are_refused_before_handoff_is_written(string suffix)
    {
        var runner=new Runner();var launcher=new SystemdUpdaterLauncher(runner,new FileHostLock(root),root);
        Assert.Equal("invalid-update-path",(await Assert.ThrowsAsync<UpdateException>(()=>launcher.LaunchAsync(Handoff() with{StagedPath=Path.Combine(root,suffix)},default))).Code);
        Assert.Empty(runner.Calls);Assert.False(File.Exists(Path.Combine(root,"updates/handoff.json")));
    }
    [Fact]
    public async Task Dotnet_and_flock_exclude_each_other_in_both_directions()
    {
        if(OperatingSystem.IsWindows())return;
        var hostLock=new FileHostLock(root);var path=Path.Combine(root,"updates/updater.lock");
        await using(var held=await hostLock.TryAcquireAsync("updater.lock",TimeSpan.Zero,default))
        {
            Assert.NotNull(held);
            using var probe=Start("-n",path,"true");await probe.WaitForExitAsync();Assert.Equal(1,probe.ExitCode);
        }
        using var child=Start("-x",path,"sh","-c","echo ready; cat >/dev/null");
        try
        {
            Assert.Equal("ready",await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.True(hostLock.IsHeldByAnotherProcess("updater.lock"));
            Assert.Null(await hostLock.TryAcquireAsync("updater.lock",TimeSpan.Zero,default));
        }
        finally
        {
            child.StandardInput.Close();
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try{await child.WaitForExitAsync(timeout.Token);}finally{if(!child.HasExited){child.Kill(true);await child.WaitForExitAsync();}}
        }
        Assert.False(hostLock.IsHeldByAnotherProcess("updater.lock"));
    }
    private static Process Start(params string[] args)
    {
        var info=new ProcessStartInfo("flock"){RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
        foreach(var arg in args)info.ArgumentList.Add(arg);
        return Process.Start(info)!;
    }
    private sealed class Runner:IProcessRunner
    {
        public bool Fail{get;init;}
        public List<IReadOnlyList<string>> Calls{get;}=[];
        public Task<ProcessResult> RunAsync(string fileName,IReadOnlyList<string> arguments,string? standardInput,TimeSpan timeout,IProgress<string>? standardOutputLines,CancellationToken cancellationToken)
        {
            Assert.Equal("systemd-run",fileName);Assert.Null(standardInput);Assert.Null(standardOutputLines);Assert.Equal(TimeSpan.FromSeconds(20),timeout);
            Calls.Add(arguments);return Task.FromResult(new ProcessResult(Fail?1:0,"","secret failure detail",false));
        }
    }
}
