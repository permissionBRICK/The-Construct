using System.Text.Json;
using System.Text.Json.Nodes;
using Construct.Companion.Core.Abstractions;
using Construct.Companion.Fakes;
using Construct.Companion.Host.Dispatch;
using Construct.Companion.Host.Ipc;
using Microsoft.Extensions.DependencyInjection;
namespace Construct.Companion.Tests.Ipc;
public sealed class HostAdminSecretsTests
{
    [Theory]
    [InlineData("secret")] [InlineData("plaintext")] [InlineData("missing")]
    public async Task IssueTokenAcceptsLegacyFieldsAndReportsMissingPlaintext(string field)
    {
        var api=new RoutingRemoteApi();var fallback=api.Handle;var secret=Guid.NewGuid().ToString("N");
        api.Handle=r=>r.Method=="POST"?new(200,JsonSerializer.SerializeToElement(new Dictionary<string,string>{{field,secret}})):fallback(r);
        await using var h=await HttpTests.Harness.Start(s=>s.AddSingleton<IRemoteApi>(api),runtimeJobs:false);
        var hosts=h.App.Services.GetRequiredService<HostAdministration>();await hosts.AddAsync(new("https://host.example:7462",null,new string('a',64)),CancellationToken.None);
        await hosts.DispatchAsync("host.example_7462",new(){["type"]="hostadmin.ready"},CancellationToken.None);
        await hosts.DispatchAsync("host.example_7462",new(){["type"]="hostadmin.action",["action"]="issueToken",["args"]=new JsonObject{["name"]="alice"}},CancellationToken.None);
        var secrets=h.Get<FakePrompts,IPrompts>().Secrets;Assert.Equal(field=="missing"?0:1,secrets.Count);
        if(field=="missing") Assert.Contains("the service returned no plaintext",StateJsonText(hosts.Snapshot("host.example_7462")["state"]?["notice"]?["text"]));
        else { Assert.True(secrets[0].Value.Reveal()==secret);Assert.Equal("Label: (none). Hand it to alice over a channel you trust.",secrets[0].Note); }
        var request=Assert.Single(api.Requests,r=>r.Method=="POST"&&r.Url.AbsolutePath!="/api/v1/host/updates/check");Assert.Equal("issued from VS Code",request.Body!.Value.GetProperty("label").GetString());
        Assert.False(hosts.Snapshot("host.example_7462").ToJsonString().Contains(secret,StringComparison.Ordinal));
    }
    private static string StateJsonText(JsonNode? node)=>Construct.Companion.Core.State.StateJson.String(node);
    [Theory]
    [InlineData("issueToken",true)] [InlineData("rotateVmToken",true)] [InlineData("rotateVmToken",false)]
    public async Task SecretGoesOnlyToNativePromptAndCancelledRotationDoesNothing(string action,bool accept)
    {
        var api=new RoutingRemoteApi();var secret=Guid.NewGuid().ToString("N");var fallback=api.Handle;
        api.Handle=r=>r.Method=="POST"?new(200,JsonSerializer.SerializeToElement(new {token=secret,vmToken=secret})):r.Url.AbsolutePath.EndsWith("/tokens",StringComparison.Ordinal)?new(200,JsonSerializer.SerializeToElement(new[]{new{id="token-id",label="label",created="2026-09-11",token=secret}})):fallback(r);
        await using var h=await HttpTests.Harness.Start(s=>s.AddSingleton<IRemoteApi>(api),runtimeJobs:false);
        var hosts=h.App.Services.GetRequiredService<HostAdministration>();await hosts.AddAsync(new("https://host.example:7462",null,new string('a',64)),CancellationToken.None);
        await hosts.DispatchAsync("host.example_7462",new(){["type"]="hostadmin.ready"},CancellationToken.None);
        var prompts=h.Get<FakePrompts,IPrompts>();if(action=="rotateVmToken") prompts.Confirmations.Enqueue(accept);
        using var subscription=h.App.Services.GetRequiredService<IpcEvents>().Subscribe();
        await hosts.DispatchAsync("host.example_7462",new(){["type"]="hostadmin.action",["action"]=action,["args"]=new JsonObject{["name"]="alice",["kind"]="LEGACY",["label"]="label"}},CancellationToken.None);
        Assert.Equal(accept?1:0,prompts.Secrets.Count);
        Assert.Equal(accept?1:0,api.Requests.Count(r=>r.Method=="POST"&&r.Url.AbsolutePath!="/api/v1/host/updates/check"));
        if(accept)
        {
            var shown=Assert.Single(prompts.Secrets);Assert.True(shown.Value.Reveal()==secret);
            Assert.Equal(action=="issueToken"?"API token for alice":"VM token of alice (legacy)",shown.Title);
            var request=Assert.Single(api.Requests,r=>r.Method=="POST"&&r.Url.AbsolutePath!="/api/v1/host/updates/check");Assert.Equal(action=="issueToken"?"/api/v1/users/alice/tokens":"/api/v1/vms/alice/token",request.Url.AbsolutePath);
            Assert.Equal(action=="issueToken"?"label":"legacy",request.Body!.Value.GetProperty(action=="issueToken"?"label":"kind").GetString());
        }
        while(subscription.Reader.TryRead(out var item)) Assert.False(item.Data.GetRawText().Contains(secret,StringComparison.Ordinal));
        Assert.Null(await h.App.Services.GetRequiredService<ITokenStore>().ReadAsync("host.example_7462"));
        Assert.False(hosts.Snapshot("host.example_7462").ToJsonString().Contains(secret,StringComparison.Ordinal));
        Assert.Empty(h.Get<FakeLauncher,ILauncher>().Detached);Assert.Empty(h.Get<FakeLauncher,ILauncher>().Elevated);
    }
}
