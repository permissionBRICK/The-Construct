using System.Net.Http.Json;
using Constructd.Api.Source;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Tests.Support;
namespace Constructd.Tests.Source;
public sealed class SourceAuditTests
{
    [Fact]
    public async Task FetchEnsureAndCleanupAuditNeverContainToken()
    {
        await using var app = new TestApp(); using var owner = await app.CreateUserClientAsync("alice"); await SourceEndpointsTests.Seed(app); SourceEndpointsTests.Add(app);
        var ensured = await SourceEndpointsTests.Ensure(owner, "source-audit"); await SourceEndpointsTests.Wait(app, ensured.GetProperty("jobId").GetString()!);
        using var guest = app.CreateVmTokenClient("vm-secret"); using var other = app.CreateVmTokenClient("other-secret");
        await guest.GetAsync("/api/v1/vms/vm/source/" + SourceFixture.Commit); await other.GetAsync("/api/v1/vms/vm/source/" + SourceFixture.Commit);
        await guest.GetAsync("/api/v1/vms/vm/source/" + SourceFixture.Other);
        using var admin = await app.CreateUserClientAsync("admin", Role.Admin);
        await admin.DeleteAsync("/api/v1/host/source-cache/" + SourceFixture.Commit);
        var delete = await admin.DeleteAsync("/api/v1/host/source-cache/" + SourceFixture.Commit + "?force=true");
        await SourceEndpointsTests.Wait(app, (await delete.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("jobId").GetString()!);
        await app.Service<ISourceCache>().EnsureAsync(SourceFixture.Commit, null, default);
        await File.WriteAllBytesAsync(app.Service<ISourceFiles>().PathFor(SourceFixture.Commit, SourceFileKind.Zip), []);
        await guest.GetAsync("/api/v1/vms/vm/source/" + SourceFixture.Commit);
        await app.Service<ISourceCache>().PruneAsync("system", null, default);
        var audit = await app.Service<IAuditLog>().QueryAsync(100, default);
        Assert.Contains(audit, a => a.Action == "vm.source.ensure" && a.Actor == "alice");
        Assert.Contains(audit, a => a.Action == "vm.source.fetch" && a.Actor == "vm:vm" && a.Detail!.Contains("outcome=success"));
        Assert.Contains(audit, a => a.Action == "vm.source.fetch" && a.Detail!.Contains("outcome=denied"));
        Assert.Contains(audit, a => a.Action == "source.fetch.completed" && a.Actor == "system");
        Assert.Contains(audit, a => a.Action == "source.removed"); Assert.Contains(audit, a => a.Action == "host.source.cleanup");
        Assert.Contains(audit, a => a.Action == "source.corrupt" && a.Detail == "reason=size");
        Assert.Contains(audit, a => a.Action == "host.source.delete" && a.Target == SourceFixture.Commit && a.Detail!.Contains("force=true"));
        Assert.Contains(audit, a => a.Action == "host.source.delete" && a.Target == SourceFixture.Commit && a.Detail!.Contains("force=false") && a.Outcome != AuditOutcome.Success);
        Assert.Contains(audit, a => a.Action == "vm.source.fetch" && a.Detail!.Contains("code=source-not-cached"));
        Assert.DoesNotContain("vm-secret", System.Text.Json.JsonSerializer.Serialize(audit));
    }
}
