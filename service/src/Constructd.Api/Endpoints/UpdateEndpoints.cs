using System.Text.Json;
using Constructd.Api.Auth;
using Constructd.Api.Hosting;
using Constructd.Api.Infrastructure;
using Constructd.Api.Jobs;
using Constructd.Core.Abstractions;
using Constructd.Core.Domain;
using Constructd.Core.Logic;
using Constructd.Windows.Updates;
namespace Constructd.Api.Endpoints;

public sealed record UpdateRequest(string? ReleaseTag=null,string? UpdateId=null,string? Action=null,string? OperationKey=null);
public static class UpdateEndpoints
{
    public static RouteGroupBuilder MapUpdateEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/host/updates/status",StatusAsync).RequireAuthorization(Policies.Admin).WithName("HostUpdateStatus");
        foreach(var action in new[]{"check","stage","apply","cancel","resolve"})
        {
            var route=api.MapPost("/host/updates/"+action,(UpdateRequest request,HttpContext http,HostUpdateJob work,UpdateRecoveryService recovery,
                PackageStager checker,IReleaseInfo release,IHostConfigStore config,IOperationKeyStore keys,IClock clock,CancellationToken ct)=>
                MutateAsync(action,request,http,work,recovery,checker,release,config,keys,clock,ct))
                .RequireAuthorization(Policies.Admin).Audited("host.update."+action).WithName("HostUpdate"+action);
            if(action is "apply" or "resolve") route.WithMetadata(new MaintenanceExempt());
        }
        return api;
    }
    private static async Task<IResult> MutateAsync(string action,UpdateRequest request,HttpContext http,HostUpdateJob work,UpdateRecoveryService recovery,
        PackageStager checker,IReleaseInfo release,IHostConfigStore config,IOperationKeyStore keys,IClock clock,CancellationToken ct)
    {
        await work.Acceptance.WaitAsync(ct);
        try
        {
            var actor=http.User.Identity!.Name!;
            CodedProblems.Audit(http,http.TraceIdentifier,actor,target:request.UpdateId);
            var key=http.Request.Headers["X-Construct-Operation-Key"].FirstOrDefault() ?? request.OperationKey;
            var kind="host-update:"+action;
            var fingerprint=OperationFingerprint.Compute(http.Request.Path,JsonSerializer.SerializeToElement(request,UpdateFiles.Json));
            if(key is not null)
            {
                if(!OperationFingerprint.ValidKey(key)) return CodedProblems.Validation("operationKey","Invalid operation key.");
                var old=await keys.GetAsync(actor,kind,key,ct);
                if(old is not null)
                {
                    if(old.Fingerprint!=fingerprint || old.ResponseJson is null) throw new UpdateException("operation-key-conflict");
                    var replay=JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonObject>(old.ResponseJson)!;
                    if(action is "stage" or "apply") replay["replayed"]=true;
                    return Results.Ok(replay);
                }
            }
            var operation=key is null ? null : new OperationKeyRecord(actor,kind,key,fingerprint,request.UpdateId ?? "host",null,OperationKeyState.InFlight,null,null,null,clock.UtcNow);
            object answer;
            switch(action)
            {
                case "check":
                    var latest=await checker.CheckAsync(request.ReleaseTag,ct);
                    var value=latest is null ? null : new {commit=latest.Manifest.Commit,packageVersion=latest.Manifest.PackageVersion,
                        publishedAt=latest.Release.PublishedAt,releaseTag=latest.Release.Tag,compatible=latest.Reasons.Length==0,reasons=latest.Reasons};
                    if(value is not null) await config.SetAsync("update-latest",new{value.commit,value.packageVersion,value.publishedAt,checkedAt=clock.UtcNow},actor,ct);
                    answer=new{installed=release.Installed,latest=value,checkedAt=clock.UtcNow};break;
                case "stage": answer=await work.StageAsync(request.ReleaseTag,actor,ct,operation);break;
                case "apply": answer=await work.ApplyAsync(request.UpdateId ?? "",actor,ct,operation);break;
                case "cancel": answer=await work.CancelAsync(request.UpdateId ?? "",actor,ct);break;
                default: answer=await recovery.ResolveAsync(request.UpdateId ?? "",request.Action ?? "",actor,ct);break;
            }
            if(key is not null && action is not ("stage" or "apply"))
            {
                var json=JsonSerializer.Serialize(answer,UpdateFiles.Json);
                await keys.TryInsertAsync(new(actor,kind,key,fingerprint,request.UpdateId ?? "host",null,OperationKeyState.Completed,null,null,json,clock.UtcNow),ct);
            }
            return action is "stage" or "apply" ? Results.Accepted(value:answer) : Results.Ok(answer);
        }
        catch(UpdateException ex)
        {
            var code=ex.Code;
            if(code=="maintenance") { http.Response.Headers.RetryAfter="30"; return Results.Problem(statusCode:503,title:code,type:"urn:construct:problem:"+code,extensions:new Dictionary<string,object?>{["code"]=code,["phase"]="maintenance",["retryAfterSeconds"]=30,["updateId"]=request.UpdateId}); }
            var status=code=="release-source-unreachable" ? 502 : code == "coverage-failed" ? 422 : 409;
            if(code is "wrong-binary" or "health-failed" or "backup-incomplete" or "installation-mixed")
                return Results.Problem(statusCode:409,title:"update-not-commitable",type:"urn:construct:problem:update-not-commitable",extensions:new Dictionary<string,object?>{["code"]="update-not-commitable",["reason"]=code});
            if(code=="update-not-resolvable")
            {
                var row=await http.RequestServices.GetRequiredService<IHostUpdateStore>().GetAsync(request.UpdateId ?? "",ct);
                return Results.Problem(statusCode:409,title:code,type:"urn:construct:problem:"+code,extensions:new Dictionary<string,object?>{["code"]=code,["state"]=row?.State});
            }
            return CodedProblems.Create(status,code,code);
        }
        finally {work.Acceptance.Release();}
    }
    private static async Task<IResult> StatusAsync(IHostUpdateStore store,IReleaseInfo release,IUpdaterLauncher launcher,PackageStager checker,IHostConfigStore config,CancellationToken ct)
    {
        var rows=await store.ListAsync(10,ct);var record=await launcher.ReadRecoveryRecordAsync(ct);
        static object Project(HostUpdateRecord r)=>new{updateId=r.Id,r.Commit,r.State,r.Phase,r.Phases,r.Started,r.Finished,r.Error,r.BlockingJobs};
        return Results.Ok(new{installed=release.Installed,current=rows.FirstOrDefault() is {} current ? Project(current) : null,
            history=rows.Select(Project),recoveryRecord=record?.Outcome=="succeeded" ? null : record,
            latestKnown=await config.GetAsync<object>("update-latest",ct)});
    }
}
