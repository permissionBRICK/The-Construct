using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Configuration;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Fakes;
using Constructd.Tests.Support;

namespace Constructd.Tests.Updates;

public sealed class LinuxHostUpdateTests
{
    [Fact]
    public async Task Pfx_pin_and_native_cli_are_written_into_the_handoff()
    {
        if(OperatingSystem.IsWindows())return;
        var path=Path.GetTempFileName();
        try
        {
            using var key=RSA.Create(2048);
            using var cert=new CertificateRequest("CN=localhost",key,HashAlgorithmName.SHA256,RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddDays(1));
            await File.WriteAllBytesAsync(path,cert.Export(X509ContentType.Pfx,"fixture-password"));
            using var app=new TestApp();var options=app.Service<ConstructdOptions>();
            // Keep every platform dependency fake; only exercise the production pin requirement.
            options.Fake=false;options.CertThumbprint=null;options.CertPath=path;options.CertPassword="fixture-password";
            using var fixture=new PackageTests();var(m,_)=fixture.Package(linux:true);var id=Guid.NewGuid().ToString("n");
            var staged=new StagedUpdate(id,m,"stage",[],"linux");app.Service<FakeUpdateStager>().Staged[id]=staged;
            await app.Service<IHostConfigStore>().SetAsync("update-staged:"+id,staged,"test",default);
            await app.Service<IHostUpdateStore>().TryStartAsync(UpdateTests.Row(id),default);
            await app.Service<HostUpdateJob>().ApplyAsync(id,"test",default);
            var link=(await app.Service<IHostConfigStore>().GetAsync<UpdateJobLink>("update-job:"+id,default))!;
            await foreach(var _ in app.Service<IJobEngine>().SubscribeAsync(link.JobId,default)){}
            var h=await app.Service<FakeUpdaterLauncher>().ReadHandoffAsync(default);
            Assert.NotNull(h);Assert.Equal(cert.Thumbprint,h.CertificateThumbprint);
            Assert.Equal(Path.Combine(AppContext.BaseDirectory,"Constructd.Api"),h.AdminCliPath);
            Assert.Equal("constructd",h.ServiceName);
        }
        finally{File.Delete(path);}
    }
    [Fact]
    public async Task After_a_rename_the_health_check_pins_the_previous_certificate()
    {
        // The health check dials 127.0.0.1 without SNI, and that handshake gets the previous certificate.
        if(OperatingSystem.IsWindows())return;
        var dir=Directory.CreateTempSubdirectory("constructd-pin-").FullName;
        try
        {
            var current=Path.Combine(dir,"tls.pfx");var previous=Path.Combine(dir,"tls-previous.pfx");
            await File.WriteAllBytesAsync(current,TestCertificates.CreatePfx("construct.example.test","construct.example.test"));
            await File.WriteAllBytesAsync(previous,TestCertificates.CreatePfx("192.0.2.10","192.0.2.10"));
            using var expected=X509CertificateLoader.LoadPkcs12FromFile(previous,TestCertificates.Password);
            using var app=new TestApp();var options=app.Service<ConstructdOptions>();
            options.Fake=false;options.CertThumbprint=null;options.CertPath=current;options.CertPassword=TestCertificates.Password;
            options.PreviousCertPath=previous;options.PreviousCertPassword=TestCertificates.Password;
            using var fixture=new PackageTests();var(m,_)=fixture.Package(linux:true);var id=Guid.NewGuid().ToString("n");
            var staged=new StagedUpdate(id,m,"stage",[],"linux");app.Service<FakeUpdateStager>().Staged[id]=staged;
            await app.Service<IHostConfigStore>().SetAsync("update-staged:"+id,staged,"test",default);
            await app.Service<IHostUpdateStore>().TryStartAsync(UpdateTests.Row(id),default);
            await app.Service<HostUpdateJob>().ApplyAsync(id,"test",default);
            var link=(await app.Service<IHostConfigStore>().GetAsync<UpdateJobLink>("update-job:"+id,default))!;
            await foreach(var _ in app.Service<IJobEngine>().SubscribeAsync(link.JobId,default)){}
            var h=await app.Service<FakeUpdaterLauncher>().ReadHandoffAsync(default);
            Assert.NotNull(h);Assert.Equal(expected.Thumbprint,h.CertificateThumbprint);
        }
        finally{Directory.Delete(dir,recursive:true);}
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_unreadable_pfx_fails_before_drain(bool supplied)
    {
        if(OperatingSystem.IsWindows())return;
        using var app=new TestApp();var options=app.Service<ConstructdOptions>();
        options.Fake=false;options.CertThumbprint=null;options.CertPath=supplied?Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".pfx"):null;
        var error=await Assert.ThrowsAsync<UpdateException>(()=>app.Service<HostUpdateJob>().ApplyAsync("absent","test",default));
        Assert.Equal("update-health-pin-required",error.Code);
        Assert.Equal(MaintenanceState.Open,app.Service<IMaintenanceGate>().State);
        Assert.Equal(0,app.Service<FakeUpdaterLauncher>().LaunchCount);
    }
}
